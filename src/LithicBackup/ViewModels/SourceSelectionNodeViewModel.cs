using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>Backup status of a file or directory relative to the catalog.</summary>
public enum BackupStatus
{
    /// <summary>No catalog data available (new backup set) or unknown.</summary>
    Unknown,
    /// <summary>File is backed up and unchanged.</summary>
    BackedUp,
    /// <summary>File is backed up but has changed since the last backup.</summary>
    Changed,
    /// <summary>File is not in the catalog.</summary>
    NotBackedUp,
    /// <summary>Directory has a mix of backed-up and not-backed-up children.</summary>
    Partial,
}

/// <summary>
/// ViewModel for a single node in the source selection treeview.
/// Implements tristate checkbox logic: parent propagates to children,
/// children propagate up to parent.
/// </summary>
public class SourceSelectionNodeViewModel : ViewModelBase
{
    private bool? _isSelected = false;
    private bool _autoIncludeNew = true;
    private bool _isExpanded;
    private bool _isLoaded;
    private bool _suppressPropagation;
    /// <summary>
    /// When true, <see cref="LoadChildrenAsync"/> skips Phase 2 (submitting
    /// child directories to the size scheduler).  Set during
    /// <see cref="ApplySelectionAsync"/> to avoid flooding the scheduler
    /// with hundreds of directories while restoring saved state.
    /// </summary>
    internal bool _suppressSizeComputation;
    private Task? _loadTask;
    /// <summary>
    /// The saved <see cref="Core.Models.SourceSelection"/> this node was
    /// restored from, captured in <see cref="ApplySelectionAsync"/>.  Used as
    /// a lossless fallback in <see cref="ToModel"/> when the directory's
    /// children could not be enumerated (drive not ready, I/O error, access
    /// denied) — without it, a transient enumeration failure would serialise
    /// an empty subtree and permanently destroy the saved selections.
    /// </summary>
    private Core.Models.SourceSelection? _restoredModel;
    /// <summary>
    /// Saved child selections whose paths were NOT present on disk when this
    /// directory's children were enumerated (e.g. a selected subfolder was
    /// renamed, moved, or temporarily disconnected).  Without preserving them,
    /// <see cref="ToModel"/> would re-derive the subtree from only the live
    /// children and silently, permanently drop the missing selection — so a
    /// folder that is later restored under its original name would no longer be
    /// backed up.  These are re-emitted verbatim by <see cref="ToModel"/> so the
    /// selection survives until the user explicitly changes it.
    /// </summary>
    private List<Core.Models.SourceSelection>? _orphanedChildModels;
    private long _size = -1;
    private int _fileCount = -1;
    /// <summary>
    /// Size accounting for the exclusion filter (0-tier tier sets, global
    /// exclusions). -1 means not yet computed.  For directories that contain
    /// no excluded content this equals <see cref="_size"/>.
    /// </summary>
    private long _filteredSize = -1;
    private int _filteredFileCount = -1;
    private BackupStatus _backupStatus = BackupStatus.Unknown;
    private readonly Func<bool>? _getShowSizes;
    private readonly Func<(SortColumn Column, bool Ascending)>? _getSortMode;
    private readonly SizeComputeScheduler? _scheduler;
    private readonly Func<bool>? _getShowSelectedOnly;
    private readonly Func<Func<string, bool>?>? _getExcludeFilter;
    private readonly Action? _onSelectionChanged;
    /// <summary>
    /// When set, a checkbox toggle defers its (potentially expensive)
    /// propagation + size-aggregation work to the owning viewmodel, which
    /// coalesces requests and runs them off the click's synchronous path so
    /// the clicked checkbox repaints immediately.  Null (e.g. in tests) means
    /// the work runs inline via <see cref="SettleSelection"/>.
    /// </summary>
    private readonly Action<SourceSelectionNodeViewModel>? _requestSelectionSettle;
    private readonly Dictionary<string, FileVersionInfo>? _catalogInfo;

    public SourceSelectionNodeViewModel(
        string path, bool isDirectory, SourceSelectionNodeViewModel? parent,
        Func<bool>? getShowSizes = null,
        Func<(SortColumn Column, bool Ascending)>? getSortMode = null,
        SizeComputeScheduler? scheduler = null, Action? onSelectionChanged = null,
        Func<bool>? getShowSelectedOnly = null,
        Dictionary<string, FileVersionInfo>? catalogInfo = null,
        Func<Func<string, bool>?>? getExcludeFilter = null,
        Action<SourceSelectionNodeViewModel>? requestSelectionSettle = null)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
            Name = string.IsNullOrEmpty(path) ? "All Drives" : path; // Drive root (e.g. "C:\")
        IsDirectory = isDirectory;
        Parent = parent;
        _getShowSizes = getShowSizes ?? parent?._getShowSizes;
        _getSortMode = getSortMode ?? parent?._getSortMode;
        _scheduler = scheduler ?? parent?._scheduler;
        _onSelectionChanged = onSelectionChanged ?? parent?._onSelectionChanged;
        _getShowSelectedOnly = getShowSelectedOnly ?? parent?._getShowSelectedOnly;
        _getExcludeFilter = getExcludeFilter ?? parent?._getExcludeFilter;
        _requestSelectionSettle = requestSelectionSettle ?? parent?._requestSelectionSettle;
        _catalogInfo = catalogInfo ?? parent?._catalogInfo;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ =>
        {
            if (IsDirectory)
                IsExpanded = !IsExpanded;
        });
        // Directories get a dummy child so the expander arrow shows.
        if (isDirectory && !_isLoaded)
            Children.Add(new SourceSelectionNodeViewModel("Loading...", false, this) { _isSelected = false });
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public SourceSelectionNodeViewModel? Parent { get; }
    public BulkObservableCollection<SourceSelectionNodeViewModel> Children { get; }

    /// <summary>Single-click on the name area toggles expand/collapse for directories.</summary>
    public ICommand ToggleExpandCommand { get; }

    /// <summary>Nesting depth (0 for root nodes). Used for indentation in the custom TreeViewItem template.</summary>
    public int Depth { get; }

    /// <summary>Whether this directory's children have been loaded from the filesystem.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        internal set => _isLoaded = value;
    }

    /// <summary>
    /// Size in bytes. For files this is the file size; for directories it is
    /// the recursive total of all contained files. -1 means not yet computed.
    /// </summary>
    public long Size
    {
        get => _size;
        internal set
        {
            if (SetProperty(ref _size, value))
            {
                OnPropertyChanged(nameof(FormattedSize));
                // In "selected only" mode, partially-selected ancestors
                // compute their displayed size from children's _size values.
                // Propagate the notification up so they re-evaluate.
                if ((_getShowSelectedOnly?.Invoke() ?? false) && Parent is not null)
                    Parent.InvalidateSelectedSize();
            }
        }
    }

    /// <summary>
    /// Total number of files. For files this is always 1; for directories it
    /// is the recursive count of all contained files. -1 means not yet computed.
    /// </summary>
    public int FileCount
    {
        get => _fileCount;
        internal set
        {
            if (SetProperty(ref _fileCount, value))
            {
                OnPropertyChanged(nameof(FormattedFileCount));
                if ((_getShowSelectedOnly?.Invoke() ?? false) && Parent is not null)
                    Parent.InvalidateSelectedFileCount();
            }
        }
    }

    /// <summary>
    /// Recursive size accounting for the exclusion filter.  Set alongside
    /// <see cref="Size"/> by the scheduler when the global filter is active.
    /// </summary>
    internal long FilteredSize
    {
        get => _filteredSize;
        set
        {
            if (_filteredSize == value) return;
            _filteredSize = value;
            OnPropertyChanged(nameof(FormattedSize));
            if ((_getShowSelectedOnly?.Invoke() ?? false) && Parent is not null)
                Parent.InvalidateSelectedSize();
        }
    }

    /// <summary>See <see cref="FilteredSize"/>.</summary>
    internal int FilteredFileCount
    {
        get => _filteredFileCount;
        set
        {
            if (_filteredFileCount == value) return;
            _filteredFileCount = value;
            OnPropertyChanged(nameof(FormattedFileCount));
            if ((_getShowSelectedOnly?.Invoke() ?? false) && Parent is not null)
                Parent.InvalidateSelectedFileCount();
        }
    }

    /// <summary>Human-readable size string (e.g. "1.2 GB"). Empty when
    /// "selected only" mode is active and this node is not selected.
    /// In "selected only" mode, partially-selected directories show the
    /// sum of their selected children rather than the full filesystem size.
    /// Shows "Working..." while any contributing child's size is unknown.</summary>
    public string FormattedSize
    {
        get
        {
            bool selectedOnly = _getShowSelectedOnly?.Invoke() ?? false;
            if (selectedOnly)
            {
                if (IsSelected == false) return "";
                var filter = _getExcludeFilter?.Invoke();
                if (IsExcludedByFilter(this, filter)) return "";

                if (IsDirectory)
                {
                    // Fully-selected directory with a filter — use the
                    // pre-computed filtered size (accounts for excluded
                    // subdirectories without requiring children to be loaded).
                    if (IsSelected == true && filter is not null)
                        return _filteredSize < 0 ? "Working..." : FormatBytes(_filteredSize);

                    // Partially-selected directory — walk loaded children
                    // to sum only selected, non-excluded nodes.
                    if (IsSelected == null && _isLoaded)
                    {
                        var total = ComputeSelectedChildrenSize();
                        return total.HasValue ? FormatBytes(total.Value) : "Working...";
                    }
                }

                return _size < 0 ? "Working..." : FormatBytes(_size);
            }
            return _size < 0 ? "Working..." : FormatBytes(_size);
        }
    }

    /// <summary>Formatted file count string (e.g. "1,234"). Empty for
    /// individual files or when the count is not yet computed.
    /// In "selected only" mode, partially-selected directories show the
    /// count of selected children only.
    /// Shows "Working..." while any contributing child's count is unknown.
    /// Drive-letter nodes whose size was set from DriveInfo (not computed
    /// recursively) show an empty string because a file count next to the
    /// drive's used-space figure is not meaningful.</summary>
    public string FormattedFileCount
    {
        get
        {
            if (!IsDirectory) return "";
            bool selectedOnly = _getShowSelectedOnly?.Invoke() ?? false;
            if (selectedOnly)
            {
                if (IsSelected == false) return "";
                var filter = _getExcludeFilter?.Invoke();
                if (IsExcludedByFilter(this, filter)) return "";

                if (IsSelected == true && filter is not null)
                    return _filteredFileCount < 0 ? "Working..." : _filteredFileCount.ToString("N0");

                if (IsSelected == null && _isLoaded)
                {
                    var total = ComputeSelectedChildrenFileCount();
                    return total.HasValue ? total.Value.ToString("N0") : "Working...";
                }
                // _size >= 0 with _fileCount < 0 means the size was set
                // externally (DriveInfo), not computed — no file count available.
                if (_fileCount < 0) return _size >= 0 ? "" : "Working...";
                return _fileCount.ToString("N0");
            }
            // Same check: drive-level nodes with DriveInfo size have no file count.
            if (_fileCount < 0) return _size >= 0 ? "" : "Working...";
            return _fileCount.ToString("N0");
        }
    }

    /// <summary>
    /// Recursively sum the sizes of selected children.
    /// Returns <c>null</c> when any contributing child's size is still unknown,
    /// signalling the caller to display "Working..." instead of a partial total.
    /// Skips nodes that match the configured exclusion filter (0-tier tier sets
    /// and global excluded extensions), since those files aren't actually backed up.
    /// </summary>
    private long? ComputeSelectedChildrenSize()
    {
        var filter = _getExcludeFilter?.Invoke();
        long total = 0;
        foreach (var child in Children)
        {
            if (child.IsSelected == false) continue;
            if (IsExcludedByFilter(child, filter)) continue;

            if (!child.IsDirectory)
            {
                if (child._size < 0) return null;
                total += child._size;
            }
            else if (child.IsSelected == true)
            {
                // Fully-selected directory — use filtered size when a
                // filter is active, unfiltered otherwise.  Return null
                // (unknown) if the needed value isn't computed yet so the
                // parent shows "Working..." consistently with the child.
                if (filter is not null)
                {
                    if (child._filteredSize < 0) return null;
                    total += child._filteredSize;
                }
                else if (child._size < 0) return null;
                else total += child._size;
            }
            else if (child._isLoaded)
            {
                // Partially-selected, loaded — recurse to skip
                // deselected and excluded children.
                var sub = child.ComputeSelectedChildrenSize();
                if (sub is null) return null;
                total += sub.Value;
            }
            else
            {
                // Not loaded, not fully selected — can't compute.
                return null;
            }
        }
        return total;
    }

    /// <summary>
    /// Recursively count files in selected children.
    /// Returns <c>null</c> when any contributing child's count is still unknown.
    /// </summary>
    private int? ComputeSelectedChildrenFileCount()
    {
        var filter = _getExcludeFilter?.Invoke();
        int total = 0;
        foreach (var child in Children)
        {
            if (child.IsSelected == false) continue;
            if (IsExcludedByFilter(child, filter)) continue;

            if (!child.IsDirectory)
            {
                total += 1;
            }
            else if (child.IsSelected == true)
            {
                if (filter is not null)
                {
                    if (child._filteredFileCount < 0) return null;
                    total += child._filteredFileCount;
                }
                else if (child._fileCount < 0) return null;
                else total += child._fileCount;
            }
            else if (child._isLoaded)
            {
                var sub = child.ComputeSelectedChildrenFileCount();
                if (sub is null) return null;
                total += sub.Value;
            }
            else
            {
                return null;
            }
        }
        return total;
    }

    /// <summary>
    /// Test whether a node is excluded by the backup exclusion filter.
    /// For directories, tests a synthetic child path (same approach as
    /// <c>DirectoryBackupService.BuildExclusionFilter</c>).
    /// For files, tests the file path directly.
    /// </summary>
    private static bool IsExcludedByFilter(
        SourceSelectionNodeViewModel node, Func<string, bool>? filter)
    {
        if (filter is null) return false;
        return node.IsDirectory
            ? filter(System.IO.Path.Combine(node.Path, "_"))
            : filter(node.Path);
    }

    /// <summary>
    /// Compute the size that would be displayed for this node in "selected only"
    /// mode, accounting for the exclusion filter.  Used as the sort key so that
    /// sort order matches the visible numbers.
    /// </summary>
    internal long GetEffectiveSize(Func<string, bool>? filter)
    {
        if (IsSelected == false) return -1;
        if (IsExcludedByFilter(this, filter)) return -1;
        if (IsDirectory && IsSelected == true && filter is not null && _filteredSize >= 0)
            return _filteredSize;
        if (IsDirectory && IsSelected == null && _isLoaded)
            return ComputeSelectedChildrenSize() ?? -1;
        return _size;
    }

    /// <summary>Backup status relative to the catalog.</summary>
    public BackupStatus BackupStatus
    {
        get => _backupStatus;
        private set => SetProperty(ref _backupStatus, value);
    }

    /// <summary>
    /// True when this node is at least partially selected (i.e. not deselected).
    /// Used to enable/disable per-node controls (tier set, auto-include).
    /// </summary>
    public bool IsNodeEnabled => _isSelected != false;

    /// <summary>
    /// Tristate: true = included, false = excluded, null = partially included.
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNodeEnabled));

            // Clear backup status for deselected files — they're not part
            // of the backup, so showing a status dot would be misleading.
            if (value == false && _backupStatus != BackupStatus.Unknown)
            {
                _backupStatus = BackupStatus.Unknown;
                OnPropertyChanged(nameof(BackupStatus));
            }

            if (_suppressPropagation)
                return;

            // The clicked checkbox's own visual state is already updated
            // (OnPropertyChanged above).  Defer the rest — pushing the state
            // down to loaded children, recomputing ancestor tristates, and the
            // whole-tree size aggregation — so the checkbox can repaint before
            // that (potentially multi-second) work runs.  The owning viewmodel
            // coalesces requests and runs them at Background priority; Save
            // waits for the pending pass.  With no viewmodel wired (tests) we
            // fall back to running it inline.
            if (_requestSelectionSettle is not null)
                _requestSelectionSettle(this);
            else
                SettleSelection();
        }
    }

    /// <summary>
    /// Push this node's current selection state down to loaded children and
    /// recompute ancestor tristates.  Split out of the <see cref="IsSelected"/>
    /// setter so the owning viewmodel can run it off the click's synchronous
    /// path (letting the clicked checkbox repaint first).  Does NOT raise the
    /// selection-changed aggregate — the caller does that once per coalesced
    /// batch (see <see cref="SettleSelection"/> and the viewmodel's settle pass).
    /// </summary>
    internal void PropagateSelection()
    {
        var value = _isSelected;

        // Propagate down: set all loaded children to the same definite state.
        if (value.HasValue && IsDirectory && _isLoaded)
        {
            foreach (var child in Children)
            {
                child._suppressPropagation = true;
                child.IsSelected = value;
                child._suppressPropagation = false;
            }
        }

        // Propagate up: recalculate parent's tristate.
        Parent?.UpdateFromChildren();
    }

    /// <summary>
    /// Inline fallback used when no viewmodel settle delegate is wired:
    /// propagate the selection and raise the aggregate notification.
    /// </summary>
    internal void SettleSelection()
    {
        PropagateSelection();
        _onSelectionChanged?.Invoke();
    }

    /// <summary>
    /// Whether new subdirectories added in the future should be automatically included.
    /// Only meaningful for directories.
    /// </summary>
    public bool AutoIncludeNew
    {
        get => _autoIncludeNew;
        set
        {
            if (!SetProperty(ref _autoIncludeNew, value))
                return;

            // Propagate down to all loaded child directories.
            if (IsDirectory && _isLoaded)
            {
                foreach (var child in Children)
                {
                    if (child.IsDirectory)
                        child.AutoIncludeNew = value;
                }
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_isLoaded && IsDirectory)
                _loadTask = LoadChildrenAsync();
        }
    }

    /// <summary>
    /// Ensure children are loaded and wait for the load to complete.
    /// Does NOT change <see cref="IsExpanded"/> — callers that want the
    /// node visually expanded must set it separately after loading.
    /// </summary>
    internal async Task EnsureChildrenLoadedAsync()
    {
        if (!IsDirectory) return;

        if (!_isLoaded)
            _loadTask = LoadChildrenAsync();

        if (_loadTask is not null)
            await _loadTask;
    }

    /// <summary>
    /// Apply a saved <see cref="Core.Models.SourceSelection"/> to this node,
    /// restoring selection state, options, expansion state, and recursing
    /// into children.
    /// </summary>
    internal async Task ApplySelectionAsync(Core.Models.SourceSelection model)
    {
        // Remember the saved subtree so ToModel can fall back to it if this
        // directory's children can't be enumerated later (see _restoredModel).
        _restoredModel = model;

        // Apply state without triggering propagation.
        _suppressPropagation = true;
        _isSelected = model.IsSelected;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsNodeEnabled));
        _suppressPropagation = false;

        _autoIncludeNew = model.AutoIncludeNewSubdirectories;
        OnPropertyChanged(nameof(AutoIncludeNew));

        // If this directory has child selections to restore, load children
        // and apply.  Suppress size computation during this phase — we're
        // restoring saved state, not responding to a user click.
        if (IsDirectory && model.Children.Count > 0)
        {
            _suppressSizeComputation = true;
            await EnsureChildrenLoadedAsync();
            _suppressSizeComputation = false;

            // Apply sibling subtrees concurrently — each child's
            // filesystem enumeration runs on the thread pool, so
            // siblings overlap instead of serialising.
            var tasks = new List<Task>(model.Children.Count);
            foreach (var childModel in model.Children)
            {
                var childNode = Children.FirstOrDefault(c =>
                    string.Equals(c.Path, childModel.Path, StringComparison.OrdinalIgnoreCase));
                if (childNode is not null)
                {
                    tasks.Add(childNode.ApplySelectionAsync(childModel));
                }
                else if (childModel.IsSelected != false)
                {
                    // The saved selection referenced a child that is no longer
                    // on disk (renamed/moved/disconnected).  Preserve it so a
                    // later save (ToModel) doesn't silently drop the selection;
                    // if the path comes back under its original name it will be
                    // backed up again.
                    (_orphanedChildModels ??= []).Add(childModel);
                }
            }
            await Task.WhenAll(tasks);
        }

        // Restore expansion state from the saved model.  Children are
        // already loaded above (if any), so setting IsExpanded here only
        // controls the visual state — it won't re-trigger LoadChildrenAsync
        // because _isLoaded is already true.
        if (IsDirectory)
        {
            if (model.IsExpanded && !_isLoaded)
            {
                // Node was expanded but had no child selections saved
                // (e.g. fully selected directory).  Load children now.
                _suppressSizeComputation = true;
                await EnsureChildrenLoadedAsync();
                _suppressSizeComputation = false;
            }
            IsExpanded = model.IsExpanded;
        }
    }

    /// <summary>
    /// Re-sort loaded children according to the current sort mode and recurse
    /// into any expanded subdirectories.
    /// </summary>
    internal void SortChildren()
    {
        if (!_isLoaded || Children.Count <= 1)
            return;

        var (column, ascending) = _getSortMode?.Invoke() ?? (SortColumn.Name, true);

        IEnumerable<SourceSelectionNodeViewModel> sorted;
        if (column == SortColumn.Size)
        {
            bool selectedOnly = _getShowSelectedOnly?.Invoke() ?? false;
            if (selectedOnly)
            {
                // Sort by the effective displayed size (accounts for
                // exclusion filters) so the visual order matches the
                // numbers the user sees.  Pre-compute once to avoid
                // repeated recursion inside the comparator.
                var filter = _getExcludeFilter?.Invoke();
                var keys = new Dictionary<SourceSelectionNodeViewModel, long>(
                    Children.Count);
                foreach (var c in Children)
                    keys[c] = c.GetEffectiveSize(filter);

                sorted = ascending
                    ? Children.OrderBy(c => keys[c])
                    : Children.OrderByDescending(c => keys[c]);
            }
            else
            {
                sorted = ascending
                    ? Children.OrderBy(c => c._size)
                    : Children.OrderByDescending(c => c._size);
            }
        }
        else
        {
            sorted = ascending
                ? Children.OrderBy(c => c.IsDirectory ? 0 : 1)
                          .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                : Children.OrderBy(c => c.IsDirectory ? 0 : 1)
                          .ThenByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase);
        }

        Children.ReplaceAll(sorted.ToList());

        // Recurse into loaded subdirectories.
        foreach (var child in Children)
        {
            if (child.IsDirectory && child._isLoaded)
                child.SortChildren();
        }
    }

    /// <summary>
    /// Lazy-load children from the filesystem when the node is first expanded.
    /// Enumerates entries and file sizes on a background thread (fast), then
    /// optionally kicks off progressive directory size computation.
    /// </summary>
    private async Task LoadChildrenAsync()
    {
        _isLoaded = true;

        // Yield at a priority below Render so the WPF layout/render pass
        // completes first — this ensures the "Loading..." placeholder is
        // painted before the filesystem enumeration begins.
        // Skip the yield during selection restore (_suppressSizeComputation)
        // because the dialog isn't visible yet — the yield would add ~16ms
        // of dead time per directory for no visual benefit.
        if (!_suppressSizeComputation)
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Background);

        try
        {
            // Populate child directory sizes cheaply during enumeration by
            // reading the shared cache: each lookup is a single dictionary read
            // plus a timestamp check, with no subtree traversal.  We deliberately
            // never walk uncached subtrees inline — that could block direct
            // children from appearing for seconds on large trees.  Any child
            // whose recursive total isn't cached is handed to the background
            // scheduler in Phase 2 (it shows "Working..." until the size lands).
            bool showSizes = _getShowSizes?.Invoke() ?? false;
            bool precomputeCachedSizes = showSizes && _scheduler is not null;

            // Grab the exclusion filter once for the whole enumeration (it
            // doesn't change mid-load and invoking the delegate is cheap
            // compared to filesystem I/O).
            var activeFilter = _scheduler?.GlobalExcludeFilter;

            // Phase 1: enumerate entries. When precomputeSizes is true,
            // directory sizes are computed inline using the shared cache.
            // When an exclusion filter is active, the filtered size is
            // also computed inline so that "selected only" display shows
            // correct values without needing children to be loaded.
            var (entries, readFailed) = await Task.Run(() =>
            {
                var result = new List<(string FullName, bool IsDirectory,
                    long Size, int FileCount, long FilteredSize, int FilteredFileCount)>();
                // Set when a top-level enumeration of this directory fails
                // (drive not ready, I/O error, access denied).  Distinguishes a
                // genuinely-empty directory (no exception) from one we simply
                // could not read — the latter must NOT clobber a saved selection.
                bool failed = false;
                var dirInfo = new DirectoryInfo(Path);

                try
                {
                    foreach (var subDir in dirInfo.EnumerateDirectories())
                    {
                        try
                        {
                            if ((subDir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0)
                                continue;

                            long dirSize = -1;
                            int dirFileCount = -1;
                            long filtDirSize = -1;
                            int filtDirFileCount = -1;

                            if (precomputeCachedSizes)
                            {
                                // O(1) cached recursive total lookup only
                                // (single dictionary lookup + timestamp check).
                                // Never a recursive walk — see the comment above.
                                var rec = _scheduler!.TryGetCachedSize(subDir.FullName);
                                if (rec.HasValue)
                                {
                                    dirSize = rec.Value.Size;
                                    dirFileCount = rec.Value.FileCount;
                                }
                            }

                            // Filtered sizes: a full recursive traversal is
                            // expensive, but cached filtered totals from a
                            // prior session are an O(1) lookup, so try those
                            // inline.  Anything not cached is left to the
                            // scheduler in Phase 2.
                            if (activeFilter is not null && precomputeCachedSizes)
                            {
                                var filtRec = _scheduler!.TryGetCachedFilteredSize(subDir.FullName);
                                if (filtRec.HasValue)
                                {
                                    filtDirSize = filtRec.Value.Size;
                                    filtDirFileCount = filtRec.Value.FileCount;
                                }
                            }

                            result.Add((subDir.FullName, true, dirSize, dirFileCount,
                                        filtDirSize, filtDirFileCount));
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { failed = true; }
                catch (IOException) { failed = true; }

                try
                {
                    foreach (var file in dirInfo.EnumerateFiles())
                    {
                        try
                        {
                            long size = 0;
                            try { size = file.Length; }
                            catch { }
                            result.Add((file.FullName, false, size, 1, size, 1));
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { failed = true; }
                catch (IOException) { failed = true; }

                // Initial sort: directories first, then files, alphabetically.
                result = result
                    .OrderBy(e => e.IsDirectory ? 0 : 1)
                    .ThenBy(e => System.IO.Path.GetFileName(e.FullName),
                            StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return (result, failed);
            });

            // If we could not read this directory at all (drive not ready,
            // I/O error, access denied) leave the node "not loaded" rather than
            // committing an empty child list.  This is critical: a partial
            // (tri-state) directory whose children were wiped to empty would be
            // serialised by ToModel as a selected-but-childless node, silently
            // destroying the saved selection on the next auto-save.  Keeping
            // _isLoaded == false makes ToModel fall back to _restoredModel and
            // lets a later expansion retry the enumeration.
            if (readFailed && entries.Count == 0)
            {
                _isLoaded = false;
                return;
            }

            // Build the full list of child nodes before touching the
            // ObservableCollection.  ReplaceAll fires a single Reset
            // notification instead of N individual Add events, avoiding
            // per-item layout storms in the TreeView.
            var childNodes = new List<SourceSelectionNodeViewModel>(entries.Count);
            foreach (var (fullName, isDir, size, fileCount, filtSize, filtFileCount) in entries)
            {
                var child = new SourceSelectionNodeViewModel(fullName, isDir, this)
                {
                    _isSelected = _isSelected ?? false,
                    _autoIncludeNew = isDir ? _autoIncludeNew : true,
                    _size = size,
                    _fileCount = fileCount,
                    _filteredSize = filtSize,
                    _filteredFileCount = filtFileCount,
                };

                // Determine backup status for files from the catalog.
                // Only for selected files — unselected files aren't part of the
                // backup, so showing "not backed up" would be misleading.
                if (!isDir && _catalogInfo is not null && child._isSelected != false)
                {
                    if (_catalogInfo.TryGetValue(fullName, out var info))
                    {
                        child._backupStatus = (size != info.SizeBytes ||
                            File.GetLastWriteTimeUtc(fullName) > info.SourceLastWriteUtc)
                            ? BackupStatus.Changed
                            : BackupStatus.BackedUp;
                    }
                    else
                    {
                        child._backupStatus = BackupStatus.NotBackedUp;
                    }
                }

                childNodes.Add(child);
            }

            // Swap in one shot: replace all items with a single Reset
            // notification instead of N individual Add events.
            Children.ReplaceAll(childNodes);

            // Apply the current sort preference (e.g. size descending).
            // When sizes were pre-computed this sorts immediately; when
            // sizes still need async computation the name sort is applied
            // now, and ComputePrioritySizesAsync re-sorts once sizes arrive.
            SortChildren();

            // Compute aggregate backup status for this directory.
            if (_catalogInfo is not null)
                UpdateDirectoryBackupStatus();

            // Phase 2: if "Show sizes" is on, submit directory children
            // that still need computation to the scheduler at high priority.
            // This includes directories that:
            // - have no unfiltered size yet (_size < 0), OR
            // - have an unfiltered size (from recursive cache) but still
            //   need their filtered size computed (when a filter is active).
            if (showSizes && _scheduler is not null && !_suppressSizeComputation)
            {
                bool hasFilter = activeFilter is not null;
                var dirNodes = Children.Where(c => c.IsDirectory
                    && (c._size < 0 || (hasFilter && c._filteredSize < 0))).ToList();
                if (dirNodes.Count > 0)
                    _ = ComputePrioritySizesAsync(dirNodes);
            }
        }
        catch (Exception)
        {
            // Remove the "Loading..." placeholder on error so it doesn't linger.
            Children.Clear();

            // Mark the node not-loaded so the failure is recoverable on a
            // later expansion and ToModel falls back to _restoredModel instead
            // of serialising the now-empty subtree (which would destroy a
            // saved selection — see _restoredModel and the readFailed guard).
            _isLoaded = false;
        }
    }

    /// <summary>
    /// Submit directory children to the size scheduler at high priority
    /// (the user just expanded this node) and re-sort when all complete.
    /// </summary>
    private async Task ComputePrioritySizesAsync(List<SourceSelectionNodeViewModel> dirNodes)
    {
        await _scheduler!.EnqueueAsync(dirNodes, isPriority: true);

        if ((_getSortMode?.Invoke().Column ?? SortColumn.Name) == SortColumn.Size)
            SortChildren();
    }

    /// <summary>
    /// Compute sizes for any loaded directory children that don't have a size
    /// yet (i.e. were expanded before "Show sizes" was enabled). Recurses into
    /// loaded subdirectories.
    /// </summary>
    /// <param name="isVisible">
    /// <c>true</c> when this node's children are currently visible to the user
    /// (top-level or expanded). Visible directories get priority scheduling and
    /// inline cache hits; deeper directories use the background queue.
    /// </param>
    internal async Task ComputeUnknownSizesAsync(bool isVisible = true)
    {
        if (!_isLoaded || _scheduler is null)
            return;

        // Include directories that need unfiltered size OR filtered size.
        var gf = _scheduler.GlobalExcludeFilter;
        bool hasFilter = gf is not null;
        var dirNodes = Children.Where(c => c.IsDirectory
            && (c._size < 0 || (hasFilter && c._filteredSize < 0))).ToList();
        if (dirNodes.Count > 0)
        {
            if (isVisible)
            {
                // Fast path: resolve unfiltered sizes from cached recursive
                // totals (single dictionary lookup per directory, no subtree
                // traversal) so the UI shows numbers instantly.  Filtered
                // sizes are NOT computed inline — the scheduler handles them
                // asynchronously so we don't block the UI.
                var needsUnfiltered = dirNodes.Where(n => n._size < 0).ToList();
                if (needsUnfiltered.Count > 0)
                {
                    var scheduler = _scheduler;
                    var (remaining, resolved) = await Task.Run(() =>
                    {
                        var rem = new List<SourceSelectionNodeViewModel>();
                        var res = new List<(SourceSelectionNodeViewModel Node,
                            long Size, int FileCount)>();

                        foreach (var node in needsUnfiltered)
                        {
                            var rec = scheduler.TryGetCachedSize(node.Path);
                            if (rec.HasValue)
                                res.Add((node, rec.Value.Size, rec.Value.FileCount));
                            else
                                rem.Add(node);
                        }

                        return (rem, res);
                    });

                    // Apply cached results on the UI thread.
                    foreach (var (node, sz, fc) in resolved)
                    {
                        node.Size = sz;
                        node.FileCount = fc;
                    }

                    // Submit uncached directories at high priority.
                    if (remaining.Count > 0)
                        await _scheduler.EnqueueAsync(remaining, isPriority: true);
                }

                // Submit nodes that still need filtered sizes to the
                // scheduler.  Fire-and-forget: filtered sizes are a
                // refinement — don't block the size report or recursion.
                if (hasFilter)
                {
                    var needFiltered = dirNodes.Where(n => n._filteredSize < 0
                        && n._size >= 0).ToList();
                    if (needFiltered.Count > 0)
                    {
                        // Fast path: resolve filtered sizes from cached
                        // values when their signature still matches, so the
                        // UI populates instantly without re-walking the tree.
                        var scheduler = _scheduler;
                        var (remaining, resolved) = await Task.Run(() =>
                        {
                            var rem = new List<SourceSelectionNodeViewModel>();
                            var res = new List<(SourceSelectionNodeViewModel Node,
                                long Size, int FileCount)>();

                            foreach (var node in needFiltered)
                            {
                                var rec = scheduler.TryGetCachedFilteredSize(node.Path);
                                if (rec.HasValue)
                                    res.Add((node, rec.Value.Size, rec.Value.FileCount));
                                else
                                    rem.Add(node);
                            }

                            return (rem, res);
                        });

                        foreach (var (node, sz, fc) in resolved)
                        {
                            node.FilteredSize = sz;
                            node.FilteredFileCount = fc;
                        }

                        if (remaining.Count > 0)
                            _ = _scheduler.EnqueueAsync(remaining, isPriority: true);
                    }
                }
            }
            else
            {
                // Deeper (non-visible) directories: background queue.
                await _scheduler.EnqueueAsync(dirNodes, isPriority: false);
            }

            if ((_getSortMode?.Invoke().Column ?? SortColumn.Name) == SortColumn.Size)
                SortChildren();
        }

        // Recurse into loaded subdirectories. Expanded children are still
        // visible to the user, so they keep priority scheduling. Loaded
        // but collapsed children use the background queue.
        foreach (var child in Children.ToList())
        {
            if (child.IsDirectory && child._isLoaded)
                await child.ComputeUnknownSizesAsync(isVisible: child.IsExpanded);
        }
    }

    /// <summary>
    /// Compute the total size and file count for a directory, dispatching to
    /// the filtered or cached path depending on whether a filter is present.
    /// </summary>
    internal static (long Size, int FileCount) ComputeDirectorySize(
        DirectoryInfo dir, DirectorySizeCache cache, Func<string, bool>? excludeFilter)
    {
        return excludeFilter is null
            ? ComputeDirectorySizeCached(dir, cache)
            : ComputeDirectorySizeFiltered(dir, excludeFilter);
    }

    /// <summary>
    /// Recursively compute the total size and file count of a directory while
    /// applying an exclusion filter. Skips the persistent cache because
    /// filtered sizes depend on patterns that can change independently of
    /// the directory's last-write timestamp.
    /// </summary>
    internal static (long Size, int FileCount) ComputeDirectorySizeFiltered(
        DirectoryInfo dir, Func<string, bool> isExcluded)
    {
        long totalSize = 0;
        int totalCount = 0;

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    if (!isExcluded(file.FullName))
                    {
                        totalSize += file.Length;
                        totalCount++;
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            foreach (var subDir in dir.EnumerateDirectories())
            {
                try
                {
                    if ((subDir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0)
                        continue;
                }
                catch { continue; }

                // Check if the directory itself is excluded (e.g. */node_modules/*)
                // by testing a synthetic child path.
                if (isExcluded(System.IO.Path.Combine(subDir.FullName, "_")))
                    continue;

                var (subSize, subCount) = ComputeDirectorySizeFiltered(subDir, isExcluded);
                totalSize += subSize;
                totalCount += subCount;
            }
        }
        catch { }

        return (totalSize, totalCount);
    }

    /// <summary>
    /// Same as <see cref="ComputeDirectorySizeFiltered"/> but consults the
    /// persistent cache.  A cached filtered total is reused when:
    /// <list type="bullet">
    /// <item>The directory's <see cref="DirectoryInfo.LastWriteTimeUtc"/> hasn't
    /// changed since the cached entry was written.</item>
    /// <item>The cached entry's filter signature matches
    /// <paramref name="filterSignature"/>.</item>
    /// </list>
    /// Otherwise the subtree is walked and the result is stored back into the
    /// cache for the next session.
    /// </summary>
    internal static (long Size, int FileCount) ComputeDirectorySizeFilteredCached(
        DirectoryInfo dir, Func<string, bool> isExcluded,
        DirectorySizeCache cache, string filterSignature)
    {
        // Fast path: cached filtered total whose directory timestamp is
        // still current and whose filter signature matches.
        var cachedRec = cache.TryGetFilteredRecursive(dir.FullName, filterSignature);
        if (cachedRec is not null)
        {
            DateTime currentLastWrite;
            try { currentLastWrite = dir.LastWriteTimeUtc; }
            catch { currentLastWrite = DateTime.MinValue; }

            var entry = cache.TryGet(dir.FullName);
            if (entry is not null && entry.Value.DirLastWriteUtc >= currentLastWrite)
                return cachedRec.Value;
        }

        long totalSize = 0;
        int totalCount = 0;

        try
        {
            foreach (var file in dir.EnumerateFiles())
            {
                try
                {
                    if (!isExcluded(file.FullName))
                    {
                        totalSize += file.Length;
                        totalCount++;
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            foreach (var subDir in dir.EnumerateDirectories())
            {
                try
                {
                    if ((subDir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0)
                        continue;
                }
                catch { continue; }

                if (isExcluded(System.IO.Path.Combine(subDir.FullName, "_")))
                    continue;

                var (subSize, subCount) = ComputeDirectorySizeFilteredCached(
                    subDir, isExcluded, cache, filterSignature);
                totalSize += subSize;
                totalCount += subCount;
            }
        }
        catch { }

        // Store the computed filtered total so future sessions / re-loads
        // can return it instantly via TryGetCachedFilteredRecursiveSize.
        cache.SetFilteredRecursive(dir.FullName, totalSize, totalCount, filterSignature);
        return (totalSize, totalCount);
    }

    /// <summary>
    /// Look up the cached filtered recursive total for a directory.  This is
    /// the filtered counterpart of <see cref="TryGetCachedRecursiveSize"/>:
    /// an O(1) lookup (single dictionary read + one filesystem stat) that
    /// returns a cached value when both the directory's timestamp and the
    /// filter signature match.
    /// </summary>
    internal static (long Size, int FileCount)? TryGetCachedFilteredRecursiveSize(
        string path, DirectorySizeCache cache, string filterSignature)
    {
        var rec = cache.TryGetFilteredRecursive(path, filterSignature);
        if (rec is null) return null;

        var entry = cache.TryGet(path);
        if (entry is null) return null;

        DateTime currentLastWrite;
        try { currentLastWrite = new DirectoryInfo(path).LastWriteTimeUtc; }
        catch { return null; }

        if (entry.Value.DirLastWriteUtc < currentLastWrite)
            return null;

        return rec;
    }

    /// <summary>
    /// Look up the cached recursive total for a directory. Returns the
    /// last-computed recursive size and file count if the directory's own
    /// <see cref="DirectoryInfo.LastWriteTimeUtc"/> hasn't changed since the
    /// cache entry was written. This is an O(1) lookup — no subdirectory
    /// traversal — suitable for instant display of previously-computed values.
    ///
    /// <para>The recursive total may be slightly stale if a deep subdirectory
    /// changed without modifying this directory's timestamp. The background
    /// scheduler runs a full <see cref="ComputeDirectorySizeCached"/> pass
    /// that detects and corrects such drift.</para>
    /// </summary>
    /// <returns>The cached recursive total, or <c>null</c> if no cached value
    /// is available or the directory's timestamp has changed.</returns>
    internal static (long Size, int FileCount)? TryGetCachedRecursiveSize(
        string path, DirectorySizeCache cache)
    {
        // First check: do we have a recursive total cached?
        var rec = cache.TryGetRecursive(path);
        if (rec is null) return null;

        // Second check: has the directory's own timestamp changed?
        // If it has, the direct file sizes may have changed, invalidating
        // the recursive total. Return null to force a full recompute.
        var entry = cache.TryGet(path);
        if (entry is null) return null;

        DateTime currentLastWrite;
        try { currentLastWrite = new DirectoryInfo(path).LastWriteTimeUtc; }
        catch { return null; }

        if (entry.Value.DirLastWriteUtc < currentLastWrite)
            return null;

        return rec;
    }

    /// <summary>
    /// Recursively compute the total size and file count of all files in a
    /// directory, using the <paramref name="cache"/> to skip file enumeration
    /// for directories whose <see cref="DirectoryInfo.LastWriteTimeUtc"/>
    /// hasn't changed since the last computation. Stores the recursive total
    /// in the cache so future calls to <see cref="TryGetCachedRecursiveSize"/>
    /// can return it instantly.
    /// </summary>
    internal static (long Size, int FileCount) ComputeDirectorySizeCached(DirectoryInfo dir, DirectorySizeCache cache)
    {
        long directFileSize;
        int directFileCount;

        DateTime currentLastWrite;
        try { currentLastWrite = dir.LastWriteTimeUtc; }
        catch { return ComputeDirectorySizeFallback(dir); }

        var cached = cache.TryGet(dir.FullName);
        if (cached.HasValue && cached.Value.DirLastWriteUtc >= currentLastWrite)
        {
            // Direct contents unchanged — reuse cached values.
            directFileSize = cached.Value.DirectFileSize;
            directFileCount = cached.Value.DirectFileCount;
        }
        else
        {
            // Enumerate files in THIS directory only (not recursive).
            directFileSize = 0;
            directFileCount = 0;
            try
            {
                foreach (var file in dir.EnumerateFiles())
                {
                    try { directFileSize += file.Length; directFileCount++; }
                    catch { }
                }
            }
            catch { }

            cache.Set(dir.FullName, directFileSize, directFileCount, currentLastWrite);
        }

        // Recursively compute subdirectory sizes (each checks its own cache).
        long subdirSizeTotal = 0;
        int subdirFileTotal = 0;
        try
        {
            foreach (var subDir in dir.EnumerateDirectories())
            {
                try
                {
                    if ((subDir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0)
                        continue;
                }
                catch { continue; }

                var (subSize, subCount) = ComputeDirectorySizeCached(subDir, cache);
                subdirSizeTotal += subSize;
                subdirFileTotal += subCount;
            }
        }
        catch { }

        long totalSize = directFileSize + subdirSizeTotal;
        int totalCount = directFileCount + subdirFileTotal;

        // Store the recursive total so TryGetCachedRecursiveSize can
        // return it instantly on the next session.
        cache.SetRecursive(dir.FullName, totalSize, totalCount);

        return (totalSize, totalCount);
    }

    /// <summary>
    /// Fallback: flat recursive file enumeration (no cache). Used when the
    /// directory's timestamp cannot be read.
    /// </summary>
    private static (long Size, int FileCount) ComputeDirectorySizeFallback(DirectoryInfo dir)
    {
        long total = 0;
        int count = 0;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += file.Length; count++; }
                catch { }
            }
        }
        catch { }
        return (total, count);
    }

    /// <summary>
    /// Reset cached filtered sizes on this node and all loaded descendants.
    /// Called when the exclusion filter changes so that stale filtered values
    /// are not displayed.
    /// </summary>
    internal void ResetFilteredSizes()
    {
        _filteredSize = -1;
        _filteredFileCount = -1;
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(FormattedFileCount));
        if (_isLoaded)
        {
            foreach (var child in Children)
                child.ResetFilteredSizes();
        }
    }

    /// <summary>Format a byte count as a comma-separated number.</summary>
    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "";
        return $"{bytes:N0}";
    }

    /// <summary>
    /// Notify that the FormattedSize and FormattedFileCount bindings should be
    /// re-evaluated on this node and all loaded children. Called when the size
    /// display mode changes.
    /// </summary>
    internal void RefreshFormattedSize()
    {
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(FormattedFileCount));
        if (_isLoaded)
        {
            foreach (var child in Children)
                child.RefreshFormattedSize();
        }
    }

    /// <summary>
    /// Called by a child when its <see cref="Size"/> changes while "selected
    /// only" mode is active.  Propagates up through partially-selected
    /// ancestors so their <see cref="FormattedSize"/> re-evaluates.
    /// </summary>
    private void InvalidateSelectedSize()
    {
        // Only partially-selected directories derive their displayed size
        // from children (ComputeSelectedChildrenSize).  Fully-selected or
        // unselected nodes use their own _size/_filteredSize, so they stop
        // the cascade.
        if (IsSelected != null) return;

        OnPropertyChanged(nameof(FormattedSize));
        Parent?.InvalidateSelectedSize();
    }

    /// <summary>
    /// Same as <see cref="InvalidateSelectedSize"/> but for
    /// <see cref="FormattedFileCount"/>.
    /// </summary>
    private void InvalidateSelectedFileCount()
    {
        if (IsSelected != null) return;

        OnPropertyChanged(nameof(FormattedFileCount));
        Parent?.InvalidateSelectedFileCount();
    }

    /// <summary>
    /// Compute aggregate backup status for a directory from its loaded children.
    /// </summary>
    private void UpdateDirectoryBackupStatus()
    {
        if (!IsDirectory || !_isLoaded || Children.Count == 0)
            return;

        // Only consider children whose status is known (files that were checked
        // against the catalog). Skip Unknown (directories not yet expanded, or
        // no catalog data).
        bool anyBackedUp = false, anyNotBackedUp = false, anyChanged = false;

        foreach (var child in Children)
        {
            switch (child._backupStatus)
            {
                case BackupStatus.BackedUp: anyBackedUp = true; break;
                case BackupStatus.NotBackedUp: anyNotBackedUp = true; break;
                case BackupStatus.Changed: anyChanged = true; break;
                case BackupStatus.Partial: anyBackedUp = true; anyNotBackedUp = true; break;
            }
        }

        if (anyChanged)
            BackupStatus = BackupStatus.Changed;
        else if (anyBackedUp && anyNotBackedUp)
            BackupStatus = BackupStatus.Partial;
        else if (anyBackedUp)
            BackupStatus = BackupStatus.BackedUp;
        else if (anyNotBackedUp)
            BackupStatus = BackupStatus.NotBackedUp;
        // else: all children are Unknown — leave as Unknown.
    }

    /// <summary>
    /// Recalculate this node's tristate based on its children's states.
    /// </summary>
    internal void UpdateFromChildren()
    {
        if (Children.Count == 0)
            return;

        bool allSelected = Children.All(c => c.IsSelected == true);
        bool allDeselected = Children.All(c => c.IsSelected == false);

        _suppressPropagation = true;

        if (allSelected)
            IsSelected = true;
        else if (allDeselected)
            IsSelected = false;
        else
            IsSelected = null; // Mixed — tristate indeterminate

        _suppressPropagation = false;

        // Continue propagating up.
        Parent?.UpdateFromChildren();
    }

    /// <summary>
    /// Build a <see cref="Core.Models.SourceSelection"/> tree from this viewmodel tree.
    /// Only includes nodes that are at least partially selected.
    /// </summary>
    public Core.Models.SourceSelection? ToModel()
    {
        if (IsSelected == false)
            return null;

        // Lossless fallback: this directory has a saved selection but its
        // children were never successfully enumerated (drive not ready, I/O
        // error, access denied — _isLoaded is false).  Re-deriving from the
        // empty Children collection would emit a selected-but-childless node
        // and permanently destroy the saved subtree.  Return the originally
        // restored model verbatim so the selection survives untouched.
        if (IsDirectory && !_isLoaded && _restoredModel is not null)
            return _restoredModel;

        var model = new Core.Models.SourceSelection
        {
            Path = Path,
            IsDirectory = IsDirectory,
            IsSelected = IsSelected,
            IsExpanded = IsExpanded,
            AutoIncludeNewSubdirectories = AutoIncludeNew,
        };

        if (IsDirectory && _isLoaded)
        {
            foreach (var child in Children)
            {
                var childModel = child.ToModel();
                if (childModel is not null)
                    model.Children.Add(childModel);
            }

            // Re-emit saved selections whose paths weren't on disk this session
            // (see _orphanedChildModels) so they survive a re-save untouched.
            if (_orphanedChildModels is not null)
            {
                foreach (var orphan in _orphanedChildModels)
                {
                    if (!model.Children.Any(c =>
                            string.Equals(c.Path, orphan.Path, StringComparison.OrdinalIgnoreCase)))
                        model.Children.Add(orphan);
                }
            }
        }

        return model;
    }
}
