namespace LithicBackup.Core.Models;

/// <summary>
/// One tier of the version retention policy. Files older than <see cref="MaxAge"/>
/// are trimmed down to at most <see cref="MaxVersions"/> versions.
/// </summary>
/// <remarks>
/// Example configuration:
///   Tier 1: MaxAge = 10 days,  MaxVersions = unlimited (keep all)
///   Tier 2: MaxAge = 365 days, MaxVersions = 3
///   Tier 3: MaxAge = null,     MaxVersions = 1  (older than 1 year: keep only 1)
/// </remarks>
public class VersionRetentionTier
{
    /// <summary>
    /// Maximum age for this tier. Files newer than this are governed by a younger tier.
    /// Null means "everything older than the previous tier."
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// Maximum number of versions to keep for files in this age range.
    /// Null means unlimited (keep all versions).
    /// </summary>
    public int? MaxVersions { get; set; }
}
