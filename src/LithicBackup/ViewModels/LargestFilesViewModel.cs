using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Views;

namespace LithicBackup.ViewModels;

/// <summary>
/// Scans a backup set's sources and presents files or directories,
/// sortable by size, name, or directory. Includes a directory tree view
/// showing directories sorted by total size with nested subdirectories.
/// Supports toggling file/directory inclusion (adds/removes exclusion patterns).
/// </summary>
public class LargestFilesViewModel : ViewModelBase
{
    private readonly IFileScanner _scanner;
    private readonly ICatalogRepository _catalog;
    private readonly BackupSet _backupSet;

    private bool _isLoading = true;
    private bool _isProgressIndeterminate = true;
    private double _scanPercent;
    private string _scanProgressText = "Preparing scan...";
    private string _summaryText = "";
    private int _totalFiles;
    private string _totalSizeText = "";
    private bool _isDirectoryMode;
    private List<DirectoryItem>? _directories;

    // Start with empty column so first ApplySort("Size") sets descending (largest first).
    private string _sortColumn = "";
    private bool _sortAscending;

    private CancellationTokenSource? _cts;
    private int _estimatedTotal;

    /// <summary>
    /// Suppresses inclusion toggle callbacks during propagation to children.
    /// </summary>
    private bool _suppressInclusionCallback;

    /// <summary>Fired when the user clicks "Close".</summary>
    public event Action? DoneRequested;

    /// <summary>Fired when the user clicks "Save".</summary>
    public event Func<Task>? SaveRequested;

    private string _saveStatusText = "";

    /// <summary>Cancel any running scan (e.g. when the host window closes).</summary>
    public void CancelScan() => _cts?.Cancel();

    public LargestFilesViewModel(
        IFileScanner scanner,
        ICatalogRepository catalog,
        BackupSet backupSet,
        int estimatedFileCount = 0)
    {
        _scanner = scanner;
        _catalog = catalog;
        _backupSet = backupSet;
        _backupSet.JobOptions ??= new JobOptions();

        _filesCollection = [];
        _filesView = CollectionViewSource.GetDefaultView(_filesCollection);

        CloseCommand = new RelayCommand(_ =>
        {
            _cts?.Cancel();
            DoneRequested?.Invoke();
        });

        SaveCommand = new RelayCommand(_ => OnSave());

        SortByNameCommand = new RelayCommand(_ => ApplySort("Name"));
        SortByDirectoryCommand = new RelayCommand(_ => ApplySort("Directory"));
        SortBySizeCommand = new RelayCommand(_ => ApplySort("Size"));
        ShowFilesCommand = new RelayCommand(_ => IsDirectoryMode = false);
        ShowDirectoriesCommand = new RelayCommand(_ => IsDirectoryMode = true);

        // Apply default sort (largest first).
        ApplySort("Size");

        _cts = new CancellationTokenSource();

        if (estimatedFileCount > 0)
        {
            _isProgressIndeterminate = false;
            _estimatedTotal = estimatedFileCount;
        }

        _ = LoadAsync(_cts.Token);
    }

    // --- Properties ---

    public string BackupSetName => _backupSet.Name;

    public string ViewTitle => _isDirectoryMode
        ? "Largest Source Directories"
        : "Largest Source Files";

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public double ScanPercent
    {
        get => _scanPercent;
        private set => SetProperty(ref _scanPercent, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public int TotalFiles
    {
        get => _totalFiles;
        private set => SetProperty(ref _totalFiles, value);
    }

    public string TotalSizeText
    {
        get => _totalSizeText;
        private set => SetProperty(ref _totalSizeText, value);
    }

    public bool IsDirectoryMode
    {
        get => _isDirectoryMode;
        set
        {
            if (SetProperty(ref _isDirectoryMode, value))
                OnPropertyChanged(nameof(ViewTitle));
        }
    }

    public List<DirectoryItem>? Directories
    {
        get => _directories;
        private set => SetProperty(ref _directories, value);
    }

    /// <summary>Sort indicator for the Name column header.</summary>
    public string NameSortIndicator => _sortColumn == "Name"
        ? (_sortAscending ? " \u25B2" : " \u25BC") : "";

    /// <summary>Sort indicator for the Directory column header.</summary>
    public string DirectorySortIndicator => _sortColumn == "Directory"
        ? (_sortAscending ? " \u25B2" : " \u25BC") : "";

    /// <summary>Sort indicator for the Size column header.</summary>
    public string SizeSortIndicator => _sortColumn == "Size"
        ? (_sortAscending ? " \u25B2" : " \u25BC") : "";

    /// <summary>Sort indicator for the directory tree "Directory" header.
    /// Shows when sort is by Name or Directory (both map to directory name in tree).</summary>
    public string DirTreeNameSortIndicator => _sortColumn is "Name" or "Directory"
        ? (_sortAscending ? " \u25B2" : " \u25BC") : "";

    private ObservableCollection<LargestFileItem> _filesCollection = [];
    public ObservableCollection<LargestFileItem> Files
    {
        get => _filesCollection;
        private set => SetProperty(ref _filesCollection, value);
    }

    private ICollectionView _filesView = null!;
    public ICollectionView FilesView
    {
        get => _filesView;
        private set => SetProperty(ref _filesView, value);
    }

    /// <summary>Transient confirmation text shown after a save.</summary>
    public string SaveStatusText
    {
        get => _saveStatusText;
        set => SetProperty(ref _saveStatusText, value);
    }

    // --- Commands ---

    public ICommand CloseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortByDirectoryCommand { get; }
    public ICommand SortBySizeCommand { get; }
    public ICommand ShowFilesCommand { get; }
    public ICommand ShowDirectoriesCommand { get; }

    private async void OnSave()
    {
        if (SaveRequested is not null)
            await SaveRequested.Invoke();

        if (!string.IsNullOrEmpty(SaveStatusText))
        {
            await Task.Delay(3000);
            SaveStatusText = "";
        }
    }

    // --- Sort ---

    /// <summary>Toggle sort direction if same column, or set new column with default direction.</summary>
    private void ApplySort(string column)
    {
        bool ascending;
        if (_sortColumn == column)
            ascending = !_sortAscending;
        else
            ascending = column != "Size"; // name/directory ascending, size descending

        SetSort(column, ascending);
    }

    /// <summary>Set sort column and direction without toggling.</summary>
    private void SetSort(string column, bool ascending)
    {
        _sortColumn = column;
        _sortAscending = ascending;

        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(DirectorySortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(DirTreeNameSortIndicator));

        // Sort flat file list.
        FilesView.SortDescriptions.Clear();
        var direction = ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        var prop = column switch
        {
            "Name" => "FileName",
            "Directory" => "Directory",
            _ => "SizeBytes",
        };
        FilesView.SortDescriptions.Add(new SortDescription(prop, direction));

        // Sort directory tree recursively.
        SortDirectoryTree();
    }

    /// <summary>
    /// Recursively sort the directory tree according to the current sort column
    /// and direction.  Preserves expansion state (IsExpanded lives on the
    /// DirectoryItem data objects, not on the UI containers).
    /// </summary>
    private void SortDirectoryTree()
    {
        if (_directories is null) return;

        Comparison<DirectoryItem> comparison = _sortColumn switch
        {
            "Name" or "Directory" => _sortAscending
                ? (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                : (a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase),
            _ => _sortAscending
                ? (a, b) => a.SizeBytes.CompareTo(b.SizeBytes)
                : (a, b) => b.SizeBytes.CompareTo(a.SizeBytes),
        };

        foreach (var root in _directories)
            SortDirectoryChildrenRecursive(root.Children, comparison);

        // Force the TreeView to re-bind.  IsExpanded is preserved because it
        // lives on the DirectoryItem objects, not on the WPF containers.
        var dirs = _directories;
        Directories = null;
        Directories = dirs;
    }

    private static void SortDirectoryChildrenRecursive(
        List<DirectoryItem> items, Comparison<DirectoryItem> comparison)
    {
        items.Sort(comparison);
        foreach (var item in items)
        {
            if (item.Children.Count > 0)
                SortDirectoryChildrenRecursive(item.Children, comparison);
        }
    }

    // --- Inclusion toggle ---

    private async void OnFileInclusionToggled(LargestFileItem item)
    {
        if (_suppressInclusionCallback) return;

        var exclusions = _backupSet.JobOptions!.ExcludedExtensions;
        if (item.IsIncluded)
            exclusions.Remove(item.FullPath);
        else if (!exclusions.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase))
            exclusions.Add(item.FullPath);

        try { await _catalog.UpdateBackupSetAsync(_backupSet); } catch { }
    }

    private async void OnDirectoryInclusionToggled(DirectoryItem item)
    {
        if (_suppressInclusionCallback) return;

        // Use glob pattern to exclude everything under this directory.
        // GlobMatcher translates * to .* which matches path separators,
        // so dir\* covers all descendants recursively.
        var pattern = item.FullPath.TrimEnd('\\') + @"\*";
        var exclusions = _backupSet.JobOptions!.ExcludedExtensions;

        if (item.IsIncluded)
            exclusions.Remove(pattern);
        else if (!exclusions.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            exclusions.Add(pattern);

        // Propagate visual state to descendants without triggering their callbacks.
        _suppressInclusionCallback = true;
        PropagateInclusion(item.Children, item.IsIncluded);
        _suppressInclusionCallback = false;

        try { await _catalog.UpdateBackupSetAsync(_backupSet); } catch { }
    }

    private static void PropagateInclusion(List<DirectoryItem> children, bool included)
    {
        foreach (var child in children)
        {
            child.IsIncluded = included;
            PropagateInclusion(child.Children, included);
        }
    }

    // --- Logic ---

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            // Build source selections and exclusion filter on a background
            // thread so deep selection trees don't stall the UI.
            var (sources, isExcluded) = await Task.Run(() =>
            {
                List<SourceSelection> src;
                if (_backupSet.SourceSelections is { Count: > 0 })
                    src = _backupSet.SourceSelections;
                else
                    src = _backupSet.SourceRoots
                        .Select(root => new SourceSelection
                        {
                            Path = root,
                            IsDirectory = true,
                            IsSelected = true,
                            AutoIncludeNewSubdirectories = true,
                        })
                        .ToList();

                var filter = (_backupSet.JobOptions?.ExcludedExtensions is { Count: > 0 } excl)
                    ? GlobMatcher.CreateFilter(excl)
                    : null;
                return (src, filter);
            });

            // When we have a catalog estimate, switch to a determinate bar.
            if (_estimatedTotal > 0)
                IsProgressIndeterminate = false;

            // Scan source files on background thread.
            // Scanner writes to a lightweight holder; UI polls every second.
            var scanProgress = new LatestProgress<ScanProgress>();

            ScanProgressText = "Scanning source directories...";
            var scanTask = Task.Run(
                () => _scanner.ScanAsync(sources, scanProgress, ct, isExcluded));

            while (!scanTask.IsCompleted && !ct.IsCancellationRequested)
            {
                var p = scanProgress.Latest;
                if (p is not null)
                {
                    ScanProgressText = $"Scanning... {p.FilesFound:N0} files ({FormatBytes(p.TotalBytes)})\n{p.CurrentDirectory}";
                    if (_estimatedTotal > 0)
                        ScanPercent = Math.Min(99, (double)p.FilesFound / _estimatedTotal * 100);
                }
                await Task.WhenAny(scanTask, Task.Delay(1000));
            }

            var scannedFiles = await scanTask;

            if (ct.IsCancellationRequested) return;

            ScanPercent = 100;
            ScanProgressText = $"Checking backup status for {scannedFiles.Count:N0} files...";

            // All heavy computation on a single background thread: diff,
            // hash sets, sorting, item creation, and directory tree.
            // Keeps the UI thread free (avoids multi-second hangs).
            const int maxDisplay = 10_000;
            var result = await Task.Run(async () =>
            {
                HashSet<string> newPaths, changedPaths;
                try
                {
                    var diff = await _scanner.ComputeDiffAsync(scannedFiles, _backupSet.Id, ct);
                    newPaths = new HashSet<string>(
                        diff.NewFiles.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
                    changedPaths = new HashSet<string>(
                        diff.ChangedFiles.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    newPaths = [];
                    changedPaths = [];
                }

                if (ct.IsCancellationRequested)
                    return (totalBytes: 0L, fileItems: new List<LargestFileItem>(), dirTree: new List<DirectoryItem>());

                long totalBytes = 0;
                foreach (var f in scannedFiles)
                    totalBytes += f.SizeBytes;

                var fileItems = scannedFiles
                    .OrderByDescending(f => f.SizeBytes)
                    .Take(maxDisplay)
                    .Select(f =>
                    {
                        var status = newPaths.Contains(f.FullPath)
                            ? BackupStatus.NotBackedUp
                            : changedPaths.Contains(f.FullPath)
                                ? BackupStatus.Changed
                                : (newPaths.Count > 0 || changedPaths.Count > 0)
                                    ? BackupStatus.BackedUp
                                    : BackupStatus.Unknown;

                        return new LargestFileItem
                        {
                            FullPath = f.FullPath,
                            FileName = Path.GetFileName(f.FullPath),
                            Directory = Path.GetDirectoryName(f.FullPath) ?? "",
                            SizeBytes = f.SizeBytes,
                            BackupStatus = status,
                        };
                    })
                    .ToList();

                var dirTree = BuildDirectoryTree(scannedFiles, newPaths, changedPaths);

                return (totalBytes, fileItems, dirTree);
            });

            if (ct.IsCancellationRequested) return;

            TotalFiles = scannedFiles.Count;
            TotalSizeText = FormatBytes(result.totalBytes);
            Directories = result.dirTree;

            // Wire up inclusion toggle callbacks.
            foreach (var item in result.fileItems)
                item.InclusionToggled = OnFileInclusionToggled;
            WireDirectoryCallbacks(result.dirTree);

            // Replace the collection wholesale so the UI sees a single
            // Reset notification instead of thousands of individual Add events
            // (which would freeze the UI thread for seconds on large scans).
            Files = new ObservableCollection<LargestFileItem>(result.fileItems);
            FilesView = CollectionViewSource.GetDefaultView(Files);
            SetSort(_sortColumn, _sortAscending);

            SummaryText = scannedFiles.Count <= maxDisplay
                ? $"{scannedFiles.Count:N0} files, {FormatBytes(result.totalBytes)} total"
                : $"Showing largest {maxDisplay:N0} of {scannedFiles.Count:N0} files ({FormatBytes(result.totalBytes)} total)";
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void WireDirectoryCallbacks(List<DirectoryItem>? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            item.InclusionToggled = OnDirectoryInclusionToggled;
            WireDirectoryCallbacks(item.Children);
        }
    }

    // --- Directory tree ---

    private static List<DirectoryItem> BuildDirectoryTree(
        IReadOnlyList<ScannedFile> files,
        HashSet<string> newPaths,
        HashSet<string> changedPaths)
    {
        bool hasDiffData = newPaths.Count > 0 || changedPaths.Count > 0;

        // Step 1: Accumulate direct file sizes and status counts per directory.
        var directSizes = new Dictionary<string, (long size, int count)>(
            StringComparer.OrdinalIgnoreCase);
        var dirStatus = new Dictionary<string, (int backedUp, int changed, int notBackedUp)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.FullPath) ?? "";

            // Accumulate size/count.
            if (directSizes.TryGetValue(dir, out var existing))
                directSizes[dir] = (existing.size + file.SizeBytes, existing.count + 1);
            else
                directSizes[dir] = (file.SizeBytes, 1);

            // Accumulate status counts.
            if (hasDiffData)
            {
                dirStatus.TryGetValue(dir, out var counts);
                if (newPaths.Contains(file.FullPath))
                    counts.notBackedUp++;
                else if (changedPaths.Contains(file.FullPath))
                    counts.changed++;
                else
                    counts.backedUp++;
                dirStatus[dir] = counts;
            }
        }

        // Step 2: Ensure all ancestor directories exist (for directories that
        // contain only subdirectories and no direct files).
        var allDirs = new HashSet<string>(
            directSizes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in directSizes.Keys.ToList())
        {
            var current = leaf;
            var parent = Path.GetDirectoryName(current);
            while (parent != null && parent != current && allDirs.Add(parent))
            {
                directSizes[parent] = (0, 0);
                current = parent;
                parent = Path.GetDirectoryName(current);
            }
        }

        // Step 3: Build parent → children map and identify roots.
        var childrenMap = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        foreach (var dir in allDirs)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent != null && parent != dir && allDirs.Contains(parent))
            {
                if (!childrenMap.TryGetValue(parent, out var children))
                {
                    children = [];
                    childrenMap[parent] = children;
                }
                children.Add(dir);
            }
            else
            {
                roots.Add(dir);
            }
        }

        // Step 4: Build tree bottom-up, accumulating sizes and status from children.
        DirectoryItem BuildNode(string path, bool isRoot, int depth)
        {
            directSizes.TryGetValue(path, out var stats);
            long totalSize = stats.size;
            int totalCount = stats.count;

            // Aggregate backup status from direct files.
            dirStatus.TryGetValue(path, out var directCounts);
            bool anyBackedUp = directCounts.backedUp > 0;
            bool anyChanged = directCounts.changed > 0;
            bool anyNotBackedUp = directCounts.notBackedUp > 0;

            List<DirectoryItem> childNodes = [];
            if (childrenMap.TryGetValue(path, out var childPaths))
            {
                foreach (var childPath in childPaths)
                {
                    var child = BuildNode(childPath, false, depth + 1);
                    totalSize += child.SizeBytes;
                    totalCount += child.FileCount;
                    childNodes.Add(child);

                    // Aggregate status from child directories.
                    switch (child.BackupStatus)
                    {
                        case BackupStatus.BackedUp: anyBackedUp = true; break;
                        case BackupStatus.NotBackedUp: anyNotBackedUp = true; break;
                        case BackupStatus.Changed: anyChanged = true; break;
                        case BackupStatus.Partial:
                            anyBackedUp = true;
                            anyNotBackedUp = true;
                            break;
                    }
                }
                childNodes.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            }

            // Compute aggregate status (same logic as SourceSelectionNodeViewModel).
            BackupStatus status;
            if (!hasDiffData)
                status = BackupStatus.Unknown;
            else if (anyChanged)
                status = BackupStatus.Changed;
            else if (anyBackedUp && anyNotBackedUp)
                status = BackupStatus.Partial;
            else if (anyBackedUp)
                status = BackupStatus.BackedUp;
            else if (anyNotBackedUp)
                status = BackupStatus.NotBackedUp;
            else
                status = BackupStatus.Unknown;

            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path; // drive root like "D:\"

            return new DirectoryItem
            {
                Name = isRoot ? path : name,
                FullPath = path,
                SizeBytes = totalSize,
                FileCount = totalCount,
                Depth = depth,
                Children = childNodes,
                IsExpanded = isRoot,
                BackupStatus = status,
            };
        }

        var driveRoots = roots
            .Select(r => BuildNode(r, true, 1))
            .OrderByDescending(r => r.SizeBytes)
            .ToList();

        // Wrap all drives under a single "All Drives" root.
        long allSize = 0;
        int allCount = 0;
        bool anyBU = false, anyCH = false, anyNBU = false;
        foreach (var dr in driveRoots)
        {
            allSize += dr.SizeBytes;
            allCount += dr.FileCount;
            switch (dr.BackupStatus)
            {
                case BackupStatus.BackedUp: anyBU = true; break;
                case BackupStatus.Changed: anyCH = true; break;
                case BackupStatus.NotBackedUp: anyNBU = true; break;
                case BackupStatus.Partial: anyBU = true; anyNBU = true; break;
            }
        }

        BackupStatus allStatus;
        if (!hasDiffData) allStatus = BackupStatus.Unknown;
        else if (anyCH) allStatus = BackupStatus.Changed;
        else if (anyBU && anyNBU) allStatus = BackupStatus.Partial;
        else if (anyBU) allStatus = BackupStatus.BackedUp;
        else if (anyNBU) allStatus = BackupStatus.NotBackedUp;
        else allStatus = BackupStatus.Unknown;

        return
        [
            new DirectoryItem
            {
                Name = "All Drives",
                FullPath = "",
                SizeBytes = allSize,
                FileCount = allCount,
                Depth = 0,
                Children = driveRoots,
                IsExpanded = true,
                BackupStatus = allStatus,
            }
        ];
    }

    internal static string FormatBytes(long bytes) => $"{bytes:N0}";
}

/// <summary>A single file entry for the largest files view.</summary>
public class LargestFileItem : ViewModelBase
{
    private bool _isIncluded = true;

    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required string Directory { get; init; }
    public required long SizeBytes { get; init; }
    public BackupStatus BackupStatus { get; init; }
    public string SizeText => LargestFilesViewModel.FormatBytes(SizeBytes);

    /// <summary>Callback invoked when <see cref="IsIncluded"/> is toggled.</summary>
    internal Action<LargestFileItem>? InclusionToggled;

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetProperty(ref _isIncluded, value))
                InclusionToggled?.Invoke(this);
        }
    }
}

/// <summary>
/// An <see cref="IProgress{T}"/> wrapper that drops reports arriving faster
/// than the specified interval. Prevents <see cref="Progress{T}"/> from
/// flooding the UI thread dispatcher when the producer reports per-file or
/// per-directory at thousands of calls per second.
/// </summary>
internal sealed class ThrottledProgress<T>(IProgress<T> inner, TimeSpan interval) : IProgress<T>
{
    private readonly long _intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
    private long _nextReportTimestamp;

    public void Report(T value)
    {
        long now = Stopwatch.GetTimestamp();
        if (now >= Volatile.Read(ref _nextReportTimestamp))
        {
            Volatile.Write(ref _nextReportTimestamp, now + _intervalTicks);
            inner.Report(value);
        }
    }
}

/// <summary>
/// Lightweight <see cref="IProgress{T}"/> that just stores the latest value.
/// Called on a background thread; the UI thread polls <see cref="Latest"/>
/// on a timer. No <see cref="SynchronizationContext"/> posting — avoids
/// dispatcher flooding and stalls.
/// </summary>
internal sealed class LatestProgress<T> : IProgress<T> where T : class
{
    public volatile T? Latest;
    public void Report(T value) => Latest = value;
}

/// <summary>A directory entry for the directory tree view.</summary>
public class DirectoryItem : ViewModelBase
{
    private bool _isExpanded;
    private bool _isIncluded = true;

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long SizeBytes { get; init; }
    public int FileCount { get; init; }
    public int Depth { get; init; }
    public BackupStatus BackupStatus { get; init; }
    public string SizeText => LargestFilesViewModel.FormatBytes(SizeBytes);
    public string FileCountText => $"{FileCount:N0}";
    public List<DirectoryItem> Children { get; init; } = [];

    /// <summary>Callback invoked when <see cref="IsIncluded"/> is toggled.</summary>
    internal Action<DirectoryItem>? InclusionToggled;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetProperty(ref _isIncluded, value))
                InclusionToggled?.Invoke(this);
        }
    }
}
