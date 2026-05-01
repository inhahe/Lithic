using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
    private bool _showSizes;
    private TierSetViewModel? _selectedTierSet;
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private bool _showSelectedSizesOnly;
    private string _selectedSizeText = "";
    private string _excludedPatterns = "";
    private string _setName = "";
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
    private bool _showLargestFiles;
    private bool _isApplyingSelections;

    /// <summary>Fired when the user clicks "Next" with a valid selection.</summary>
    public event Action<List<SourceSelection>>? NextRequested;

    /// <summary>Fired when the user clicks "Cancel".</summary>
    public event Action? CancelRequested;

    /// <summary>Fired when the user clicks "Largest Files &amp; Directories".</summary>
    public event Action? LargestFilesRequested;

    /// <summary>Fired whenever a checkbox selection changes in the tree.</summary>
    public event Action? SelectionChanged;

    public SourceSelectionViewModel(
        Dictionary<string, FileVersionInfo>? catalogInfo = null)
    {
        _catalogInfo = catalogInfo;
        Roots = [];
        RetentionTiers = [];
        TierSets = [];
        AvailableTierSetNames = ["Default", "None"];
        NextCommand = new RelayCommand(_ => OnNext(), _ => HasSelection);
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
    /// Names of all available tier sets, including the "(Inherit)" sentinel.
    /// Bound by the per-node ComboBox in the tree view column.
    /// </summary>
    public ObservableCollection<string> AvailableTierSetNames { get; }

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
            if (!SetProperty(ref _selectedTierSet, value))
                return;
            OnPropertyChanged(nameof(CanEditSelectedTierSet));
        }
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

    /// <summary>
    /// Global exclusion patterns (delegated to the root node).
    /// Used by MainViewModel to read/write the "global" exclusion list
    /// for backward compatibility with JobOptions.ExcludedExtensions.
    /// </summary>
    public string ExcludedPatterns
    {
        get => RootNode?.ExcludedPatterns ?? _excludedPatterns;
        set
        {
            if (RootNode is not null)
                RootNode.ExcludedPatterns = value;
            else
                _excludedPatterns = value;
            OnPropertyChanged();
        }
    }

    // --- Backup set settings ---

    /// <summary>Name of the backup set (editable).</summary>
    public string SetName
    {
        get => _setName;
        set => SetProperty(ref _setName, value);
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
        set => SetProperty(ref _targetDirectory, value);
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
    /// </summary>
    public bool IsApplyingSelections => _isApplyingSelections;

    // --- Commands ---

    public ICommand NextCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LargestFilesCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortBySizeCommand { get; }
    public ICommand BrowseDirectoryCommand { get; }
    public ICommand AddRetentionTierCommand { get; }
    public ICommand AddTierSetCommand { get; }
    public ICommand RemoveTierSetCommand { get; }
    public ICommand AddPathCommand { get; }

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
        ComputeSelectedSize(Roots, ref total, ref fileCount, ref dirCount);

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
        ref long total, ref int fileCount, ref int dirCount)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected == false)
                continue;

            if (!node.IsDirectory)
            {
                // Selected file — add its size.
                if (node.Size >= 0) total += node.Size;
                fileCount++;
            }
            else if (node.IsSelected == true && !node.IsLoaded)
            {
                // Fully selected directory that hasn't been expanded yet.
                // Use its total filesystem size as best estimate.
                if (node.Size >= 0) total += node.Size;
                dirCount++;
            }
            else
            {
                // Partially or fully selected directory that's been loaded.
                // Recurse to count only selected children.
                dirCount++;
                ComputeSelectedSize(node.Children, ref total, ref fileCount, ref dirCount);
            }
        }
    }

    /// <summary>
    /// The virtual root node ("All Drives") that contains all drive nodes.
    /// Its exclusion patterns serve as the "global" exclusion list.
    /// </summary>
    public SourceSelectionNodeViewModel? RootNode { get; private set; }

    private void LoadDriveRoots()
    {
        // Create a virtual "All Drives" root node.
        var root = new SourceSelectionNodeViewModel(
            "", true, null,
            () => ShowSizes, () => (_sortColumn, _sortAscending), _scheduler,
            () => { RefreshHasSelection(); RefreshSelectedSize(); SelectionChanged?.Invoke(); },
            () => ShowSelectedSizesOnly,
            _catalogInfo);
        RootNode = root;

        // Mark as loaded BEFORE setting IsExpanded — otherwise the
        // IsExpanded setter triggers LoadChildrenAsync on the empty path.
        root.IsLoaded = true;
        root.Children.Clear(); // Remove the "Loading..." dummy child.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;

            if (drive.DriveType == DriveType.CDRom)
                continue;

            var node = new SourceSelectionNodeViewModel(
                drive.RootDirectory.FullName, true, root,
                catalogInfo: _catalogInfo);
            try { node.Size = drive.TotalSize - drive.AvailableFreeSpace; }
            catch { }
            root.Children.Add(node);
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
            catalogInfo: _catalogInfo)
        {
            IsSelected = isSelected,
        };
        RootNode!.Children.Add(node);
        RefreshHasSelection();
        return node;
    }

    private async Task ComputeAllUnknownSizesAsync()
    {
        foreach (var root in Roots.ToList())
            await root.ComputeUnknownSizesAsync();
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

        _isApplyingSelections = true;
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
            foreach (var selection in selections)
            {
                var driveNode = RootNode.Children.FirstOrDefault(c =>
                    string.Equals(c.Path, selection.Path, StringComparison.OrdinalIgnoreCase));
                if (driveNode is not null)
                    await driveNode.ApplySelectionAsync(selection);

                // Create nodes for custom paths (network shares, etc.)
                // that aren't pre-populated as drive roots.
                if (driveNode is null && Directory.Exists(selection.Path))
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
            _isApplyingSelections = false;
        }
    }

    private void OnNext()
    {
        var selections = GetSelections();
        if (selections.Count > 0)
            NextRequested?.Invoke(selections);
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
        // "None" has no tiers — files are overwritten without version history.

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
        WireTierSetNameChanged(newSet);
        TierSets.Add(newSet);
        AvailableTierSetNames.Add(name);
        SelectedTierSet = newSet;
    }

    /// <summary>
    /// Subscribe to a tier set's name change so references in the tree and
    /// the available-names list stay in sync.
    /// </summary>
    private void WireTierSetNameChanged(TierSetViewModel ts)
    {
        ts.NameChanged += (oldName, newName) =>
        {
            // Update the available names list.
            int idx = AvailableTierSetNames.IndexOf(oldName);
            if (idx >= 0)
                AvailableTierSetNames[idx] = newName;

            // Rename references in the source tree.
            RenameTierSetReferences(Roots, oldName, newName);
        };
    }

    private static void RenameTierSetReferences(
        IEnumerable<SourceSelectionNodeViewModel> nodes, string oldName, string newName)
    {
        foreach (var node in nodes)
        {
            if (node.VersionTierSetName == oldName)
                node.VersionTierSetName = newName;
            if (node.IsDirectory && node.IsLoaded)
                RenameTierSetReferences(node.Children, oldName, newName);
        }
    }

    private void RemoveSelectedTierSet()
    {
        if (_selectedTierSet is null || _selectedTierSet.IsBuiltIn)
            return;

        string removedName = _selectedTierSet.Name;

        // Reset any nodes using this tier set back to "(Inherit)".
        ResetTierSetReferences(Roots, removedName);

        TierSets.Remove(_selectedTierSet);
        AvailableTierSetNames.Remove(removedName);
        SelectedTierSet = TierSets.FirstOrDefault();
    }

    private static void ResetTierSetReferences(
        IEnumerable<SourceSelectionNodeViewModel> nodes, string tierSetName)
    {
        foreach (var node in nodes)
        {
            if (node.VersionTierSetName == tierSetName)
                node.VersionTierSetName = SourceSelectionNodeViewModel.InheritTierName;
            if (node.IsDirectory && node.IsLoaded)
                ResetTierSetReferences(node.Children, tierSetName);
        }
    }

    /// <summary>
    /// Rebuild the <see cref="AvailableTierSetNames"/> list from the current
    /// <see cref="TierSets"/> collection. Call after loading/restoring tier sets.
    /// </summary>
    internal void RebuildAvailableTierSetNames()
    {
        AvailableTierSetNames.Clear();
        foreach (var ts in TierSets)
            AvailableTierSetNames.Add(ts.Name);
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
            var vm = new TierSetViewModel(model.Name, isBuiltIn);
            if (!isBuiltIn)
                WireTierSetNameChanged(vm);
            foreach (var tier in model.Tiers)
            {
                var tierVm = RetentionTierViewModel.FromModel(tier);
                tierVm.RemoveRequested += t => vm.Tiers.Remove(t);
                vm.Tiers.Add(tierVm);
            }
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

        RebuildAvailableTierSetNames();
        SelectedTierSet = TierSets.FirstOrDefault();
    }
}
