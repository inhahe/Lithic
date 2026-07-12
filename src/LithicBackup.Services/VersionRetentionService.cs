using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Applies tiered version retention policies to backup sets.
/// Identifies file versions that should be marked as deleted based on
/// age-based retention tiers.
/// </summary>
public class VersionRetentionService : IVersionRetentionService
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
    /// Apply retention tiers to all files in a backup set.
    /// Returns the list of FileRecords that should be deleted.
    /// </summary>
    public async Task<IReadOnlyList<FileRecord>> ComputeRetentionAsync(
        int backupSetId,
        IReadOnlyList<VersionRetentionTier> tiers,
        CancellationToken ct = default)
    {
        return await ComputeRetentionAsync(
            backupSetId,
            _ => tiers,
            ct);
    }

    /// <summary>
    /// Apply retention tiers to all files in a backup set, using per-file
    /// tier resolution.  The <paramref name="tierSelector"/> receives a source
    /// path and returns the retention tiers to apply to that file's versions.
    /// </summary>
    public async Task<IReadOnlyList<FileRecord>> ComputeRetentionAsync(
        int backupSetId,
        Func<string, IReadOnlyList<VersionRetentionTier>> tierSelector,
        CancellationToken ct = default)
    {
        var allFiles = await _catalog.GetAllFilesForBackupSetAsync(backupSetId, ct);
        var now = DateTime.UtcNow;

        // Group by source path.
        var groupedByPath = allFiles
            .Where(f => !f.IsDeleted)
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);

        var toDelete = new List<FileRecord>();

        foreach (var group in groupedByPath)
        {
            ct.ThrowIfCancellationRequested();

            // Tier-based retention only applies to actual previous-version
            // records, which always live under "{drive}_prev/..." (see
            // DirectoryBackupService.GetPrevDiscPath).  A SourcePath with
            // only current-location rows isn't carrying historical
            // versions, so there's nothing for retention to trim — even if
            // there's more than one such row (which is a catalog anomaly
            // surfaced separately by the Cleanup view's CatalogDuplicate
            // detection, not a retention concern).
            var versions = group
                .Where(f => IsPreviousVersionPath(f.DiscPath))
                .OrderByDescending(f => f.BackedUpUtc)
                .ToList();

            if (versions.Count == 0)
                continue; // No real prev-version records to consider.

            // Resolve this file's retention tiers.
            var tiers = tierSelector(group.Key);
            if (tiers.Count == 0)
                continue; // "None" tier set — no retention rules to apply.

            // Never delete the most recent prev version (we'd lose the
            // ability to roll back at all).  The current-location record
            // is automatically safe — it's not in `versions`.
            long newestId = versions[0].Id;

            // Walk tiers from youngest to oldest.
            // Each tier defines an age range and max versions for that range.
            var sortedTiers = tiers
                .OrderBy(t => t.MaxAge ?? TimeSpan.MaxValue)
                .ToList();

            // Track which versions have been processed (kept or deleted).
            var processed = new HashSet<long>();

            TimeSpan previousBoundary = TimeSpan.Zero;

            foreach (var tier in sortedTiers)
            {
                TimeSpan upperBoundary = tier.MaxAge ?? TimeSpan.MaxValue;

                // Find versions in this tier's age range.
                var tierVersions = versions
                    .Where(v => !processed.Contains(v.Id))
                    .Where(v =>
                    {
                        var age = now - v.BackedUpUtc;
                        return age >= previousBoundary && age < upperBoundary;
                    })
                    .OrderByDescending(v => v.BackedUpUtc) // Keep newest first.
                    .ToList();

                if (tier.MaxVersions.HasValue && tierVersions.Count > tier.MaxVersions.Value)
                {
                    // Keep MaxVersions newest, mark rest for deletion.
                    int toKeep = tier.MaxVersions.Value;
                    for (int i = 0; i < tierVersions.Count; i++)
                    {
                        processed.Add(tierVersions[i].Id);
                        if (i >= toKeep && tierVersions[i].Id != newestId)
                        {
                            toDelete.Add(tierVersions[i]);
                        }
                    }
                }
                else
                {
                    // Unlimited or within limit — keep all.
                    foreach (var v in tierVersions)
                        processed.Add(v.Id);
                }

                previousBoundary = upperBoundary;
            }
        }

        return toDelete;
    }

    /// <summary>
    /// Apply retention and mark the identified files as deleted in the catalog.
    /// </summary>
    /// <remarks>
    /// WARNING: this only flips the catalog <c>IsDeleted</c> bit — it does NOT
    /// physically remove the version file from the destination.  Wiring this into
    /// the backup path as-is would recreate the "catalog-deleted (still on disk)"
    /// inconsistency (records marked deleted while their bytes linger).  It has no
    /// callers today; the live backup path uses
    /// <see cref="ComputeRetentionAsync(int, Func{string, IReadOnlyList{VersionRetentionTier}}, CancellationToken)"/>
    /// and performs the physical delete itself (see
    /// <c>DirectoryBackupService.ExecuteAsync</c>, retention section), flipping
    /// <c>IsDeleted</c> only after the file is confirmed gone.  If this method is
    /// ever revived, it must delete the physical file first and mark the record
    /// deleted only on confirmed removal.
    /// </remarks>
    public async Task ApplyRetentionAsync(
        int backupSetId,
        IReadOnlyList<VersionRetentionTier> tiers,
        CancellationToken ct = default)
    {
        var filesToDelete = await ComputeRetentionAsync(backupSetId, tiers, ct);

        foreach (var file in filesToDelete)
        {
            ct.ThrowIfCancellationRequested();
            file.IsDeleted = true;
            await _catalog.UpdateFileRecordAsync(file, ct);
        }
    }

    /// <summary>
    /// True when <paramref name="discPath"/> points at a previous version of
    /// a file (i.e. lives under a "{drive}_prev/" subtree).  See
    /// <c>DirectoryBackupService.GetPrevDiscPath</c> for the writer side.
    /// Tolerates both path separators because catalog DiscPaths normalize
    /// inconsistently in different code paths.
    /// </summary>
    private static bool IsPreviousVersionPath(string discPath)
    {
        if (string.IsNullOrEmpty(discPath)) return false;
        int sep = discPath.IndexOfAny(['\\', '/']);
        if (sep < 0) return false;
        var firstSegment = discPath.AsSpan(0, sep);
        return firstSegment.EndsWith("_prev", StringComparison.OrdinalIgnoreCase);
    }
}
