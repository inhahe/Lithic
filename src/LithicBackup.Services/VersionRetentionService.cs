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

            // Sort versions by BackedUpUtc descending (newest first).
            var versions = group
                .OrderByDescending(f => f.BackedUpUtc)
                .ToList();

            if (versions.Count <= 1)
                continue; // Only one version, nothing to trim.

            // Never delete the most recent version of any file.
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
}
