using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Services;

namespace LithicBackup.ViewModels;

/// <summary>
/// Shows directories in a backup set that are no longer covered by the
/// current source roots — either because the user removed them or because
/// the directory was deleted from disk.  Also auto-detects files matching
/// configured exclusion patterns and excess file versions that no longer
/// fit the retention tier rules.  Supports scanning for additional files
/// that match user-typed exclusion patterns so the user can purge them
/// from the catalog.
/// </summary>
public class OrphanedDirectoriesViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly BackupSet _backupSet;
    /// <summary>Destination directory for physical deletion. Null when the backup set has no target configured.</summary>
    private readonly string? _targetDir;
    private bool _isLoading;
    private bool _isPurging;
    private bool _isScanningDestination;
    private string _summaryText = "Loading...";
    private string _exclusionPatterns = "";
    private string _destinationScanStatusText = "";
    private CleanupSortColumn _sortColumn = CleanupSortColumn.Name;
    private bool _sortAscending = true;

    /// <summary>Cached active files from the catalog, loaded once during init.</summary>
    private List<FileRecord>? _activeFiles;

    private string _purgeStatusText = "";
    private string _lastCleanupResultText = "";

    /// <summary>Catalog-vs-destination reconcile (flip stale filerefs, prune missing rows).</summary>
    private readonly CatalogReconcileService _reconcile;
    private bool _isReconciling;
    private string _reconcileStatusText = "";
    /// <summary>Dry-run result awaiting the user's "Apply" confirmation. Null until Analyze runs.</summary>
    private ReconcileReport? _reconcileReport;

    public event Action? DoneRequested;

    public OrphanedDirectoriesViewModel(ICatalogRepository catalog, BackupSet backupSet)
    {
        _catalog = catalog;
        _backupSet = backupSet;
        _targetDir = backupSet.JobOptions?.TargetDirectory;
        _reconcile = new CatalogReconcileService(catalog);

        Items = [];
        Categories = [];
        PurgeSelectedCommand = new RelayCommand(
            _ => PurgeSelected(),
            _ => !IsPurging && Categories.Any(c => c.HasCheckedItems));
        ScanExcludedCommand = new RelayCommand(_ => _ = ScanForExcludedAsync(), _ => !IsLoading && !IsPurging);
        ScanDestinationCommand = new RelayCommand(
            _ => _ = ScanDestinationAsync(),
            _ => !IsLoading && !IsPurging && !IsScanningDestination && _targetDir is not null);
        SortByNameCommand = new RelayCommand(_ => ToggleSort(CleanupSortColumn.Name));
        SortByFilesCommand = new RelayCommand(_ => ToggleSort(CleanupSortColumn.Files));
        SortBySizeCommand = new RelayCommand(_ => ToggleSort(CleanupSortColumn.Size));
        ReconcileAnalyzeCommand = new RelayCommand(
            _ => _ = ReconcileAnalyzeAsync(),
            _ => !IsLoading && !IsPurging && !IsReconciling && _targetDir is not null);
        ReconcileApplyCommand = new RelayCommand(
            _ => _ = ReconcileApplyAsync(),
            _ => !IsLoading && !IsPurging && !IsReconciling
                 && _targetDir is not null && _reconcileReport?.HasChanges == true);
        CloseCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        // Fire-and-forget the initial load; it runs its heavy work on a
        // background thread and drives the view's own progress (SummaryText /
        // IsLoading), so nothing needs to await it.
        _ = LoadAsync();
    }

    // ------------------------------------------------------------------
    // Sort state
    // ------------------------------------------------------------------

    /// <summary>Current sort column for all category trees.  Defaults to Name ascending.</summary>
    public CleanupSortColumn SortColumn => _sortColumn;

    /// <summary>True for ascending, false for descending.</summary>
    public bool SortAscending => _sortAscending;

    public string NameSortIndicator =>
        _sortColumn == CleanupSortColumn.Name ? (_sortAscending ? " ▲" : " ▼") : "";

    public string FilesSortIndicator =>
        _sortColumn == CleanupSortColumn.Files ? (_sortAscending ? " ▲" : " ▼") : "";

    public string SizeSortIndicator =>
        _sortColumn == CleanupSortColumn.Size ? (_sortAscending ? " ▲" : " ▼") : "";

    /// <summary>
    /// Toggle sort: clicking the current column flips direction; clicking
    /// a different column switches to that column with a sensible default
    /// direction (ascending for name, descending for size/files since
    /// largest-first is more useful in a cleanup view).
    /// </summary>
    private void ToggleSort(CleanupSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = column == CleanupSortColumn.Name;
        }
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(FilesSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));

        foreach (var category in Categories)
            category.ApplySort(_sortColumn, _sortAscending);
    }

    /// <summary>
    /// Flat list of every <see cref="OrphanedDirectoryItem"/> across all
    /// categories. The view binds to <see cref="Categories"/> for display;
    /// this collection backs the purge logic so it can iterate items
    /// without walking the tree.
    /// </summary>
    public ObservableCollection<OrphanedDirectoryItem> Items { get; }

    /// <summary>
    /// One category per <see cref="OrphanedReason"/>. Each category contains
    /// a tree of <see cref="OrphanedNodeViewModel"/>s built from its items.
    /// </summary>
    public ObservableCollection<OrphanedCategoryViewModel> Categories { get; }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsPurging
    {
        get => _isPurging;
        set
        {
            if (SetProperty(ref _isPurging, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// True while the optional destination-filesystem walk is in flight.
    /// Used to disable the scan button and to keep the load cursor up.
    /// </summary>
    public bool IsScanningDestination
    {
        get => _isScanningDestination;
        set
        {
            if (SetProperty(ref _isScanningDestination, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>True when the backup set has a target directory and so destination scanning is possible.</summary>
    public bool CanScanDestination => _targetDir is not null;

    /// <summary>
    /// Live progress text shown next to the "Scan destination filesystem"
    /// button while the walk is running.
    /// </summary>
    public string DestinationScanStatusText
    {
        get => _destinationScanStatusText;
        set => SetProperty(ref _destinationScanStatusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    /// <summary>
    /// Comma-separated exclusion patterns. Files in the catalog matching these
    /// patterns are shown as candidates for purging.
    /// </summary>
    public string ExclusionPatterns
    {
        get => _exclusionPatterns;
        set => SetProperty(ref _exclusionPatterns, value);
    }

    /// <summary>
    /// Header checkbox — tristate aggregate spanning every category.
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            if (Categories.Count == 0) return false;
            bool allTrue = Categories.All(c => c.IsAllChecked == true);
            bool allFalse = Categories.All(c => c.IsAllChecked == false);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            bool? target = value ?? true;
            foreach (var category in Categories)
                category.IsAllChecked = target;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Live progress text shown during purge (e.g. "Purging 3/12: D:\Photos\Old").
    /// </summary>
    public string PurgeStatusText
    {
        get => _purgeStatusText;
        set => SetProperty(ref _purgeStatusText, value);
    }

    /// <summary>
    /// Persistent summary of the last cleanup operation.  Unlike
    /// <see cref="SummaryText"/>, this is only set when a purge finishes and
    /// is never overwritten by other actions (loading, scanning, etc.) — so
    /// the user can still see what happened even if they were away when the
    /// purge finished.
    /// </summary>
    public string LastCleanupResultText
    {
        get => _lastCleanupResultText;
        set
        {
            if (SetProperty(ref _lastCleanupResultText, value))
                OnPropertyChanged(nameof(HasLastCleanupResult));
        }
    }

    public bool HasLastCleanupResult => !string.IsNullOrEmpty(_lastCleanupResultText);

    /// <summary>True while a reconcile analysis or apply is running.</summary>
    public bool IsReconciling
    {
        get => _isReconciling;
        set
        {
            if (SetProperty(ref _isReconciling, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Live status / dry-run summary for the catalog reconcile tool. Shows the
    /// pending flip/prune counts after Analyze, then progress during Apply.
    /// </summary>
    public string ReconcileStatusText
    {
        get => _reconcileStatusText;
        set => SetProperty(ref _reconcileStatusText, value);
    }

    public ICommand PurgeSelectedCommand { get; }
    public ICommand ScanExcludedCommand { get; }
    public ICommand ScanDestinationCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortByFilesCommand { get; }
    public ICommand SortBySizeCommand { get; }
    public ICommand ReconcileAnalyzeCommand { get; }
    public ICommand ReconcileApplyCommand { get; }
    public ICommand CloseCommand { get; }

    // ------------------------------------------------------------------

    private async Task LoadAsync()
    {
        IsLoading = true;
        SummaryText = "Loading catalogue...";

        // A DispatcherTimer polls a thread-safe progress counter at the shared
        // ProgressUpdateIntervalMs cadence so the user gets live feedback without
        // cross-thread marshaling per file/row. Started BEFORE the catalog read
        // so the (multi-second, synchronous) load also shows a running count.
        var progress = new ClassifyProgress();
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ProgressUpdateIntervalMs),
        };
        timer.Tick += (_, _) => SummaryText = FormatProgress(progress.Snapshot());
        timer.Start();

        try
        {
            // Do the whole load + classify off the UI thread. The catalog read is
            // a SYNCHRONOUS SQLite scan (ExecuteReader + row loop); for a large set
            // it blocks its thread for many seconds, so running it on the awaiting
            // UI thread would freeze the window. Task.Run keeps the UI responsive,
            // rowProgress drives a live record count during the read, and the
            // classification + tree construction (also heavy for hundreds of
            // thousands of files) continues off-thread. Only the final
            // Categories.Add / Items.Add marshal back to the UI.
            (List<OrphanedDirectoryItem> AllItems,
             List<OrphanedCategoryViewModel> Categories) classified;
            try
            {
                classified = await Task.Run(() =>
                {
                    progress.SetPhase("Loading catalog from database", 0);
                    var rowProgress = new SyncProgress<int>(progress.SetDone);
                    var files = _catalog
                        .GetAllFilesForBackupSetAsync(_backupSet.Id, CancellationToken.None, rowProgress)
                        .GetAwaiter().GetResult();

                    progress.SetPhase("Filtering active records", 0);
                    _activeFiles = files.Where(f => !f.IsDeleted).ToList();

                    return ClassifyAndBuild(progress);
                });
            }
            finally
            {
                timer.Stop();
            }

            // ---- UI thread from here on ----
            Items.Clear();
            foreach (var item in classified.AllItems)
            {
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            // Detach old category notifications.
            foreach (var oldCat in Categories)
                oldCat.PropertyChanged -= OnCategorySelectionChanged;
            Categories.Clear();

            foreach (var cat in classified.Categories)
            {
                cat.PropertyChanged += OnCategorySelectionChanged;
                Categories.Add(cat);
            }

            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            SummaryText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Heavy classification + tree-building pass.  Returns a flat
    /// <see cref="OrphanedDirectoryItem"/> list and a pre-built list of
    /// <see cref="OrphanedCategoryViewModel"/>s.  Safe to call off the UI
    /// thread: it only constructs view-model objects, never adds them to
    /// the bound <see cref="Items"/> / <see cref="Categories"/> collections.
    /// </summary>
    private (List<OrphanedDirectoryItem> AllItems,
             List<OrphanedCategoryViewModel> Categories)
        ClassifyAndBuild(ClassifyProgress progress)
    {
        int totalFiles = _activeFiles!.Count;

        progress.SetPhase("Grouping files by directory", totalFiles);

        // Group files by parent directory.
        var dirGroups = _activeFiles!
            .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                     StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress.SetDone(totalFiles);

        // ----------------------------------------------------------------
        // Classification precedence (strongest reason wins):
        //   1. RemovedFromSources       (parent dir lies outside every source root)
        //   2. MatchesConfiguredExclusion (file path matches an exclusion filter)
        //   3. DeletedFromDisk          (parent dir is under a source root but no longer exists)
        //   4. ExcessVersion            (extra versions beyond retention tier limits)
        //
        // A file that matches multiple reasons appears under the strongest
        // one — e.g. a file whose source dir has been removed from the
        // selection must not be reported as ExcessVersion.
        // ----------------------------------------------------------------

        // --- Phase 1: RemovedFromSources ---
        progress.SetPhase("Finding orphaned directories", totalFiles);
        var removedDirs = new List<OrphanedDirectoryItem>();
        foreach (var group in dirGroups)
        {
            string dir = group.Key;
            bool inSources = IsDirectoryInSources(dir);
            int groupCount = group.Count();
            if (!inSources)
            {
                // Dedupe by SourcePath so retention versions of a single
                // source file collapse to one displayed row.  Each
                // FileRecord (version) still gets purged via DiscFilePaths.
                var byPath = group
                    .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                removedDirs.Add(new OrphanedDirectoryItem
                {
                    DirectoryPath = dir,
                    Reason = OrphanedReason.RemovedFromSources,
                    FileCount = byPath.Count,
                    TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    Files = byPath
                        .Select(g => new OrphanedFileInfo(
                            Path.GetFileName(g.Key), g.Key, g.Sum(f => f.SizeBytes)))
                        .ToList(),
                    DiscFilePaths = _targetDir is null ? null
                        : group.Select(f => f.DiscPath.Replace('/', '\\')).ToList(),
                });
            }
            progress.Bump(groupCount);
        }

        // Collapse children of removed dirs into their highest ancestor.
        // Sorted by length so parents (shorter paths) come first.
        removedDirs.Sort((a, b) => a.DirectoryPath.Length.CompareTo(b.DirectoryPath.Length));
        var collapsedRemoved = new List<OrphanedDirectoryItem>();
        foreach (var item in removedDirs)
        {
            var parent = collapsedRemoved.FirstOrDefault(c =>
                IsPathUnderRoot(item.DirectoryPath, c.DirectoryPath)
                && !item.DirectoryPath.Equals(c.DirectoryPath, StringComparison.OrdinalIgnoreCase));
            if (parent is not null)
            {
                parent.FileCount += item.FileCount;
                parent.TotalSizeBytes += item.TotalSizeBytes;
                // Merge child's file list into the parent so the displayed
                // file rows include every file that would be purged.  The
                // child item itself is discarded (not added to collapsedRemoved).
                if (item.Files is not null)
                {
                    parent.Files ??= [];
                    parent.Files.AddRange(item.Files);
                }
                if (item.DiscFilePaths is not null)
                {
                    parent.DiscFilePaths ??= [];
                    parent.DiscFilePaths.AddRange(item.DiscFilePaths);
                }
            }
            else
            {
                collapsedRemoved.Add(item);
            }
        }

        // --- Phase 2: MatchesConfiguredExclusion ---
        progress.SetPhase("Detecting excluded files", totalFiles);
        var excludedItems = DetectExcludedFiles(collapsedRemoved, progress);

        // --- Phase 3: DeletedFromDisk ---
        var excludedPaths = new HashSet<string>(
            excludedItems
                .Where(i => i.MatchingSourcePaths is not null)
                .SelectMany(i => i.MatchingSourcePaths!),
            StringComparer.OrdinalIgnoreCase);

        // Fast lookup of removed-dir prefixes for the skip check.
        var collapsedRemovedPaths = collapsedRemoved
            .Select(r => r.DirectoryPath).ToList();

        progress.SetPhase("Checking deleted directories", totalFiles);
        var deletedDirs = new List<OrphanedDirectoryItem>();
        foreach (var group in dirGroups)
        {
            string dir = group.Key;
            int groupCount = group.Count();
            if (collapsedRemovedPaths.Any(p => IsPathUnderRoot(dir, p)))
            {
                progress.Bump(groupCount);
                continue;
            }
            bool inSources = IsDirectoryInSources(dir);
            if (!inSources || Directory.Exists(dir))
            {
                progress.Bump(groupCount);
                continue;
            }
            var remaining = group
                .Where(f => !excludedPaths.Contains(f.SourcePath))
                .ToList();
            if (remaining.Count == 0)
            {
                progress.Bump(groupCount);
                continue;
            }
            // Dedupe by SourcePath so retention versions collapse to one row.
            var remainingByPath = remaining
                .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            deletedDirs.Add(new OrphanedDirectoryItem
            {
                DirectoryPath = dir,
                Reason = OrphanedReason.DeletedFromDisk,
                FileCount = remainingByPath.Count,
                TotalSizeBytes = remaining.Sum(f => f.SizeBytes),
                Files = remainingByPath
                    .Select(g => new OrphanedFileInfo(
                        Path.GetFileName(g.Key), g.Key, g.Sum(f => f.SizeBytes)))
                    .ToList(),
                DiscFilePaths = _targetDir is null ? null
                    : remaining.Select(f => f.DiscPath.Replace('/', '\\')).ToList(),
            });
            progress.Bump(groupCount);
        }

        // --- Phase 4: ExcessVersion ---
        progress.SetPhase("Detecting excess versions", totalFiles);
        var phase1And3 = collapsedRemoved.Concat(deletedDirs).ToList();
        var excessItems = DetectExcessVersions(phase1And3, excludedItems, progress);

        // --- Phase 5: CatalogDuplicate ---
        // Distinct from ExcessVersion: these are stray catalog rows for
        // CURRENT-location files (e.g. left over from a non-idempotent
        // re-seed).  They share a SourcePath with a survivor but don't
        // correspond to any real "_prev" copy on disk.
        progress.SetPhase("Detecting catalog duplicates", totalFiles);
        var duplicateItems = DetectCatalogDuplicates(phase1And3, excludedItems, progress);

        // Build the flat item list.
        var allItems = collapsedRemoved
            .Concat(excludedItems)
            .Concat(deletedDirs)
            .Concat(excessItems)
            .Concat(duplicateItems)
            .OrderBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build per-reason trees as view-model objects (still off the UI
        // thread).  Categories will be added to the bound collection on the
        // UI thread by the caller.
        progress.SetPhase("Building directory trees", allItems.Count);
        var categories = new List<OrphanedCategoryViewModel>();
        foreach (var group in allItems.GroupBy(i => i.Reason).OrderBy(g => (int)g.Key))
        {
            var groupItems = group.ToList();
            if (groupItems.Count == 0) continue;
            var rootNodes = BuildTree(groupItems, progress);
            categories.Add(new OrphanedCategoryViewModel(group.Key, rootNodes));
        }

        return (allItems, categories);
    }

    // ------------------------------------------------------------------
    // Category tree assembly
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuild <see cref="Categories"/> from the current <see cref="Items"/>.
    /// Items are grouped by <see cref="OrphanedDirectoryItem.Reason"/>, and
    /// each group is turned into a tree based on the items' directory paths.
    /// Single-child interior chains are collapsed for readability.
    /// </summary>
    private void RebuildCategories()
    {
        // Detach old category notifications so old categories don't keep
        // raising IsAllSelected changes after they're discarded.
        foreach (var oldCat in Categories)
            oldCat.PropertyChanged -= OnCategorySelectionChanged;

        Categories.Clear();

        var groups = Items
            .GroupBy(i => i.Reason)
            .OrderBy(g => (int)g.Key);

        foreach (var group in groups)
        {
            var groupItems = group.ToList();
            if (groupItems.Count == 0) continue;

            var rootNodes = BuildTree(groupItems);
            var cat = new OrphanedCategoryViewModel(group.Key, rootNodes);
            cat.PropertyChanged += OnCategorySelectionChanged;
            // Apply the current sort to the newly-built tree so a re-scan
            // after the user changed sort doesn't revert to alphabetical.
            cat.ApplySort(_sortColumn, _sortAscending);
            Categories.Add(cat);
        }
    }

    private void OnCategorySelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrphanedCategoryViewModel.IsAllChecked))
            OnPropertyChanged(nameof(IsAllSelected));
    }

    /// <summary>
    /// Build a hierarchical tree from a flat list of directory items.
    /// Each item's <see cref="OrphanedDirectoryItem.DirectoryPath"/> determines
    /// its position in the tree.
    /// </summary>
    private static ObservableCollection<OrphanedNodeViewModel> BuildTree(
        List<OrphanedDirectoryItem> items,
        ClassifyProgress? progress = null)
    {
        // Root sentinel — its children become the top-level nodes.
        var root = new OrphanedNodeViewModel("", "", isDirectory: true);

        foreach (var item in items.OrderBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase))
        {
            progress?.Bump();
            string[] parts = SplitPath(item.DirectoryPath);
            if (parts.Length == 0) continue;

            var current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isLast = i == parts.Length - 1;
                // Only match directory children — file leaves added below
                // share the parent's child collection, and we must not treat
                // a file named "Bar" as if it were the directory "Bar".
                var existing = current.Children
                    .FirstOrDefault(c => c.IsDirectory
                        && string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    string segmentPath = string.Join('\\', parts.Take(i + 1));
                    existing = new OrphanedNodeViewModel(
                        parts[i],
                        segmentPath,
                        isDirectory: true,
                        parent: current,
                        item: isLast ? item : null)
                    { Depth = i };
                    current.Children.Add(existing);
                }
                else if (isLast && existing.Item is null)
                {
                    // Same directory was created earlier as an interior node;
                    // upgrade it to a leaf by attaching the item.  We can't
                    // mutate Item (it's get-only), so swap the node in place
                    // with a new leaf that adopts the existing children.
                    var replacement = new OrphanedNodeViewModel(
                        existing.Name, existing.FullPath, isDirectory: true,
                        parent: current, item: item)
                    { Depth = existing.Depth };
                    foreach (var c in existing.Children)
                    {
                        c.Parent = replacement;
                        replacement.Children.Add(c);
                    }
                    int idx = current.Children.IndexOf(existing);
                    current.Children[idx] = replacement;
                    existing = replacement;
                }

                // Propagate file count / size up the tree.
                if (isLast)
                {
                    existing.FileCount += item.FileCount;
                    existing.SizeBytes += item.TotalSizeBytes;
                    var ancestor = existing.Parent;
                    while (ancestor is not null && ancestor != root)
                    {
                        ancestor.FileCount += item.FileCount;
                        ancestor.SizeBytes += item.TotalSizeBytes;
                        ancestor = ancestor.Parent;
                    }

                    // Add per-file leaf rows under this directory so the user
                    // can see exactly which files would be purged.  These are
                    // display-only: GetCheckedItems still yields the wrapped
                    // OrphanedDirectoryItem, not synthetic per-file items.
                    if (item.Files is not null)
                    {
                        foreach (var file in item.Files
                            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
                        {
                            var fileNode = new OrphanedNodeViewModel(
                                file.DisplayName, file.Path,
                                isDirectory: false, parent: existing)
                            {
                                Depth = existing.Depth + 1,
                                FileCount = 1,
                                SizeBytes = file.Size,
                            };
                            existing.Children.Add(fileNode);
                        }
                    }
                }

                current = existing;
            }
        }

        // NOTE: We intentionally don't collapse single-child interior chains
        // here (e.g. "mIRC → backups" into "mIRC\backups").  Users prefer to
        // see the real directory hierarchy expanded out one segment at a time
        // so they can read paths the same way they appear on disk.
        return root.Children;
    }

    /// <summary>
    /// Split an absolute path into segments, keeping the drive letter as
    /// its own segment so "D:\foo\bar" becomes ["D:", "foo", "bar"].
    /// </summary>
    private static string[] SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        return path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------------
    // Phase 2: Configured exclusion detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Detect files in the catalog that match the backup set's configured
    /// exclusion patterns (global excluded extensions + tier sets with 0 tiers).
    /// Mirrors the logic of DirectoryBackupService.BuildExclusionFilter.
    /// </summary>
    private List<OrphanedDirectoryItem> DetectExcludedFiles(
        List<OrphanedDirectoryItem> orphanedDirs,
        ClassifyProgress? progress = null)
    {
        if (_activeFiles is null) return [];

        var jobOptions = _backupSet.JobOptions;
        if (jobOptions is null) return [];

        // Build exclusion filter — same logic as DirectoryBackupService.BuildExclusionFilter.
        var globalFilter = jobOptions.ExcludedExtensions.Count > 0
            ? GlobMatcher.CreateFilter(jobOptions.ExcludedExtensions) : null;

        Func<string, VersionTierSet>? tierResolver = null;
        if (jobOptions.TierSets.Count > 0)
        {
            var resolver = VersionTierSet.BuildTierResolver(jobOptions.TierSets);
            bool hasExclusionTierSet = jobOptions.TierSets.Any(ts =>
                ts.Tiers.Count == 0
                && ts.FilePatterns.Count > 0
                && !string.Equals(ts.Name, "Default", StringComparison.OrdinalIgnoreCase));
            if (hasExclusionTierSet)
                tierResolver = resolver;
        }

        if (globalFilter is null && tierResolver is null)
            return [];

        Func<string, bool> exclusionFilter = path =>
        {
            if (globalFilter?.Invoke(path) ?? false)
                return true;
            if (tierResolver is not null && tierResolver(path).Tiers.Count == 0)
                return true;
            return false;
        };

        // Build list of orphaned directory paths to skip (files there are
        // already covered by orphaned directory items). IsPathUnderRoot
        // handles separator boundaries correctly.
        var orphanedDirPaths = orphanedDirs.Select(i => i.DirectoryPath).ToList();

        var excluded = _activeFiles
            .Where(f =>
            {
                progress?.Bump();
                if (orphanedDirPaths.Any(p =>
                    IsPathUnderRoot(f.SourcePath, p)))
                    return false;

                return exclusionFilter(f.SourcePath);
            })
            .ToList();

        if (excluded.Count == 0)
            return [];

        // Group by parent directory.
        var results = new List<OrphanedDirectoryItem>();
        var dirGroups = excluded
            .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                     StringComparer.OrdinalIgnoreCase);

        foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Dedupe by SourcePath so retention versions collapse to one row.
            var byPath = group
                .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            results.Add(new OrphanedDirectoryItem
            {
                DirectoryPath = group.Key,
                Reason = OrphanedReason.MatchesConfiguredExclusion,
                FileCount = byPath.Count,
                TotalSizeBytes = group.Sum(f => f.SizeBytes),
                MatchingSourcePaths = group.Select(f => f.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Files = byPath
                    .Select(g => new OrphanedFileInfo(
                        Path.GetFileName(g.Key), g.Key, g.Sum(f => f.SizeBytes)))
                    .ToList(),
                DiscFilePaths = _targetDir is null ? null
                    : group.Select(f => f.DiscPath.Replace('/', '\\')).ToList(),
            });
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Phase 3: Excess version detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Detect file versions that exceed the configured retention tier limits.
    /// Replicates the core logic of VersionRetentionService.ComputeRetentionAsync
    /// using the already-loaded in-memory file list.
    /// </summary>
    private List<OrphanedDirectoryItem> DetectExcessVersions(
        List<OrphanedDirectoryItem> orphanedDirs,
        List<OrphanedDirectoryItem> excludedItems,
        ClassifyProgress? progress = null)
    {
        if (_activeFiles is null) return [];

        var jobOptions = _backupSet.JobOptions;
        if (jobOptions is null) return [];

        // Build per-file tier selector: file path → retention tiers.
        Func<string, IReadOnlyList<VersionRetentionTier>> tierSelector;

        if (jobOptions.TierSets.Count > 0)
        {
            var resolver = VersionTierSet.BuildTierResolver(jobOptions.TierSets);
            tierSelector = path => resolver(path).Tiers;
        }
        else if (jobOptions.RetentionTiers.Count > 0)
        {
            var flatTiers = jobOptions.RetentionTiers;
            tierSelector = _ => flatTiers;
        }
        else
        {
            return []; // No retention rules configured.
        }

        // Build skip-sets: files in orphaned dirs or matched by exclusion filter.
        // IsPathUnderRoot handles path-separator boundaries correctly.
        var orphanedDirPaths = orphanedDirs.Select(i => i.DirectoryPath).ToList();
        var excludedPaths = new HashSet<string>(
            excludedItems
                .Where(i => i.MatchingSourcePaths is not null)
                .SelectMany(i => i.MatchingSourcePaths!),
            StringComparer.OrdinalIgnoreCase);

        // Filter active files to only those not already flagged.
        var eligibleFiles = _activeFiles
            .Where(f =>
            {
                progress?.Bump();
                if (orphanedDirPaths.Any(p =>
                    IsPathUnderRoot(f.SourcePath, p)))
                    return false;
                if (excludedPaths.Contains(f.SourcePath))
                    return false;
                return true;
            })
            .ToList();

        var now = DateTime.UtcNow;
        var excessRecords = new List<FileRecord>();

        // Group by source path — same approach as VersionRetentionService.
        var groupedByPath = eligibleFiles
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedByPath)
        {
            // A "real" previous version is a FileRecord whose DiscPath lives
            // under "{drive}_prev/..." — that's where every actual backup
            // run writes superseded copies (see
            // DirectoryBackupService.GetPrevDiscPath).  Records pointing at
            // the current location are NOT versions, even if there are
            // several of them sharing a SourcePath — that situation is a
            // catalog anomaly (e.g. a non-idempotent re-seed) and is
            // surfaced in the separate Catalog-Duplicate phase below, not
            // here.  Restricting the tier walk to "_prev" records keeps the
            // Excess-Versions category meaningful for users who haven't
            // backed up yet.
            var versions = group
                .Where(f => IsPreviousVersionPath(f.DiscPath))
                .OrderByDescending(f => f.BackedUpUtc)
                .ToList();

            if (versions.Count == 0)
                continue; // No real prev-version records for this file.

            var tiers = tierSelector(group.Key);
            if (tiers.Count == 0)
                continue; // 0 tiers = no retention rules (excluded or no history).

            // Never delete the most recent prev version: we'd lose the
            // ability to roll back at all.  (Note: the "current" record is
            // automatically safe — it isn't in `versions` because its
            // DiscPath isn't a `_prev` path.)
            long newestId = versions[0].Id;

            // Walk tiers from youngest to oldest.
            var sortedTiers = tiers
                .OrderBy(t => t.MaxAge ?? TimeSpan.MaxValue)
                .ToList();

            var processed = new HashSet<long>();
            TimeSpan previousBoundary = TimeSpan.Zero;

            foreach (var tier in sortedTiers)
            {
                TimeSpan upperBoundary = tier.MaxAge ?? TimeSpan.MaxValue;

                // Find versions in this tier's age range.
                var tierVersions = versions
                    .Where(v => !processed.Contains(v.Id))
                    .Where(v =>
                    {
                        var age = now - v.BackedUpUtc;
                        return age >= previousBoundary && age < upperBoundary;
                    })
                    .OrderByDescending(v => v.BackedUpUtc) // Keep newest first.
                    .ToList();

                if (tier.MaxVersions.HasValue && tierVersions.Count > tier.MaxVersions.Value)
                {
                    // Keep MaxVersions newest, mark rest for deletion.
                    int toKeep = tier.MaxVersions.Value;
                    for (int i = 0; i < tierVersions.Count; i++)
                    {
                        processed.Add(tierVersions[i].Id);
                        if (i >= toKeep && tierVersions[i].Id != newestId)
                        {
                            excessRecords.Add(tierVersions[i]);
                        }
                    }
                }
                else
                {
                    // Unlimited or within limit — keep all.
                    foreach (var v in tierVersions)
                        processed.Add(v.Id);
                }

                previousBoundary = upperBoundary;
            }
        }

        if (excessRecords.Count == 0)
            return [];

        // Group excess records by parent directory.
        var results = new List<OrphanedDirectoryItem>();
        var dirGroups = excessRecords
            .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                     StringComparer.OrdinalIgnoreCase);

        foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var records = group.ToList();
            results.Add(new OrphanedDirectoryItem
            {
                DirectoryPath = group.Key,
                Reason = OrphanedReason.ExcessVersion,
                FileCount = records.Count,
                TotalSizeBytes = records.Sum(f => f.SizeBytes),
                ExcessVersionRecords = records,
                // Tag each row with its backup timestamp so the user can
                // tell otherwise-identical filenames apart when several
                // older versions of the same file are listed together.
                Files = records
                    .OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(r => r.BackedUpUtc)
                    .Select(r => new OrphanedFileInfo(
                        $"{Path.GetFileName(r.SourcePath)} (backed up {r.BackedUpUtc.ToLocalTime():yyyy-MM-dd HH:mm})",
                        r.SourcePath,
                        r.SizeBytes))
                    .ToList(),
                DiscFilePaths = _targetDir is null ? null
                    : records.Select(r => r.DiscPath.Replace('/', '\\')).ToList(),
            });
        }

        return results;
    }

    /// <summary>
    /// Returns true when <paramref name="discPath"/> points at a previous
    /// version of a file — i.e. lives under a "{drive}_prev/" subtree.
    /// LithicBackup writes every superseded copy under that prefix (see
    /// <c>DirectoryBackupService.GetPrevDiscPath</c>), so this is the
    /// authoritative test for "is this record a real older version" versus
    /// just a stray catalog row pointing at the current file.
    /// Tolerates both path separators because catalog DiscPaths normalize
    /// inconsistently in different code paths.
    /// </summary>
    private static bool IsPreviousVersionPath(string discPath)
    {
        if (string.IsNullOrEmpty(discPath)) return false;
        int sep = discPath.IndexOfAny(['\\', '/']);
        if (sep < 0) return false;
        var firstSegment = discPath.AsSpan(0, sep);
        return firstSegment.EndsWith("_prev", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Phase 5: Catalog-duplicate detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Detect FileRecords that share a SourcePath with another non-deleted
    /// FileRecord while both point at the CURRENT disc location (i.e.
    /// neither is in a "_prev" subtree).  This is a catalog anomaly — the
    /// destination only holds one physical copy at the current path, so the
    /// extra rows are dead weight that confuse retention logic and inflate
    /// counts.  The most common cause is running "Seed from existing
    /// backup" more than once on the same destination before
    /// <see cref="DirectoryBackupService.SeedFromExistingDirectoryAsync"/>
    /// became idempotent.
    ///
    /// For each affected SourcePath the newest BackedUpUtc record is kept;
    /// older "current"-pointing copies are returned as cleanup candidates.
    /// Physical-file deletion is intentionally skipped because every
    /// duplicate row points at the same on-disk file as the survivor.
    /// </summary>
    private List<OrphanedDirectoryItem> DetectCatalogDuplicates(
        List<OrphanedDirectoryItem> orphanedDirs,
        List<OrphanedDirectoryItem> excludedItems,
        ClassifyProgress? progress = null)
    {
        if (_activeFiles is null) return [];

        var orphanedDirPaths = orphanedDirs.Select(i => i.DirectoryPath).ToList();
        var excludedPaths = new HashSet<string>(
            excludedItems
                .Where(i => i.MatchingSourcePaths is not null)
                .SelectMany(i => i.MatchingSourcePaths!),
            StringComparer.OrdinalIgnoreCase);

        // Restrict to records that point at a CURRENT location and aren't
        // already claimed by an earlier phase.
        var eligibleCurrent = _activeFiles
            .Where(f =>
            {
                progress?.Bump();
                if (IsPreviousVersionPath(f.DiscPath)) return false;
                if (orphanedDirPaths.Any(p => IsPathUnderRoot(f.SourcePath, p)))
                    return false;
                if (excludedPaths.Contains(f.SourcePath)) return false;
                return true;
            })
            .ToList();

        var duplicates = new List<FileRecord>();
        var groupedByPath = eligibleCurrent
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedByPath)
        {
            var rows = group
                .OrderByDescending(f => f.BackedUpUtc)
                .ToList();
            if (rows.Count <= 1) continue;
            // Keep newest BackedUpUtc, mark the rest as duplicates.
            for (int i = 1; i < rows.Count; i++)
                duplicates.Add(rows[i]);
        }

        if (duplicates.Count == 0) return [];

        var results = new List<OrphanedDirectoryItem>();
        var dirGroups = duplicates
            .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                     StringComparer.OrdinalIgnoreCase);

        foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var records = group.ToList();
            results.Add(new OrphanedDirectoryItem
            {
                DirectoryPath = group.Key,
                Reason = OrphanedReason.CatalogDuplicate,
                FileCount = records.Count,
                TotalSizeBytes = records.Sum(f => f.SizeBytes),
                ExcessVersionRecords = records,
                Files = records
                    .OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(r => r.BackedUpUtc)
                    .Select(r => new OrphanedFileInfo(
                        $"{Path.GetFileName(r.SourcePath)} (seeded {r.BackedUpUtc.ToLocalTime():yyyy-MM-dd HH:mm})",
                        r.SourcePath,
                        r.SizeBytes))
                    .ToList(),
                // Intentionally NULL: every duplicate points at the same
                // physical file as the surviving record.  Deleting the file
                // would orphan the survivor.  Only the extra catalog rows
                // need to be removed.
                DiscFilePaths = null,
            });
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Manual exclusion scan
    // ------------------------------------------------------------------

    /// <summary>
    /// Scan the catalog for files matching the exclusion patterns and add them
    /// to the list as purgeable items.
    /// </summary>
    private async Task ScanForExcludedAsync()
    {
        if (_activeFiles is null)
            return;

        // Remove previous manual exclusion-pattern items (keep auto-detected ones).
        bool removedAny = false;
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Reason == OrphanedReason.MatchesExclusionPattern)
            {
                Items.RemoveAt(i);
                removedAny = true;
            }
        }

        var patterns = ParsePatterns(ExclusionPatterns);
        if (patterns.Count == 0)
        {
            if (removedAny) RebuildCategories();
            UpdateSummaryText();
            return;
        }

        var filter = GlobMatcher.CreateFilter(patterns);
        if (filter is null)
        {
            if (removedAny) RebuildCategories();
            UpdateSummaryText();
            return;
        }

        IsLoading = true;
        SummaryText = "Scanning for excluded files...";

        try
        {
            // Build set of directories already shown as orphaned so we don't
            // double-count files. IsPathUnderRoot handles separator boundaries.
            var orphanedDirPaths = Items
                .Where(i => i.Reason is OrphanedReason.RemovedFromSources or OrphanedReason.DeletedFromDisk)
                .Select(i => i.DirectoryPath)
                .ToList();

            // Also skip files already flagged by auto-detected exclusions.
            var alreadyFlaggedPaths = new HashSet<string>(
                Items
                    .Where(i => i.Reason == OrphanedReason.MatchesConfiguredExclusion && i.MatchingSourcePaths is not null)
                    .SelectMany(i => i.MatchingSourcePaths!),
                StringComparer.OrdinalIgnoreCase);

            // Run the filter on a background thread — could be thousands of files.
            var excluded = await Task.Run(() =>
            {
                return _activeFiles
                    .Where(f =>
                    {
                        // Skip files already covered by an orphaned directory.
                        if (orphanedDirPaths.Any(p =>
                            IsPathUnderRoot(f.SourcePath, p)))
                            return false;

                        // Skip files already covered by auto-detected exclusions.
                        if (alreadyFlaggedPaths.Contains(f.SourcePath))
                            return false;

                        return filter(f.SourcePath);
                    })
                    .ToList();
            });

            if (excluded.Count == 0)
            {
                if (removedAny) RebuildCategories();
                UpdateSummaryText();
                return;
            }

            // Group by parent directory.
            var dirGroups = excluded
                .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                         StringComparer.OrdinalIgnoreCase);

            foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Dedupe by SourcePath so retention versions collapse to one row.
                var byPath = group
                    .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var item = new OrphanedDirectoryItem
                {
                    DirectoryPath = group.Key,
                    Reason = OrphanedReason.MatchesExclusionPattern,
                    FileCount = byPath.Count,
                    TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    MatchingSourcePaths = group.Select(f => f.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Files = byPath
                        .Select(g => new OrphanedFileInfo(
                            Path.GetFileName(g.Key), g.Key, g.Sum(f => f.SizeBytes)))
                        .ToList(),
                    DiscFilePaths = _targetDir is null ? null
                        : group.Select(f => f.DiscPath.Replace('/', '\\')).ToList(),
                };
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            RebuildCategories();
            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            SummaryText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ------------------------------------------------------------------
    // Destination filesystem scan
    // ------------------------------------------------------------------

    /// <summary>
    /// Walk the backup destination directory and surface two extra categories
    /// that the catalog-only classification can't discover:
    ///   • <see cref="OrphanedReason.UntrackedFile"/> — files present in the
    ///     destination but absent from the catalog.
    ///   • <see cref="OrphanedReason.CatalogDeleted"/> — files whose catalog
    ///     records are all <see cref="FileRecord.IsDeleted"/> = true but
    ///     which are still physically on disk.
    /// Removes any prior results from these two categories before re-running,
    /// so a second scan replaces (not duplicates) the prior findings.
    /// </summary>
    private async Task ScanDestinationAsync()
    {
        if (_activeFiles is null || _targetDir is null)
            return;

        // Give immediate feedback the moment the button is pressed: flip the
        // busy flag (greys the Scan button via CanExecute) and show a wait
        // cursor.  The initialization below — clearing a potentially large
        // Items collection and loading every catalog record — runs on the UI
        // thread and can take a few seconds before the background walk starts,
        // so without this the button stayed enabled and the cursor normal
        // during that gap.
        IsScanningDestination = true;
        DestinationScanStatusText = "Scanning destination directory...";
        Mouse.OverrideCursor = Cursors.Wait;

        // Yield at Background priority so WPF actually renders the disabled
        // button + wait cursor before we block the dispatcher clearing Items.
        await Dispatcher.Yield(DispatcherPriority.Background);

        bool removedAny = false;
        try
        {
            // Remove any prior destination-only items so re-scanning replaces
            // (not duplicates) previous findings.
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (Items[i].Reason is OrphanedReason.UntrackedFile or OrphanedReason.CatalogDeleted)
                {
                    Items.RemoveAt(i);
                    removedAny = true;
                }
            }

            // Pull ALL records (including deleted) so we can detect the
            // CatalogDeleted category — _activeFiles excludes them by design.
            var allFiles = await _catalog.GetAllFilesForBackupSetAsync(_backupSet.Id);

            // Build disc-path → records lookup, separating deleted from active.
            var discPathLookup = new Dictionary<string, List<FileRecord>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var f in allFiles)
            {
                string normalised = f.DiscPath.Replace('/', '\\');
                if (!discPathLookup.TryGetValue(normalised, out var list))
                {
                    list = [];
                    discPathLookup[normalised] = list;
                }
                list.Add(f);
            }

            string targetDir = _targetDir;
            var progress = new Progress<string>(msg => DestinationScanStatusText = msg);

            // Initialization is done; the walk below runs on a background
            // thread with live progress text, so drop the wait cursor here —
            // it only needed to cover the synchronous init gap above.  The
            // button stays greyed (IsScanningDestination) for the whole walk.
            Mouse.OverrideCursor = null;

            // Walk the destination on a background thread so the UI stays
            // responsive during multi-minute walks of large backups.
            var (untracked, catalogDeleted, directoriesSkipped, filesScanned) = await Task.Run(() =>
                WalkDestination(targetDir, discPathLookup, progress));

            // ---- UI thread from here on ----
            void AddCategory(OrphanedReason reason, List<(string DiscRel, long Size, string? SourcePath)> hits)
            {
                if (hits.Count == 0) return;

                // Group by parent directory in disc-relative space so the
                // tree shows "C\Users\foo\file.txt" rather than a flat list.
                var dirGroups = hits.GroupBy(
                    h => Path.GetDirectoryName(h.DiscRel) ?? "",
                    StringComparer.OrdinalIgnoreCase);

                foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var hitList = group.ToList();
                    // DirectoryPath here lives in disc-relative space (e.g.
                    // "C\Users\foo").  That's a different "path space" from
                    // the source-absolute paths in the catalog-side
                    // categories, but BuildTree builds one tree per
                    // category so there's no cross-category mixing.
                    string dirKey = string.IsNullOrEmpty(group.Key) ? "(root)" : group.Key;
                    var item = new OrphanedDirectoryItem
                    {
                        DirectoryPath = dirKey,
                        Reason = reason,
                        FileCount = hitList.Count,
                        TotalSizeBytes = hitList.Sum(h => h.Size),
                        Files = hitList
                            .OrderBy(h => h.DiscRel, StringComparer.OrdinalIgnoreCase)
                            .Select(h => new OrphanedFileInfo(
                                Path.GetFileName(h.DiscRel),
                                // Prefer the reconstructed/known source path
                                // for the tooltip; fall back to the disc
                                // path so the user always sees something
                                // meaningful.
                                h.SourcePath ?? h.DiscRel,
                                h.Size))
                            .ToList(),
                        DiscFilePaths = hitList.Select(h => h.DiscRel).ToList(),
                    };
                    item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                    Items.Add(item);
                }
            }

            AddCategory(OrphanedReason.UntrackedFile, untracked);
            AddCategory(OrphanedReason.CatalogDeleted, catalogDeleted);

            RebuildCategories();
            UpdateSummaryText();

            int newCount = untracked.Count + catalogDeleted.Count;
            string skippedSuffix = directoriesSkipped > 0
                ? $", {directoriesSkipped:N0} inaccessible directories skipped"
                : "";
            DestinationScanStatusText = newCount == 0
                ? $"Destination scan complete — examined {filesScanned:N0} files, no extras found{skippedSuffix}."
                : $"Destination scan complete — examined {filesScanned:N0} files, found {untracked.Count:N0} untracked + {catalogDeleted.Count:N0} catalog-deleted{skippedSuffix}.";
        }
        catch (Exception ex)
        {
            DestinationScanStatusText = $"Scan failed: {ex.Message}";
            if (removedAny) RebuildCategories();
        }
        finally
        {
            IsScanningDestination = false;
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// Background-thread destination walk.  For every file under
    /// <paramref name="targetDir"/>, either skips it (when the catalog
    /// records the path as active), routes it to <c>catalogDeleted</c>
    /// (when only deleted records remain), or routes it to <c>untracked</c>
    /// (when no record matches at all).  Skips the shared <c>_blocks</c> and
    /// <c>_filestore</c> stores.
    /// </summary>
    private static (List<(string DiscRel, long Size, string? SourcePath)> Untracked,
                    List<(string DiscRel, long Size, string? SourcePath)> CatalogDeleted,
                    int DirectoriesSkipped,
                    int FilesScanned)
        WalkDestination(
            string targetDir,
            Dictionary<string, List<FileRecord>> discPathLookup,
            IProgress<string> progress)
    {
        var untracked = new List<(string, long, string?)>();
        var catalogDeleted = new List<(string, long, string?)>();

        var targetInfo = new DirectoryInfo(targetDir);
        if (!targetInfo.Exists)
            throw new DirectoryNotFoundException($"Destination directory not found: {targetDir}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastProgressMs = 0;
        int filesScanned = 0;
        int directoriesSkipped = 0;

        // Iterative DFS using an explicit stack to avoid any concern about
        // deep recursion blowing the thread stack on extremely-nested
        // destinations.  Each directory's file + subdirectory enumerations
        // are wrapped in their own try/catch so a single unreadable folder
        // (UnauthorizedAccessException, PathTooLongException, ArgumentException
        // from weird path chars, COMException from network shares dropping,
        // etc.) is recorded as a skip and the walk continues.
        //
        // We deliberately catch the broad Exception base type at the
        // directory-enumeration boundary — the .NET docs only list a few
        // exception types for these calls, but in practice security
        // providers, antivirus drivers, and reparse points can throw
        // arbitrary derived exceptions, and we never want one of those to
        // silently terminate the entire scan.
        var stack = new Stack<(DirectoryInfo Dir, string RelativeDir)>();
        stack.Push((targetInfo, ""));

        while (stack.Count > 0)
        {
            var (dir, relativeDir) = stack.Pop();

            // Skip shared content-addressed stores at the top level — these
            // aren't user-visible backup files and are managed internally.
            if (relativeDir.Length > 0 && (
                    relativeDir.Equals("_blocks", StringComparison.OrdinalIgnoreCase) ||
                    relativeDir.Equals("_filestore", StringComparison.OrdinalIgnoreCase) ||
                    relativeDir.StartsWith("_blocks\\", StringComparison.OrdinalIgnoreCase) ||
                    relativeDir.StartsWith("_filestore\\", StringComparison.OrdinalIgnoreCase)))
                continue;

            // --- Enumerate files in this directory ---
            FileInfo[]? files = null;
            try
            {
                // Materialise to an array immediately — this makes a single
                // failure point we can guard, rather than the deferred
                // enumeration throwing midway through the foreach.
                files = dir.GetFiles();
            }
            catch (Exception ex)
            {
                directoriesSkipped++;
                System.Diagnostics.Debug.WriteLine(
                    $"WalkDestination: skip files in '{dir.FullName}': {ex.GetType().Name}: {ex.Message}");
            }

            if (files is not null)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];
                    filesScanned++;
                    if (sw.ElapsedMilliseconds - lastProgressMs >= ProgressUpdateIntervalMs)
                    {
                        lastProgressMs = sw.ElapsedMilliseconds;
                        progress.Report($"Scanning: {filesScanned:N0} files examined — {dir.Name}");
                    }

                    long size;
                    string fileName;
                    try
                    {
                        size = file.Length;
                        fileName = file.Name;
                    }
                    catch (Exception)
                    {
                        // File vanished or became inaccessible between enumeration and stat — skip just this file.
                        continue;
                    }

                    // Skip partial-copy temp files left by an interrupted backup
                    // (DirectoryBackupService.CopyFileAsync writes to "*.lbtmp"
                    // before the atomic rename).  They aren't real backup
                    // content and shouldn't surface as untracked files.
                    if (fileName.EndsWith(".lbtmp", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativePath = relativeDir.Length == 0
                        ? fileName
                        : relativeDir + "\\" + fileName;

                    // A catalog record's DiscPath for a deduplicated file carries
                    // a ".fileref" / ".dedup" manifest suffix (e.g.
                    // "D\AI\foo.zip.fileref"), while the manifest can later be
                    // MATERIALISED back into a plain, suffix-less file on disk
                    // ("D\AI\foo.zip") whose bytes ARE the referenced content
                    // (DirectoryBackupService.MaterialiseFileRef removes the
                    // manifest and writes the plain file).  So a plain on-disk
                    // file must match not only an exact-path catalog record but
                    // also a "<path>.fileref"/"<path>.dedup" record — otherwise
                    // legitimate, catalog-referenced backup content is wrongly
                    // reported as untracked, and "cleaning" it would delete real
                    // backup data (and it reappears once the worker
                    // re-materialises the reference).  Exact match wins; the
                    // manifest-suffix fallbacks only fire for suffix-less files.
                    if (discPathLookup.TryGetValue(relativePath, out var records)
                        || discPathLookup.TryGetValue(relativePath + ".fileref", out records)
                        || discPathLookup.TryGetValue(relativePath + ".dedup", out records))
                    {
                        bool hasActive = false;
                        for (int r = 0; r < records.Count; r++)
                        {
                            if (!records[r].IsDeleted) { hasActive = true; break; }
                        }
                        if (hasActive) continue; // Active record exists — file is properly tracked.

                        string? src = records.Count > 0 ? records[0].SourcePath : null;
                        catalogDeleted.Add((relativePath, size, src));
                    }
                    else
                    {
                        string? reconstructed = TryReconstructSourcePath(relativePath);
                        untracked.Add((relativePath, size, reconstructed));
                    }
                }
            }

            // --- Enumerate subdirectories and push for later traversal ---
            DirectoryInfo[]? subdirs = null;
            try
            {
                subdirs = dir.GetDirectories();
            }
            catch (Exception ex)
            {
                directoriesSkipped++;
                System.Diagnostics.Debug.WriteLine(
                    $"WalkDestination: skip subdirs of '{dir.FullName}': {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // Push in reverse so traversal order matches alphabetical-ish
            // (purely cosmetic — affects only the progress messages).
            for (int i = subdirs.Length - 1; i >= 0; i--)
            {
                var sub = subdirs[i];
                string subRel = relativeDir.Length == 0
                    ? sub.Name
                    : relativeDir + "\\" + sub.Name;
                stack.Push((sub, subRel));
            }
        }

        return (untracked, catalogDeleted, directoriesSkipped, filesScanned);
    }

    /// <summary>
    /// Best-effort source-path reconstruction from a disc-relative path.
    /// Examples:
    ///   <c>C\Users\foo\file.txt</c> → <c>C:\Users\foo\file.txt</c>
    ///   <c>C_prev\Users\foo\file.txt.v3</c> → <c>C:\Users\foo\file.txt</c>
    /// Used purely for tooltip display so the user can guess where a
    /// destination file came from; returning <c>null</c> just falls back to
    /// showing the disc-relative path.
    /// </summary>
    private static string? TryReconstructSourcePath(string discRelativePath)
    {
        if (discRelativePath.Length < 2) return null;

        string first = discRelativePath.Split('\\', '/')[0];

        string drivePrefix;
        if (first.Length == 1 && char.IsLetter(first[0]))
        {
            drivePrefix = first;
        }
        else if (first.Length > 1 && first.EndsWith("_prev", StringComparison.OrdinalIgnoreCase)
                 && char.IsLetter(first[0]))
        {
            drivePrefix = first[0].ToString();
        }
        else
        {
            return null;
        }

        if (discRelativePath.Length <= first.Length + 1) return null;
        string afterDrive = discRelativePath[(first.Length + 1)..];

        if (first.EndsWith("_prev", StringComparison.OrdinalIgnoreCase))
        {
            afterDrive = StripBackupSuffixes(afterDrive);
        }
        else
        {
            if (afterDrive.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase))
                afterDrive = afterDrive[..^6];
            else if (afterDrive.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase))
                afterDrive = afterDrive[..^8];
        }

        return drivePrefix + @":\" + afterDrive;
    }

    private static string StripBackupSuffixes(string path)
    {
        if (path.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase))
            path = path[..^6];
        else if (path.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase))
            path = path[..^8];

        int lastDot = path.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < path.Length - 1
            && path[lastDot + 1] == 'v'
            && int.TryParse(path[(lastDot + 2)..], out _))
        {
            path = path[..lastDot];
        }

        return path;
    }

    // ------------------------------------------------------------------
    // Purge
    // ------------------------------------------------------------------

    private async void PurgeSelected()
    {
        // Selected items live on the tree leaves; each leaf node keeps the
        // wrapped item's IsSelected in sync with its IsChecked, so we can
        // iterate the flat Items list to find what to purge.
        var selected = Categories
            .SelectMany(c => c.GetCheckedItems())
            .Distinct()
            .ToList();
        if (selected.Count == 0) return;

        IsPurging = true;
        SummaryText = "Purging...";
        PurgeStatusText = "";
        LastCleanupResultText = "";

        try
        {
            // Snapshot data needed by the background thread before leaving
            // the UI thread.  MatchingSourcePaths, ExcessVersionRecords,
            // DiscFilePaths, and DirectoryPath are plain properties, safe
            // to capture.
            var workItems = selected.Select(item => new
            {
                item.DirectoryPath,
                item.Reason,
                item.MatchingSourcePaths,
                item.ExcessVersionRecords,
                item.DiscFilePaths,
            }).ToList();

            int backupSetId = _backupSet.Id;
            string? targetDir = _targetDir;
            var progress = new Progress<string>(status => PurgeStatusText = status);

            // Run all DB work + disk deletion on a background thread — the
            // catalog methods are synchronous (ExecuteNonQuery /
            // Task.FromResult) and the filesystem walk for empty-dir
            // cleanup can iterate thousands of directories.
            var (catalogPurged, filesDeleted, deleteFailures, bytesFreed) = await Task.Run(() =>
            {
                int catPurged = 0;
                int fDeleted = 0;
                int fFailed = 0;
                long bytes = 0;

                // Shared throttle for both progress phases — matches the
                // pattern used by every other long-running operation in
                // the codebase (seed, scan, etc.).  Without this, large
                // purges (tens or hundreds of thousands of work items)
                // flood the UI thread with one Report() per iteration and
                // visibly slow the work down — the user observed exactly
                // this.  Negative initial value guarantees the first
                // Report fires immediately.
                var progressSw = System.Diagnostics.Stopwatch.StartNew();
                long lastProgressMs = -ProgressUpdateIntervalMs;

                // -- 1. Catalog updates inside a single transaction. --
                var tx = _catalog.BeginTransactionAsync(backupSetId).GetAwaiter().GetResult();
                try
                {
                    for (int i = 0; i < workItems.Count; i++)
                    {
                        var wi = workItems[i];

                        long nowMs = progressSw.ElapsedMilliseconds;
                        // Always report the very last item so the user sees
                        // a final "N/N" before the disk-delete phase takes
                        // over; throttle every intermediate update.
                        if (nowMs - lastProgressMs >= ProgressUpdateIntervalMs
                            || i == workItems.Count - 1)
                        {
                            lastProgressMs = nowMs;
                            var dirName = Path.GetFileName(wi.DirectoryPath.TrimEnd('\\'));
                            ((IProgress<string>)progress).Report(
                                $"Updating catalog {i + 1:N0}/{workItems.Count:N0}: {dirName}");
                        }

                        if (wi.Reason is OrphanedReason.UntrackedFile)
                        {
                            // No catalog record exists for untracked files;
                            // nothing to purge from the catalog.  Physical
                            // deletion happens in the next phase.
                            continue;
                        }

                        if (wi.Reason is OrphanedReason.CatalogDeleted)
                        {
                            // Catalog already marks these as deleted — no DB
                            // work needed, only the disk-delete pass.
                            continue;
                        }

                        if ((wi.Reason == OrphanedReason.ExcessVersion
                             || wi.Reason == OrphanedReason.CatalogDuplicate)
                            && wi.ExcessVersionRecords is not null)
                        {
                            foreach (var record in wi.ExcessVersionRecords)
                            {
                                record.IsDeleted = true;
                                _catalog.UpdateFileRecordAsync(record).GetAwaiter().GetResult();
                                catPurged++;
                            }
                        }
                        else if (wi.Reason is OrphanedReason.MatchesExclusionPattern
                                           or OrphanedReason.MatchesConfiguredExclusion
                            && wi.MatchingSourcePaths is not null)
                        {
                            catPurged += _catalog.MarkFilesDeletedBySourcePathsAsync(
                                backupSetId, wi.MatchingSourcePaths).GetAwaiter().GetResult();
                        }
                        else
                        {
                            catPurged += _catalog.MarkFilesDeletedByDirectoryAsync(
                                backupSetId, wi.DirectoryPath).GetAwaiter().GetResult();
                        }
                    }

                    tx.Complete();
                }
                finally
                {
                    tx.Dispose();
                }

                // -- 2. Physical file deletion (outside the catalog tx so
                //       failures don't roll the catalog back).  Shared with the
                //       post-edit "remove deleted sources" flow via
                //       DestinationFilePurger so both behave identically. --
                if (targetDir is not null)
                {
                    // Collect every disc path to delete, deduplicated so a
                    // single file shared between categories isn't deleted
                    // (or counted) twice.
                    var allDiscPaths = new HashSet<string>(
                        workItems.Where(w => w.DiscFilePaths is not null)
                                 .SelectMany(w => w.DiscFilePaths!),
                        StringComparer.OrdinalIgnoreCase);

                    var (deleted, delFailed, delBytes) =
                        Services.DestinationFilePurger.DeleteFilesAndSweep(
                            targetDir, allDiscPaths, progress);
                    fDeleted += deleted;
                    fFailed += delFailed;
                    bytes += delBytes;
                }

                // -- 3. Drop the purged files from the in-memory active-file
                //       list so subsequent classification phases don't see
                //       them. --
                if (_activeFiles is not null)
                {
                    var purgedPaths = new HashSet<string>(
                        workItems.Where(w => w.MatchingSourcePaths is not null)
                                 .SelectMany(w => w.MatchingSourcePaths!),
                        StringComparer.OrdinalIgnoreCase);
                    var purgedDirs = workItems
                        .Where(w => w.Reason is OrphanedReason.RemovedFromSources or OrphanedReason.DeletedFromDisk)
                        .Select(w => w.DirectoryPath)
                        .ToList();
                    var purgedRecordIds = new HashSet<long>(
                        workItems.Where(w => w.ExcessVersionRecords is not null)
                                 .SelectMany(w => w.ExcessVersionRecords!)
                                 .Select(r => r.Id));

                    _activeFiles.RemoveAll(f =>
                        purgedPaths.Contains(f.SourcePath)
                        || purgedDirs.Any(d => IsPathUnderRoot(f.SourcePath, d))
                        || purgedRecordIds.Contains(f.Id));
                }

                return (catPurged, fDeleted, fFailed, bytes);
            });

            // Back on the UI thread — update the observable collection.
            foreach (var item in selected)
                Items.Remove(item);

            RebuildCategories();

            // Compose a summary that reflects both halves of the cleanup.
            var summary = new List<string>();
            if (catalogPurged > 0)
                summary.Add($"purged {catalogPurged:N0} catalog record{(catalogPurged == 1 ? "" : "s")}");
            if (filesDeleted > 0)
                summary.Add($"deleted {filesDeleted:N0} file{(filesDeleted == 1 ? "" : "s")} ({FormatSizeText(bytesFreed)})");
            if (deleteFailures > 0)
                summary.Add($"{deleteFailures:N0} deletion failure{(deleteFailures == 1 ? "" : "s")}");
            if (summary.Count == 0)
                summary.Add("nothing to clean");

            string composed = char.ToUpperInvariant(summary[0][0]) + summary[0][1..]
                + (summary.Count > 1 ? ", " + string.Join(", ", summary.Skip(1)) : "")
                + $". {Items.Count} item{(Items.Count == 1 ? "" : "s")} remaining.";

            SummaryText = composed;

            // Stamp the persistent result line with a timestamp so users
            // know which cleanup the message refers to if they come back
            // later and run other actions in the meantime.
            LastCleanupResultText =
                $"Last cleanup at {DateTime.Now:HH:mm:ss}: {composed}";
        }
        catch (Exception ex)
        {
            SummaryText = $"Cleanup failed: {ex.Message}";
            LastCleanupResultText =
                $"Last cleanup at {DateTime.Now:HH:mm:ss} failed: {ex.Message}";
        }
        finally
        {
            PurgeStatusText = "";
            IsPurging = false;
        }
    }

    // ------------------------------------------------------------------
    // Catalog reconcile (dry-run analyze, then explicit apply)
    // ------------------------------------------------------------------

    /// <summary>
    /// Dry run: walk the catalog against the destination and report how many
    /// stale <c>.fileref</c> rows would be flipped to plain and how many
    /// active rows point at content that is missing. Mutates nothing; the
    /// result is held for a subsequent <see cref="ReconcileApplyAsync"/>.
    /// </summary>
    private async Task ReconcileAnalyzeAsync()
    {
        if (_targetDir is null || IsReconciling || IsPurging)
            return;

        IsReconciling = true;
        _reconcileReport = null;
        ReconcileStatusText = "Analyzing catalog against destination...";

        try
        {
            var progress = new Progress<string>(msg => ReconcileStatusText = msg);
            var report = await Task.Run(() =>
                _reconcile.AnalyzeAsync(_backupSet.Id, _targetDir, progress));

            _reconcileReport = report;

            long flipBytes = report.Flips.Sum(f => f.SizeBytes);
            long pruneBytes = report.Prunes.Sum(p => p.SizeBytes);

            if (!report.HasChanges)
            {
                ReconcileStatusText = report.TargetPresent
                    ? $"Catalog is consistent — examined {report.RecordsExamined:N0} records, nothing to reconcile."
                    : $"Destination not found or empty — examined {report.RecordsExamined:N0} records, "
                      + "no stale references to flip (pruning of missing rows was skipped for safety).";
            }
            else
            {
                var parts = new List<string>();
                if (report.Flips.Count > 0)
                    parts.Add($"{report.Flips.Count:N0} stale reference{(report.Flips.Count == 1 ? "" : "s")} "
                              + $"to flip to plain ({flipBytes:N0} bytes)");
                if (report.Prunes.Count > 0)
                    parts.Add($"{report.Prunes.Count:N0} missing row{(report.Prunes.Count == 1 ? "" : "s")} "
                              + $"to prune ({pruneBytes:N0} bytes)");

                string suffix = report.TargetPresent
                    ? ""
                    : " (destination absent/empty — prune skipped; only reference flips shown).";
                ReconcileStatusText =
                    $"Found {string.Join(" and ", parts)}. Review, then click Apply.{suffix}";
            }
        }
        catch (Exception ex)
        {
            _reconcileReport = null;
            ReconcileStatusText = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsReconciling = false;
        }
    }

    /// <summary>
    /// Apply the changes from the last <see cref="ReconcileAnalyzeAsync"/>. Each
    /// change is re-verified against the current destination before commit, so a
    /// file reappearing or a drive reconnecting can only skip a change, never
    /// destroy data.
    /// </summary>
    private async Task ReconcileApplyAsync()
    {
        var report = _reconcileReport;
        if (_targetDir is null || report is null || !report.HasChanges || IsReconciling || IsPurging)
            return;

        IsReconciling = true;
        ReconcileStatusText = "Applying reconcile...";

        try
        {
            var progress = new Progress<string>(msg => ReconcileStatusText = msg);
            var result = await Task.Run(() =>
                _reconcile.ApplyAsync(_backupSet.Id, report, _targetDir, progress));

            _reconcileReport = null;

            var parts = new List<string>();
            if (result.Flipped > 0)
                parts.Add($"flipped {result.Flipped:N0} reference{(result.Flipped == 1 ? "" : "s")} to plain");
            if (result.Pruned > 0)
                parts.Add($"pruned {result.Pruned:N0} missing row{(result.Pruned == 1 ? "" : "s")}");
            if (result.Skipped > 0)
                parts.Add($"{result.Skipped:N0} skipped (changed since analysis)");

            ReconcileStatusText = parts.Count == 0
                ? "Reconcile applied — no changes were needed."
                : $"Reconcile applied at {DateTime.Now:HH:mm:ss}: {string.Join(", ", parts)}. "
                  + "Re-run Analyze to confirm the catalog is now clean.";
        }
        catch (Exception ex)
        {
            ReconcileStatusText = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsReconciling = false;
        }
    }


    /// <summary>
    /// Size formatter for purge-summary strings. Reports a raw byte count (no
    /// KB/MB/GB) so every size shown in the Cleanup view is consistent.
    /// </summary>
    private static string FormatSizeText(long bytes) => $"{bytes:N0} bytes";

    // ------------------------------------------------------------------

    private void UpdateSummaryText()
    {
        if (Items.Count == 0)
        {
            SummaryText = "Nothing to clean up — the catalog and destination are consistent.";
            return;
        }

        int orphanedCount = Items.Count(i => i.Reason is OrphanedReason.RemovedFromSources or OrphanedReason.DeletedFromDisk);
        int excludedCount = Items.Count(i => i.Reason is OrphanedReason.MatchesExclusionPattern or OrphanedReason.MatchesConfiguredExclusion);
        int excessCount = Items.Count(i => i.Reason == OrphanedReason.ExcessVersion);
        int untrackedCount = Items.Count(i => i.Reason == OrphanedReason.UntrackedFile);
        int catalogDeletedCount = Items.Count(i => i.Reason == OrphanedReason.CatalogDeleted);
        int duplicateCount = Items.Count(i => i.Reason == OrphanedReason.CatalogDuplicate);

        var parts = new List<string>();
        if (orphanedCount > 0)
            parts.Add($"{orphanedCount} orphaned director{(orphanedCount == 1 ? "y" : "ies")}");
        if (excludedCount > 0)
        {
            int totalExcludedFiles = Items
                .Where(i => i.Reason is OrphanedReason.MatchesExclusionPattern or OrphanedReason.MatchesConfiguredExclusion)
                .Sum(i => i.FileCount);
            parts.Add($"{totalExcludedFiles:N0} excluded file{(totalExcludedFiles == 1 ? "" : "s")} in {excludedCount} director{(excludedCount == 1 ? "y" : "ies")}");
        }
        if (excessCount > 0)
        {
            int totalExcessVersions = Items
                .Where(i => i.Reason == OrphanedReason.ExcessVersion)
                .Sum(i => i.FileCount);
            parts.Add($"{totalExcessVersions:N0} excess version{(totalExcessVersions == 1 ? "" : "s")} in {excessCount} director{(excessCount == 1 ? "y" : "ies")}");
        }
        if (untrackedCount > 0)
        {
            int totalUntracked = Items
                .Where(i => i.Reason == OrphanedReason.UntrackedFile)
                .Sum(i => i.FileCount);
            parts.Add($"{totalUntracked:N0} untracked file{(totalUntracked == 1 ? "" : "s")}");
        }
        if (catalogDeletedCount > 0)
        {
            int totalCatalogDeleted = Items
                .Where(i => i.Reason == OrphanedReason.CatalogDeleted)
                .Sum(i => i.FileCount);
            parts.Add($"{totalCatalogDeleted:N0} catalog-deleted file{(totalCatalogDeleted == 1 ? "" : "s")}");
        }
        if (duplicateCount > 0)
        {
            int totalDuplicates = Items
                .Where(i => i.Reason == OrphanedReason.CatalogDuplicate)
                .Sum(i => i.FileCount);
            parts.Add($"{totalDuplicates:N0} duplicate catalog row{(totalDuplicates == 1 ? "" : "s")} in {duplicateCount} director{(duplicateCount == 1 ? "y" : "ies")}");
        }

        SummaryText = string.Join(", ", parts) + " found.";
    }

    /// <summary>
    /// Returns true when <paramref name="dirPath"/> is currently covered by
    /// the backup set's source selection.  Uses the rich
    /// <see cref="BackupSet.SourceSelections"/> tree when present so that
    /// subdirectories the user explicitly deselected (e.g.
    /// <c>C:\AdobeTemp</c> under a <c>C:\</c> root) are correctly treated as
    /// out-of-sources; falls back to the flat <see cref="BackupSet.SourceRoots"/>
    /// list for legacy backup sets that have no selection tree.
    /// </summary>
    private bool IsDirectoryInSources(string dirPath)
    {
        var selections = _backupSet.SourceSelections;
        if (selections is { Count: > 0 })
        {
            foreach (var root in selections)
            {
                if (IsPathUnderRoot(dirPath, root.Path))
                    return SelectionCoversPath(root, dirPath);
            }
            return false;
        }

        return _backupSet.SourceRoots.Any(root => IsPathUnderRoot(dirPath, root));
    }

    /// <summary>
    /// Recursive descent that mirrors <c>FileScanner.ScanNode</c>'s inclusion
    /// rules: a node with <c>IsSelected == false</c> excludes its entire
    /// subtree; a node with <c>true</c> includes everything (unless an
    /// explicitly-listed child overrides); <c>null</c> means partial — only
    /// children with explicit decisions cover their subtrees.
    /// </summary>
    private static bool SelectionCoversPath(SourceSelection node, string targetPath)
    {
        if (node.IsSelected == false)
            return false;

        if (string.Equals(node.Path, targetPath, StringComparison.OrdinalIgnoreCase))
            return node.IsSelected != false;

        // Look for a child that covers (or equals) the target path.
        foreach (var child in node.Children)
        {
            if (IsPathUnderRoot(targetPath, child.Path))
                return SelectionCoversPath(child, targetPath);
        }

        // No child explicitly covers targetPath.  A fully-selected directory
        // with AutoIncludeNewSubdirectories=true picks up unlisted descendants;
        // a partially-selected (null) or auto-include-off node does not.
        return node.IsSelected == true
            && node.IsDirectory
            && node.AutoIncludeNewSubdirectories;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is the same as
    /// <paramref name="root"/> or a descendant of it. Comparison is case-
    /// insensitive and respects path-separator boundaries, so
    /// <c>D:\caitlin's files Backup</c> is NOT treated as being under
    /// <c>D:\caitlin's files</c>.
    /// </summary>
    private static bool IsPathUnderRoot(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;
        string rootWithSep = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Classification progress reporter
    // ------------------------------------------------------------------

    /// <summary>
    /// Thread-safe progress counter shared between the background classifier
    /// (which bumps via <see cref="Interlocked"/>) and a UI-thread
    /// <see cref="DispatcherTimer"/> that polls the latest snapshot to
    /// update <see cref="SummaryText"/>.  Avoids cross-thread marshaling
    /// per file at the cost of slightly coarse update granularity.
    /// </summary>
    private sealed class ClassifyProgress
    {
        private readonly object _phaseLock = new();
        private string _phase = "";
        private int _done;
        private int _total;

        public void SetPhase(string name, int total)
        {
            lock (_phaseLock) _phase = name;
            System.Threading.Interlocked.Exchange(ref _total, total);
            System.Threading.Interlocked.Exchange(ref _done, 0);
        }

        public void Bump() => System.Threading.Interlocked.Increment(ref _done);

        public void Bump(int n) => System.Threading.Interlocked.Add(ref _done, n);

        public void SetDone(int value) =>
            System.Threading.Interlocked.Exchange(ref _done, value);

        public (string Phase, int Done, int Total) Snapshot()
        {
            string phase;
            lock (_phaseLock) phase = _phase;
            return (phase, _done, _total);
        }
    }

    private static string FormatProgress((string Phase, int Done, int Total) snap)
    {
        if (string.IsNullOrEmpty(snap.Phase))
            return "Classifying...";
        if (snap.Total <= 0)
            // Unknown total (e.g. the DB read): show the running count once we
            // have one so the phase still visibly ticks rather than sitting on
            // a static "...".
            return snap.Done > 0 ? $"{snap.Phase} ({snap.Done:N0})..." : $"{snap.Phase}...";
        int done = Math.Min(snap.Done, snap.Total);
        return $"{snap.Phase} ({done:N0} of {snap.Total:N0})...";
    }

    /// <summary>
    /// Minimal synchronous <see cref="IProgress{T}"/> that invokes its callback
    /// on the reporting thread, unlike <see cref="Progress{T}"/> which marshals
    /// through a captured <see cref="SynchronizationContext"/> (and, with none,
    /// hops through the thread pool — reordering monotonic counts). The catalog
    /// read already runs on a background thread and only bumps a thread-safe
    /// counter, so a direct, in-order call is both cheaper and correct.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static List<string> ParsePatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return input
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

// ------------------------------------------------------------------

/// <summary>
/// One file shown as a leaf row under its parent directory in the cleanup
/// tree.  <see cref="DisplayName"/> is what the user sees (e.g. just the
/// filename, or the filename plus a version stamp for excess versions);
/// <see cref="Path"/> is the actual full source path used for tooltips.
/// </summary>
public sealed record OrphanedFileInfo(string DisplayName, string Path, long Size);

public enum OrphanedReason
{
    /// <summary>The directory still exists on disk but is no longer in the backup sources.</summary>
    RemovedFromSources,

    /// <summary>The directory no longer exists on disk.</summary>
    DeletedFromDisk,

    /// <summary>Files in this directory match a user-typed exclusion pattern (manual scan).</summary>
    MatchesExclusionPattern,

    /// <summary>Files in this directory match the backup set's configured exclusion rules
    /// (global excluded extensions or tier sets with 0 tiers).</summary>
    MatchesConfiguredExclusion,

    /// <summary>File versions that exceed the configured retention tier limits.</summary>
    ExcessVersion,

    /// <summary>
    /// Stray FileRecord pointing at a CURRENT disc location whose SourcePath
    /// is shared by at least one other non-deleted record at the same kind
    /// of location.  Almost always the result of running "Seed from
    /// existing backup" more than once on the same destination before the
    /// seed became idempotent — there's only one physical file on disk, so
    /// the extra catalog rows are dead weight.  Cleaning these does NOT
    /// touch the physical file.
    ///
    /// Purely catalog-derived (like the categories above): detected during the
    /// initial catalog analysis, not the optional destination scan.  Ordered
    /// here so its card groups with the other catalog-scan categories rather
    /// than the destination-scan ones below.
    /// </summary>
    CatalogDuplicate,

    /// <summary>
    /// File found in the backup destination directory that is not tracked by
    /// the catalog at all (e.g. leftover from a previous backup tool, or a
    /// file the catalog forgot about).  Discovered by the optional
    /// "Scan destination filesystem" pass.
    /// </summary>
    UntrackedFile,

    /// <summary>
    /// File marked <see cref="FileRecord.IsDeleted"/> in the catalog but still
    /// physically present in the destination directory.  Discovered by the
    /// optional "Scan destination filesystem" pass.
    /// </summary>
    CatalogDeleted,
}

/// <summary>Column used to sort the cleanup-view tree(s).</summary>
public enum CleanupSortColumn
{
    Name,
    Files,
    Size,
}

/// <summary>
/// One orphaned directory entry in the list.
/// </summary>
public class OrphanedDirectoryItem : ViewModelBase
{
    // Defaults to checked so that newly-classified items appear pre-selected
    // (matching every other node in the tree, which also default to checked).
    // This is also what the OrphanedNodeViewModel constructor expects:
    // it mirrors the wrapped item's IsSelected into its own IsChecked, so a
    // false default would leave every leaf node unchecked even though its
    // interior-directory ancestors are checked — and Purge Selected would
    // silently skip those leaves.
    private bool _isSelected = true;

    public string DirectoryPath { get; set; } = string.Empty;
    public OrphanedReason Reason { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// For <see cref="OrphanedReason.MatchesExclusionPattern"/> and
    /// <see cref="OrphanedReason.MatchesConfiguredExclusion"/> items, the specific
    /// source paths of matching files.  Used for targeted purging (instead of
    /// deleting all files under the directory).
    /// </summary>
    public List<string>? MatchingSourcePaths { get; set; }

    /// <summary>
    /// For <see cref="OrphanedReason.ExcessVersion"/> items, the specific
    /// file records (individual versions) that exceed the retention tier limits.
    /// Used for targeted purging of individual version records.
    /// </summary>
    public List<FileRecord>? ExcessVersionRecords { get; set; }

    /// <summary>
    /// Per-file display rows shown as leaf children under this directory in
    /// the tree.  Populated by every classification phase so the user can
    /// see exactly which files would be affected without having to guess
    /// from the file count.  This is display-only — purge logic still
    /// operates at the directory / record / source-path granularity
    /// described above.
    /// </summary>
    public List<OrphanedFileInfo>? Files { get; set; }

    /// <summary>
    /// Disc-relative paths (e.g. <c>C\Users\foo\file.txt</c>) of physical
    /// files in the backup destination directory that should be removed
    /// from disk as part of the cleanup action.  Populated:
    ///   • For source-side categories — from the matching file records'
    ///     <see cref="FileRecord.DiscPath"/> values, so cleaning a catalog
    ///     entry also removes the corresponding destination file.
    ///   • For <see cref="OrphanedReason.UntrackedFile"/> and
    ///     <see cref="OrphanedReason.CatalogDeleted"/> — from the
    ///     destination-filesystem scan, since those categories have no
    ///     catalog record (untracked) or only stale ones (catalog-deleted).
    /// Null when the backup set has no target directory configured.
    /// </summary>
    public List<string>? DiscFilePaths { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string ReasonText => Reason switch
    {
        OrphanedReason.RemovedFromSources => "Removed from sources",
        OrphanedReason.DeletedFromDisk => "Deleted from disk",
        OrphanedReason.MatchesExclusionPattern => "Matches exclusion pattern",
        OrphanedReason.MatchesConfiguredExclusion => "Matches configured exclusion",
        OrphanedReason.ExcessVersion => "Excess version (retention)",
        _ => "Unknown",
    };

    public string SizeText => FormatBytes(TotalSizeBytes);

    private static string FormatBytes(long bytes) => $"{bytes:N0}";
}
