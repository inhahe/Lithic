using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>Which column a <see cref="ReviewTreeViewModel"/> is sorted by.</summary>
public enum ReviewTreeSortColumn
{
    Name,
    Files,
    Size,
}

/// <summary>
/// One node in a read-only reconcile-review tree (either the "these backed-up
/// files will be removed" purge preview or the "these files were added" backup
/// preview).  Purely informational — no checkboxes — so both previews are
/// all-or-nothing.  Rendered flat via <see cref="Depth"/> (matching the Cleanup
/// tree) so the Files and Size columns stay aligned across nesting levels.
/// </summary>
public sealed class ReviewTreeNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Count of file leaves at or below this node (directories only).</summary>
    public int FileCount { get; set; }

    /// <summary>Tree depth, used by the view to indent the name column.</summary>
    public int Depth { get; set; }

    /// <summary>Top-level nodes are expanded; everything deeper starts collapsed
    /// so a large removal/addition stays scannable.</summary>
    public bool IsExpanded { get; set; }

    public ObservableCollection<ReviewTreeNode> Children { get; } = [];

    // Sizes are shown as a raw byte count (no KB/MB/GB), matching the rest of
    // the reconcile/cleanup UI.
    public string SizeText => SizeBytes >= 0 ? $"{SizeBytes:N0}" : "";

    // Only directories carry a meaningful roll-up count; file leaves show blank.
    public string FileCountText => IsDirectory ? FileCount.ToString("N0") : "";

    /// <summary>
    /// Re-sort this node's children (recursively) by the given column and
    /// direction.  Directories always come before file leaves regardless of
    /// direction, matching the Cleanup and source-selection trees.
    /// </summary>
    internal void SortChildren(ReviewTreeSortColumn column, bool ascending)
    {
        if (Children.Count == 0)
            return;

        var sorted = Children.ToList();
        sorted.Sort((a, b) => Compare(a, b, column, ascending));

        Children.Clear();
        foreach (var c in sorted)
            Children.Add(c);

        foreach (var c in Children)
            c.SortChildren(column, ascending);
    }

    internal static int Compare(ReviewTreeNode a, ReviewTreeNode b,
        ReviewTreeSortColumn column, bool ascending)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        int cmp = column switch
        {
            ReviewTreeSortColumn.Files => a.FileCount.CompareTo(b.FileCount),
            ReviewTreeSortColumn.Size => a.SizeBytes.CompareTo(b.SizeBytes),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (cmp == 0)
            cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return ascending ? cmp : -cmp;
    }
}

/// <summary>
/// View-model backing the read-only reconcile-review dialog used after a set's
/// sources are edited: either the purge preview (files whose sources were
/// removed) or the additions preview (files newly covered).  Builds a directory
/// tree from a flat <c>(path, size)</c> list, collapsing single-child directory
/// chains so a deeply-nested folder shows as one root row, and supports
/// click-to-sort by Name, Files, or Size.
/// </summary>
public sealed class ReviewTreeViewModel : ViewModelBase
{
    private ReviewTreeSortColumn _sortColumn = ReviewTreeSortColumn.Size;
    private bool _sortAscending; // Size defaults to descending (biggest first).

    public ObservableCollection<ReviewTreeNode> RootNodes { get; }
    public string HeaderText { get; }
    public string WindowTitle { get; }
    public string ConfirmButtonText { get; }

    public ICommand SortByNameCommand { get; }
    public ICommand SortByFilesCommand { get; }
    public ICommand SortBySizeCommand { get; }

    private ReviewTreeViewModel(
        List<ReviewTreeNode> roots, string headerText,
        string windowTitle, string confirmButtonText)
    {
        RootNodes = new ObservableCollection<ReviewTreeNode>(roots);
        HeaderText = headerText;
        WindowTitle = windowTitle;
        ConfirmButtonText = confirmButtonText;

        SortByNameCommand = new RelayCommand(_ => ToggleSort(ReviewTreeSortColumn.Name));
        SortByFilesCommand = new RelayCommand(_ => ToggleSort(ReviewTreeSortColumn.Files));
        SortBySizeCommand = new RelayCommand(_ => ToggleSort(ReviewTreeSortColumn.Size));

        ApplySort();
    }

    public string NameSortIndicator =>
        _sortColumn == ReviewTreeSortColumn.Name ? (_sortAscending ? " \u25B2" : " \u25BC") : "";
    public string FilesSortIndicator =>
        _sortColumn == ReviewTreeSortColumn.Files ? (_sortAscending ? " \u25B2" : " \u25BC") : "";
    public string SizeSortIndicator =>
        _sortColumn == ReviewTreeSortColumn.Size ? (_sortAscending ? " \u25B2" : " \u25BC") : "";

    /// <summary>Clicking the active column flips direction; a new column
    /// switches to it (ascending for name, descending for files/size, since
    /// largest-first is the more useful default there).</summary>
    private void ToggleSort(ReviewTreeSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = column == ReviewTreeSortColumn.Name;
        }
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(FilesSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        ApplySort();
    }

    private void ApplySort()
    {
        var sorted = RootNodes.ToList();
        sorted.Sort((a, b) => ReviewTreeNode.Compare(a, b, _sortColumn, _sortAscending));
        RootNodes.Clear();
        foreach (var n in sorted)
            RootNodes.Add(n);
        foreach (var n in RootNodes)
            n.SortChildren(_sortColumn, _sortAscending);
    }

    // ------------------------------------------------------------------
    // Factories
    // ------------------------------------------------------------------

    /// <summary>
    /// Build the purge-preview tree from removed source files (already
    /// de-duplicated by source path, with per-path size being the sum of that
    /// file's versions).
    /// </summary>
    public static ReviewTreeViewModel ForRemoval(
        IReadOnlyList<(string SourcePath, long SizeBytes)> removedFiles)
    {
        var (roots, fileCount, totalBytes) = BuildTree(removedFiles);
        string loc = roots.Count == 1 ? "location" : "locations";
        string files = fileCount == 1 ? "file" : "files";
        string header =
            $"{fileCount:N0} {files} ({totalBytes:N0} bytes) in {roots.Count} {loc} "
            + "are no longer covered by this set's sources. "
            + "Remove their backed-up copies from the destination?";
        return new ReviewTreeViewModel(
            roots, header, "Remove Files No Longer in Sources", "Remove");
    }

    /// <summary>
    /// Build the additions-preview tree from files newly covered by the set's
    /// sources after an edit (path + on-disk size).
    /// </summary>
    public static ReviewTreeViewModel ForAdditions(
        IReadOnlyList<(string SourcePath, long SizeBytes)> addedFiles)
    {
        var (roots, fileCount, totalBytes) = BuildTree(addedFiles);
        string loc = roots.Count == 1 ? "location" : "locations";
        string files = fileCount == 1 ? "file" : "files";
        string header =
            $"{fileCount:N0} {files} ({totalBytes:N0} bytes) in {roots.Count} {loc} "
            + "were added to this set's sources. Back them up now? "
            + "(Exclusion filters still apply when the backup actually runs.)";
        return new ReviewTreeViewModel(
            roots, header, "Back Up Added Sources", "Back up");
    }

    // ------------------------------------------------------------------
    // Tree building
    // ------------------------------------------------------------------

    private static (List<ReviewTreeNode> Roots, int FileCount, long TotalBytes) BuildTree(
        IReadOnlyList<(string SourcePath, long SizeBytes)> filesIn)
    {
        var dirs = new Dictionary<string, ReviewTreeNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<ReviewTreeNode>();

        ReviewTreeNode EnsureDir(string dirPath)
        {
            if (dirs.TryGetValue(dirPath, out var existing))
                return existing;

            var node = new ReviewTreeNode { IsDirectory = true, FullPath = dirPath };
            dirs[dirPath] = node;

            var parent = Path.GetDirectoryName(dirPath.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent))
                roots.Add(node);
            else
                EnsureDir(parent).Children.Add(node);
            return node;
        }

        int fileCount = 0;
        long totalBytes = 0;
        foreach (var (path, size) in filesIn)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                continue;

            var dirNode = EnsureDir(dir);
            dirNode.Children.Add(new ReviewTreeNode
            {
                IsDirectory = false,
                FullPath = path,
                Name = Path.GetFileName(path),
                SizeBytes = size,
            });
            fileCount++;
            totalBytes += size;
        }

        var collapsed = roots.Select(CollapseChain).ToList();
        foreach (var r in collapsed)
        {
            FixNames(r, isRoot: true);
            ComputeSize(r);
            ComputeCount(r);
            AssignDepthAndExpand(r, depth: 0);
        }

        return (collapsed, fileCount, totalBytes);
    }

    /// <summary>
    /// Merge chains of directories that have a single subdirectory child and no
    /// files (e.g. <c>D:\</c> → <c>mypage</c> → <c>inhahe.com</c>) into one node
    /// so the tree roots at the meaningful folder.
    /// </summary>
    private static ReviewTreeNode CollapseChain(ReviewTreeNode node)
    {
        if (!node.IsDirectory)
            return node;

        var kids = node.Children.Select(CollapseChain).ToList();
        node.Children.Clear();
        foreach (var k in kids)
            node.Children.Add(k);

        while (node.Children.Count == 1 && node.Children[0].IsDirectory)
        {
            var only = node.Children[0];
            var merged = new ReviewTreeNode { IsDirectory = true, FullPath = only.FullPath };
            foreach (var gk in only.Children)
                merged.Children.Add(gk);
            node = merged;
        }
        return node;
    }

    private static void FixNames(ReviewTreeNode node, bool isRoot)
    {
        // Roots show their full path (e.g. "D:\mypage\inhahe.com"); nested nodes
        // show just their own segment.
        node.Name = isRoot ? node.FullPath : Path.GetFileName(node.FullPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(node.Name))
            node.Name = node.FullPath;
        foreach (var c in node.Children)
            FixNames(c, isRoot: false);
    }

    private static long ComputeSize(ReviewTreeNode node)
    {
        if (!node.IsDirectory)
            return node.SizeBytes;
        long s = 0;
        foreach (var c in node.Children)
            s += ComputeSize(c);
        node.SizeBytes = s;
        return s;
    }

    private static int ComputeCount(ReviewTreeNode node)
    {
        if (!node.IsDirectory)
            return 1;
        int c = 0;
        foreach (var child in node.Children)
            c += ComputeCount(child);
        node.FileCount = c;
        return c;
    }

    private static void AssignDepthAndExpand(ReviewTreeNode node, int depth)
    {
        node.Depth = depth;
        node.IsExpanded = depth == 0;
        foreach (var c in node.Children)
            AssignDepthAndExpand(c, depth + 1);
    }
}
