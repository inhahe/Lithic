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
    private long _size = -1;
    private int _fileCount = -1;
    private BackupStatus _backupStatus = BackupStatus.Unknown;
    private readonly Func<bool>? _getShowSizes;
    private readonly Func<(SortColumn Column, bool Ascending)>? _getSortMode;
    private readonly SizeComputeScheduler? _scheduler;
    private readonly Func<bool>? _getShowSelectedOnly;
    private readonly Func<Func<string, bool>?>? _getExcludeFilter;
    private readonly Action? _onSelectionChanged;
    private readonly Dictionary<string, FileVersionInfo>? _catalogInfo;

    public SourceSelectionNodeViewModel(
        string path, bool isDirectory, SourceSelectionNodeViewModel? parent,
        Func<bool>? getShowSizes = null,
        Func<(SortColumn Column, bool Ascending)>? getSortMode = null,
        SizeComputeScheduler? scheduler = null, Action? onSelectionChanged = null,
        Func<bool>? getShowSelectedOnly = null,
        Dictionary<string, FileVersionInfo>? catalogInfo = null,
        Func<Func<string, bool>?>? getExcludeFilter = null)
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
                // Node itself excluded by the backup filter (0-tier tier
                // set pattern or global exclusion) — nothing will be backed up.
                if (IsExcludedByFilter(this, _getExcludeFilter?.Invoke())) return "";
                if (IsDirectory && IsSelected == null && _isLoaded)
                {
                    var total = ComputeSelectedChildrenSize();
                    return total.HasValue ? FormatBytes(total.Value) : "Working...";
                }
                // Fully-selected directory or file in "selected only" mode.
                return _size < 0 ? "Working..." : FormatBytes(_size);
            }
            return _size < 0 ? "Working..." : FormatBytes(_size);
        }
    }

    /// <summary>Formatted file count string (e.g. "1,234"). Empty for
    /// individual files or when the count is not yet computed.
    /// In "selected only" mode, partially-selected directories show the
    /// count of selected children only.
    /// Shows "Working..." while any contributing child's count is unknown.</summary>
    public string FormattedFileCount
    {
        get
        {
            if (!IsDirectory) return "";
            bool selectedOnly = _getShowSelectedOnly?.Invoke() ?? false;
            if (selectedOnly)
            {
                if (IsSelected == false) return "";
                if (IsExcludedByFilter(this, _getExcludeFilter?.Invoke())) return "";
                if (IsSelected == null && _isLoaded)
                {
                    var total = ComputeSelectedChildrenFileCount();
                    return total.HasValue ? total.Value.ToString("N0") : "Working...";
                }
                return _fileCount < 0 ? "Working..." : _fileCount.ToString("N0");
            }
            return _fileCount < 0 ? "Working..." : _fileCount.ToString("N0");
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

            if (!child.IsDirectory || child.IsSelected == true)
            {
                if (child._size < 0) return null;
                total += child._size;
            }
            else if (child._isLoaded)
            {
                var sub = child.ComputeSelectedChildrenSize();
                if (sub is null) return null;
                total += sub.Value;
            }
            else
            {
                // Child directory not loaded — can't compute its contribution.
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
                if (child._fileCount < 0) return null;
                total += child._fileCount;
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

            // Propagate down: set all children to the same definite state.
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

            // Notify the SourceSelectionViewModel so it can refresh the selected size.
            _onSelectionChanged?.Invoke();
        }
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
                    tasks.Add(childNode.ApplySelectionAsync(childModel));
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
            // Pre-compute sizes on the background thread when the cache
            // likely has the data, making each child lookup fast (timestamp
            // check + dictionary read, no file enumeration for unchanged dirs).
            //
            // This fires in two cases:
            // 1. Parent was computed by the scheduler this session (both _size
            //    and _fileCount are set), which guarantees the cache is warm
            //    for all child directories.
            // 2. Parent has an entry in the persistent cache from a prior
            //    session, so child directories are very likely cached too.
            //
            // Drive nodes have _size set from DriveInfo but _fileCount == -1
            // and typically no cache entry, so they correctly skip pre-
            // computation — otherwise ComputeInline would recursively scan
            // the entire drive during expansion.
            bool showSizes = _getShowSizes?.Invoke() ?? false;
            bool computedThisSession = _size >= 0 && _fileCount >= 0;
            bool cachedFromPriorSession = !computedThisSession
                && _scheduler is not null && _scheduler.HasCacheEntry(Path);
            bool precomputeSizes = showSizes && _scheduler is not null
                && (computedThisSession || cachedFromPriorSession);

            // Phase 1: enumerate entries. When precomputeSizes is true,
            // directory sizes are computed inline using the shared cache.
            var entries = await Task.Run(() =>
            {
                var result = new List<(string FullName, bool IsDirectory, long Size, int FileCount)>();
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
                            if (precomputeSizes)
                            {
                                try
                                {
                                    var (sz, fc) = _scheduler!.ComputeInline(subDir, excludeFilter: null);
                                    dirSize = sz;
                                    dirFileCount = fc;
                                }
                                catch { }
                            }

                            result.Add((subDir.FullName, true, dirSize, dirFileCount));
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                try
                {
                    foreach (var file in dirInfo.EnumerateFiles())
                    {
                        try
                        {
                            long size = 0;
                            try { size = file.Length; }
                            catch { }
                            result.Add((file.FullName, false, size, 1));
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                // Initial sort: directories first, then files, alphabetically.
                result = result
                    .OrderBy(e => e.IsDirectory ? 0 : 1)
                    .ThenBy(e => System.IO.Path.GetFileName(e.FullName),
                            StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return result;
            });

            // Build the full list of child nodes before touching the
            // ObservableCollection.  ReplaceAll fires a single Reset
            // notification instead of N individual Add events, avoiding
            // per-item layout storms in the TreeView.
            var childNodes = new List<SourceSelectionNodeViewModel>(entries.Count);
            foreach (var (fullName, isDir, size, fileCount) in entries)
            {
                var child = new SourceSelectionNodeViewModel(fullName, isDir, this)
                {
                    _isSelected = _isSelected ?? false,
                    _autoIncludeNew = isDir ? _autoIncludeNew : true,
                    _size = size,
                    _fileCount = fileCount,
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

            // Phase 2: if "Show sizes" is on and sizes weren't already
            // pre-computed above, submit directory children to the scheduler
            // at high priority for progressive fill-in.
            if (showSizes && _scheduler is not null && !precomputeSizes && !_suppressSizeComputation)
            {
                var dirNodes = Children.Where(c => c.IsDirectory).ToList();
                if (dirNodes.Count > 0)
                    _ = ComputePrioritySizesAsync(dirNodes);
            }
        }
        catch (Exception)
        {
            // Remove the "Loading..." placeholder on error so it doesn't linger.
            Children.Clear();
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
    internal async Task ComputeUnknownSizesAsync()
    {
        if (!_isLoaded || _scheduler is null)
            return;

        var dirNodes = Children.Where(c => c.IsDirectory && c._size < 0).ToList();
        if (dirNodes.Count > 0)
        {
            await _scheduler.EnqueueAsync(dirNodes, isPriority: false);

            if ((_getSortMode?.Invoke().Column ?? SortColumn.Name) == SortColumn.Size)
                SortChildren();
        }

        // Recurse into loaded subdirectories.
        foreach (var child in Children.ToList())
        {
            if (child.IsDirectory && child._isLoaded)
                await child.ComputeUnknownSizesAsync();
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
    /// Recursively compute the total size and file count of all files in a
    /// directory, using the <paramref name="cache"/> to skip file enumeration
    /// for directories whose <see cref="DirectoryInfo.LastWriteTimeUtc"/>
    /// hasn't changed since the last computation.
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

        return (directFileSize + subdirSizeTotal, directFileCount + subdirFileTotal);
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
        // unselected nodes use their own _size, so they stop the cascade.
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
        }

        return model;
    }
}
