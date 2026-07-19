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
    /// <summary>
    /// True when this node's <see cref="_isSelected"/> is <c>true</c> ONLY because
    /// it was auto-include-derived at creation (an unlisted descendant of a
    /// partially-selected, auto-include-on directory — see
    /// <see cref="CreateChildNode"/>), not because the user or a saved model
    /// selected it.  Display-only: the checkbox renders checked so it agrees with
    /// what auto-include actually backs up, but <see cref="ToModel"/> skips such a
    /// node so the saved selection stays byte-for-byte what it was before this
    /// display change — the folder is re-derived from the parent's auto-include on
    /// reload rather than being pinned as an explicit selection.  Any real change
    /// (the user toggling this node, or a descendant edit rippling up through the
    /// <see cref="IsSelected"/> setter, or a saved-model restore) clears the flag,
    /// at which point the node serialises normally.
    /// </summary>
    private bool _isAutoIncludeDerived;
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
    /// <summary>Guards against overlapping reconcile passes (re-expand while a
    /// previous re-enumeration is still running).</summary>
    private bool _reconciling;
    /// <summary>
    /// True while <see cref="ApplySelectionAsync"/> is restoring saved state on
    /// this node.  Suppresses the reconcile-on-expand pass so that programmatic
    /// expansion during restore doesn't trigger a redundant re-enumeration
    /// (children were just freshly loaded).
    /// </summary>
    private bool _isApplyingSelection;
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
    /// <summary>
    /// Set when <see cref="ApplySelectionAsync"/> deferred restoring this
    /// collapsed directory's child selections to keep the initial dialog open
    /// fast (we only eagerly restore currently-visible/expanded subtrees).  The
    /// saved subtree lives in <see cref="_restoredModel"/>; when the user later
    /// expands this node, <see cref="LoadChildrenAsync"/> consumes this flag and
    /// applies the saved child selections on top of the freshly-loaded children.
    /// Until then, <see cref="ToModel"/>'s _restoredModel fallback keeps the save
    /// lossless.
    /// </summary>
    private bool _pendingDeferredRestore;
    private bool _isSelectionRestored;
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
    /// <summary>
    /// When set, registers an in-flight async task (e.g. the load-then-pin work
    /// kicked off when auto-include-new is toggled on a not-yet-enumerated
    /// directory) with the owning viewmodel so a subsequent Save awaits it before
    /// serialising.  Null (e.g. in tests) means such work runs without a Save gate.
    /// </summary>
    private readonly Action<Task>? _registerPendingWork;
    /// <summary>
    /// When set, records a directory path whose <em>coverage</em> changed via
    /// something other than a checkbox toggle — specifically an auto-include-new
    /// flip — into the owning viewmodel's changed-paths set, so the post-edit
    /// destination reconcile scans that subtree for files it just dropped.  The
    /// checkbox <see cref="IsSelected"/> path already records via
    /// <see cref="_requestSelectionSettle"/>; auto-include has no such hook, and
    /// turning it OFF on a directory whose existing descendants were covered
    /// only by the rule (e.g. an unexpanded <c>C:\</c>) silently evicts them —
    /// which must trigger a reconcile.  Null (tests) means no recording.
    /// </summary>
    private readonly Action<string>? _recordChangedPath;
    // Catalog data is provided via a getter rather than a captured value so it
    // can arrive AFTER the tree is built.  The full catalog dictionary can hold
    // ~1M entries and take several seconds to query, so it is loaded in the
    // background after the editor window is already visible (see
    // MainViewModel.StartEditFlow / SourceSelectionViewModel.SetCatalogInfo);
    // nodes read the shared reference lazily so late arrival is transparent.
    private readonly Func<Dictionary<string, FileVersionInfo>?>? _getCatalogInfo;

    public SourceSelectionNodeViewModel(
        string path, bool isDirectory, SourceSelectionNodeViewModel? parent,
        Func<bool>? getShowSizes = null,
        Func<(SortColumn Column, bool Ascending)>? getSortMode = null,
        SizeComputeScheduler? scheduler = null, Action? onSelectionChanged = null,
        Func<bool>? getShowSelectedOnly = null,
        Func<Dictionary<string, FileVersionInfo>?>? getCatalogInfo = null,
        Func<Func<string, bool>?>? getExcludeFilter = null,
        Action<SourceSelectionNodeViewModel>? requestSelectionSettle = null,
        Action<Task>? registerPendingWork = null,
        Action<string>? recordChangedPath = null)
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
        _registerPendingWork = registerPendingWork ?? parent?._registerPendingWork;
        _recordChangedPath = recordChangedPath ?? parent?._recordChangedPath;
        _getCatalogInfo = getCatalogInfo ?? parent?._getCatalogInfo;
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

    /// <summary>
    /// True when this entry is a Hidden or System directory/file. These used to be
    /// omitted from the editor tree entirely, which meant a user could never see
    /// (or deselect) something like <c>C:\ProgramData</c> even though the backup
    /// engine happily included it — a confusing "backed up but invisible" mismatch.
    /// They are now shown but rendered in a distinct (dimmer) colour so it's clear
    /// they're special. Set by <see cref="CreateChildNode"/> from the enumerated
    /// file-system attributes.
    /// </summary>
    public bool IsHiddenOrSystem { get; internal set; }

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
            // Any actual state change — a user click, or a descendant edit
            // rippling up through UpdateFromChildren — means this node is no
            // longer a pure auto-include derivation, so it must serialise
            // normally from now on (see _isAutoIncludeDerived).
            _isAutoIncludeDerived = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNodeEnabled));

            // Clear backup status for deselected files — they're not part
            // of the backup, so showing a status dot would be misleading.
            if (value == false && _backupStatus != BackupStatus.Unknown)
            {
                _backupStatus = BackupStatus.Unknown;
                OnPropertyChanged(nameof(BackupStatus));
            }

            // A user-initiated exclusion must invalidate this directory's deferred
            // child restore *synchronously*, before the coalesced settle pass runs.
            // Otherwise a lazy expand racing the settle would re-apply the saved
            // child selections (and any worker-materialised auto-include leaves
            // under them), and UpdateFromChildren would then re-derive this node
            // from those children back to partial (null) — silently undoing the
            // exclusion. That is exactly why excluding a directory that had
            // materialised descendant selections (e.g. C:\ProgramData with pinned
            // auto-include junk) never stuck, while a childless one (C:\Users) did.
            if (value == false && IsDirectory && !_suppressPropagation)
                DiscardSavedSubtreeSelections();

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

        if (value == false && IsDirectory)
        {
            // Exclusion: hard-exclude the entire subtree. Discard any saved or
            // deferred child selections this node holds, then drive every loaded
            // descendant to excluded (clearing *its* deferred state too). This is
            // what makes an exclusion stick: no lazy expand's deferred restore and
            // no UpdateFromChildren ripple from a materialised descendant can
            // resurrect the old selection and re-derive this node to partial.
            DiscardSavedSubtreeSelections();
            if (_isLoaded)
                foreach (var child in Children)
                    child.ExcludeSubtree();
        }
        else if (value.HasValue && IsDirectory && _isLoaded)
        {
            // Inclusion: push the definite state down to loaded children.
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
    /// Recursively force this node and every loaded descendant into the excluded
    /// state, discarding each one's saved/deferred child selections. Used when an
    /// ancestor is excluded: the whole subtree is out, and any materialised or
    /// deferred descendant selection must be dropped so a later expand — or an
    /// <see cref="UpdateFromChildren"/> ripple — can't resurrect it and undo the
    /// exclusion. Does not propagate up (the caller owns the excluded parent).
    /// </summary>
    private void ExcludeSubtree()
    {
        _suppressPropagation = true;
        _isSelected = false;
        _isAutoIncludeDerived = false;
        _suppressPropagation = false;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsNodeEnabled));

        if (_backupStatus != BackupStatus.Unknown)
        {
            _backupStatus = BackupStatus.Unknown;
            OnPropertyChanged(nameof(BackupStatus));
        }

        DiscardSavedSubtreeSelections();

        if (IsDirectory && _isLoaded)
            foreach (var child in Children)
                child.ExcludeSubtree();
    }

    /// <summary>
    /// Drop any saved/deferred child-selection state this node is holding: the
    /// deferred-restore flag, the restored-model fallback, and preserved orphan
    /// child models. Called when the node becomes definitively excluded so none of
    /// them can later re-apply the old subtree selection. Safe for an excluded
    /// node because <see cref="ToModel"/> serialises it as a bare tombstone
    /// (returning at its <c>IsSelected == false</c> branch before it would ever
    /// consult <see cref="_restoredModel"/> or the orphan models).
    /// </summary>
    private void DiscardSavedSubtreeSelections()
    {
        _pendingDeferredRestore = false;
        _restoredModel = null;
        _orphanedChildModels = null;
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
    /// Whether this node's <see cref="IsSelected"/> value reflects its final,
    /// intended state — i.e. it has been settled (restored from the saved model,
    /// or derived at creation for a freshly enumerated child).  Bound by the
    /// tree's Include checkbox visibility: the box stays hidden (space reserved)
    /// until this is true, so the user never sees a column of default-unchecked
    /// boxes flip to their real state.  Because each node reveals itself the
    /// instant its own state settles — independent of its descendants' restore —
    /// checkboxes appear top-down almost immediately instead of waiting for the
    /// entire recursive restore (including deep filesystem enumeration) to finish.
    /// </summary>
    public bool IsSelectionRestored
    {
        get => _isSelectionRestored;
        internal set => SetProperty(ref _isSelectionRestored, value);
    }

    /// <summary>
    /// Whether new subdirectories added in the future should be automatically included.
    /// Only meaningful for directories.
    /// </summary>
    public bool AutoIncludeNew
    {
        get => _autoIncludeNew;
        set => ApplyAutoIncludeNew(value, userInitiated: true);
    }

    /// <summary>
    /// Core of the <see cref="AutoIncludeNew"/> setter, split so the change can be
    /// applied both by a user toggle (<paramref name="userInitiated"/> = true) and by
    /// recursive propagation into loaded children (false).
    /// </summary>
    /// <remarks>
    /// "Auto-include NEW" governs *future* folders; turning it off must NOT
    /// retroactively evict a folder it already adopted.  Any descendant that rendered
    /// checked only because auto-include was on (<see cref="_isAutoIncludeDerived"/>)
    /// would, once the rule is off, stop being a covered unlisted descendant and drop
    /// out of scope — silently removing already-backed-up content.  So at the moment
    /// the rule flips off we PIN the current folders as explicit selections: clear the
    /// derived flag so ToModel serialises them, and — crucially — make sure the
    /// directory's children are actually LOADED so it stops looking like a
    /// "fully-selected childless" node (which <see cref="Core.Models.SourceSelection.IncludesUnlistedDescendants"/>
    /// treats as "whole subtree in, flag ignored").  Only with children materialised
    /// does the off-flag actually exclude genuinely-new subdirectories.
    ///
    /// For a drive root like <c>C:\</c> that was restored fully-selected but never
    /// expanded, its children are unloaded, so a plain toggle would (a) not persist
    /// meaningfully and (b) be moot per the rule above.  We therefore load one level
    /// of children asynchronously and pin them, registering that task so Save waits
    /// for it (see <see cref="LoadThenApplyAutoIncludeAsync"/>).
    /// </remarks>
    private void ApplyAutoIncludeNew(bool value, bool userInitiated)
    {
        bool turningOff = _autoIncludeNew && !value;
        if (!SetProperty(ref _autoIncludeNew, value, nameof(AutoIncludeNew)))
            return;

        // Self-pin: if this node rendered checked only via a parent's auto-include
        // rule, a change here makes it an explicit selection so ToModel serialises it.
        if (turningOff && _isAutoIncludeDerived)
            _isAutoIncludeDerived = false;

        if (IsDirectory && _isLoaded)
        {
            PropagateAutoIncludeToLoadedChildren(value, turningOff);
        }
        else if (IsDirectory && userInitiated && turningOff)
        {
            // Turning the rule OFF on a not-yet-enumerated directory (e.g. an
            // unexpanded C:\).  A fully-selected childless node ignores the flag
            // entirely (IncludesUnlistedDescendants treats it as "whole subtree in"),
            // so simply persisting off would be a no-op on reload.  Load one level of
            // children and pin them so the directory becomes "fully-selected WITH
            // explicit children" — making the off-flag actually exclude future
            // subdirectories.  Gate Save on that task.  (Turning the rule ON needs no
            // load: a childless fully-selected node is already "everything in".)
            var task = LoadThenApplyAutoIncludeAsync(value, turningOff);
            _registerPendingWork?.Invoke(task);
        }

        // Mark the set dirty (enable Save) for the user's own toggle — not for each
        // recursively-propagated child, which would fire the aggregate many times.
        if (userInitiated)
        {
            _onSelectionChanged?.Invoke();

            // Record this directory as a changed subtree so the post-edit
            // destination reconcile rescans it.  Turning auto-include OFF on a
            // directory whose existing descendants were covered ONLY by the rule
            // (e.g. an unexpanded C:\ with a handful of explicit children) evicts
            // those descendants from the selection, yet fires no checkbox toggle —
            // so without this the reconcile's changed-paths set stays empty and the
            // now-orphaned catalog rows are never marked deleted or purged from the
            // destination.  Recording on turn-ON too is harmless: the reconcile's
            // "included before AND excluded now" filter yields no removals when
            // coverage only grew.
            if (IsDirectory && !string.IsNullOrEmpty(Path))
                _recordChangedPath?.Invoke(Path);
        }
    }

    /// <summary>
    /// Push an auto-include-new change down to already-loaded child directories
    /// (their own <see cref="ApplyAutoIncludeNew"/> self-pins recursively) and pin any
    /// auto-include-derived files directly.  Does NOT force-load unloaded children.
    /// </summary>
    private void PropagateAutoIncludeToLoadedChildren(bool value, bool turningOff)
    {
        foreach (var child in Children)
        {
            if (child.IsDirectory)
                child.ApplyAutoIncludeNew(value, userInitiated: false);
            else if (turningOff && child._isAutoIncludeDerived)
                child._isAutoIncludeDerived = false;
        }
    }

    /// <summary>
    /// Enumerate this directory's children (if not already loaded) and then pin them
    /// to the given auto-include-new value.  Kicked off when the user toggles the flag
    /// on a directory whose children were never loaded, so that turning the rule off
    /// actually excludes future subdirectories instead of being ignored (see
    /// <see cref="Core.Models.SourceSelection.IncludesUnlistedDescendants"/>).
    /// </summary>
    private async Task LoadThenApplyAutoIncludeAsync(bool value, bool turningOff)
    {
        await EnsureChildrenLoadedAsync();

        // The user may have toggled again while the load was in flight; only act if
        // our flag still matches what this load was for.
        if (_autoIncludeNew != value)
            return;

        // Enumeration may have failed (unreadable dir) leaving _isLoaded false; the
        // persisted flag alone will have to stand for those — nothing to pin.
        if (!_isLoaded)
            return;

        PropagateAutoIncludeToLoadedChildren(value, turningOff);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value) || !value || !IsDirectory)
                return;

            if (!_isLoaded)
            {
                // First expansion — enumerate this directory's children.
                _loadTask = LoadChildrenAsync();
            }
            else if (!_isApplyingSelection)
            {
                // Re-expanding an already-loaded directory: re-read the folder
                // so files/folders created, renamed, or deleted since the last
                // enumeration show up (the tree is otherwise a one-time snapshot
                // with no live refresh).  Skipped during selection restore, where
                // expansion is programmatic and children were just loaded.
                _loadTask = ReconcileChildrenAsync();
            }
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
        // Restored from a saved model → this is an explicit, persisted selection,
        // not an auto-include derivation, so it serialises normally.
        _isAutoIncludeDerived = false;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsNodeEnabled));
        _suppressPropagation = false;

        _autoIncludeNew = model.AutoIncludeNewSubdirectories;
        OnPropertyChanged(nameof(AutoIncludeNew));

        // This node's own state is now settled — reveal its checkbox immediately,
        // without waiting for its (possibly deep, slow-to-enumerate) descendants
        // to finish restoring.  This is what makes checkboxes appear top-down.
        IsSelectionRestored = true;

        // Decide whether to restore this subtree's children eagerly now, or
        // defer it until the user expands the node.  To keep the initial dialog
        // open snappy we only eagerly walk currently-visible subtrees: the
        // permanently-expanded virtual root (Parent is null) and any node the
        // saved model had expanded.  Collapsed subtrees are deferred — their
        // saved child selections are re-applied on first expand (see
        // LoadChildrenAsync), and until then ToModel's _restoredModel fallback
        // keeps the save lossless.
        bool restoreChildrenNow = model.IsExpanded || Parent is null;

        // If this directory has child selections to restore, load children
        // and apply.  Suppress size computation during this phase — we're
        // restoring saved state, not responding to a user click.
        if (IsDirectory && model.Children.Count > 0)
        {
            if (restoreChildrenNow)
            {
                _suppressSizeComputation = true;
                await EnsureChildrenLoadedAsync();
                _suppressSizeComputation = false;

                await ApplyChildModelsAsync(model.Children);
            }
            else
            {
                // Defer: remember that this collapsed directory still owes a
                // child-selection restore.  _restoredModel (set above) holds the
                // saved subtree; LoadChildrenAsync re-applies it on first expand.
                _pendingDeferredRestore = true;
            }
        }

        // Restore expansion state from the saved model.  Children are
        // already loaded above (if any), so setting IsExpanded here only
        // controls the visual state.
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
            // This expansion is programmatic and the children were just loaded,
            // so suppress the reconcile-on-expand re-enumeration the setter would
            // otherwise trigger for an already-loaded directory.  The flag scope
            // is synchronous: the setter reads it before returning.
            _isApplyingSelection = true;
            IsExpanded = model.IsExpanded;
            _isApplyingSelection = false;
        }
    }

    /// <summary>
    /// Apply a set of saved child <see cref="Core.Models.SourceSelection"/>
    /// models to this node's already-loaded children, recursing into each.
    /// Saved children whose paths are no longer present on disk are preserved as
    /// orphans (see <see cref="_orphanedChildModels"/>) so a later save doesn't
    /// silently drop them.  Sibling subtrees are applied concurrently — each
    /// child's filesystem enumeration runs on the thread pool, so siblings
    /// overlap instead of serialising.
    /// </summary>
    private async Task ApplyChildModelsAsync(
        IReadOnlyList<Core.Models.SourceSelection> childModels)
    {
        // Reveal every direct child's checkbox up front.  Children already carry
        // their correct state: freshly enumerated ones inherited it at creation
        // (CreateChildNode), and any that also appear in the saved model have it
        // corrected synchronously by the ApplySelectionAsync calls below — all
        // before the UI thread next renders — so no checkbox flashes a wrong
        // value.  Doing this before recursing means a directory's own row and its
        // immediate children appear without waiting for grandchildren to load.
        foreach (var child in Children)
            child.IsSelectionRestored = true;

        var tasks = new List<Task>(childModels.Count);
        foreach (var childModel in childModels)
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

    /// <summary>
    /// Reveal this node's Include checkbox and those of all currently-loaded
    /// descendants.  A safety net run once the restore finishes: it flips any
    /// node the restore never touched (e.g. a drive present on the system but
    /// absent from the saved model, or the old serialisation format's untouched
    /// nodes) from hidden to visible.  Nodes reached by the restore already set
    /// this eagerly, so this only affects the stragglers and never regresses a
    /// value that's already correct.
    /// </summary>
    internal void RevealCheckboxesRecursive()
    {
        IsSelectionRestored = true;
        if (_isLoaded)
            foreach (var child in Children)
                child.RevealCheckboxesRecursive();
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
            var (entries, readFailed) = await EnumerateChildEntriesAsync();

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
            foreach (var entry in entries)
                childNodes.Add(CreateChildNode(entry));

            // Swap in one shot: replace all items with a single Reset
            // notification instead of N individual Add events.
            Children.ReplaceAll(childNodes);

            // Apply the current sort preference (e.g. size descending).
            // When sizes were pre-computed this sorts immediately; when
            // sizes still need async computation the name sort is applied
            // now, and ComputePrioritySizesAsync re-sorts once sizes arrive.
            SortChildren();

            // If this directory's saved child selections were deferred (it was
            // collapsed during the initial restore), re-apply them now that the
            // children exist.  This runs the first time the user expands the node.
            if (_pendingDeferredRestore && _restoredModel is not null)
            {
                _pendingDeferredRestore = false;
                await ApplyChildModelsAsync(_restoredModel.Children);
                SortChildren();

                // Reconcile this node's tristate (and its ancestors') with the
                // children that were just materialised.  The saved parent state can
                // disagree with the restored subtree — e.g. a directory saved as
                // fully-selected (a full check) that actually has some children
                // excluded keeps showing a full check until its children load.  Now
                // that every child exists (freshly enumerated, with saved overrides
                // applied on top), recompute so the parent shows the correct
                // partial/full state instead of a stale one.  Suppressed inside
                // UpdateFromChildren, so this neither pushes state back down nor
                // marks the set dirty.
                UpdateFromChildren();
            }

            // Compute aggregate backup status for this directory.
            if (_getCatalogInfo?.Invoke() is not null)
                UpdateDirectoryBackupStatus();

            // Phase 2: submit directory children that still need size
            // computation to the scheduler at high priority (the user just
            // expanded this node).
            if (!_suppressSizeComputation)
                SubmitDirectorySizeComputation(Children);
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

    /// <summary>Shape of one enumerated child entry: its full path, whether it
    /// is a directory, whether it is Hidden/System, and any inline-computed
    /// (cached) size accounting.</summary>
    private readonly record struct ChildEntry(
        string FullName, bool IsDirectory, bool IsHiddenOrSystem,
        long Size, int FileCount, long FilteredSize, int FilteredFileCount);

    /// <summary>
    /// Enumerate this directory's immediate children on a background thread,
    /// flagging (but no longer hiding) System/Hidden subdirectories and
    /// precomputing cached recursive (and, when a filter is active, filtered)
    /// sizes for directory entries.
    /// Returns the sorted entries (directories first, then files, alphabetically)
    /// and a flag indicating whether the top-level enumeration failed (drive not
    /// ready, I/O error, access denied) — used by callers to avoid clobbering a
    /// saved selection with an empty child list.
    /// </summary>
    private Task<(List<ChildEntry> Entries, bool ReadFailed)> EnumerateChildEntriesAsync()
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

        return Task.Run(() =>
        {
            var result = new List<ChildEntry>();
            // Set when a top-level enumeration of this directory fails
            // (drive not ready, I/O error, access denied).  Distinguishes a
            // genuinely-empty directory (no exception) from one we simply
            // could not read — the latter must NOT clobber a saved selection.
            bool failed = false;

            // The virtual "All Drives" root has an empty Path and no real
            // filesystem directory (its children are the drive roots, managed
            // by SourceSelectionViewModel, not enumerated here).  `new
            // DirectoryInfo("")` throws ArgumentException, and because this
            // runs in a Task whose result may be discarded (e.g. a re-expand
            // reconcile), that surfaced as an unobserved-task crash.  Report it
            // as a read failure so callers leave the existing children intact.
            if (string.IsNullOrEmpty(Path))
                return (result, true);

            var dirInfo = new DirectoryInfo(Path);

            try
            {
                foreach (var subDir in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        // Hidden/System directories are shown (so they can be
                        // seen and deselected) but flagged for distinct colouring.
                        bool hiddenOrSystem =
                            (subDir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0;

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

                        result.Add(new ChildEntry(subDir.FullName, true, hiddenOrSystem,
                                    dirSize, dirFileCount, filtDirSize, filtDirFileCount));
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
                        bool hiddenOrSystem =
                            (file.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0;
                        result.Add(new ChildEntry(file.FullName, false, hiddenOrSystem, size, 1, size, 1));
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
    }

    /// <summary>
    /// Build a child node from an enumerated entry, inheriting this node's
    /// selection and auto-include state and stamping file backup status from
    /// the catalog.
    /// </summary>
    private SourceSelectionNodeViewModel CreateChildNode(ChildEntry entry)
    {
        var child = new SourceSelectionNodeViewModel(entry.FullName, entry.IsDirectory, this)
        {
            // A freshly-enumerated child with no saved-model entry is an
            // "unlisted descendant".  Its default selection must match the rule
            // the scanner and continuous-backup actually apply
            // (SourceSelection.IncludesUnlistedDescendants): included under a
            // fully-selected parent; included under a partially-selected parent
            // only when auto-include-new is on; excluded under an excluded
            // parent.  Using the parent's auto-include flag for the partial case
            // (instead of a flat "unchecked") makes auto-included new folders
            // render CHECKED, so the checkbox no longer contradicts what will be
            // backed up.  If the child also appears in the saved model,
            // ApplyChildModelsAsync overrides this below.
            _isSelected = _isSelected switch
            {
                false => false,
                true => true,
                null => _autoIncludeNew,
            },
            // When the parent is partially selected and auto-include is on, the
            // "checked" state above is a pure derivation — the folder isn't in
            // the saved selection, it's covered by IncludesUnlistedDescendants.
            // Flag it so ToModel doesn't pin it as an explicit selection (which
            // would survive an auto-include-off toggle and diverge from what the
            // scanner does).  The flag is cleared the moment the user or a
            // saved-model restore gives the node a real state.
            _isAutoIncludeDerived = _isSelected is null && _autoIncludeNew,
            _autoIncludeNew = entry.IsDirectory ? _autoIncludeNew : true,
            _size = entry.Size,
            _fileCount = entry.FileCount,
            _filteredSize = entry.FilteredSize,
            _filteredFileCount = entry.FilteredFileCount,
            // A freshly enumerated child's selection state is known synchronously
            // here (inherited from this parent).  If it also appears in a saved
            // model, ApplyChildModelsAsync corrects it before the next render, so
            // its checkbox can be shown immediately without a wrong-state flash.
            _isSelectionRestored = true,
            // Hidden/System entries are shown but coloured differently in the tree.
            IsHiddenOrSystem = entry.IsHiddenOrSystem,
        };

        // Determine backup status for files from the catalog.
        // Only for selected files — unselected files aren't part of the
        // backup, so showing "not backed up" would be misleading.
        var catalog = _getCatalogInfo?.Invoke();
        if (!entry.IsDirectory && catalog is not null && child._isSelected != false)
        {
            if (catalog.TryGetValue(entry.FullName, out var info))
            {
                child._backupStatus = (entry.Size != info.SizeBytes ||
                    File.GetLastWriteTimeUtc(entry.FullName) > info.SourceLastWriteUtc)
                    ? BackupStatus.Changed
                    : BackupStatus.BackedUp;
            }
            else
            {
                child._backupStatus = BackupStatus.NotBackedUp;
            }
        }

        return child;
    }

    /// <summary>
    /// Submit the directory nodes among <paramref name="candidates"/> that still
    /// need a size computed to the scheduler at high priority.  A directory needs
    /// computation when it has no unfiltered size yet, or (when a filter is
    /// active) still needs its filtered size.  No-op when "Show sizes" is off or
    /// no scheduler is wired.
    /// </summary>
    private void SubmitDirectorySizeComputation(IEnumerable<SourceSelectionNodeViewModel> candidates)
    {
        bool showSizes = _getShowSizes?.Invoke() ?? false;
        if (!showSizes || _scheduler is null)
            return;

        bool hasFilter = _scheduler.GlobalExcludeFilter is not null;
        var dirNodes = candidates.Where(c => c.IsDirectory
            && (c._size < 0 || (hasFilter && c._filteredSize < 0))).ToList();
        if (dirNodes.Count > 0)
            _ = ComputePrioritySizesAsync(dirNodes);
    }

    /// <summary>
    /// Re-read an already-loaded directory when it is re-expanded, adding
    /// children that appeared on disk and removing ones that disappeared since
    /// the last enumeration.  Existing child nodes are preserved (keeping their
    /// selection, expansion, and computed sizes); only the set difference is
    /// applied.  The tree is otherwise a one-time snapshot with no live refresh,
    /// so this keeps it in sync with filesystem changes made outside the app.
    /// </summary>
    private async Task ReconcileChildrenAsync()
    {
        // Guard against overlapping passes (rapid collapse/expand).
        if (_reconciling)
            return;
        _reconciling = true;
        try
        {
            var (entries, readFailed) = await EnumerateChildEntriesAsync();

            // Could not read the directory at all — leave the existing snapshot
            // untouched rather than wiping it (mirrors the LoadChildrenAsync
            // guard that protects a saved selection).
            if (readFailed && entries.Count == 0)
                return;

            var onDisk = new HashSet<string>(
                entries.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
            var existing = new Dictionary<string, SourceSelectionNodeViewModel>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var child in Children)
                existing[child.Path] = child;

            bool anyAdded = entries.Any(e => !existing.ContainsKey(e.FullName));
            bool anyRemoved = Children.Any(c => !onDisk.Contains(c.Path));
            if (!anyAdded && !anyRemoved)
                return;

            // Build the merged list, reusing existing nodes (to preserve their
            // selection/expansion/sizes) and creating nodes for new entries.
            var merged = new List<SourceSelectionNodeViewModel>(entries.Count);
            var newDirNodes = new List<SourceSelectionNodeViewModel>();
            foreach (var entry in entries)
            {
                if (existing.TryGetValue(entry.FullName, out var node))
                {
                    merged.Add(node);
                }
                else
                {
                    var child = CreateChildNode(entry);
                    merged.Add(child);
                    if (child.IsDirectory)
                        newDirNodes.Add(child);
                }
            }

            Children.ReplaceAll(merged);
            SortChildren();

            if (_getCatalogInfo?.Invoke() is not null)
                UpdateDirectoryBackupStatus();

            // A removed child could flip this node's tristate (e.g. the only
            // unselected child is gone → now fully selected).
            UpdateFromChildren();

            // Compute sizes for newly-appeared directories only.
            SubmitDirectorySizeComputation(newDirNodes);

            _onSelectionChanged?.Invoke();
        }
        finally
        {
            _reconciling = false;
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
                // Hidden/System directories are included in size totals so the
                // displayed size matches what the backup engine actually copies.
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
                // Hidden/System directories are included in size totals so the
                // displayed size matches what the backup engine actually copies.
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
                // Hidden/System directories are included in size totals so the
                // displayed size matches what the backup engine actually copies.
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
    /// <summary>
    /// Re-stamp backup status across this already-loaded subtree.  Used when the
    /// catalog dictionary is loaded lazily (after the tree is built) so nodes
    /// created before it arrived pick up their BackedUp/Changed/NotBackedUp
    /// badges.  Only walks nodes that are already loaded — collapsed directories
    /// stamp themselves when expanded (their children read the shared catalog
    /// getter at that point).
    /// </summary>
    internal void RefreshBackupStatusRecursive()
    {
        var catalog = _getCatalogInfo?.Invoke();
        if (catalog is null)
            return;

        foreach (var child in Children)
        {
            if (child.IsDirectory)
            {
                if (child._isLoaded)
                    child.RefreshBackupStatusRecursive();
            }
            else if (child._isSelected != false)
            {
                child.BackupStatus =
                    catalog.TryGetValue(child.Path, out var info)
                        ? (child._size != info.SizeBytes ||
                           File.GetLastWriteTimeUtc(child.Path) > info.SourceLastWriteUtc)
                            ? BackupStatus.Changed
                            : BackupStatus.BackedUp
                        : BackupStatus.NotBackedUp;
            }
        }

        if (IsDirectory && _isLoaded && Children.Count > 0)
            UpdateDirectoryBackupStatus();
    }

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
    /// Depth-first, bottom-up: recompute every loaded directory's tristate from its
    /// children so a node restored with a state that disagrees with its subtree is
    /// corrected.  During a selection restore each node's <see cref="IsSelected"/> is
    /// applied verbatim from the saved model (see <see cref="ApplySelectionAsync"/>),
    /// which can leave a directory saved as fully-checked showing a full check even
    /// though some of its children are excluded.  Unlike <see cref="UpdateFromChildren"/>
    /// this walks <em>down</em> first (so children are settled before their parent) and
    /// never ripples up to <see cref="Parent"/>, so it can be run once on the tree root
    /// after a restore without racing concurrent sibling restores.  Assignments are
    /// suppressed, so they don't push state back down or mark the set dirty.
    /// </summary>
    internal void RecomputeLoadedTristate()
    {
        if (!IsDirectory || !_isLoaded || Children.Count == 0)
            return;

        foreach (var child in Children)
            child.RecomputeLoadedTristate();

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
    }

    /// <summary>
    /// Re-derive this node's <see cref="AutoIncludeNew"/> flag from its directory
    /// children.  Used for the virtual "All Drives" root, whose own state is never
    /// persisted (GetSelections unwraps it and saves only the drive children).
    /// Without this the root's auto-include flag reverts to the constructor default
    /// (true) on every reload, silently undoing a user who turned it off.  The
    /// backing field is set directly (not via the setter) so this restore-time
    /// derivation neither propagates back down to the drives nor marks the set dirty.
    /// </summary>
    internal void UpdateAutoIncludeFromChildren()
    {
        var dirs = Children.Where(c => c.IsDirectory).ToList();
        if (dirs.Count == 0)
            return;

        bool derived = dirs.All(c => c.AutoIncludeNew);
        if (_autoIncludeNew != derived)
        {
            _autoIncludeNew = derived;
            OnPropertyChanged(nameof(AutoIncludeNew));
        }
    }

    /// <summary>
    /// Build a <see cref="Core.Models.SourceSelection"/> tree from this viewmodel tree.
    /// Only includes nodes that are at least partially selected.
    /// </summary>
    public Core.Models.SourceSelection? ToModel()
    {
        if (IsSelected == false)
        {
            // Normally a deselected node doesn't need saving — on reload it
            // starts deselected by default.  BUT if the parent would auto-include
            // this node (fully-selected parent, or partially-selected + auto-
            // include-on), the exclusion must be persisted explicitly — otherwise
            // on reload the node comes back selected and the user's exclusion is
            // silently undone.
            bool parentWouldAutoInclude =
                Parent is not null &&
                Parent.IsSelected != false &&
                (Parent.IsSelected == true || Parent.AutoIncludeNew);
            if (!parentWouldAutoInclude)
                return null;

            // Persist just the exclusion — no need to recurse into children.
            return new Core.Models.SourceSelection
            {
                Path = Path,
                IsDirectory = IsDirectory,
                IsSelected = false,
            };
        }

        // Auto-include derivation: this node renders checked only because it's an
        // unlisted descendant of a partially-selected auto-include directory (see
        // _isAutoIncludeDerived).  Don't serialise it — the parent's auto-include
        // re-derives it on reload, and skipping it keeps the saved selection
        // identical to what it was before the checkbox began showing checked
        // (no pinning, and MainViewModel's SelectionsEquivalent sees no change).
        // Any real edit clears the flag, so a genuinely curated subtree still
        // serialises.
        if (_isAutoIncludeDerived)
            return null;

        // Lossless fallback: this directory has a saved selection but its
        // children were never successfully enumerated (drive not ready, I/O
        // error, access denied — _isLoaded is false).  Re-deriving from the
        // empty Children collection would emit a selected-but-childless node
        // and permanently destroy the saved subtree.  Return the originally
        // restored model verbatim so the selection survives untouched.
        if (IsDirectory && !_isLoaded && _restoredModel is not null)
        {
            // Normally return the saved subtree verbatim.  But fold in the user's
            // current auto-include-new choice, which may have been toggled since
            // restore: the normal toggle path loads + pins children (making _isLoaded
            // true so we never reach here), but this guards the race where children
            // never loaded — e.g. an unreadable dir, or Save racing the load — so the
            // flag itself isn't silently lost.
            if (_restoredModel.AutoIncludeNewSubdirectories == AutoIncludeNew)
                return _restoredModel;
            return new Core.Models.SourceSelection
            {
                Path = _restoredModel.Path,
                IsDirectory = _restoredModel.IsDirectory,
                IsSelected = _restoredModel.IsSelected,
                IsExpanded = _restoredModel.IsExpanded,
                AutoIncludeNewSubdirectories = AutoIncludeNew,
                Children = _restoredModel.Children,
            };
        }

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
