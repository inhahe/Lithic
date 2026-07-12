using System.Collections.ObjectModel;
using System.IO;

namespace LithicBackup.ViewModels;

/// <summary>
/// One node in the read-only "these files will be removed" review tree shown
/// after a set's sources are edited to drop directories.  Purely informational:
/// the purge is all-or-nothing (removing only some files would leave the rest as
/// permanent orphans that Cleanup would re-flag forever), so there are no
/// editable checkboxes.
/// </summary>
public sealed class PurgePreviewNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Top-level nodes are expanded; everything deeper starts collapsed
    /// so a large removal stays scannable.</summary>
    public bool IsExpanded { get; set; }

    public ObservableCollection<PurgePreviewNode> Children { get; } = [];

    public string SizeText => SizeBytes >= 0 ? $"{SizeBytes:N0}" : "";
}

/// <summary>
/// View-model backing the removal-review dialog.  Builds a directory tree from
/// the flat list of removed source files, collapsing single-child directory
/// chains so a deeply-nested removed folder shows as one root row.
/// </summary>
public sealed class DeletedSourcesReviewViewModel
{
    public ObservableCollection<PurgePreviewNode> RootNodes { get; }
    public string HeaderText { get; }

    private DeletedSourcesReviewViewModel(
        List<PurgePreviewNode> roots, int fileCount, long totalBytes)
    {
        RootNodes = new ObservableCollection<PurgePreviewNode>(roots);
        string loc = roots.Count == 1 ? "location" : "locations";
        string files = fileCount == 1 ? "file" : "files";
        HeaderText =
            $"{fileCount:N0} {files} ({totalBytes:N0} bytes) in {roots.Count} {loc} "
            + "are no longer covered by this set's sources. "
            + "Remove their backed-up copies from the destination?";
    }

    /// <summary>
    /// Build the review tree from removed source files (already de-duplicated by
    /// source path, with per-path size being the sum of that file's versions).
    /// </summary>
    public static DeletedSourcesReviewViewModel Build(
        IReadOnlyList<(string SourcePath, long SizeBytes)> removedFiles)
    {
        var dirs = new Dictionary<string, PurgePreviewNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<PurgePreviewNode>();

        PurgePreviewNode EnsureDir(string dirPath)
        {
            if (dirs.TryGetValue(dirPath, out var existing))
                return existing;

            var node = new PurgePreviewNode { IsDirectory = true, FullPath = dirPath };
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
        foreach (var (path, size) in removedFiles)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                continue;

            var dirNode = EnsureDir(dir);
            dirNode.Children.Add(new PurgePreviewNode
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
            Finalize(r, depth: 0);
        }

        // Order roots by descending size so the biggest removal is on top.
        collapsed.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        return new DeletedSourcesReviewViewModel(collapsed, fileCount, totalBytes);
    }

    /// <summary>
    /// Merge chains of directories that have a single subdirectory child and no
    /// files (e.g. <c>D:\</c> → <c>mypage</c> → <c>inhahe.com</c>) into one node
    /// so the tree roots at the meaningful removed folder.
    /// </summary>
    private static PurgePreviewNode CollapseChain(PurgePreviewNode node)
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
            var merged = new PurgePreviewNode { IsDirectory = true, FullPath = only.FullPath };
            foreach (var gk in only.Children)
                merged.Children.Add(gk);
            node = merged;
        }
        return node;
    }

    private static void FixNames(PurgePreviewNode node, bool isRoot)
    {
        // Roots show their full path (e.g. "D:\mypage\inhahe.com"); nested nodes
        // show just their own segment.
        node.Name = isRoot ? node.FullPath : Path.GetFileName(node.FullPath.TrimEnd('\\'));
        if (string.IsNullOrEmpty(node.Name))
            node.Name = node.FullPath;
        foreach (var c in node.Children)
            FixNames(c, isRoot: false);
    }

    private static long ComputeSize(PurgePreviewNode node)
    {
        if (!node.IsDirectory)
            return node.SizeBytes;
        long s = 0;
        foreach (var c in node.Children)
            s += ComputeSize(c);
        node.SizeBytes = s;
        return s;
    }

    private static void Finalize(PurgePreviewNode node, int depth)
    {
        node.IsExpanded = depth == 0;

        var sorted = node.Children
            .OrderBy(c => c.IsDirectory ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (var c in sorted)
            node.Children.Add(c);

        foreach (var c in node.Children)
            Finalize(c, depth + 1);
    }
}
