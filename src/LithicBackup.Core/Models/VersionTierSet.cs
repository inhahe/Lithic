namespace LithicBackup.Core.Models;

/// <summary>
/// A named collection of version retention tiers. Each backup set can define
/// multiple tier sets (e.g. "Default", "None", "Aggressive") and assign them
/// per-directory or per-file in the source selection tree.
/// </summary>
public class VersionTierSet
{
    /// <summary>
    /// Display name of this tier set (e.g. "Default", "None", "Photos").
    /// Must be unique within a backup set's tier set collection.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Retention tiers applied to files assigned to this tier set.
    /// An empty list means no version history is kept (files overwritten in place).
    /// </summary>
    public List<VersionRetentionTier> Tiers { get; set; } = [];
}
