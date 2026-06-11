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
    /// Whether this directory node was expanded in the TreeView when the
    /// selection was last saved.  Persisted so the UI can restore the same
    /// expansion state instead of expanding every ancestor of a selected node.
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>Child nodes (subdirectories and files).</summary>
    public List<SourceSelection> Children { get; set; } = [];

    /// <summary>
    /// Collect the minimal set of directory paths that cover every selected
    /// file or directory in the tree. Used to scope file-system watchers (and
    /// change-relevance checks) to the directories the user actually selected,
    /// rather than watching whole drive roots.
    /// </summary>
    /// <remarks>
    /// Walks the tree by selection state:
    /// <list type="bullet">
    ///   <item><c>IsSelected == false</c> — skip the subtree entirely.</item>
    ///   <item><c>IsSelected == true</c> on a directory — add its path and stop
    ///         descending (the whole subtree is included).</item>
    ///   <item><c>IsSelected == true</c> on a file — add its containing directory.</item>
    ///   <item><c>IsSelected == null</c> (partial) — descend into children.</item>
    /// </list>
    /// </remarks>
    public static List<string> CollectSelectedRoots(IEnumerable<SourceSelection> roots)
    {
        var result = new List<string>();
        CollectSelectedRootsRecursive(roots, result);

        // De-duplicate and drop any directory already covered by an ancestor in
        // the result (e.g. a watched file's parent that sits under a watched dir).
        var distinct = result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();

        var minimal = new List<string>();
        foreach (var path in distinct)
        {
            bool covered = minimal.Any(existing =>
            {
                var prefix = existing.TrimEnd('\\') + "\\";
                return path.Equals(existing, StringComparison.OrdinalIgnoreCase)
                       || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });
            if (!covered)
                minimal.Add(path);
        }

        return minimal;
    }

    private static void CollectSelectedRootsRecursive(
        IEnumerable<SourceSelection> nodes, List<string> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false)
                continue;

            if (node.IsSelected == true)
            {
                if (node.IsDirectory)
                {
                    result.Add(node.Path);
                }
                else
                {
                    var dir = System.IO.Path.GetDirectoryName(node.Path);
                    if (!string.IsNullOrEmpty(dir))
                        result.Add(dir);
                }

                // Fully-selected subtree — no need to descend further.
                continue;
            }

            // IsSelected == null → partially selected; descend into children.
            if (node.IsDirectory)
                CollectSelectedRootsRecursive(node.Children, result);
        }
    }

    /// <summary>
    /// Determine whether an absolute file path would be included by this
    /// selection tree, mirroring the inclusion logic of the file scanner.
    /// Used by the continuous-backup path, which is handed changed paths
    /// directly (from the USN journal) and must apply the same selection
    /// semantics the scanner would, without enumerating the whole tree.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>FileScanner.ScanNode</c>:
    /// <list type="bullet">
    ///   <item>A node with <c>IsSelected == false</c> excludes its whole subtree.</item>
    ///   <item>A file node is included unless explicitly deselected.</item>
    ///   <item>A directory follows explicit child selections; an unlisted
    ///         descendant is included only when the governing directory is fully
    ///         selected and either has no child overrides or auto-includes new
    ///         entries.</item>
    /// </list>
    /// Glob/extension exclusions are applied separately by the backup service.
    /// </remarks>
    public static bool IsPathIncluded(IEnumerable<SourceSelection> roots, string filePath)
    {
        foreach (var root in roots)
        {
            if (NodeContains(root, filePath))
                return EvaluateNode(root, filePath);
        }
        return false;
    }

    private static bool NodeContains(SourceSelection node, string filePath)
    {
        if (string.Equals(node.Path, filePath, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!node.IsDirectory)
            return false;
        var prefix = node.Path.TrimEnd('\\') + "\\";
        return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateNode(SourceSelection node, string filePath)
    {
        if (node.IsSelected == false)
            return false;

        if (!node.IsDirectory)
            return string.Equals(node.Path, filePath, StringComparison.OrdinalIgnoreCase);

        // Directory node that contains filePath — follow the governing child.
        foreach (var child in node.Children)
        {
            if (NodeContains(child, filePath))
                return EvaluateNode(child, filePath);
        }

        // No explicit child governs filePath: it's an unlisted descendant.
        // Included only when the directory is fully selected and either has no
        // child overrides (everything included) or auto-includes new entries.
        return node.IsSelected == true
               && (node.Children.Count == 0 || node.AutoIncludeNewSubdirectories);
    }
}
