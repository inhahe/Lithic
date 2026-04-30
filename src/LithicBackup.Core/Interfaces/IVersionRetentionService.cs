using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Enforces tiered version retention policies on backup sets.
/// Marks old file versions as deleted based on age-based retention tiers.
/// </summary>
public interface IVersionRetentionService
{
    /// <summary>
    /// Apply retention tiers to all files in a backup set, marking excess
    /// versions as deleted in the catalog.
    /// </summary>
    Task ApplyRetentionAsync(int backupSetId, IReadOnlyList<VersionRetentionTier> tiers, CancellationToken ct = default);
}
