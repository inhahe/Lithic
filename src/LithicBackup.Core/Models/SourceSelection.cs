namespace LithicBackup.Core.Models;

/// <summary>
/// Represents a node in the source directory tree with tristate selection.
/// </summary>
public class SourceSelection
{
    /// <summary>Full path to this file or directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Whether this is a directory (true) or file (false).</summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Selection state: true = included, false = excluded, null = partially included
    /// (some children included, some not).
    /// </summary>
    public bool? IsSelected { get; set; }

    /// <summary>
    /// For directories: whether new subdirectories added in the future should be
    /// automatically included in backups.
    /// </summary>
    public bool AutoIncludeNewSubdirectories { get; set; } = true;

    /// <summary>
    /// Name of the version tier set assigned to this node, or null to inherit
    /// from the parent node. The tier set name references a
    /// <see cref="VersionTierSet"/> defined in <see cref="JobOptions.TierSets"/>.
    /// </summary>
    /// <remarks>
    /// "None" is a reserved name meaning no version history is kept.
    /// "Default" is the built-in tier set with standard retention rules.
    /// Null means inherit from the nearest ancestor with an explicit assignment.
    /// </remarks>
    public string? VersionTierSetName { get; set; }

    /// <summary>
    /// Backward-compatibility shim for old serialised data that stored a boolean
    /// <c>KeepVersionHistory</c> flag. During JSON deserialization the setter
    /// converts <c>false</c> → <c>VersionTierSetName = "None"</c>.
    /// New code should use <see cref="VersionTierSetName"/> instead.
    /// </summary>
    [Obsolete("Use VersionTierSetName instead. Retained for JSON backward compat.")]
    public bool KeepVersionHistory
    {
        get => VersionTierSetName != "None";
        set
        {
            // Only act when deserializing old data (VersionTierSetName not yet set).
            if (!value && VersionTierSetName is null)
                VersionTierSetName = "None";
        }
    }

    /// <summary>
    /// Glob patterns to exclude within this directory's subtree.
    /// Uses the same syntax as <see cref="GlobMatcher"/>: filename patterns
    /// (e.g. <c>*.log</c>) or path patterns (e.g. <c>*/bin/*</c>).
    /// Patterns are inherited by child directories.
    /// </summary>
    public List<string> ExcludedPatterns { get; set; } = [];

    /// <summary>
    /// Glob patterns to re-include within this directory's subtree,
    /// overriding exclusions inherited from parent directories.
    /// </summary>
    public List<string> IncludedPatterns { get; set; } = [];

    /// <summary>Child nodes (subdirectories and files).</summary>
    public List<SourceSelection> Children { get; set; } = [];

    /// <summary>
    /// Collect all paths (files and directories) where version history is disabled.
    /// Used by the backup service to skip the move-to-prev step.
    /// </summary>
    /// <returns>
    /// <list type="bullet">
    ///   <item><c>Files</c> — explicit file paths with versioning disabled.</item>
    ///   <item><c>DirectoryPrefixes</c> — directory path prefixes (ending in \) where
    ///         all contained files skip versioning.</item>
    ///   <item><c>NoVersionGlobs</c> — glob patterns from include/re-include rules
    ///         marked with <c>~nv:</c> (no version history for matching files).</item>
    /// </list>
    /// </returns>
    public static (HashSet<string> Files, List<string> DirectoryPrefixes, List<string> NoVersionGlobs) CollectNoVersionPaths(
        IEnumerable<SourceSelection> roots)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirPrefixes = new List<string>();
        var noVersionGlobs = new List<string>();
        CollectNoVersionPathsRecursive(roots, files, dirPrefixes, noVersionGlobs, parentTierSetName: "Default");
        return (files, dirPrefixes, noVersionGlobs);
    }

    private static void CollectNoVersionPathsRecursive(
        IEnumerable<SourceSelection> nodes,
        HashSet<string> files,
        List<string> dirPrefixes,
        List<string> noVersionGlobs,
        string parentTierSetName)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false)
                continue;

            // Resolve this node's effective tier set name.
            string effectiveTier = node.VersionTierSetName ?? parentTierSetName;
            bool keepsVersions = !string.Equals(effectiveTier, "None", StringComparison.OrdinalIgnoreCase);

            // Collect include/re-include patterns marked with ~nv: prefix.
            foreach (var pattern in node.IncludedPatterns)
            {
                if (pattern.StartsWith("~nv:"))
                    noVersionGlobs.Add(pattern[4..]);
            }

            if (!keepsVersions)
            {
                if (node.IsDirectory)
                {
                    // All files under this directory skip versioning.
                    string prefix = node.Path.TrimEnd('\\') + "\\";
                    dirPrefixes.Add(prefix);
                }
                else
                {
                    files.Add(node.Path);
                }
            }
            else if (node.IsDirectory)
            {
                // Only recurse into children if this directory keeps versions —
                // children under a no-version directory are already covered.
                CollectNoVersionPathsRecursive(node.Children, files, dirPrefixes, noVersionGlobs, effectiveTier);
            }
        }
    }
}
