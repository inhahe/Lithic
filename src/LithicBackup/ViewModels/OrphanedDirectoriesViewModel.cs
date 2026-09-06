using System.Collections.Concurrent;
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

    /// <summary>
    /// Whether <see cref="_targetDir"/> was reachable at the last check.  This
    /// is CACHED rather than tested on demand because it is read from command
    /// CanExecute predicates, which WPF re-evaluates on every
    /// <c>CommandManager.RequerySuggested</c> — testing the filesystem there
    /// would put disk I/O (and, for a disconnected network target, a multi-second
    /// stall) on the UI thread many times a second.  Refreshed by
    /// <see cref="RefreshDestinationAvailability"/> at the start of every action
    /// that needs the destination, which is the moment the answer has to be right.
    /// </summary>
    private bool _destinationAvailable;
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
        RefreshDestinationAvailability();

        Items = [];
        Categories = [];
        PurgeSelectedCommand = new RelayCommand(
            _ => PurgeSelected(),
            _ => !IsPurging && Categories.Any(c => c.HasCheckedItems));
        ScanCatalogCommand = new RelayCommand(
            _ => _ = LoadAsync(),
            _ => !IsLoading && !IsPurging);
        ScanExcludedCommand = new RelayCommand(_ => _ = ScanForExcludedAsync(), _ => !IsLoading && !IsPurging);
        ScanDestinationCommand = new RelayCommand(
            _ => _ = ScanDestinationAsync(),
            _ => !IsLoading && !IsPurging && !IsScanningDestination && _destinationAvailable);
        SortByNameCommand = new RelayCommand(_ => ToggleSort(CleanupSortColumn.Name));
        SortByFilesCommand = new RelayCommand(_ => ToggleSort(CleanupSortColumn.Files));
        SortBySizeCommand = new RelayCommand(_ => ToggleSort(CleanupSortColumn.Size));
        ReconcileAnalyzeCommand = new RelayCommand(
            _ => _ = ReconcileAnalyzeAsync(),
            _ => !IsLoading && !IsPurging && !IsReconciling && _destinationAvailable);
        ReconcileApplyCommand = new RelayCommand(
            _ => _ = ReconcileApplyAsync(),
            _ => !IsLoading && !IsPurging && !IsReconciling
                 && _destinationAvailable && _reconcileReport?.HasChanges == true);
        CloseCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        // The catalog classification (read every record + group/classify into
        // the removed/deleted/excluded/excess-version categories) is a
        // synchronous multi-second SQLite scan on large sets.  It is NOT needed
        // when the user only wants to scan the destination filesystem or
        // reconcile, so it is now an explicit action (ScanCatalogCommand)
        // rather than an automatic load on open.  Destination scan and reconcile
        // load whatever catalog data they need themselves; the manual
        // exclusion-pattern scan lazily runs the catalog load first.
        SummaryText = "Click \u201CScan catalog\u201D to classify catalog records "
            + "(removed from sources, deleted from disk, configured exclusions, "
            + "excess versions). You can also scan the destination filesystem or "
            + "reconcile below without scanning the catalog first.";
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

    /// <summary>
    /// True when the backup set has a target directory AND that directory is
    /// currently reachable, so destination scanning is possible.
    /// </summary>
    public bool CanScanDestination => _destinationAvailable;

    /// <summary>
    /// True when the set has a destination CONFIGURED, regardless of whether it
    /// is reachable. Drives the visibility of the destination-dependent cards:
    /// they must stay on screen (and simply not work) when the drive is
    /// unplugged, rather than vanishing and leaving the user to wonder where the
    /// destination scan went.
    /// </summary>
    public bool HasDestination => _targetDir is not null;

    /// <summary>
    /// True when the set has a destination configured but it isn't reachable —
    /// the removable drive is unplugged, the network share is down, or the drive
    /// letter has changed. Drives the warning banner.
    /// </summary>
    public bool IsDestinationUnavailable => _targetDir is not null && !_destinationAvailable;

    /// <summary>Banner text naming the destination that could not be reached.</summary>
    public string DestinationUnavailableText =>
        $"Destination not connected: {_targetDir}  —  nothing can be deleted from the " +
        "backup and the destination cannot be scanned until it is available.";

    /// <summary>
    /// Re-test whether the destination is reachable and republish the properties
    /// that depend on it.
    ///
    /// Called at the start of every action that needs the destination rather
    /// than from a property getter, because the answer can change while the
    /// dialog is open (this is a removable drive) and because a getter is the
    /// wrong place for I/O — see <see cref="_destinationAvailable"/>.
    ///
    /// <para><b>Why this exists at all:</b> the destination used to be treated
    /// as present whenever the backup set merely had one CONFIGURED, since
    /// <c>_targetDir</c> is just the saved path string. With the drive
    /// unplugged, a purge would mark catalog rows deleted, then find
    /// <c>File.Exists</c> false for every one of them, delete nothing, count
    /// zero failures, and report success. That is worse than doing nothing: the
    /// rows are now deleted rows, so the catalog-side categories (which only
    /// classify ACTIVE files) will never list those files again, and the only
    /// thing that can still find them is the destination scan's
    /// "catalog-deleted" category.</para>
    /// </summary>
    private void RefreshDestinationAvailability()
    {
        bool available = Services.DestinationFilePurger.IsAvailable(_targetDir);

        if (available == _destinationAvailable)
            return;

        _destinationAvailable = available;
        OnPropertyChanged(nameof(CanScanDestination));
        OnPropertyChanged(nameof(IsDestinationUnavailable));
        OnPropertyChanged(nameof(DestinationUnavailableText));
        CommandManager.InvalidateRequerySuggested();
    }

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

    /// <summary>
    /// True once <see cref="ReconcileAnalyzeAsync"/> has produced a dry-run
    /// report with pending changes. The Apply button is hidden until then, so
    /// it only appears after Analyze surfaces something to apply (mirroring the
    /// other cards, whose action buttons/results appear only after a scan).
    /// </summary>
    public bool CanApplyReconcile => _reconcileReport?.HasChanges == true;

    /// <summary>Assigns the reconcile report and notifies the Apply button's
    /// enabled/visible state.</summary>
    private void SetReconcileReport(ReconcileReport? report)
    {
        _reconcileReport = report;
        OnPropertyChanged(nameof(CanApplyReconcile));
    }

    public ICommand PurgeSelectedCommand { get; }
    public ICommand ScanCatalogCommand { get; }
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

        // Fresh volume probes each pass: a drive can be plugged in between scans.
        _rootAvailability.Clear();

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
        //   3. DeletedFromDisk          (the source file is gone: either its directory
        //                                no longer exists, or the directory is still there
        //                                but the file itself has been moved or deleted)
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
                    // The exact paths this item is offering, so the purge marks
                    // THESE rows and not everything sharing the directory prefix.
                    MatchingSourcePaths = byPath.Select(g => g.Key).ToList(),
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

        // Keep every removed directory as its own item (no collapsing into the
        // highest ancestor).  Collapsing merged all descendant files into one
        // flat Files list on the top ancestor, so BuildTree could only render a
        // single node with every file dumped in as a flat list — losing the
        // directory hierarchy that all the other categories show.  With one item
        // per directory, BuildTree nests files under their real subdirectories.
        // Purge coverage is unchanged: checking a parent node cascades to all
        // descendant items, and each item still carries its own DiscFilePaths.
        var collapsedRemoved = removedDirs;

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

        progress.SetPhase("Checking deleted files and directories", totalFiles);
        var deletedDirs = new List<OrphanedDirectoryItem>();
        _unavailableRoots.Clear();
        _rootAvailability.Clear();

        // Narrow to the groups phases 1-2 have not already claimed, so the
        // expensive probe below runs over as few directories as possible.
        var candidates = new List<IGrouping<string, FileRecord>>();
        foreach (var group in dirGroups)
        {
            string dir = group.Key;
            int groupCount = group.Count();
            if (collapsedRemovedPaths.Any(p => IsPathUnderRoot(dir, p))
                || !IsDirectoryInSources(dir)
                // UNREACHABLE IS NOT DELETED. A drive that is merely unplugged
                // answers "missing" for every path on it, so without this an
                // unmounted source volume would present its ENTIRE contents as
                // deleted-from-disk, and cleaning that would delete live backups
                // of files that still exist. Each root is probed once - serially,
                // before the parallel pass, so the bookkeeping needs no locking -
                // and an unavailable one is skipped wholesale and reported to the
                // user. Same rule CacheMaintenance already applies to its sweep;
                // see design.md.
                || !IsRootAvailable(dir))
            {
                progress.Bump(groupCount);
                continue;
            }
            candidates.Add(group);
        }

        // Probe the surviving directories CONCURRENTLY.
        //
        // This is pure disk latency, not work: a cold directory open measured
        // 58.6 ms on this machine's volumes, and 139,470 of them serially is over
        // two hours. Sixteen at a time is the figure CacheMaintenance arrived at
        // by measurement (235 probes/s against 21 single-threaded); more makes
        // the drive unresponsive to everything else for no real gain.
        //
        // Reading the NAMES costs almost nothing on top of the open - measured
        // 64.0 ms against 58.6 ms for a bare Directory.Exists, i.e. +9% - which
        // is what makes per-file detection affordable at all. And it is needed,
        // because a directory that still exists says NOTHING about the files that
        // used to be in it: move a folder's contents elsewhere and leave the
        // folder (or one remaining subfolder) behind, and every one of those
        // files keeps an ACTIVE catalog row. The destination scan skips them as
        // properly tracked, and this phase used to skip them too because it asked
        // only Directory.Exists, so their backups sat on the destination reported
        // by no category at all. Measured on a real set: 174 files / 4.8 GB
        // stranded in a single folder that way.
        var probes = new ConcurrentDictionary<string, DirProbe>(StringComparer.OrdinalIgnoreCase);
        System.Threading.Tasks.Parallel.ForEach(
            candidates,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = DirectoryProbeParallelism,
            },
            group =>
            {
                probes[group.Key] = ProbeDirectory(group.Key);
                progress.Bump(group.Count());
            });

        // Classify from the probe results: CPU only, and in the original order so
        // the output does not shuffle between runs.
        foreach (var group in candidates)
        {
            string dir = group.Key;
            if (!probes.TryGetValue(dir, out var probe) || probe.Skip)
                continue;
            var presentNames = probe.PresentNames;

            var remaining = group
                .Where(f => !excludedPaths.Contains(f.SourcePath))
                .Where(f => IsSourceGone(f.SourcePath, presentNames))
                .ToList();
            if (remaining.Count == 0)
                continue;
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
                // CRITICAL. This item can now be a directory that still EXISTS,
                // holding a mixture of files that are gone and files that are
                // not, so the purge must mark exactly the gone ones. Marking by
                // directory prefix instead tombstoned the entire subtree: on a
                // real set that turned 79,364 genuinely-missing files into
                // 1,203,628 tombstoned rows, because one missing file under a
                // high-level directory condemned everything beneath it.
                MatchingSourcePaths = remainingByPath.Select(g => g.Key).ToList(),
                Files = remainingByPath
                    .Select(g => new OrphanedFileInfo(
                        Path.GetFileName(g.Key), g.Key, g.Sum(f => f.SizeBytes)))
                    .ToList(),
                DiscFilePaths = _targetDir is null ? null
                    : remaining.Select(f => f.DiscPath.Replace('/', '\\')).ToList(),
            });
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
    /// Detect files in the catalog that the backup would no longer store — both
    /// the set's configured exclusions (global excluded extensions + tier sets
    /// with 0 tiers) and the app's unconditional hard exclusions (its own data
    /// directory, NTFS volume metadata such as <c>$Extend</c>).
    /// </summary>
    /// <remarks>
    /// Calls <see cref="DirectoryBackupService.BuildExclusionFilter(IReadOnlyList{string}, IReadOnlyList{VersionTierSet})"/>
    /// directly. This used to be a hand-written copy of that logic and had
    /// drifted: it only ever applied the user's own patterns, so files the app
    /// hard-excludes were invisible here — which is precisely the case that
    /// matters, since a hard-excluded path (like anything under <c>C:\$Extend</c>)
    /// cannot be seen in the source treeview either, leaving the user no way at
    /// all to find its leftover destination copies. Now this dialog is the place
    /// they surface.
    /// </remarks>
    private List<OrphanedDirectoryItem> DetectExcludedFiles(
        List<OrphanedDirectoryItem> orphanedDirs,
        ClassifyProgress? progress = null)
    {
        if (_activeFiles is null) return [];

        var jobOptions = _backupSet.JobOptions;
        if (jobOptions is null) return [];

        var exclusionFilter = DirectoryBackupService.BuildExclusionFilter(
            jobOptions.ExcludedExtensions, jobOptions.TierSets);
        if (exclusionFilter is null)
            return [];

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
        // The manual exclusion scan filters the catalog's active records, so it
        // needs the catalog loaded.  With the catalog scan now on-demand, load
        // it here if the user jumped straight to this without scanning first.
        if (_activeFiles is null)
            await LoadAsync();
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
        // The destination walk loads its own catalog snapshot (below), so it no
        // longer depends on the on-demand catalog classification having run —
        // only on the set having a destination directory that is actually there.
        // Checking here as well as in CanExecute matters because the drive can be
        // unplugged while the dialog sits open; without it the walk would get as
        // far as throwing DirectoryNotFoundException and report the failure as if
        // it were an unexpected error rather than a disconnected drive.
        RefreshDestinationAvailability();
        if (_targetDir is null)
            return;
        if (!_destinationAvailable)
        {
            DestinationScanStatusText =
                $"Destination not connected: {_targetDir} — connect it and scan again.";
            return;
        }

        // Give immediate feedback the moment the button is pressed: flip the
        // busy flag (greys the Scan button via CanExecute) and show a wait
        // cursor.  The initialization below — clearing a potentially large
        // Items collection and loading every catalog record — runs on the UI
        // thread and can take a few seconds before the background walk starts,
        // so without this the button stayed enabled and the cursor normal
        // during that gap.
        IsScanningDestination = true;
        DestinationScanStatusText = "Scanning destination directory...";

        // Yield at Background priority so WPF actually renders the disabled
        // button before the work below starts.
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

            string targetDir = _targetDir;
            var progress = new Progress<string>(msg => DestinationScanStatusText = msg);
            int setId = _backupSet.Id;

            // Do the WHOLE catalog load + lookup build + destination walk off the
            // UI thread. The catalog read is a SYNCHRONOUS SQLite scan
            // (ExecuteReader + row loop) whose awaited lock completes
            // synchronously when uncontended — so `ConfigureAwait(false)` never
            // actually hops threads and, for a large set, the load and the
            // dictionary build would run on (and freeze) the UI thread. Offload
            // all three phases; only the final Items.Add marshals back.
            var (untracked, catalogDeleted, directoriesSkipped, filesScanned) = await Task.Run(async () =>
            {
                // Pull a lightweight (DiscPath, IsDeleted, SourcePath) row per
                // catalog record — INCLUDING deleted ones so we can detect the
                // CatalogDeleted category (_activeFiles excludes them by design).
                // This dedicated query skips the full 14-column record hydration
                // and, crucially, the ORDER BY sort of the general "get all
                // files" call: the sort returned no rows (so no progress) until
                // the entire set was materialised, which made a large set look
                // frozen for many minutes before the walk began. The unsorted
                // streaming read advances the counter from the first batch.
                var loadProgress = new Progress<int>(
                    n => DestinationScanStatusText = $"Loading catalog: {n:N0} records\u2026");

                // Aggregate as the rows arrive rather than materialising them.
                // The walk needs one bit per disc path (is anything still
                // active?) and, only for paths where everything is deleted, one
                // source path for display - so a list of records per path, on top
                // of a list of every row, costs about as much again as the answer.
                // Measured on a 2,810,190-row set: 1,751 MB the old way, 754 MB
                // this way, with identical verdicts on all 2,803,380 paths.
                var discPathLookup = new Dictionary<string, DestPathState>(
                    StringComparer.OrdinalIgnoreCase);

                await _catalog.ForEachDiscPathEntryAsync(
                    setId,
                    (discPath, isDeleted, sourcePath) =>
                    {
                        string normalised = discPath.Replace('/', '\\');
                        discPathLookup.TryGetValue(normalised, out var current);

                        if (!isDeleted)
                        {
                            // An active record settles the question, and drops
                            // the source path we were holding for display: it is
                            // only ever shown when nothing active remains. This
                            // is where most of the memory goes - the vast
                            // majority of paths are active.
                            discPathLookup[normalised] = new DestPathState(true, null);
                        }
                        else if (!current.HasActive && current.SourcePathWhenAllDeleted is null)
                        {
                            // First deleted record for a path not yet claimed by
                            // an active one: keep its source path in case none
                            // ever arrives.
                            discPathLookup[normalised] = new DestPathState(false, sourcePath);
                        }
                        // Otherwise the path is already answered; store nothing.
                    },
                    CancellationToken.None,
                    loadProgress).ConfigureAwait(false);

                return WalkDestination(targetDir, discPathLookup, progress);
            });

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
        }
    }

    /// <summary>
    /// Whether a catalogued source file is really gone, given the set of file
    /// names its directory was found to contain (<c>null</c> when the directory
    /// itself is gone).
    ///
    /// <para>A name missing from the directory listing is CONFIRMED with a direct
    /// <see cref="File.Exists"/> before the file is reported. The name test is
    /// what makes the scan affordable — one directory read instead of a stat per
    /// catalogued file — but it compares two strings that reached us by different
    /// routes: the listing comes from the live directory, while the catalog path
    /// may have been recorded from a USN journal record. Any disagreement in form
    /// (an 8.3 short name, a differently normalised Unicode name) would read as
    /// "missing" and put a perfectly good backup on the deletion list. The
    /// confirming stat costs one call per file ALREADY believed missing, on a
    /// directory the probe just warmed, and it removes that entire class of false
    /// positive. A directory that is itself gone needs no confirmation.</para>
    /// </summary>
    private static bool IsSourceGone(string sourcePath, HashSet<string>? presentNames)
    {
        if (presentNames is null)
            return true;    // the whole directory is gone

        if (presentNames.Contains(Path.GetFileName(sourcePath)))
            return false;

        try
        {
            return !File.Exists(sourcePath);
        }
        catch
        {
            // Cannot tell: treat as present, because the cost of being wrong in
            // that direction is one uncleaned file, and in the other direction is
            // a deleted backup.
            return false;
        }
    }

    /// <summary>
    /// How many directories to probe at once in the deleted-file check. Matches
    /// the value CacheMaintenance measured for the same kind of work (cold NTFS
    /// metadata lookups): 16 gave 235 probes/s against 21 single-threaded, and 32
    /// bought only ~20% more while making the drive unresponsive to everything
    /// else.
    /// </summary>
    private const int DirectoryProbeParallelism = 16;

    /// <summary>
    /// One directory's probe result.
    /// <list type="bullet">
    /// <item><c>Skip</c> - the directory could not be read, so nothing about it
    /// can be proven and none of its files may be offered for deletion.</item>
    /// <item><c>PresentNames == null</c> (and not skipped) - the directory itself
    /// is gone, so every catalogued file under it is gone with it.</item>
    /// <item>otherwise - the file names that ARE present, to test each catalogued
    /// file against.</item>
    /// </list>
    /// </summary>
    private readonly record struct DirProbe(bool Skip, HashSet<string>? PresentNames);

    /// <summary>
    /// Read one directory's file names, or classify it as gone/unreadable.
    /// Static and self-contained so it is safe to call from many threads at once.
    /// </summary>
    private static DirProbe ProbeDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return new DirProbe(false, null);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(dir))
                names.Add(Path.GetFileName(file));
            return new DirProbe(false, names);
        }
        catch
        {
            // Permission denied, a reparse point that will not open, a share that
            // dropped mid-walk. We cannot prove anything here is missing, and an
            // unverifiable claim must never become an offer to delete a backup.
            return new DirProbe(true, null);
        }
    }

    /// <summary>
    /// Per-root availability results for one classification pass, so each volume
    /// is probed once rather than once per directory. Keyed by path root
    /// (e.g. <c>D:\</c>).
    /// </summary>
    private readonly Dictionary<string, bool> _rootAvailability =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Roots found unavailable during the last pass, for reporting.</summary>
    private readonly List<string> _unavailableRoots = [];

    /// <summary>
    /// Whether the volume <paramref name="path"/> lives on is currently
    /// reachable. An unreachable root must never be classified: every path on it
    /// answers "missing", so an unplugged source drive would otherwise present
    /// its whole contents as deleted-from-disk, and cleaning that would delete
    /// live backups.
    /// </summary>
    private bool IsRootAvailable(string path)
    {
        string root;
        try
        {
            root = Path.GetPathRoot(path) ?? "";
        }
        catch
        {
            return false;
        }
        if (root.Length == 0)
            return false;

        if (_rootAvailability.TryGetValue(root, out bool known))
            return known;

        bool available;
        try
        {
            available = Directory.Exists(root);
        }
        catch
        {
            available = false;
        }

        _rootAvailability[root] = available;
        if (!available)
            _unavailableRoots.Add(root);
        return available;
    }

    /// <summary>
    /// What the destination walk needs to know about one disc path, ALREADY
    /// AGGREGATED across every catalog row that mentions it.
    ///
    /// <para>Holding the rows themselves would mean a <c>List</c> object per
    /// path plus a source-path string per row, for a question that reduces to a
    /// single bit for all but a handful of paths. <c>SourcePathWhenAllDeleted</c>
    /// is null whenever <c>HasActive</c> is true, so the strings for the
    /// overwhelming majority of paths become garbage as soon as they are read.
    /// See <c>ICatalogRepository.ForEachDiscPathEntryAsync</c> for the numbers.</para>
    /// </summary>
    /// <param name="HasActive">
    /// True if any record for this disc path is still active, meaning the file on
    /// disk is properly tracked and the walk should skip it.
    /// </param>
    /// <param name="SourcePathWhenAllDeleted">
    /// Source path to show for a file whose records are ALL deleted; null when
    /// <paramref name="HasActive"/> is true.
    /// </param>
    private readonly record struct DestPathState(bool HasActive, string? SourcePathWhenAllDeleted);

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
            Dictionary<string, DestPathState> discPathLookup,
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
                    if (discPathLookup.TryGetValue(relativePath, out var state)
                        || discPathLookup.TryGetValue(relativePath + ".fileref", out state)
                        || discPathLookup.TryGetValue(relativePath + ".dedup", out state))
                    {
                        if (state.HasActive)
                            continue; // Active record exists — file is properly tracked.

                        catalogDeleted.Add((relativePath, size, state.SourcePathWhenAllDeleted));
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

        // Refuse outright if the set has a destination that isn't reachable.
        //
        // Cleaning is two halves — mark the catalog rows deleted, then delete the
        // backed-up files — and only the first half can run without the drive.
        // Doing that half alone is strictly worse than doing nothing: not one byte
        // is freed, and because every catalog-side category classifies only ACTIVE
        // files, the rows it just tombstoned drop out of the catalog scan forever.
        // The files would then be findable only through the destination scan's
        // "catalog-deleted" category, which is not where anyone would think to look
        // for them. Previously this ran silently: _targetDir is only the CONFIGURED
        // path, so with the drive unplugged the purge marked the rows, found
        // File.Exists false for every path, counted zero deletions AND zero
        // failures, and reported plain success.
        RefreshDestinationAvailability();
        if (IsDestinationUnavailable)
        {
            string msg =
                $"Destination not connected ({_targetDir}) — nothing was cleaned. " +
                "Connect the drive and run the cleanup again; marking the catalog " +
                "without deleting the backed-up files would free no space and would " +
                "hide those files from the catalog scan.";
            SummaryText = msg;
            LastCleanupResultText = $"Last cleanup at {DateTime.Now:HH:mm:ss}: {msg}";
            return;
        }

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
            // Route live progress into BOTH the transient action-row line
            // (PurgeStatusText) and the prominent header subtitle
            // (SummaryText, right under the "Cleanup" title).  The header is
            // where the user's eye rests, so echoing progress there is what
            // makes the purge visibly "doing something" instead of sitting on
            // a static "Purging..." while the real updates hide in the small
            // grey action-row text.
            // Cleanup surfaces progress as text labels (not a percentage bar), so
            // it only needs the report's Text; the shared DestinationFilePurger now
            // speaks ProgressReport (text + optional percent).
            var progress = new Progress<Services.ProgressReport>(report =>
            {
                PurgeStatusText = report.Text;
                SummaryText = report.Text;
            });

            // Run all DB work + disk deletion on a background thread — the
            // catalog methods are synchronous (ExecuteNonQuery /
            // Task.FromResult) and the filesystem walk for empty-dir
            // cleanup can iterate thousands of directories.
            var (catalogPurged, filesDeleted, deleteFailures, bytesFreed, alreadyAbsent) = await Task.Run(() =>
            {
                int catPurged = 0;
                int fDeleted = 0;
                int fFailed = 0;
                int fAbsent = 0;
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
                            int pct = workItems.Count == 0
                                ? 100 : (int)((i + 1) * 100L / workItems.Count);
                            ((IProgress<Services.ProgressReport>)progress).Report(
                                $"Updating catalog {i + 1:N0}/{workItems.Count:N0} ({pct}%): {dirName}");
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
                        else if (wi.MatchingSourcePaths is not null)
                        {
                            // Mark exactly the paths this item listed. Every
                            // category that reaches here now carries its own
                            // path list, which is the only way the catalog write
                            // can be guaranteed to match what the user saw and
                            // ticked.
                            catPurged += _catalog.MarkFilesDeletedBySourcePathsAsync(
                                backupSetId, wi.MatchingSourcePaths).GetAwaiter().GetResult();
                        }
                        else
                        {
                            // FALLBACK ONLY, and a dangerous one: this marks
                            // every row under the directory prefix, RECURSIVELY,
                            // whether or not the item listed it. That was safe
                            // only while "deleted from disk" could not mean
                            // anything but "the whole directory is gone". It no
                            // longer can, so nothing should be reaching this
                            // branch; it is kept solely so an item that somehow
                            // has no path list still purges something rather
                            // than silently doing nothing. If you add a category,
                            // give it MatchingSourcePaths.
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

                    var (deleted, delFailed, delBytes, delAbsent) =
                        Services.DestinationFilePurger.DeleteFilesAndSweep(
                            targetDir, allDiscPaths, progress);
                    fDeleted += deleted;
                    fFailed += delFailed;
                    bytes += delBytes;
                    fAbsent += delAbsent;
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

                return (catPurged, fDeleted, fFailed, bytes, fAbsent);
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
            // Reported rather than ignored: a file the catalog expects but the
            // destination doesn't have is how a half-completed earlier purge — or
            // a destination that isn't really the one the catalog describes —
            // shows itself. Silence here is what let an offline purge look like a
            // successful one.
            if (alreadyAbsent > 0)
                summary.Add($"{alreadyAbsent:N0} already absent from the destination");
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

        // Reconcile compares the catalog against the destination, so an absent
        // destination would make every active row look like missing content.
        RefreshDestinationAvailability();
        if (!_destinationAvailable)
        {
            ReconcileStatusText =
                $"Destination not connected: {_targetDir} - connect it and analyze again.";
            return;
        }

        IsReconciling = true;
        SetReconcileReport(null);
        ReconcileStatusText = "Analyzing catalog against destination...";

        try
        {
            var progress = new Progress<Services.ProgressReport>(r => ReconcileStatusText = r.Text);
            var report = await Task.Run(() =>
                _reconcile.AnalyzeAsync(_backupSet.Id, _targetDir, progress));

            SetReconcileReport(report);

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
            SetReconcileReport(null);
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

        // The report was computed against a destination that may since have been
        // unplugged; applying it then would prune rows whose content is merely
        // unreachable.
        RefreshDestinationAvailability();
        if (!_destinationAvailable)
        {
            ReconcileStatusText =
                $"Destination not connected: {_targetDir} - connect it and analyze again.";
            return;
        }

        IsReconciling = true;
        ReconcileStatusText = "Applying reconcile...";

        try
        {
            var progress = new Progress<Services.ProgressReport>(r => ReconcileStatusText = r.Text);
            var result = await Task.Run(() =>
                _reconcile.ApplyAsync(_backupSet.Id, report, _targetDir, progress));

            SetReconcileReport(null);

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
        // Skipped roots are reported whether or not anything was found: a scan
        // that silently omitted an unplugged drive is a scan whose "nothing to
        // clean up" is not true of the whole set.
        string skipped = _unavailableRoots.Count == 0
            ? ""
            : $"  Skipped {_unavailableRoots.Count} unavailable source "
              + $"root{(_unavailableRoots.Count == 1 ? "" : "s")} "
              + $"({string.Join(", ", _unavailableRoots)}) — files there were not "
              + "classified, because an unreachable drive is not a deleted one.";

        if (Items.Count == 0)
        {
            SummaryText = "Nothing to clean up — the catalog and destination are consistent."
                + skipped;
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

        SummaryText = string.Join(", ", parts) + " found." + skipped;
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
    /// explicitly-listed child overrides); a <c>null</c> (partial) node covers
    /// its explicitly-decided children, plus any unlisted descendants when it
    /// auto-includes new entries (see <c>SourceSelection.IncludesUnlistedDescendants</c>).
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

        // No child explicitly covers targetPath: it's an unlisted descendant.
        // A directory that auto-includes new entries covers it — whether the
        // directory is fully or partially selected (see IncludesUnlistedDescendants).
        return SourceSelection.IncludesUnlistedDescendants(node);
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
