namespace LithicBackup.Core.Models;

/// <summary>
/// A named collection of version retention tiers. Each backup set can define
/// multiple tier sets (e.g. "Default", "None", "Aggressive").
///
/// Tier set assignment is determined by <see cref="FilePatterns"/> — glob
/// patterns matched against the full source file path.  Tier sets are
/// evaluated in display order; the first non-Default set whose patterns
/// match (and whose <see cref="FileExemptPatterns"/> don't exclude) wins.
/// The "Default" tier set is the implicit fallback for unmatched files.
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

    /// <summary>
    /// Glob patterns for file paths that should use this tier set.
    /// Evaluated in tier set display order; the first matching non-Default
    /// set wins.  Not meaningful for the "Default" tier set (which is the
    /// implicit fallback for all unmatched files).
    /// </summary>
    /// <remarks>
    /// Uses the same glob syntax as <see cref="GlobMatcher"/>:
    /// filename patterns (e.g. <c>*.log</c>) or path patterns
    /// (e.g. <c>*\Temp\*</c>).
    /// </remarks>
    public List<string> FilePatterns { get; set; } = [];

    /// <summary>
    /// Glob patterns for file paths that are exempt from this tier set,
    /// overriding <see cref="FilePatterns"/>.  Allows re-including specific
    /// paths that a broad pattern would otherwise capture.
    /// </summary>
    public List<string> FileExemptPatterns { get; set; } = [];

    /// <summary>
    /// Build a resolver function that maps a file path to its matching tier set.
    /// Tier sets are evaluated in <paramref name="tierSets"/> order; the first
    /// non-Default set whose <see cref="FilePatterns"/> match (and whose
    /// <see cref="FileExemptPatterns"/> don't exclude) wins.  The "Default"
    /// tier set is returned for unmatched files.
    /// </summary>
    public static Func<string, VersionTierSet> BuildTierResolver(
        IReadOnlyList<VersionTierSet> tierSets)
    {
        VersionTierSet? defaultSet = null;
        var matchers = new List<(
            VersionTierSet TierSet,
            Func<string, bool> Matches,
            Func<string, bool>? Exempt)>();

        foreach (var ts in tierSets)
        {
            if (string.Equals(ts.Name, "Default", StringComparison.OrdinalIgnoreCase))
            {
                defaultSet = ts;
                continue;
            }

            if (ts.FilePatterns.Count == 0)
                continue; // No patterns → can't match anything.

            var matchFilter = GlobMatcher.CreateFilter(ts.FilePatterns);
            if (matchFilter is null) continue;

            var exemptFilter = ts.FileExemptPatterns.Count > 0
                ? GlobMatcher.CreateFilter(ts.FileExemptPatterns)
                : null;

            matchers.Add((ts, matchFilter, exemptFilter));
        }

        defaultSet ??= new VersionTierSet { Name = "Default", Tiers = [] };

        // Fast path: no non-Default sets have patterns → everything uses Default.
        if (matchers.Count == 0)
            return _ => defaultSet;

        return path =>
        {
            foreach (var (tierSet, matches, exempt) in matchers)
            {
                if (matches(path) && !(exempt?.Invoke(path) ?? false))
                    return tierSet;
            }
            return defaultSet;
        };
    }
}
