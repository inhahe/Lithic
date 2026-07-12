using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Services;

namespace LithicBackup.ViewModels;

/// <summary>Which column the source selection tree is sorted by.</summary>
public enum SortColumn { Name, Size }

/// <summary>
/// ViewModel for the source selection view. Presents the filesystem as a
/// tristate checkbox treeview rooted at available drives, plus editable
/// backup-set-level settings (name, destination, exclusions, dedup,
/// retention tiers).
/// </summary>
public class SourceSelectionViewModel : ViewModelBase
{
    private bool _hasSelection;
    private bool _updatingAllAutoInclude;
    private bool _updatingAllSelected;
    private bool _showSizes = true;
    private TierSetViewModel? _selectedTierSet;
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private bool _showSelectedSizesOnly;
    private string _selectedSizeText = "";
    private string _setName = "";
    private string _excludedExtensions = "";
    private string _targetDirectory = "";
    private bool _isDirectoryMode;
    private bool _createSubdirectory;
    private string _subdirectoryName = "";
    private bool _enableFileDeduplication;
    private bool _enableBlockDeduplication;
    private string _blockSizeKb = "64";
    private ZipMode _zipMode = ZipMode.IncompatibleOnly;
    private FilesystemType _filesystemType = FilesystemType.UDF;
    private string _capacityOverrideGb = "";
    private bool _verifyAfterBurn = true;
    private bool _includeCatalogOnDisc = true;
    private bool _allowFileSplitting = true;
    private bool _scheduleEnabled;
    private ScheduleMode _scheduleMode = ScheduleMode.Interval;
    private string _scheduleIntervalHours = "24";
    private int _scheduleDailyHour = 2;
    private int _scheduleDailyMinute;
    private string _scheduleDebounceSeconds = "60";
    private readonly SizeComputeScheduler _scheduler = new();
    private Dictionary<string, FileVersionInfo>? _catalogInfo;
    private readonly List<DriveData>? _preloadedDrives;
    private bool _showLargestFiles;
    private bool _isApplyingSelections;
    private Func<string, bool>? _cachedExcludeFilter;
    private string? _cachedExcludeFilterSignature;
    private bool _excludeFilterDirty = true;
    private bool _isEditMode;
    private string _saveStatusText = "";
    private bool _isCalculatingSize;
    private string _sizeCalculationResult = "";
    private bool _isSeeding;
    private string _seedResult = "";
    private bool _isClearingHistory;
    private string _clearHistoryResult = "";
    private bool _seedSkipHashing = true;
    private CancellationTokenSource? _seedCts;
    private bool _needsSave = true;
    private string _destinationSpaceText = "";
    private readonly FileHashCache? _fileHashCache;
    private readonly IFileScanner? _scanner;
    private bool _isAnalyzingDedup;
    private string _dedupAnalysisResult = "";
    private CancellationTokenSource? _dedupCts;

    /// <summary>
    /// Nodes whose checkbox was toggled but whose (potentially expensive)
    /// propagation + size-aggregation work has been deferred off the click's
    /// synchronous path.  Drained by <see cref="SettlePendingSelections"/> at
    /// Background priority so the clicked checkbox repaints first.
    /// </summary>
    private readonly HashSet<SourceSelectionNodeViewModel> _pendingSelectionNodes = [];
    /// <summary>
    /// The full path of every node whose checkbox the user toggled during this
    /// editing session.  Recorded in <see cref="RequestSelectionSettle"/>, which
    /// receives only user-clicked nodes (propagation to children/ancestors is
    /// suppressed before it reaches the setter's settle call).  Used by the
    /// post-edit reconcile to query the catalog for ONLY the changed subtrees
    /// instead of loading the entire (potentially ~1M-row) file table: every
    /// file whose inclusion changed sits under one of these toggled nodes, so
    /// scoping catalog reads to them is both correct and far cheaper.
    /// </summary>
    private readonly HashSet<string> _changedSelectionPaths =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True once a settle pass has been scheduled but not yet run.</summary>
    private bool _selectionSettleScheduled;
    /// <summary>
    /// Completes when the currently-pending settle pass finishes.  Save awaits
    /// this (with a busy cursor) so it never persists a half-propagated tree.
    /// Null when nothing is pending.
    /// </summary>
    private TaskCompletionSource? _selectionSettleTcs;

    /// <summary>
    /// Raised just before a deferred selection-settle pass runs its
    /// propagation/aggregation work.  The view uses this to snapshot the tree's
    /// scroll offset so it can restore it after the pass — the propagation can
    /// trigger a layout re-measure that would otherwise nudge the scroll.
    /// </summary>
    public event Action? SelectionSettleStarting;

    /// <summary>
    /// Raised right after a deferred selection-settle pass completes.  The view
    /// restores the scroll offset captured on <see cref="SelectionSettleStarting"/>.
    /// </summary>
    public event Action? SelectionSettleCompleted;

    /// <summary>Fired when the user clicks "Next" with a valid selection.</summary>
    public event Action<List<SourceSelection>>? NextRequested;

    /// <summary>Fired when the user clicks "Save" in edit mode.</summary>
    public event Func<Task>? SaveRequested;

    /// <summary>Fired when the user clicks "Cancel" / "Close".</summary>
    public event Action? CancelRequested;

    /// <summary>Fired when the user clicks "Largest Files &amp; Directories".</summary>
    public event Action? LargestFilesRequested;

    /// <summary>Fired whenever a checkbox selection changes in the tree.</summary>
    public event Action? SelectionChanged;

    /// <summary>Fired when tier set file patterns or exempt patterns change.</summary>
    public event Action? ExclusionSettingsChanged;

    /// <summary>Fired when the user clicks "Seed from Existing Backup".</summary>
    public event Func<Task>? SeedFromExistingRequested;

    /// <summary>Fired when the user clicks "Clear Backup History".</summary>
    public event Func<Task>? ClearHistoryRequested;

    /// <summary>
    /// Pre-computed drive data from a background thread.
    /// Avoids blocking the UI thread on <see cref="DriveInfo.GetDrives"/>
    /// and <see cref="DriveInfo.TotalSize"/> calls which can be slow when
    /// network or removable drives are present.
    /// </summary>
    public record DriveData(string RootPath, long UsedSize);

    /// <summary>
    /// Enumerate available drives on a background thread.
    /// Call this from <c>Task.Run</c> and pass the result to the constructor.
    /// </summary>
    public static List<DriveData> EnumerateDrives()
    {
        var result = new List<DriveData>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            if (drive.DriveType == DriveType.CDRom) continue;
            long usedSize = -1;
            try { usedSize = drive.TotalSize - drive.AvailableFreeSpace; }
            catch { }
            result.Add(new DriveData(drive.RootDirectory.FullName, usedSize));
        }
        return result;
    }

    public SourceSelectionViewModel(
        Dictionary<string, FileVersionInfo>? catalogInfo = null,
        List<DriveData>? preloadedDrives = null,
        FileHashCache? fileHashCache = null,
        IFileScanner? scanner = null)
    {
        _catalogInfo = catalogInfo;
        _preloadedDrives = preloadedDrives;
        _fileHashCache = fileHashCache;
        _scanner = scanner;
        Roots = [];
        RetentionTiers = [];
        TierSets = [];
        NextCommand = new RelayCommand(_ => OnNext(), _ => HasSelection && !IsEditMode);
        SaveCommand = new RelayCommand(_ => OnSave(), _ => _needsSave && IsEditMode);
        CancelCommand = new RelayCommand(_ => CancelRequested?.Invoke());
        LargestFilesCommand = new RelayCommand(
            _ => LargestFilesRequested?.Invoke(),
            _ => ShowLargestFiles);
        SortByNameCommand = new RelayCommand(_ => ToggleSort(SortColumn.Name));
        SortBySizeCommand = new RelayCommand(_ => ToggleSort(SortColumn.Size));
        BrowseDirectoryCommand = new RelayCommand(_ => BrowseDirectory());
        AddRetentionTierCommand = new RelayCommand(_ => AddRetentionTier());
        AddTierSetCommand = new RelayCommand(_ => AddTierSet());
        RemoveTierSetCommand = new RelayCommand(_ => RemoveSelectedTierSet(),
            _ => _selectedTierSet is not null && !_selectedTierSet.IsBuiltIn);
        AddPathCommand = new RelayCommand(_ => AddCustomPath());
        AutoIncludeCheckedCommand = new RelayCommand(_ => SetAutoIncludeOnChecked());
        CalculateSizeCommand = new RelayCommand(
            _ => _ = OnCalculateSize(),
            _ => !IsCalculatingSize);
        SeedFromExistingCommand = new RelayCommand(
            _ => _ = OnSeedFromExisting(),
            _ => !IsSeeding && IsEditMode && IsDirectoryMode && !string.IsNullOrWhiteSpace(TargetDirectory));
        ClearHistoryCommand = new RelayCommand(
            _ => OnClearHistory(),
            _ => IsEditMode && !_isClearingHistory);
        CancelSeedCommand = new RelayCommand(
            _ => _seedCts?.Cancel(),
            _ => IsSeeding && _seedCts is not null && !_seedCts.IsCancellationRequested);
        ApplyFiltersCommand = new RelayCommand(_ => ApplyFilters(), _ => _filtersPendingApply);
        AnalyzeDedupCommand = new RelayCommand(
            _ => _ = OnAnalyzeDedupAsync(),
            _ => !_isAnalyzingDedup);
        CancelDedupCommand = new RelayCommand(
            _ => _dedupCts?.Cancel(),
            _ => _isAnalyzingDedup && _dedupCts is not null && !_dedupCts.IsCancellationRequested);

        // Clear the "Saved" indicator and mark dirty whenever the user changes anything.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SaveStatusText)
                    or nameof(HasSelection)
                    or nameof(ShowSizes) or nameof(ShowSelectedSizesOnly)
                    or nameof(SelectedSizeText) or nameof(NameSortIndicator)
                    or nameof(SizeSortIndicator) or nameof(CurrentSortColumn)
                    or nameof(SortAscending) or nameof(IsApplyingSelections)
                    or nameof(ShowLargestFiles) or nameof(IsEditMode)
                    or nameof(IsAllAutoIncludeNew) or nameof(IsAllSelected)
                    or nameof(CanEditSelectedTierSet)
                    or nameof(IsCalculatingSize) or nameof(SizeCalculationResult)
                    or nameof(IsSeeding) or nameof(SeedResult)
                    or nameof(DestinationSpaceText)
                    or nameof(IsAnalyzingDedup) or nameof(DedupAnalysisResult)))
            {
                if (!string.IsNullOrEmpty(_saveStatusText))
                    SaveStatusText = "";
                if (!_needsSave)
                {
                    _needsSave = true;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        };
        SelectionChanged += () =>
        {
            if (!string.IsNullOrEmpty(_saveStatusText))
                SaveStatusText = "";
            if (!_needsSave)
            {
                _needsSave = true;
                CommandManager.InvalidateRequerySuggested();
            }
        };

        LoadDriveRoots();

        // Initialise retention tiers from defaults.
        foreach (var tier in VersionRetentionService.DefaultTiers)
        {
            var vm = RetentionTierViewModel.FromModel(tier);
            vm.RemoveRequested += t => RetentionTiers.Remove(t);
            RetentionTiers.Add(vm);
        }

        // Initialise built-in tier sets.
        InitializeDefaultTierSets();
    }

    public ObservableCollection<SourceSelectionNodeViewModel> Roots { get; }

    public bool HasSelection
    {
        get => _hasSelection;
        set => SetProperty(ref _hasSelection, value);
    }

    /// <summary>
    /// Header checkbox for the Auto-include column.
    /// Setting this propagates to all directory nodes in the tree.
    /// Toggle logic: if all checked → uncheck all, otherwise → check all.
    /// </summary>
    public bool? IsAllAutoIncludeNew
    {
        get
        {
            var dirs = GetAllDirectoryNodes();
            if (dirs.Count == 0) return false;
            bool allTrue = dirs.All(d => d.AutoIncludeNew);
            bool allFalse = dirs.All(d => !d.AutoIncludeNew);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            if (_updatingAllAutoInclude) return;
            _updatingAllAutoInclude = true;
            try
            {
                var dirs = GetAllDirectoryNodes();
                if (dirs.Count == 0) return;
                bool target = !dirs.All(d => d.AutoIncludeNew);
                foreach (var node in dirs)
                    node.AutoIncludeNew = target;
                OnPropertyChanged();
            }
            finally
            {
                _updatingAllAutoInclude = false;
            }
        }
    }

    /// <summary>
    /// Header checkbox for the Include column.
    /// Setting this propagates to all root nodes (which cascade to children).
    /// Toggle logic: if all checked → uncheck all, otherwise → check all.
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            if (Roots.Count == 0) return false;
            bool allTrue = Roots.All(r => r.IsSelected == true);
            bool allFalse = Roots.All(r => r.IsSelected == false);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            if (_updatingAllSelected) return;
            _updatingAllSelected = true;
            try
            {
                bool target = !Roots.All(r => r.IsSelected == true);
                foreach (var root in Roots)
                    root.IsSelected = target;
                OnPropertyChanged();
            }
            finally
            {
                _updatingAllSelected = false;
            }
        }
    }

    /// <summary>
    /// Named tier set definitions. The "Default" and "None" sets are built-in;
    /// users can add custom sets. Each set contains an editable list of
    /// retention tiers. The currently selected set's tiers are shown in the
    /// backup settings panel.
    /// </summary>
    public ObservableCollection<TierSetViewModel> TierSets { get; }

    /// <summary>
    /// Currently selected tier set in the backup settings editor.
    /// Determines which retention tiers are displayed for editing.
    /// </summary>
    public TierSetViewModel? SelectedTierSet
    {
        get => _selectedTierSet;
        set
        {
            if (_selectedTierSet is not null)
                _selectedTierSet.PropertyChanged -= OnTierSetPropertyChanged;
            if (!SetProperty(ref _selectedTierSet, value))
                return;
            if (_selectedTierSet is not null)
                _selectedTierSet.PropertyChanged += OnTierSetPropertyChanged;
            OnPropertyChanged(nameof(CanEditSelectedTierSet));
        }
    }

    private void OnTierSetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_saveStatusText))
            SaveStatusText = "";
        if (!_needsSave)
        {
            _needsSave = true;
            CommandManager.InvalidateRequerySuggested();
        }

        // Mark the cached exclusion filter as stale so the next
        // ApplyFilters call rebuilds it.  Don't trigger the expensive
        // tree refresh automatically — the user clicks "Apply" when
        // they're done editing patterns.
        if (e.PropertyName is nameof(TierSetViewModel.FilePatternsText)
                           or nameof(TierSetViewModel.FileExemptPatternsText))
        {
            _excludeFilterDirty = true;
            _filtersPendingApply = true;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Whether the user has edited filter patterns since the last Apply.
    /// Drives the Apply button's enabled state.
    /// </summary>
    private bool _filtersPendingApply;

    /// <summary>
    /// Rebuild the exclusion filter from the current pattern fields and
    /// refresh all filtered sizes in the tree.  Called when the user clicks
    /// the "Apply" button next to the pattern fields.
    /// </summary>
    internal void ApplyFilters()
    {
        _filtersPendingApply = false;
        CommandManager.InvalidateRequerySuggested();

        var newFilter = GetExcludeFilter();
        _scheduler.GlobalExcludeFilter = newFilter;
        _scheduler.GlobalExcludeFilterSignature = _cachedExcludeFilterSignature;
        foreach (var root in Roots)
            root.ResetFilteredSizes();
        if (_showSelectedSizesOnly)
            RefreshSelectedSize();
        _ = ComputeAllUnknownSizesAsync();

        ExclusionSettingsChanged?.Invoke();
    }

    /// <summary>
    /// Build the combined exclusion filter from tier sets with 0 tiers and
    /// global excluded extensions.  Mirrors <c>DirectoryBackupService.BuildExclusionFilter</c>.
    /// The result is cached and rebuilt only when tier set patterns change.
    /// </summary>
    internal Func<string, bool>? GetExcludeFilter()
    {
        if (!_excludeFilterDirty)
            return _cachedExcludeFilter;

        _excludeFilterDirty = false;
        _cachedExcludeFilter = null;
        _cachedExcludeFilterSignature = null;

        // Build tier-set-based exclusion (0-tier sets with patterns).
        Func<string, VersionTierSet>? tierResolver = null;
        var tierModels = TierSets.Select(ts => ts.ToModel()).ToList();
        if (tierModels.Count > 0)
        {
            bool hasExclusion = tierModels.Any(ts =>
                ts.Tiers.Count == 0
                && ts.FilePatterns.Count > 0
                && !string.Equals(ts.Name, "Default", StringComparison.OrdinalIgnoreCase));
            if (hasExclusion)
                tierResolver = VersionTierSet.BuildTierResolver(tierModels);
        }

        if (tierResolver is null)
            return null;

        _cachedExcludeFilter = path =>
        {
            if (tierResolver(path).Tiers.Count == 0)
                return true;
            return false;
        };
        _cachedExcludeFilterSignature = ComputeExcludeFilterSignature(tierModels);
        return _cachedExcludeFilter;
    }

    /// <summary>
    /// Compute a deterministic signature for the active exclusion filter.
    /// Used by <see cref="DirectorySizeCache"/> to invalidate cached filtered
    /// recursive sizes when the user changes tier-set patterns. All tier-set
    /// inputs that <c>BuildTierResolver</c> consumes are included, since any
    /// of them can change which tier set a given path resolves to.
    /// </summary>
    private static string ComputeExcludeFilterSignature(List<VersionTierSet> tierModels)
    {
        var sb = new System.Text.StringBuilder();
        // Sort by name for determinism — TierSets order should not affect
        // the signature when the same sets are present.
        foreach (var ts in tierModels.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("ts=").Append(ts.Name);
            sb.Append("|tiers=").Append(ts.Tiers.Count);
            sb.Append("|fp=");
            foreach (var p in ts.FilePatterns.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                sb.Append(p).Append(';');
            sb.Append("|fxp=");
            foreach (var p in ts.FileExemptPatterns.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                sb.Append(p).Append(';');
            sb.Append('\n');
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Whether the selected tier set's tiers can be edited (not for "None").
    /// </summary>
    public bool CanEditSelectedTierSet =>
        _selectedTierSet is not null && _selectedTierSet.Name != "None";

    /// <summary>
    /// Whether to display file and directory sizes. When enabled, directory
    /// sizes are computed in the background as directories are expanded and
    /// fill in progressively.
    /// </summary>
    public bool ShowSizes
    {
        get => _showSizes;
        set
        {
            if (!SetProperty(ref _showSizes, value)) return;

            if (value)
            {
                // Compute sizes for any directories already expanded.
                _ = ComputeAllUnknownSizesAsync();
            }
            else
            {
                // Can't sort by size or filter by selection when sizes aren't shown.
                if (_sortColumn == SortColumn.Size)
                    ToggleSort(SortColumn.Name);
                if (_showSelectedSizesOnly) ShowSelectedSizesOnly = false;
            }
        }
    }

    /// <summary>
    /// When true, the size column only shows sizes for selected (checked)
    /// items. Unselected items show no size. Automatically enables
    /// <see cref="ShowSizes"/> if not already on.
    /// </summary>
    public bool ShowSelectedSizesOnly
    {
        get => _showSelectedSizesOnly;
        set
        {
            if (!SetProperty(ref _showSelectedSizesOnly, value)) return;

            if (value && !_showSizes)
                ShowSizes = true;

            // Refresh all loaded nodes so FormattedSize re-evaluates.
            foreach (var root in Roots)
                root.RefreshFormattedSize();

            // Re-sort when the size column is active because the effective
            // sort keys change between raw size and selected-only size.
            if (_sortColumn == SortColumn.Size)
            {
                SortRoots();
                foreach (var root in Roots)
                    root.SortChildren();
            }
        }
    }

    /// <summary>Which column is currently sorted.</summary>
    public SortColumn CurrentSortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    /// <summary>True = ascending (A-Z or smallest first), False = descending.</summary>
    public bool SortAscending
    {
        get => _sortAscending;
        private set => SetProperty(ref _sortAscending, value);
    }

    /// <summary>Sort indicator text for the Name column header.</summary>
    public string NameSortIndicator => _sortColumn == SortColumn.Name
        ? (_sortAscending ? " \u25B2" : " \u25BC")
        : "";

    /// <summary>Sort indicator text for the Size column header.</summary>
    public string SizeSortIndicator => _sortColumn == SortColumn.Size
        ? (_sortAscending ? " \u25B2" : " \u25BC")
        : "";

    /// <summary>
    /// Summary text showing the total size of selected files.
    /// Computed from loaded nodes whose sizes are known.
    /// </summary>
    public string SelectedSizeText
    {
        get => _selectedSizeText;
        private set => SetProperty(ref _selectedSizeText, value);
    }

    // --- Backup set settings ---

    /// <summary>Name of the backup set (editable).</summary>
    public string SetName
    {
        get => _setName;
        set => SetProperty(ref _setName, value);
    }

    /// <summary>
    /// Newline-separated glob patterns to exclude from the backup at the set
    /// level. Round-trips to <see cref="JobOptions.ExcludedExtensions"/>.
    /// Despite the stored field's legacy name these are full glob patterns
    /// (e.g. <c>*.log</c>, <c>*/bin/*</c>), not just file extensions.
    /// </summary>
    public string ExcludedExtensions
    {
        get => _excludedExtensions;
        set => SetProperty(ref _excludedExtensions, value);
    }

    /// <summary>Whether to back up to a directory instead of optical disc.</summary>
    public bool IsDirectoryMode
    {
        get => _isDirectoryMode;
        set => SetProperty(ref _isDirectoryMode, value);
    }

    /// <summary>Target directory path for directory-mode backups.</summary>
    public string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            if (!SetProperty(ref _targetDirectory, value)) return;
            RefreshDestinationSpace();
        }
    }

    /// <summary>
    /// Info line showing destination drive total capacity and free space.
    /// Refreshed automatically when <see cref="TargetDirectory"/> changes.
    /// </summary>
    public string DestinationSpaceText
    {
        get => _destinationSpaceText;
        private set => SetProperty(ref _destinationSpaceText, value);
    }

    /// <summary>Whether to create a subdirectory under the target.</summary>
    public bool CreateSubdirectory
    {
        get => _createSubdirectory;
        set => SetProperty(ref _createSubdirectory, value);
    }

    /// <summary>Name of the subdirectory to create.</summary>
    public string SubdirectoryName
    {
        get => _subdirectoryName;
        set => SetProperty(ref _subdirectoryName, value);
    }

    // -- Disc-mode options --

    public ZipMode ZipMode
    {
        get => _zipMode;
        set => SetProperty(ref _zipMode, value);
    }

    public FilesystemType FilesystemType
    {
        get => _filesystemType;
        set => SetProperty(ref _filesystemType, value);
    }

    /// <summary>Capacity override in GB (empty = auto-detect).</summary>
    public string CapacityOverrideGb
    {
        get => _capacityOverrideGb;
        set => SetProperty(ref _capacityOverrideGb, value);
    }

    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    public bool IncludeCatalogOnDisc
    {
        get => _includeCatalogOnDisc;
        set => SetProperty(ref _includeCatalogOnDisc, value);
    }

    public bool AllowFileSplitting
    {
        get => _allowFileSplitting;
        set => SetProperty(ref _allowFileSplitting, value);
    }

    public ZipMode[] ZipModeOptions { get; } = Enum.GetValues<ZipMode>();
    public FilesystemType[] FilesystemTypeOptions { get; } = Enum.GetValues<FilesystemType>();

    // -- Directory-mode options --

    /// <summary>Whether to enable file-level deduplication (identical files stored once).</summary>
    public bool EnableFileDeduplication
    {
        get => _enableFileDeduplication;
        set => SetProperty(ref _enableFileDeduplication, value);
    }

    /// <summary>Whether to enable block-level deduplication.</summary>
    public bool EnableBlockDeduplication
    {
        get => _enableBlockDeduplication;
        set => SetProperty(ref _enableBlockDeduplication, value);
    }

    /// <summary>Block size in KB for block-level deduplication.</summary>
    public string BlockSizeKb
    {
        get => _blockSizeKb;
        set => SetProperty(ref _blockSizeKb, value);
    }

    /// <summary>Configurable version retention tiers.</summary>
    public ObservableCollection<RetentionTierViewModel> RetentionTiers { get; }

    // -- Schedule (directory mode) --

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetProperty(ref _scheduleEnabled, value);
    }

    public ScheduleMode ScheduleMode
    {
        get => _scheduleMode;
        set => SetProperty(ref _scheduleMode, value);
    }

    public ScheduleMode[] ScheduleModeOptions { get; } = Enum.GetValues<ScheduleMode>();

    public string ScheduleIntervalHours
    {
        get => _scheduleIntervalHours;
        set => SetProperty(ref _scheduleIntervalHours, value);
    }

    public int ScheduleDailyHour
    {
        get => _scheduleDailyHour;
        set => SetProperty(ref _scheduleDailyHour, value);
    }

    public int ScheduleDailyMinute
    {
        get => _scheduleDailyMinute;
        set => SetProperty(ref _scheduleDailyMinute, value);
    }

    public string ScheduleDebounceSeconds
    {
        get => _scheduleDebounceSeconds;
        set => SetProperty(ref _scheduleDebounceSeconds, value);
    }

    /// <summary>
    /// Whether to show the "Largest Files &amp; Directories" button.
    /// Enabled only when editing an existing backup set (not for new sets).
    /// </summary>
    public bool ShowLargestFiles
    {
        get => _showLargestFiles;
        set => SetProperty(ref _showLargestFiles, value);
    }

    /// <summary>
    /// True while <see cref="ApplySelectionsAsync"/> is restoring saved state.
    /// External listeners (e.g. auto-save) should ignore <see cref="SelectionChanged"/>
    /// events while this is set, to avoid overwriting saved data with partially-restored state.
    /// The view can bind to this to show a loading overlay while the tree is populated.
    /// </summary>
    public bool IsApplyingSelections
    {
        get => _isApplyingSelections;
        internal set => SetProperty(ref _isApplyingSelections, value);
    }

    /// <summary>
    /// When true the view is in "edit existing set" mode: shows Save/Close
    /// buttons instead of Next/Cancel, and hides the wizard-step flow.
    /// </summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set => SetProperty(ref _isEditMode, value);
    }

    /// <summary>
    /// Brief status text shown after a save ("Saved", "Save failed", etc.).
    /// Cleared automatically when the user makes further changes.
    /// </summary>
    public string SaveStatusText
    {
        get => _saveStatusText;
        set => SetProperty(ref _saveStatusText, value);
    }

    /// <summary>True while a backup size calculation is in progress.</summary>
    public bool IsCalculatingSize
    {
        get => _isCalculatingSize;
        set => SetProperty(ref _isCalculatingSize, value);
    }

    /// <summary>
    /// Result of the most recent size calculation (multi-line text summary).
    /// Empty when no calculation has been run or the result was cleared.
    /// </summary>
    public string SizeCalculationResult
    {
        get => _sizeCalculationResult;
        set => SetProperty(ref _sizeCalculationResult, value);
    }

    /// <summary>True while a seed-from-existing operation is in progress.</summary>
    public bool IsSeeding
    {
        get => _isSeeding;
        set => SetProperty(ref _isSeeding, value);
    }

    /// <summary>Result of the most recent seed operation.</summary>
    public string SeedResult
    {
        get => _seedResult;
        set => SetProperty(ref _seedResult, value);
    }

    /// <summary>Result of the most recent clear-history operation.</summary>
    public string ClearHistoryResult
    {
        get => _clearHistoryResult;
        set => SetProperty(ref _clearHistoryResult, value);
    }

    /// <summary>
    /// When true, the seed operation skips SHA-256 hashing and records only
    /// file size + last-write-time.  Much faster — hashing is not needed for
    /// incremental detection (which uses size + timestamp).
    /// </summary>
    public bool SeedSkipHashing
    {
        get => _seedSkipHashing;
        set => SetProperty(ref _seedSkipHashing, value);
    }

    /// <summary>Token for the in-progress seed operation (read by the handler).</summary>
    public CancellationToken SeedCancellationToken => _seedCts?.Token ?? CancellationToken.None;

    /// <summary>True while a dedup analysis is running.</summary>
    public bool IsAnalyzingDedup
    {
        get => _isAnalyzingDedup;
        private set => SetProperty(ref _isAnalyzingDedup, value);
    }

    /// <summary>Multi-line result string from the last dedup analysis.</summary>
    public string DedupAnalysisResult
    {
        get => _dedupAnalysisResult;
        private set => SetProperty(ref _dedupAnalysisResult, value);
    }

    /// <summary>
    /// True when there are unsaved changes. Used by the dialog to decide
    /// whether to prompt before closing, and by the Save button CanExecute.
    /// </summary>
    public bool HasUnsavedChanges => _needsSave;

    /// <summary>
    /// Paths of every node the user toggled this session.  The post-edit
    /// reconcile uses these to scope its catalog reads to just the changed
    /// subtrees.  A snapshot is returned so callers can iterate safely.
    /// </summary>
    public IReadOnlyCollection<string> ChangedSelectionPaths => _changedSelectionPaths.ToList();

    /// <summary>
    /// Mark the current state as clean (just saved or freshly loaded).
    /// Disables the Save button until the user makes further changes.
    /// </summary>
    public void MarkClean()
    {
        _needsSave = false;
        CommandManager.InvalidateRequerySuggested();
    }

    // --- Commands ---

    public ICommand NextCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LargestFilesCommand { get; }
    public ICommand CalculateSizeCommand { get; }
    public ICommand SeedFromExistingCommand { get; }
    public ICommand CancelSeedCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortBySizeCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand AddRetentionTierCommand { get; }
    public ICommand AddTierSetCommand { get; }
    public ICommand RemoveTierSetCommand { get; }
    public ICommand AddPathCommand { get; }
    public ICommand AutoIncludeCheckedCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand AnalyzeDedupCommand { get; }
    public ICommand CancelDedupCommand { get; }

    /// <summary>
    /// Called by the view (or a timer) to recheck whether any files are selected.
    /// </summary>
    public void RefreshHasSelection()
    {
        HasSelection = Roots.Any(r => r.IsSelected != false);
    }

    /// <summary>
    /// Recompute the total size of selected files from all loaded nodes.
    /// Called automatically when selection changes.
    /// </summary>
    public void RefreshSelectedSize()
    {
        long total = 0;
        int fileCount = 0;
        int dirCount = 0;
        var filter = _showSelectedSizesOnly ? GetExcludeFilter() : null;
        ComputeSelectedSize(Roots, filter, ref total, ref fileCount, ref dirCount);

        if (fileCount == 0 && dirCount == 0)
        {
            SelectedSizeText = "";
        }
        else
        {
            var parts = new List<string>();
            if (dirCount > 0) parts.Add($"{dirCount:N0} {(dirCount == 1 ? "directory" : "directories")}");
            if (fileCount > 0) parts.Add($"{fileCount:N0} {(fileCount == 1 ? "file" : "files")}");
            SelectedSizeText = $"Selected: {string.Join(", ", parts)}, {SourceSelectionNodeViewModel.FormatBytes(total)}";
        }
    }

    private static void ComputeSelectedSize(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        Func<string, bool>? excludeFilter,
        ref long total, ref int fileCount, ref int dirCount)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false)
                continue;

            // Skip nodes excluded by the backup filter (0-tier tier sets).
            if (excludeFilter is not null)
            {
                string testPath = node.IsDirectory
                    ? System.IO.Path.Combine(node.Path, "_")
                    : node.Path;
                if (excludeFilter(testPath))
                    continue;
            }

            if (!node.IsDirectory)
            {
                // Selected file — add its size.
                if (node.Size >= 0) total += node.Size;
                fileCount++;
            }
            else if (node.IsSelected == true)
            {
                // Fully selected directory — use its computed totals.
                // FileCount is the recursive count of all contained files,
                // which gives us an accurate number for catalog comparison
                // regardless of whether the node has been expanded.
                if (node.Size >= 0) total += node.Size;
                if (node.FileCount >= 0)
                    fileCount += node.FileCount;
                dirCount++;
            }
            else if (node.IsLoaded)
            {
                // Partially selected directory — recurse to count only
                // selected children.
                dirCount++;
                ComputeSelectedSize(node.Children, excludeFilter, ref total, ref fileCount, ref dirCount);
            }
        }
    }

    /// <summary>
    /// The virtual root node ("All Drives") that contains all drive nodes.
    /// Its exclusion patterns serve as the "global" exclusion list.
    /// </summary>
    public SourceSelectionNodeViewModel? RootNode { get; private set; }

    /// <summary>
    /// Aggregate handler fired once per settle pass (not once per node): refresh
    /// the has-selection flag and selected-size totals, invalidate the stale
    /// size-calculation result, and notify external listeners.
    /// </summary>
    private void HandleSelectionChanged()
    {
        RefreshHasSelection();
        RefreshSelectedSize();
        SizeCalculationResult = "";
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Called from a node's <see cref="SourceSelectionNodeViewModel.IsSelected"/>
    /// setter to defer the expensive propagation/aggregation work off the click's
    /// synchronous path.  Records the node, marks the set dirty synchronously (so
    /// the Save button enables immediately even before the pass runs), and
    /// schedules a single coalesced settle pass at Background priority — which
    /// runs only after the clicked checkbox has had a chance to repaint.
    /// </summary>
    private void RequestSelectionSettle(SourceSelectionNodeViewModel node)
    {
        _pendingSelectionNodes.Add(node);

        // Record the toggled path so the post-edit reconcile can scope its
        // catalog reads to just the changed subtrees.  This setter fires only
        // for the node the user actually clicked (propagation to descendants is
        // suppressed before it reaches here), so the set stays minimal.
        _changedSelectionPaths.Add(node.Path);

        // Mark dirty right away.  The heavy aggregation is deferred, but the
        // fact that *something* changed is known now, so the Save button should
        // enable without waiting for the settle pass.
        if (!string.IsNullOrEmpty(_saveStatusText))
            SaveStatusText = "";
        if (!_needsSave)
        {
            _needsSave = true;
            CommandManager.InvalidateRequerySuggested();
        }

        if (_selectionSettleScheduled)
            return;

        _selectionSettleScheduled = true;
        _selectionSettleTcs ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No dispatcher (tests) — settle inline.
            SettlePendingSelections();
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(SettlePendingSelections));
    }

    /// <summary>
    /// Drain the pending-selection set: propagate each toggled node's state to
    /// its children/ancestors, then raise the aggregate notification once.
    /// Completes the settle TCS so a waiting Save can proceed.
    /// </summary>
    private void SettlePendingSelections()
    {
        _selectionSettleScheduled = false;

        if (_pendingSelectionNodes.Count > 0)
        {
            // Snapshot so re-entrant toggles during propagation queue a fresh
            // pass rather than mutating the set we're iterating.
            var nodes = _pendingSelectionNodes.ToList();
            _pendingSelectionNodes.Clear();

            // Let the view snapshot the scroll offset: propagation can trigger a
            // layout re-measure that would otherwise nudge the tree's scroll.
            SelectionSettleStarting?.Invoke();

            foreach (var node in nodes)
                node.PropagateSelection();

            HandleSelectionChanged();

            SelectionSettleCompleted?.Invoke();
        }

        var tcs = _selectionSettleTcs;
        _selectionSettleTcs = null;
        tcs?.TrySetResult();
    }

    /// <summary>
    /// A task that completes when all pending selection work has settled.  If
    /// nothing is pending, returns a completed task.  Save awaits this so it
    /// never persists a half-propagated tree.
    /// </summary>
    private Task WaitForSelectionSettledAsync()
    {
        if (!_selectionSettleScheduled && _pendingSelectionNodes.Count == 0)
            return Task.CompletedTask;
        return (_selectionSettleTcs ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    private void LoadDriveRoots()
    {
        // Create a virtual "All Drives" root node.
        var root = new SourceSelectionNodeViewModel(
            "", true, null,
            () => ShowSizes, () => (_sortColumn, _sortAscending), _scheduler,
            HandleSelectionChanged,
            () => ShowSelectedSizesOnly,
            () => _catalogInfo,
            getExcludeFilter: () => GetExcludeFilter(),
            requestSelectionSettle: RequestSelectionSettle);
        RootNode = root;

        // Mark as loaded BEFORE setting IsExpanded — otherwise the
        // IsExpanded setter triggers LoadChildrenAsync on the empty path.
        root.IsLoaded = true;
        root.Children.Clear(); // Remove the "Loading..." dummy child.

        if (_preloadedDrives is not null)
        {
            // Use pre-computed drive data (enumerated on a background thread).
            foreach (var dd in _preloadedDrives)
            {
                var node = new SourceSelectionNodeViewModel(
                    dd.RootPath, true, root, getCatalogInfo: () => _catalogInfo);
                if (dd.UsedSize >= 0) node.Size = dd.UsedSize;
                root.Children.Add(node);
            }
        }
        else
        {
            // Fallback: enumerate on the calling thread (legacy callers).
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType == DriveType.CDRom) continue;
                var node = new SourceSelectionNodeViewModel(
                    drive.RootDirectory.FullName, true, root,
                    getCatalogInfo: () => _catalogInfo);
                try { node.Size = drive.TotalSize - drive.AvailableFreeSpace; }
                catch { }
                root.Children.Add(node);
            }
        }

        root.IsExpanded = true;
        Roots.Add(root);
    }

    /// <summary>
    /// Add a custom directory path (e.g. a network share) as a source under the root node.
    /// </summary>
    private void AddCustomPath()
    {
        if (RootNode is null) return;

        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a directory to add to backup sources",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        string path = dialog.SelectedPath.TrimEnd('\\');

        // Don't add duplicates.
        if (RootNode.Children.Any(c =>
            string.Equals(c.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                $"\"{path}\" is already in the source list.",
                "Path Already Added",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Don't add paths that are already under an existing drive root —
        // the user can navigate to them in the tree.
        foreach (var child in RootNode.Children)
        {
            if (path.StartsWith(child.Path, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"\"{path}\" is already accessible under \"{child.Name}\" in the source tree.",
                    "Path Already Covered",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        AddPathNode(path, isSelected: true);
    }

    /// <summary>
    /// Create a source node for a custom path and add it under the root.
    /// </summary>
    private SourceSelectionNodeViewModel AddPathNode(string path, bool isSelected)
    {
        var node = new SourceSelectionNodeViewModel(
            path, true, RootNode!,
            getCatalogInfo: () => _catalogInfo)
        {
            IsSelected = isSelected,
        };
        RootNode!.Children.Add(node);
        RefreshHasSelection();
        return node;
    }

    /// <summary>
    /// Supply the catalog version dictionary after construction.  The editor
    /// window opens before this large (~1M-entry) query completes, so nodes read
    /// the catalog through a shared getter; setting it here makes the data
    /// available to all future lazy loads and re-stamps backup status on any
    /// subtree that was already built.
    /// </summary>
    internal void SetCatalogInfo(Dictionary<string, FileVersionInfo>? catalogInfo)
    {
        _catalogInfo = catalogInfo;
        if (catalogInfo is null)
            return;

        foreach (var root in Roots)
            root.RefreshBackupStatusRecursive();
    }

    internal async Task ComputeAllUnknownSizesAsync()
    {
        foreach (var root in Roots.ToList())
            await root.ComputeUnknownSizesAsync();

        // Auto-show size report once background sizes are ready.
        RefreshSelectedSize();
        BuildSizeReport();
    }

    private void ToggleSort(SortColumn column)
    {
        if (_sortColumn == column)
        {
            SortAscending = !_sortAscending;
        }
        else
        {
            CurrentSortColumn = column;
            // Default: name ascending, size descending (largest first).
            SortAscending = column == SortColumn.Name;
        }

        // Sort by size requires sizes to be visible.
        if (_sortColumn == SortColumn.Size && !_showSizes)
            ShowSizes = true;

        OnPropertyChanged(nameof(NameSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));

        SortRoots();
        foreach (var root in Roots)
            root.SortChildren();
    }

    private void SortRoots()
    {
        // With the "All Drives" root, sorting applies to its children (drives).
        RootNode?.SortChildren();
    }

    /// <summary>
    /// Build a list of <see cref="SourceSelection"/> models from the current
    /// treeview state (only includes selected/partially-selected nodes).
    /// </summary>
    public List<SourceSelection> GetSelections()
    {
        var result = new List<SourceSelection>();
        foreach (var root in Roots)
        {
            var model = root.ToModel();

            // Unwrap the virtual "All Drives" root — downstream consumers
            // (scanner, database) can't handle Path="".
            if (root.Path == "")
            {
                if (model is not null && model.Children.Count > 0)
                {
                    // Normal case: root is selected/partial, children are in ToModel().
                    result.AddRange(model.Children);
                }
                else
                {
                    // Safety: root's IsSelected may be stale (false) even though
                    // children have selections.  Walk children directly.
                    foreach (var child in root.Children)
                    {
                        var childModel = child.ToModel();
                        if (childModel is not null)
                            result.Add(childModel);
                    }
                }
                continue;
            }

            if (model is not null)
                result.Add(model);
        }
        return result;
    }

    /// <summary>
    /// Restore saved source selections into the treeview by expanding the
    /// relevant paths and applying checked/option state.
    /// Handles both old format (drive roots at top level) and new format
    /// (virtual "All Drives" root with empty path containing drive children).
    /// </summary>
    public async Task ApplySelectionsAsync(List<SourceSelection> selections)
    {
        if (RootNode is null) return;

        IsApplyingSelections = true;
        try
        {

        // New format: single root with empty path wrapping drive children.
        var virtualRoot = selections.FirstOrDefault(s => s.Path == "");
        if (virtualRoot is not null)
        {
            await RootNode.ApplySelectionAsync(virtualRoot);

            // Create nodes for custom paths (network shares, etc.) that
            // aren't pre-populated as drive roots.
            foreach (var childModel in virtualRoot.Children)
            {
                if (RootNode.Children.Any(c =>
                    string.Equals(c.Path, childModel.Path, StringComparison.OrdinalIgnoreCase)))
                    continue; // already matched by ApplySelectionAsync

                var node = AddPathNode(childModel.Path, isSelected: false);
                await node.ApplySelectionAsync(childModel);
            }
        }
        else
        {
            // Old format: drive roots at top level — match against the
            // root's children (the drive nodes).
            // Process pre-existing drive nodes in parallel (independent subtrees).
            var driveTasks = new List<Task>();
            var customSelections = new List<SourceSelection>();

            foreach (var selection in selections)
            {
                var driveNode = RootNode.Children.FirstOrDefault(c =>
                    string.Equals(c.Path, selection.Path, StringComparison.OrdinalIgnoreCase));
                if (driveNode is not null)
                    driveTasks.Add(driveNode.ApplySelectionAsync(selection));
                else
                    customSelections.Add(selection);
            }
            await Task.WhenAll(driveTasks);

            // Create nodes for custom paths (network shares, etc.)
            // that aren't pre-populated as drive roots.  Sequential because
            // AddPathNode modifies the Children collection.
            foreach (var selection in customSelections)
            {
                if (Directory.Exists(selection.Path))
                {
                    var node = AddPathNode(selection.Path, isSelected: false);
                    await node.ApplySelectionAsync(selection);
                }
            }

            // ApplySelectionAsync bypasses the IsSelected setter, so the
            // virtual root's tristate was never recomputed from its children.
            RootNode.UpdateFromChildren();
        }

        RefreshHasSelection();
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsAllAutoIncludeNew));

        }
        finally
        {
            IsApplyingSelections = false;
        }
    }

    private void OnNext()
    {
        var selections = GetSelections();
        if (selections.Count > 0)
            NextRequested?.Invoke(selections);
    }

    private async void OnClearHistory()
    {
        if (ClearHistoryRequested is null)
            return;

        _isClearingHistory = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await ClearHistoryRequested.Invoke();
        }
        finally
        {
            _isClearingHistory = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async void OnSave()
    {
        // A checkbox toggle defers its propagation/aggregation work to a
        // Background-priority settle pass.  If one is still pending, wait for
        // it (with a busy cursor) so we never serialise a half-propagated tree.
        var pending = WaitForSelectionSettledAsync();
        if (!pending.IsCompleted)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                await pending;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        if (SaveRequested is not null)
            await SaveRequested.Invoke();

        // Mark clean after a successful save so the button disables.
        if (SaveStatusText == "Saved")
        {
            _needsSave = false;
            CommandManager.InvalidateRequerySuggested();
        }

        // Auto-clear the status after a few seconds so it doesn't persist forever.
        if (!string.IsNullOrEmpty(SaveStatusText))
        {
            await Task.Delay(3000);
            SaveStatusText = "";
        }
    }

    private async Task OnCalculateSize()
    {
        IsCalculatingSize = true;

        // Find all selected directories that don't have computed sizes yet.
        var uncalculated = new List<SourceSelectionNodeViewModel>();
        CollectUncalculatedSelectedDirs(Roots, uncalculated);

        if (uncalculated.Count > 0)
        {
            int total = uncalculated.Count;
            int done = 0;
            var firstName = System.IO.Path.GetFileName(uncalculated[0].Path.TrimEnd('\\'));
            SizeCalculationResult = $"Scanning 1/{total:N0}: {firstName}";

            // Progress<T> marshals callbacks to the UI thread via
            // SynchronizationContext, so property sets are safe here.
            var progress = new Progress<string>(path =>
            {
                done++;
                var dirName = System.IO.Path.GetFileName(path.TrimEnd('\\'));
                SizeCalculationResult = $"Scanning {Math.Min(done + 1, total):N0}/{total:N0}: {dirName}";
            });

            await _scheduler.EnqueueAsync(uncalculated, isPriority: true, progress);
        }

        RefreshSelectedSize();
        BuildSizeReport();
        IsCalculatingSize = false;
    }

    // -------------------------------------------------------------------
    // Dedup analysis
    // -------------------------------------------------------------------

    /// <summary>
    /// Run file-level dedup analysis: scan selected files, group by size,
    /// hash same-size candidates (reusing cached hashes where possible),
    /// and report savings. Results are persisted in <see cref="FileHashCache"/>
    /// so the actual backup reuses them.
    /// </summary>
    private async Task OnAnalyzeDedupAsync()
    {
        if (_fileHashCache is null || _scanner is null) return;

        IsAnalyzingDedup = true;
        DedupAnalysisResult = "Scanning selected files...";
        _dedupCts = new CancellationTokenSource();
        var ct = _dedupCts.Token;

        try
        {
            // 1. Collect the set of files to analyse by scanning the
            //    current selections with the exclusion filter applied.
            var selections = GetSelections();
            if (selections.Count == 0)
            {
                DedupAnalysisResult = "No files selected.";
                return;
            }

            var excludeFilter = GetExcludeFilter();
            var scanned = await _scanner.ScanAsync(selections, progress: null, ct, excludeFilter);
            ct.ThrowIfCancellationRequested();

            if (scanned.Count == 0)
            {
                DedupAnalysisResult = "No files found.";
                return;
            }

            // 2. Group files by size — only groups with 2+ files need hashing.
            var sizeGroups = scanned
                .GroupBy(f => f.SizeBytes)
                .Where(g => g.Count() >= 2)
                .ToList();

            int candidateFiles = sizeGroups.Sum(g => g.Count());
            int uniqueSizeFiles = scanned.Count - candidateFiles;

            DedupAnalysisResult = $"Scanning: {scanned.Count:N0} files, " +
                $"{candidateFiles:N0} share a size with another file...";

            // 3. Hash candidates on a background thread.
            int filesHashed = 0;
            int filesProcessed = 0;
            long lastProgressTick = 0;
            long totalRawSize = scanned.Sum(f => f.SizeBytes);
            var hashMap = new Dictionary<string, List<ScannedFile>>();

            await Task.Run(async () =>
            {
                foreach (var group in sizeGroups)
                {
                    foreach (var file in group)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Rate-limited progress (~4 updates/sec).
                        long now = Environment.TickCount64;
                        if (now - lastProgressTick >= 250)
                        {
                            lastProgressTick = now;
                            int n = filesProcessed;
                            int h = filesHashed;
                            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                DedupAnalysisResult =
                                    $"Hashing candidate {n + 1:N0} of {candidateFiles:N0}" +
                                    (h > 0 ? $" ({h:N0} new)" : "") + "...");
                        }

                        string? hash = _fileHashCache.TryGetHash(
                            file.FullPath, file.SizeBytes, file.LastWriteUtc);

                        if (hash is null)
                        {
                            try
                            {
                                await using var stream = new FileStream(
                                    file.FullPath, FileMode.Open, FileAccess.Read,
                                    FileShare.Read, bufferSize: 81920, useAsync: true);
                                var bytes = await SHA256.HashDataAsync(stream, ct);
                                hash = Convert.ToHexString(bytes).ToLowerInvariant();

                                _fileHashCache.Set(
                                    file.FullPath, file.SizeBytes, file.LastWriteUtc, hash);
                                filesHashed++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch
                            {
                                // File locked or inaccessible — skip it.
                                filesProcessed++;
                                continue;
                            }
                        }

                        if (!hashMap.TryGetValue(hash, out var list))
                        {
                            list = [];
                            hashMap[hash] = list;
                        }
                        list.Add(file);

                        filesProcessed++;
                    }
                }

                _fileHashCache.Flush();
            }, ct);

            ct.ThrowIfCancellationRequested();

            // 4. Compute savings.
            long bytesSaved = 0;
            int duplicateFiles = 0;
            foreach (var (_, files) in hashMap)
            {
                if (files.Count <= 1) continue;
                // Keep one copy; the rest are duplicates.
                var ordered = files.OrderByDescending(f => f.SizeBytes).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    bytesSaved += ordered[i].SizeBytes;
                    duplicateFiles++;
                }
            }

            int uniqueHashes = hashMap.Count + uniqueSizeFiles;
            long dedupSize = totalRawSize - bytesSaved;

            // 5. Build result text.
            var lines = new List<string>
            {
                $"Scanned: {scanned.Count:N0} files, {SourceSelectionNodeViewModel.FormatBytes(totalRawSize)}",
                $"Unique files: {uniqueHashes:N0}  |  Duplicates: {duplicateFiles:N0}",
                $"Dedup savings: {SourceSelectionNodeViewModel.FormatBytes(bytesSaved)} " +
                    $"({(totalRawSize > 0 ? (double)bytesSaved / totalRawSize * 100 : 0):F1}%)",
                $"Post-dedup size: {SourceSelectionNodeViewModel.FormatBytes(dedupSize)}",
            };
            if (filesHashed > 0)
                lines.Add($"({filesHashed:N0} files hashed, {candidateFiles - filesHashed:N0} from cache)");

            DedupAnalysisResult = string.Join("\n", lines);
        }
        catch (OperationCanceledException)
        {
            DedupAnalysisResult = "Dedup analysis cancelled.";
        }
        catch (Exception ex)
        {
            DedupAnalysisResult = $"Dedup analysis failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzingDedup = false;
            _dedupCts?.Dispose();
            _dedupCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Build a detailed multi-line size report showing total selected,
    /// already-backed-up, new/changed to write, and destination space.
    /// </summary>
    internal void BuildSizeReport()
    {
        long totalSize = 0;
        int fileCount = 0;
        int dirCount = 0;
        ComputeSelectedSize(Roots, GetExcludeFilter(), ref totalSize, ref fileCount, ref dirCount);

        if (fileCount == 0 && dirCount == 0)
        {
            SizeCalculationResult = "No selected files or directories found.";
            return;
        }

        var lines = new List<string>();
        var parts = new List<string>();
        if (dirCount > 0) parts.Add($"{dirCount:N0} {(dirCount == 1 ? "directory" : "directories")}");
        if (fileCount > 0) parts.Add($"{fileCount:N0} {(fileCount == 1 ? "file" : "files")}");
        lines.Add($"Selected: {string.Join(", ", parts)}, {SourceSelectionNodeViewModel.FormatBytes(totalSize)}");

        // Compute catalog coverage by scanning _catalogInfo against the
        // selected paths.  This works regardless of whether tree nodes
        // are expanded — the catalog has the full picture.
        if (_catalogInfo is { Count: > 0 })
        {
            // Build the set of selected directory prefixes (with trailing \)
            // and individual file paths from the tree.
            var dirPrefixes = new List<string>();
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSelectedPaths(Roots, dirPrefixes, filePaths);

            // Apply the same exclusion filter used for the "Selected" line
            // so the two numbers are comparable.  Without this, catalog
            // entries in excluded directories (e.g. *\target\*) inflate
            // the "already backed up" total.
            var excludeFilter = GetExcludeFilter();

            // Single pass over the catalog: sum entries that fall under
            // any selected prefix or match a selected file.
            long catalogSize = 0;
            int catalogFiles = 0;
            foreach (var (path, info) in _catalogInfo)
            {
                bool covered = filePaths.Contains(path);
                if (!covered)
                {
                    for (int i = 0; i < dirPrefixes.Count; i++)
                    {
                        if (path.StartsWith(dirPrefixes[i], StringComparison.OrdinalIgnoreCase))
                        {
                            covered = true;
                            break;
                        }
                    }
                }
                if (covered)
                {
                    // Skip files that match the exclusion filter (0-tier
                    // tier sets) — they won't be backed up regardless of
                    // whether they were backed up in a prior session.
                    if (excludeFilter is not null && excludeFilter(path))
                        continue;

                    catalogSize += info.SizeBytes;
                    catalogFiles++;
                }
            }

            if (catalogFiles > 0)
                lines.Add($"Already backed up: {catalogFiles:N0} files, {SourceSelectionNodeViewModel.FormatBytes(catalogSize)}");
        }

        // Show destination free space (directory-mode only).
        // Accurate "insufficient" warnings come from PlanAsync, not here.
        if (_isDirectoryMode && !string.IsNullOrWhiteSpace(_targetDirectory))
        {
            try
            {
                string pathRoot = Path.GetPathRoot(_targetDirectory) ?? _targetDirectory;
                var driveInfo = new DriveInfo(pathRoot);
                if (driveInfo.IsReady)
                {
                    long free = driveInfo.AvailableFreeSpace;
                    string driveLetter = pathRoot.TrimEnd('\\');
                    lines.Add($"Destination ({driveLetter}): {SourceSelectionNodeViewModel.FormatBytes(free)} free");
                }
            }
            catch { }
        }

        SizeCalculationResult = string.Join("\n", lines);
    }

    /// <summary>
    /// Collect selected directory prefixes (ending with \) and individual
    /// selected file paths from the tree.  Used for matching against the
    /// catalog to compute coverage without requiring all nodes to be loaded.
    /// </summary>
    private static void CollectSelectedPaths(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        List<string> dirPrefixes, HashSet<string> filePaths)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false)
                continue;

            if (!node.IsDirectory)
            {
                // Individual selected file.
                filePaths.Add(node.Path);
            }
            else if (node.IsSelected == true && !node.IsLoaded)
            {
                // Fully selected unexpanded directory — everything under
                // this path is selected.  Use as a prefix for catalog matching.
                string prefix = node.Path.EndsWith('\\') ? node.Path : node.Path + "\\";
                dirPrefixes.Add(prefix);
            }
            else if (node.IsSelected == true && node.IsLoaded)
            {
                // Fully selected expanded directory — also a prefix match
                // (covers any files deeper than the loaded children).
                string prefix = node.Path.EndsWith('\\') ? node.Path : node.Path + "\\";
                dirPrefixes.Add(prefix);
            }
            else if (node.IsLoaded)
            {
                // Partially selected — recurse to find specific children.
                CollectSelectedPaths(node.Children, dirPrefixes, filePaths);
            }
        }
    }

    private static void CollectUncalculatedSelectedDirs(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        List<SourceSelectionNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false || !node.IsDirectory)
                continue;

            if (node.Size < 0)
                result.Add(node);

            // Only recurse into partially selected directories.
            // ComputeSelectedSize uses the aggregate Size/FileCount for
            // fully-selected directories, so their children don't need
            // individual sizes computed.
            if (node.IsSelected != true && node.IsLoaded)
                CollectUncalculatedSelectedDirs(node.Children, result);
        }
    }

    private void SetAutoIncludeOnChecked()
    {
        var dirs = GetAllDirectoryNodes();
        foreach (var node in dirs)
        {
            if (node.IsSelected != false && !node.AutoIncludeNew)
                node.AutoIncludeNew = true;
        }
        OnPropertyChanged(nameof(IsAllAutoIncludeNew));
    }

    private async Task OnSeedFromExisting()
    {
        if (SeedFromExistingRequested is null) return;

        _seedCts = new CancellationTokenSource();
        IsSeeding = true;
        SeedResult = "Importing files from existing backup...";
        try
        {
            await SeedFromExistingRequested.Invoke();
        }
        catch (OperationCanceledException)
        {
            SeedResult = "Import cancelled.";
        }
        catch (Exception ex)
        {
            SeedResult = $"Error: {ex.Message}";
        }
        finally
        {
            IsSeeding = false;
            _seedCts?.Dispose();
            _seedCts = null;
        }
    }

    private List<SourceSelectionNodeViewModel> GetAllDirectoryNodes()
    {
        var result = new List<SourceSelectionNodeViewModel>();
        CollectDirectories(Roots, result);
        return result;
    }

    private static void CollectDirectories(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        List<SourceSelectionNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsDirectory)
            {
                result.Add(node);
                CollectDirectories(node.Children, result);
            }
        }
    }

    private List<SourceSelectionNodeViewModel> GetAllNodes()
    {
        var result = new List<SourceSelectionNodeViewModel>();
        CollectAllNodes(Roots, result);
        return result;
    }

    private static void CollectAllNodes(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        List<SourceSelectionNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            result.Add(node);
            CollectAllNodes(node.Children, result);
        }
    }

    private void BrowseDirectory()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select target directory for backup",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (!string.IsNullOrWhiteSpace(TargetDirectory) && Directory.Exists(TargetDirectory))
            dialog.SelectedPath = TargetDirectory;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TargetDirectory = dialog.SelectedPath;
    }

    private void AddRetentionTier()
    {
        if (_selectedTierSet is null) return;
        var tier = new RetentionTierViewModel();
        tier.RemoveRequested += t => _selectedTierSet.Tiers.Remove(t);
        _selectedTierSet.Tiers.Add(tier);
    }

    // ---------------------------------------------------------------
    // Tier set management
    // ---------------------------------------------------------------

    /// <summary>
    /// Populate the default built-in tier sets ("Default" and "None").
    /// </summary>
    private void InitializeDefaultTierSets()
    {
        var defaultSet = new TierSetViewModel("Default", isBuiltIn: true);
        foreach (var tier in VersionRetentionService.DefaultTiers)
        {
            var vm = RetentionTierViewModel.FromModel(tier);
            vm.RemoveRequested += t => defaultSet.Tiers.Remove(t);
            defaultSet.Tiers.Add(vm);
        }

        var noneSet = new TierSetViewModel("None", isBuiltIn: true);
        // "None" has no tiers — matching files are excluded from backup.

        TierSets.Add(defaultSet);
        TierSets.Add(noneSet);
        SelectedTierSet = defaultSet;
    }

    private void AddTierSet()
    {
        // Generate a unique name.
        int index = 1;
        string name;
        do
        {
            name = $"Custom {index++}";
        } while (TierSets.Any(t => t.Name == name));

        var newSet = new TierSetViewModel(name, isBuiltIn: false);
        TierSets.Add(newSet);
        SelectedTierSet = newSet;
    }

    private void RemoveSelectedTierSet()
    {
        if (_selectedTierSet is null || _selectedTierSet.IsBuiltIn)
            return;

        TierSets.Remove(_selectedTierSet);
        SelectedTierSet = TierSets.FirstOrDefault();
    }

    /// <summary>
    /// Convert the current tier set definitions to model objects for persistence.
    /// </summary>
    public List<VersionTierSet> GetTierSetModels()
    {
        return TierSets.Select(ts => ts.ToModel()).ToList();
    }

    /// <summary>
    /// Replace tier set definitions from saved data.
    /// </summary>
    public void LoadTierSets(List<VersionTierSet> tierSets)
    {
        _excludeFilterDirty = true;
        TierSets.Clear();

        if (tierSets.Count == 0)
        {
            // Old data without tier sets — use defaults.
            InitializeDefaultTierSets();
            return;
        }

        foreach (var model in tierSets)
        {
            bool isBuiltIn = model.Name is "Default" or "None";
            var vm = TierSetViewModel.FromModel(model, isBuiltIn);
            TierSets.Add(vm);
        }

        // Ensure both built-in sets exist (in case old data only had "Default").
        if (!TierSets.Any(t => t.Name == "Default"))
        {
            var defaultSet = new TierSetViewModel("Default", isBuiltIn: true);
            foreach (var tier in VersionRetentionService.DefaultTiers)
            {
                var vm = RetentionTierViewModel.FromModel(tier);
                vm.RemoveRequested += t => defaultSet.Tiers.Remove(t);
                defaultSet.Tiers.Add(vm);
            }
            TierSets.Insert(0, defaultSet);
        }
        if (!TierSets.Any(t => t.Name == "None"))
        {
            TierSets.Insert(1, new TierSetViewModel("None", isBuiltIn: true));
        }

        SelectedTierSet = TierSets.FirstOrDefault();

        // Push the exclusion filter to the scheduler so that both inline
        // and queued size computations produce filtered values.
        _scheduler.GlobalExcludeFilter = GetExcludeFilter();
        _scheduler.GlobalExcludeFilterSignature = _cachedExcludeFilterSignature;
    }

    /// <summary>
    /// Query the destination drive's total capacity and free space and update
    /// <see cref="DestinationSpaceText"/>.  Called automatically when
    /// <see cref="TargetDirectory"/> changes.
    /// </summary>
    public void RefreshDestinationSpace()
    {
        if (string.IsNullOrWhiteSpace(_targetDirectory))
        {
            DestinationSpaceText = "";
            return;
        }

        try
        {
            string pathRoot = Path.GetPathRoot(_targetDirectory) ?? _targetDirectory;
            var driveInfo = new DriveInfo(pathRoot);
            if (!driveInfo.IsReady)
            {
                DestinationSpaceText = "";
                return;
            }

            long total = driveInfo.TotalSize;
            long free = driveInfo.AvailableFreeSpace;
            DestinationSpaceText = $"{pathRoot.TrimEnd('\\')}  —  " +
                $"{SourceSelectionNodeViewModel.FormatBytes(total)} total, " +
                $"{SourceSelectionNodeViewModel.FormatBytes(free)} free";
        }
        catch
        {
            DestinationSpaceText = "";
        }
    }
}
