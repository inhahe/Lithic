using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Tiered version retention: which earlier versions of a file its tiers no
/// longer keep. The rule lives here once (<see cref="SelectExpired"/>) and serves
/// the backup engine's retention pass, Cleanup's Excess Versions, and the
/// editor's offer after a retention change.
/// </summary>
public class VersionRetentionService
{
    private readonly ICatalogRepository _catalog;

    public VersionRetentionService(ICatalogRepository catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Sensible default retention tiers.
    /// </summary>
    public static IReadOnlyList<VersionRetentionTier> DefaultTiers => [
        new() { MaxAge = TimeSpan.FromDays(10), MaxVersions = null },     // Keep all < 10 days
        new() { MaxAge = TimeSpan.FromDays(365), MaxVersions = 3 },       // Keep 3 versions < 1 year
        new() { MaxAge = null, MaxVersions = 1 },                          // Keep 1 version for older
    ];

    /// <summary>
    /// The earlier versions the backup engine's retention pass retires, under
    /// per-file tiers. A file whose tiers are empty is left alone here: this pass
    /// runs unattended after every backup, and a set's empty Default would
    /// otherwise discard history the moment it was saved. Removing those is
    /// <see cref="FindVersionsNotKept"/>'s, for Cleanup and the editor, after the
    /// user has seen the list.
    /// </summary>
    /// <param name="tierSelector">A source path's retention tiers.</param>
    /// <param name="pathScope">
    /// When null, every path in the set is evaluated. When non-null, only these
    /// source paths are — used by continuous/targeted backups, which write a new
    /// version for a handful of paths and would otherwise pay for the whole set.
    /// </param>
    /// <param name="onIndexing">
    /// Called once, for the whole set, if its earlier-versions index has to be
    /// built first: a full scan of the set under its write lock, which takes
    /// minutes on a large set, once per set.
    /// </param>
    /// <remarks>
    /// <para>
    /// Retention is computed per source path from that path's own version rows
    /// and the current time — no cross-path state — so evaluating a subset of
    /// paths yields exactly the same verdicts for those paths as evaluating all
    /// of them.
    /// </para>
    /// <para>
    /// The scoping is nonetheless a deliberate trade, because retention is partly
    /// age-driven: a path nobody touched can drift into an older, stricter tier
    /// purely by the passage of time. Those paths are pruned by the next
    /// <em>full</em> backup, which always passes a null scope. What must not be
    /// deferred — a path exceeding its quota <em>because this run just added a
    /// version to it</em> — is always in scope: <c>DirectoryBackupService</c>
    /// passes every file it gave a second or later version. (A file's first
    /// version leaves no previous one, so it can have nothing to prune.)
    /// </para>
    /// <para>
    /// The whole-set form reads only the set's earlier versions, through their
    /// index (<see cref="ICatalogRepository.GetActivePreviousVersionsAsync"/>):
    /// they are a small fraction of a large set's rows (21,919 of 12.3 million on
    /// one real set), and this runs after every full backup.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<FileRecord>> ComputeRetentionAsync(
        int backupSetId,
        Func<string, IReadOnlyList<VersionRetentionTier>> tierSelector,
        IReadOnlyCollection<string>? pathScope,
        Action? onIndexing = null,
        CancellationToken ct = default)
    {
        IEnumerable<FileRecord> previous;
        if (pathScope is null)
        {
            previous = await _catalog.GetActivePreviousVersionsAsync(backupSetId, onIndexing, ct);
        }
        else
        {
            var scoped = new List<FileRecord>();
            foreach (var path in pathScope.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                scoped.AddRange(await _catalog.GetFileRecordsByPathAsync(backupSetId, path, ct));
            }
            previous = scoped.Where(f => !f.IsDeleted && HistoryPaths.IsPreviousVersion(f.DiscPath));
        }

        var now = DateTime.UtcNow;
        var toDelete = new List<FileRecord>();
        foreach (var group in previous.GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var tiers = tierSelector(group.Key);
            if (tiers.Count > 0)
                toDelete.AddRange(SelectExpired(group.ToList(), tiers, now));
        }
        return toDelete;
    }

    /// <summary>
    /// Every earlier version among <paramref name="previousVersions"/> that the
    /// set's retention settings no longer keep: those its tiers have expired,
    /// and every one of a file that falls to an empty Default tier set (backed up
    /// with no history). A file a tier set excludes from backup is left alone:
    /// its copies are the business of Cleanup's exclusion category.
    /// </summary>
    /// <param name="previousVersions">
    /// Live earlier-version records (anything else is ignored), all of a file's
    /// together or not at all: the newest earlier version of a file with tiers is
    /// always kept, and which one that is depends on seeing them all.
    /// </param>
    /// <param name="options">The set's options: its tier sets, or for an old set its flat tiers.</param>
    /// <param name="now">The time ages are measured from.</param>
    public static List<FileRecord> FindVersionsNotKept(
        IEnumerable<FileRecord> previousVersions, JobOptions options, DateTime now)
    {
        Func<string, IReadOnlyList<VersionRetentionTier>> tierSelector;
        Func<string, bool> keepsNoHistory = _ => false;

        if (options.TierSets.Count > 0)
        {
            var resolver = VersionTierSet.BuildTierResolver(options.TierSets);
            tierSelector = path => resolver(path).Tiers;
            // Only for a Default that is really there and empty. The resolver
            // makes up an empty one when the list has none (old or hand-edited
            // data), and the editor shows that set with the stock tiers - its
            // files' versions must not all be offered for deletion.
            if (options.TierSets.Any(t => t.KeepsNoHistory))
                keepsNoHistory = path => resolver(path).KeepsNoHistory;
        }
        else if (options.RetentionTiers.Count > 0)
        {
            var flat = options.RetentionTiers;
            tierSelector = _ => flat;
        }
        else
        {
            return []; // No retention rules configured.
        }

        var notKept = new List<FileRecord>();
        foreach (var group in previousVersions
                     .Where(f => !f.IsDeleted && HistoryPaths.IsPreviousVersion(f.DiscPath))
                     .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var tiers = tierSelector(group.Key);
            if (tiers.Count > 0)
                notKept.AddRange(SelectExpired(group.ToList(), tiers, now));
            else if (keepsNoHistory(group.Key))
                notKept.AddRange(group); // Backed up with no history: every earlier version goes.
            // Otherwise excluded from backup: not a retention matter.
        }
        return notKept;
    }

    /// <summary>
    /// The earlier versions of one file that <paramref name="tiers"/> no longer
    /// keep. Tiers are walked youngest first, each covering the ages from the
    /// previous tier's limit up to its own; within one, its newest
    /// <see cref="VersionRetentionTier.MaxVersions"/> are kept and older ones
    /// expire. The file's newest earlier version is always kept, so the last
    /// change can always be rolled back.
    /// </summary>
    /// <param name="versions">All of one file's live earlier versions, in any order.</param>
    /// <param name="tiers">The file's tiers, in any order; empty keeps everything.</param>
    /// <param name="now">The time ages are measured from.</param>
    public static List<FileRecord> SelectExpired(
        IReadOnlyCollection<FileRecord> versions, IReadOnlyList<VersionRetentionTier> tiers, DateTime now)
    {
        var expired = new List<FileRecord>();
        if (versions.Count == 0 || tiers.Count == 0)
            return expired;

        // Ties in time (a seed dates versions by their files' write times, which
        // a tool can preserve) go to the higher version, then the later record:
        // callers pass versions in different orders, and must all keep the same.
        var newestFirst = versions
            .OrderByDescending(v => v.BackedUpUtc)
            .ThenByDescending(v => v.Version)
            .ThenByDescending(v => v.Id)
            .ToList();
        long newestId = newestFirst[0].Id;

        TimeSpan previousBoundary = TimeSpan.Zero;
        foreach (var tier in tiers.OrderBy(t => t.MaxAge ?? TimeSpan.MaxValue))
        {
            TimeSpan upperBoundary = tier.MaxAge ?? TimeSpan.MaxValue;
            if (tier.MaxVersions is int keep)
            {
                // Versions in this tier's age range, newest first.
                int index = 0;
                foreach (var v in newestFirst)
                {
                    var age = now - v.BackedUpUtc;
                    if (age < previousBoundary || age >= upperBoundary)
                        continue;
                    if (index++ >= keep && v.Id != newestId)
                        expired.Add(v);
                }
            }
            previousBoundary = upperBoundary;
        }
        return expired;
    }
}
