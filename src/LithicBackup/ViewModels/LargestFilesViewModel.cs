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

    // --- Commands ---

    public ICommand CloseCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortByDirectoryCommand { get; }
    public ICommand SortBySizeCommand { get; }
    public ICommand ShowFilesCommand { get; }
    public ICommand ShowDirectoriesCommand { get; }

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

    // --- Per-directory exclusion editing ---

    private void OnDirectoryExclusionEditRequested(DirectoryItem item)
    {
        var selections = _backupSet.SourceSelections ?? [];
        var node = FindSelectionNode(selections, item.FullPath);

        // Collect current patterns (empty if no node exists yet).
        var excluded = node?.ExcludedPatterns ?? [];
        var included = node?.IncludedPatterns ?? [];

        // Build inherited exclusions text by walking ancestor directories.
        var inherited = BuildInheritedText(selections, item.FullPath);

        var editorVm = new ExclusionEditorViewModel(
            item.Name, item.FullPath, excluded, included, inherited);

        var dialog = new ExclusionEditorDialog
        {
            DataContext = editorVm,
            Owner = Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true) return;

        // Parse results.
        var newExcluded = ParseLines(editorVm.ExcludedPatterns);
        var newIncluded = ParseLines(editorVm.IncludedPatterns);

        // Find or create the node in the selection tree.
        node = FindOrCreateSelectionNode(selections, item.FullPath);
        node.ExcludedPatterns = newExcluded;
        node.IncludedPatterns = newIncluded;

        _backupSet.SourceSelections = selections;
        _ = SaveBackupSetAsync();
    }

    private async Task SaveBackupSetAsync()
    {
        try { await _catalog.UpdateBackupSetAsync(_backupSet); } catch { }
    }

    private static SourceSelection? FindSelectionNode(
        IReadOnlyList<SourceSelection> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase))
                return node;
            var found = FindSelectionNode(node.Children, path);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Find or create a SourceSelection node for the given path, building
    /// intermediate parent nodes as needed.
    /// </summary>
    private static SourceSelection FindOrCreateSelectionNode(
        List<SourceSelection> roots, string targetPath)
    {
        // Try to find an existing node first.
        var existing = FindSelectionNode(roots, targetPath);
        if (existing is not null) return existing;

        // Walk up from targetPath to find the deepest existing ancestor.
        var ancestors = new List<string>();
        string? current = targetPath;
        while (current is not null)
        {
            ancestors.Add(current);
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }

        // Walk from shallowest to deepest, creating nodes as needed.
        List<SourceSelection> container = roots;
        SourceSelection? parentNode = null;

        // Also check the virtual root (empty path).
        var virtualRoot = roots.FirstOrDefault(r => r.Path == "");
        if (virtualRoot is not null)
        {
            parentNode = virtualRoot;
            container = virtualRoot.Children;
        }

        for (int i = ancestors.Count - 1; i >= 0; i--)
        {
            var path = ancestors[i];
            var node = container.FirstOrDefault(n =>
                string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = new SourceSelection
                {
                    Path = path,
                    IsDirectory = true,
                    IsSelected = true,
                    AutoIncludeNewSubdirectories = true,
                };
                container.Add(node);
            }
            parentNode = node;
            container = node.Children;
        }

        return parentNode!;
    }

    private static string BuildInheritedText(
        IReadOnlyList<SourceSelection> roots, string targetPath)
    {
        var lines = new List<string>();

        // Collect the ancestor chain.
        var ancestors = new List<string>();
        string? current = Path.GetDirectoryName(targetPath);
        while (current is not null)
        {
            ancestors.Add(current);
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }

        // Check virtual root.
        var virtualRoot = roots.FirstOrDefault(r => r.Path == "");
        if (virtualRoot?.ExcludedPatterns is { Count: > 0 } rootPatterns)
        {
            foreach (var p in rootPatterns)
                lines.Add($"{p}  (All Drives)");
        }

        // Walk shallowest-first.
        for (int i = ancestors.Count - 1; i >= 0; i--)
        {
            var node = FindSelectionNode(roots, ancestors[i]);
            if (node?.ExcludedPatterns is { Count: > 0 } patterns)
            {
                string label = Path.GetFileName(node.Path);
                if (string.IsNullOrEmpty(label)) label = node.Path;
                foreach (var p in patterns)
                    lines.Add($"{p}  ({label})");
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "";
    }

    private static List<string> ParseLines(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        return input
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

                var filter = GlobMatcher.CreateCombinedFilter(
                    _backupSet.JobOptions?.ExcludedExtensions ?? [],
                    src);
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
            item.ExclusionEditRequested = OnDirectoryExclusionEditRequested;
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

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
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

    /// <summary>Callback invoked when the user wants to edit exclusion rules.</summary>
    internal Action<DirectoryItem>? ExclusionEditRequested;

    /// <summary>Opens the exclusion editor for this directory.</summary>
    public ICommand EditExclusionsCommand => new RelayCommand(
        _ => ExclusionEditRequested?.Invoke(this));

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
