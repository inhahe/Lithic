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
    /// Whether to keep previous versions of files when they change.
    /// When false, changed files are overwritten in place without preserving history.
    /// Useful for logs and other files where old versions aren't needed.
    /// </summary>
    public bool KeepVersionHistory { get; set; } = true;

    /// <summary>Child nodes (subdirectories and files).</summary>
    public List<SourceSelection> Children { get; set; } = [];

    /// <summary>
    /// Collect all paths (files and directories) where version history is disabled.
    /// Used by the backup service to skip the move-to-prev step.
    /// </summary>
    public static (HashSet<string> Files, List<string> DirectoryPrefixes) CollectNoVersionPaths(
        IEnumerable<SourceSelection> roots)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirPrefixes = new List<string>();
        CollectNoVersionPathsRecursive(roots, files, dirPrefixes);
        return (files, dirPrefixes);
    }

    private static void CollectNoVersionPathsRecursive(
        IEnumerable<SourceSelection> nodes,
        HashSet<string> files,
        List<string> dirPrefixes)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false)
                continue;

            if (!node.KeepVersionHistory)
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
                CollectNoVersionPathsRecursive(node.Children, files, dirPrefixes);
            }
        }
    }
}
