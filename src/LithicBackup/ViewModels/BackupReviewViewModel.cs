using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace LithicBackup.ViewModels;

/// <summary>Which column the review tree is sorted by.</summary>
public enum ReviewSortColumn
{
    Name,
    Status,
    Files,
    Size,
}

/// <summary>
/// Post-scan review dialog: shows the files that will actually be backed up
/// (the incremental delta) in a tristate treeview, with per-directory/file
/// sizes reflecting only that delta.  Lets the user deselect items to shrink
/// the run — for example to fit a destination with insufficient free space —
/// and optionally remove the deselected paths from the backup set's sources.
/// </summary>
public class BackupReviewViewModel : ViewModelBase
{
    private readonly long _freeBytes;
    private readonly bool _hasFreeSpaceInfo;
    private bool _removeDeselectedFromSources;

    // Sort state — defaults to largest-first by size.
    private ReviewSortColumn _sortColumn = ReviewSortColumn.Size;
    private bool _sortDescending = true;

    /// <summary>Fired when the user confirms the (possibly filtered) backup.</summary>
    public event Action? ProceedRequested;

    /// <summary>Fired when the user cancels the backup entirely.</summary>
    public event Action? CancelRequested;

    /// <summary>True only after the user clicked "Back Up These Files".</summary>
    public bool Confirmed { get; private set; }

    public BackupReviewViewModel(
        string backupSetName,
        BackupDiff diff,
        long totalBytes,
        long freeBytes,
        bool hasFreeSpaceInfo,
        bool triggeredByLowSpace)
    {
        BackupSetName = backupSetName;
        _freeBytes = freeBytes;
        _hasFreeSpaceInfo = hasFreeSpaceInfo;
        TriggeredByLowSpace = triggeredByLowSpace;

        Roots = new ObservableCollection<BackupReviewNodeViewModel>(
            BuildTree(diff, RefreshTotals));

        // Expand the roots so the user sees structure immediately.
        foreach (var root in Roots)
            root.IsExpanded = true;

        ProceedCommand = new RelayCommand(_ =>
        {
            Confirmed = true;
            ProceedRequested?.Invoke();
        }, _ => SelectedFileCount > 0);

        CancelCommand = new RelayCommand(_ => CancelRequested?.Invoke());

        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        DeselectAllCommand = new RelayCommand(_ => SetAll(false));
        ExpandAllCommand = new RelayCommand(_ =>
        {
            foreach (var root in Roots)
                root.ExpandAll();
        });
        CollapseAllCommand = new RelayCommand(_ =>
        {
            foreach (var root in Roots)
                CollapseRecursive(root);
        });

        SortByNameCommand = new RelayCommand(_ => ToggleSort(ReviewSortColumn.Name));
        SortByStatusCommand = new RelayCommand(_ => ToggleSort(ReviewSortColumn.Status));
        SortByFilesCommand = new RelayCommand(_ => ToggleSort(ReviewSortColumn.Files));
        SortBySizeCommand = new RelayCommand(_ => ToggleSort(ReviewSortColumn.Size));

        // Apply the default sort (size, largest first).
        ApplySort();
    }

    // --- Properties ---

    public string BackupSetName { get; }

    public ObservableCollection<BackupReviewNodeViewModel> Roots { get; }

    /// <summary>True when the dialog was auto-opened because the destination
    /// lacked free space for the full delta.</summary>
    public bool TriggeredByLowSpace { get; }

    public string HeaderText => TriggeredByLowSpace
        ? $"The destination doesn't have enough free space for everything that needs " +
          $"backing up from \"{BackupSetName}\". Review and deselect items to fit, " +
          $"then continue \u2014 or cancel."
        : $"Review the files that will be backed up from \"{BackupSetName}\". " +
          $"Deselect anything you don't want in this backup.";

    /// <summary>Total selected size across the whole tree.</summary>
    public long SelectedSizeBytes
    {
        get
        {
            long total = 0;
            foreach (var root in Roots)
                total += root.SelectedSizeBytes;
            return total;
        }
    }

    public int SelectedFileCount
    {
        get
        {
            int total = 0;
            foreach (var root in Roots)
                total += root.SelectedFileCount;
            return total;
        }
    }

    public string SelectedSummaryText
        => $"{SelectedFileCount:N0} file(s) selected \u2014 {BackupReviewNodeViewModel.FormatBytes(SelectedSizeBytes)}";

    public string FreeSpaceText => _hasFreeSpaceInfo
        ? $"Destination free space: {BackupReviewNodeViewModel.FormatBytes(_freeBytes)}"
        : "";

    public bool HasFreeSpaceInfo => _hasFreeSpaceInfo;

    /// <summary>True when the current selection fits in the destination's free space.</summary>
    public bool FitsInFreeSpace => !_hasFreeSpaceInfo || SelectedSizeBytes <= _freeBytes;

    public string FitStatusText
    {
        get
        {
            if (!_hasFreeSpaceInfo)
                return "";
            if (FitsInFreeSpace)
                return $"Fits \u2014 {BackupReviewNodeViewModel.FormatBytes(_freeBytes - SelectedSizeBytes)} would remain free.";
            long over = SelectedSizeBytes - _freeBytes;
            return $"Too large by {BackupReviewNodeViewModel.FormatBytes(over)} \u2014 deselect items to fit.";
        }
    }

    public Brush FitStatusBrush => FitsInFreeSpace
        ? Brushes.SeaGreen
        : Brushes.Firebrick;

    /// <summary>
    /// When true, deselected files/directories are removed from the backup
    /// set's saved source selections (so future backups skip them too), not
    /// just skipped for this one run.
    /// </summary>
    public bool RemoveDeselectedFromSources
    {
        get => _removeDeselectedFromSources;
        set => SetProperty(ref _removeDeselectedFromSources, value);
    }

    // --- Commands ---

    public ICommand ProceedCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortByStatusCommand { get; }
    public ICommand SortByFilesCommand { get; }
    public ICommand SortBySizeCommand { get; }

    // --- Sort indicators (arrow suffixes for the active column header) ---

    public string NameSortIndicator => SortIndicator(ReviewSortColumn.Name);
    public string StatusSortIndicator => SortIndicator(ReviewSortColumn.Status);
    public string FilesSortIndicator => SortIndicator(ReviewSortColumn.Files);
    public string SizeSortIndicator => SortIndicator(ReviewSortColumn.Size);

    private string SortIndicator(ReviewSortColumn column)
        => _sortColumn != column ? string.Empty
            : _sortDescending ? " \u25BC" : " \u25B2";

    // --- Results (read after Confirmed) ---

    /// <summary>Absolute paths of every file the user left selected.</summary>
    public HashSet<string> SelectedFilePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
            CollectSelectedFiles(root, set);
        return set;
    }

    /// <summary>
    /// Absolute paths of every individual <em>file</em> the user deselected in
    /// the review. Only files actually shown in this dialog are returned — a
    /// deselected directory is expanded to the shown files beneath it rather
    /// than the directory itself.
    /// <para>
    /// This is deliberate: the review tree contains only the current backup
    /// delta (new/changed files), never a source directory's full contents. A
    /// directory node that reads as fully deselected only means "every delta
    /// file shown under it is unchecked" — it says nothing about the other,
    /// unchanged files that live in that same source directory but weren't
    /// listed here. Collapsing such a node to a "dir\*" source exclusion would
    /// silently drop those unshown files from the backup, which the user has no
    /// way to see or intend. Removing only the shown files guarantees we never
    /// exclude anything the user didn't actually look at and deselect.
    /// </para>
    /// </summary>
    public List<string> DeselectedFiles()
    {
        var result = new List<string>();
        foreach (var root in Roots)
            CollectDeselectedFiles(root, result);
        return result;
    }

    // --- Internals ---

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(SelectedSizeBytes));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedSummaryText));
        OnPropertyChanged(nameof(FitsInFreeSpace));
        OnPropertyChanged(nameof(FitStatusText));
        OnPropertyChanged(nameof(FitStatusBrush));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetAll(bool selected)
    {
        foreach (var root in Roots)
            root.IsSelected = selected;
        RefreshTotals();
    }

    private static void CollapseRecursive(BackupReviewNodeViewModel node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children)
            CollapseRecursive(child);
    }

    private static void CollectSelectedFiles(
        BackupReviewNodeViewModel node, HashSet<string> set)
    {
        if (node.IsDirectory)
        {
            if (node.IsSelected == false)
                return;
            foreach (var child in node.Children)
                CollectSelectedFiles(child, set);
        }
        else if (node.IsSelected == true)
        {
            set.Add(node.FullPath);
        }
    }

    private static void CollectDeselectedFiles(
        BackupReviewNodeViewModel node, List<string> result)
    {
        if (node.IsDirectory)
        {
            // Always descend. A directory's tri-state is derived from its
            // children, so its deselected file leaves are found below whether
            // the directory itself reads as fully or partially deselected. We
            // never record the directory itself — only the concrete files that
            // were shown here (see DeselectedFiles for why).
            foreach (var child in node.Children)
                CollectDeselectedFiles(child, result);
        }
        else if (node.IsSelected == false)
        {
            result.Add(node.FullPath);
        }
    }

    /// <summary>
    /// Build the review tree from the diff.  Directories aggregate the sizes and
    /// counts of the delta files beneath them; files are leaves carrying their
    /// own size and new/changed status.
    /// </summary>
    private static List<BackupReviewNodeViewModel> BuildTree(
        BackupDiff diff, Action onSelectionChanged)
    {
        // Flatten the delta with per-file status.
        var files = new List<(string Path, long Size, ReviewFileStatus Status)>();
        foreach (var f in diff.NewFiles)
            files.Add((f.FullPath, f.SizeBytes, ReviewFileStatus.New));
        foreach (var f in diff.ChangedFiles)
            files.Add((f.FullPath, f.SizeBytes, ReviewFileStatus.Changed));

        // Directory path -> node (created lazily as files are inserted).
        var dirNodes = new Dictionary<string, BackupReviewNodeViewModel>(
            StringComparer.OrdinalIgnoreCase);
        var roots = new List<BackupReviewNodeViewModel>();

        BackupReviewNodeViewModel GetOrCreateDir(string dirPath)
        {
            if (dirNodes.TryGetValue(dirPath, out var existing))
                return existing;

            var parentPath = Path.GetDirectoryName(dirPath);
            var name = Path.GetFileName(dirPath);
            if (string.IsNullOrEmpty(name))
                name = dirPath; // drive root like "C:\"

            if (string.IsNullOrEmpty(parentPath) || string.Equals(parentPath, dirPath, StringComparison.OrdinalIgnoreCase))
            {
                var root = new BackupReviewNodeViewModel(
                    name, dirPath, isDirectory: true, parent: null, onSelectionChanged);
                dirNodes[dirPath] = root;
                roots.Add(root);
                return root;
            }

            var parent = GetOrCreateDir(parentPath);
            var node = new BackupReviewNodeViewModel(
                name, dirPath, isDirectory: true, parent, onSelectionChanged);
            parent.Children.Add(node);
            dirNodes[dirPath] = node;
            return node;
        }

        foreach (var (path, size, status) in files
                     .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                continue; // shouldn't happen for absolute paths

            var dirNode = GetOrCreateDir(dir);
            var fileNode = new BackupReviewNodeViewModel(
                Path.GetFileName(path), path, isDirectory: false, dirNode, onSelectionChanged)
            {
                OwnSizeBytes = size,
                Status = status,
            };
            dirNode.Children.Add(fileNode);
        }

        // Ordering is applied afterwards by ApplySort() using the current
        // sort column/direction (default: size, largest first).
        return roots;
    }

    // --- Sorting ---

    private void ToggleSort(ReviewSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            // Size/files default to largest-first; name/status ascend
            // (A→Z, New→Changed→Mixed).
            _sortDescending = column is ReviewSortColumn.Size or ReviewSortColumn.Files;
        }

        ApplySort();
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(StatusSortIndicator));
        OnPropertyChanged(nameof(FilesSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
    }

    private void ApplySort()
    {
        var sortedRoots = OrderNodes(Roots).ToList();
        Roots.Clear();
        foreach (var root in sortedRoots)
        {
            Roots.Add(root);
            SortChildrenRecursive(root);
        }
    }

    private void SortChildrenRecursive(BackupReviewNodeViewModel node)
    {
        if (node.Children.Count == 0)
            return;

        var sorted = OrderNodes(node.Children).ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            SortChildrenRecursive(child);
        }
    }

    private IEnumerable<BackupReviewNodeViewModel> OrderNodes(
        IEnumerable<BackupReviewNodeViewModel> nodes)
    {
        IOrderedEnumerable<BackupReviewNodeViewModel> ordered = _sortColumn switch
        {
            ReviewSortColumn.Size => _sortDescending
                ? nodes.OrderByDescending(n => n.TotalSizeBytes)
                : nodes.OrderBy(n => n.TotalSizeBytes),
            ReviewSortColumn.Files => _sortDescending
                ? nodes.OrderByDescending(n => n.TotalFileCount)
                : nodes.OrderBy(n => n.TotalFileCount),
            ReviewSortColumn.Status => _sortDescending
                ? nodes.OrderByDescending(n => n.StatusSortKey)
                : nodes.OrderBy(n => n.StatusSortKey),
            // Name: keep directories grouped ahead of files, then alphabetical.
            _ => _sortDescending
                ? nodes.OrderByDescending(n => n.IsDirectory)
                       .ThenByDescending(n => n.Name, StringComparer.OrdinalIgnoreCase)
                : nodes.OrderByDescending(n => n.IsDirectory)
                       .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
        };

        // Stable tiebreaker so equal sizes/counts stay alphabetical.
        return ordered.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
    }
}
