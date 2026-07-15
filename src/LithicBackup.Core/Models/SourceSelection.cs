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
    ///         descendant is included per <see cref="IncludesUnlistedDescendants"/>
    ///         (auto-include-new applies to partially-selected directories too).</item>
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
        return IncludesUnlistedDescendants(node);
    }

    /// <summary>
    /// Whether a directory node backs up descendants that aren't explicitly listed
    /// among its children (newly-created folders/files, or entries the user never
    /// individually decided on). The single source of truth shared by the scanner
    /// (<c>FileScanner.ScanNode</c>), the continuous-backup predicate
    /// (<see cref="IsPathIncluded"/>), and orphan detection, so all three agree on
    /// what a partially-selected auto-include directory covers.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>Excluded (<c>IsSelected == false</c>) or a file node — never.</item>
    ///   <item>Fully selected with no child overrides — always (the whole subtree
    ///         is in, regardless of the auto-include flag).</item>
    ///   <item>Otherwise (fully selected with some overrides, or partially selected)
    ///         — only when <see cref="AutoIncludeNewSubdirectories"/> is on. This is
    ///         what makes "back up D:\ except these few folders, and auto-include
    ///         anything new" work on a partially-selected root.</item>
    /// </list>
    /// </remarks>
    public static bool IncludesUnlistedDescendants(SourceSelection node)
    {
        if (node.IsSelected == false || !node.IsDirectory)
            return false;

        if (node.IsSelected == true && node.Children.Count == 0)
            return true;

        return node.AutoIncludeNewSubdirectories;
    }

    /// <summary>
    /// Promote a directory that is currently included <em>only</em> via a
    /// parent's auto-include-new rule into an explicit, checked selection entry —
    /// exactly as if the user had ticked it themselves in the editor. Used by the
    /// continuous-backup worker when it discovers a newly-created folder under a
    /// partially-selected, auto-include-on parent, so the folder's membership
    /// becomes <em>persisted</em> and survives the user later turning auto-include
    /// off (a live-rule-only folder would silently drop out of scope at that point).
    /// </summary>
    /// <remarks>
    /// No-ops (returns <c>false</c>, leaving the tree untouched) when the directory
    /// is not covered by the tree at all, is already an explicit entry, or is
    /// already permanently covered by a fully-selected ancestor (a fully-selected
    /// childless directory means "the whole subtree is in" regardless of the
    /// auto-include flag, so nothing needs pinning). It only mutates the tree when
    /// the directory is a genuine unlisted auto-include descendant.
    ///
    /// Intermediate directories between the governing ancestor and the target are
    /// materialised as partial (<c>IsSelected == null</c>) nodes so their own
    /// unlisted descendants keep the same coverage they had via the rule; the
    /// target itself becomes <c>IsSelected == true</c>. Every created node inherits
    /// the governing ancestor's <see cref="AutoIncludeNewSubdirectories"/> value, so
    /// the freshly-pinned subtree behaves identically to the rule it replaces.
    /// </remarks>
    /// <param name="roots">The set's top-level selection nodes (mutated in place).</param>
    /// <param name="directoryPath">Absolute path of the directory to pin.</param>
    /// <returns><c>true</c> if the tree was modified and should be persisted.</returns>
    public static bool MaterializeDirectory(IReadOnlyList<SourceSelection> roots, string directoryPath)
    {
        foreach (var root in roots)
        {
            if (NodeContains(root, directoryPath))
                return MaterializeInNode(root, directoryPath);
        }
        return false;
    }

    private static bool MaterializeInNode(SourceSelection node, string directoryPath)
    {
        // The target is already an explicit node in the tree — nothing to add.
        if (string.Equals(node.Path, directoryPath, StringComparison.OrdinalIgnoreCase))
            return false;

        // Excluded subtree, or a file node that happens to prefix-match: not a
        // covered directory, so there is nothing to pin.
        if (node.IsSelected == false || !node.IsDirectory)
            return false;

        // Descend through the explicit child that governs the target, if any.
        foreach (var child in node.Children)
        {
            if (NodeContains(child, directoryPath))
                return MaterializeInNode(child, directoryPath);
        }

        // No explicit child governs the target: it is an unlisted descendant of
        // this node. If the whole subtree is already permanently included (a
        // fully-selected childless directory), or this node does not actually
        // cover unlisted descendants, there is nothing to pin.
        if (node.IsSelected == true && node.Children.Count == 0)
            return false;
        if (!node.AutoIncludeNewSubdirectories)
            return false;

        AddDescendantChain(node, directoryPath);
        return true;
    }

    /// <summary>
    /// Build the chain of explicit directory nodes from <paramref name="ancestor"/>
    /// down to <paramref name="directoryPath"/>, creating each missing intermediate
    /// directory as a partial node and the final target as a selected node. The
    /// caller guarantees no existing child of <paramref name="ancestor"/> governs
    /// the target, so the chain is built from scratch.
    /// </summary>
    private static void AddDescendantChain(SourceSelection ancestor, string directoryPath)
    {
        bool autoInclude = ancestor.AutoIncludeNewSubdirectories;

        var ancestorPath = ancestor.Path.TrimEnd('\\');
        var rest = directoryPath.Length > ancestorPath.Length
            ? directoryPath.Substring(ancestorPath.Length).Trim('\\')
            : string.Empty;
        var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return;

        var current = ancestor;
        var accum = ancestorPath;
        for (int i = 0; i < segments.Length; i++)
        {
            accum = accum + "\\" + segments[i];
            bool isTarget = i == segments.Length - 1;

            var existing = current.Children.FirstOrDefault(c =>
                string.Equals(c.Path, accum, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new SourceSelection
                {
                    Path = accum,
                    IsDirectory = true,
                    // Intermediates stay partial (only the child on the path is
                    // decided); the target itself becomes an explicit inclusion.
                    IsSelected = isTarget ? true : null,
                    AutoIncludeNewSubdirectories = autoInclude,
                    Children = [],
                };
                current.Children.Add(existing);
            }
            else if (isTarget)
            {
                existing.IsSelected = true;
            }

            current = existing;
        }
    }
}
