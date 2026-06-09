using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Burning;
using LithicBackup.Services;
using LithicBackup.Views;

namespace LithicBackup.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IDiscBurner _burner;
    private readonly IFileScanner _scanner;
    private readonly IBackupOrchestrator _orchestrator;
    private readonly IRestoreService _restoreService;
    private readonly DirectoryBackupService _directoryBackupService;
    private readonly Services.TrayService? _trayService;
    private readonly SimulatedDiscBurner? _simulatedBurner;
    private readonly FileHashCache? _fileHashCache;

    private string _statusText = "Ready";
    private string _backgroundStatusText = "";
    private int _recorderCount;
    private ViewModelBase? _currentView;
    private BackupSet? _selectedBackupSet;
    private bool _isBurning;
    private BurnProgressViewModel? _activeBackupProgress;
    private string _serviceStatusText = "";
    private int? _runningBackupSetId;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _checkSizeCts;
    private int? _checkingSizeSetId;
    private BackupSetEditorWindow? _editorWindow;
    private Window? _largestFilesWindow;
    private Func<Task>? _pendingSettingsSave;
    private int? _unsavedNewSetId;

    public MainViewModel(
        ICatalogRepository catalog,
        IDiscBurner burner,
        IFileScanner scanner,
        IBackupOrchestrator orchestrator,
        IRestoreService restoreService,
        DirectoryBackupService directoryBackupService,
        Services.TrayService? trayService = null,
        FileHashCache? fileHashCache = null)
    {
        _catalog = catalog;
        _burner = burner;
        _scanner = scanner;
        _orchestrator = orchestrator;
        _restoreService = restoreService;
        _directoryBackupService = directoryBackupService;
        _trayService = trayService;
        _fileHashCache = fileHashCache;
        _simulatedBurner = burner as SimulatedDiscBurner;

        BackupSets = [];

        NewBackupSetCommand = new RelayCommand(
            _ => StartNewBackupFlow(),
            _ => !IsBurning);
        CancelScanCommand = new RelayCommand(
            _ => _scanCts?.Cancel(),
            _ => _scanCts is not null && !_scanCts.IsCancellationRequested);
        AbortBackupCommand = new RelayCommand(
            _ =>
            {
                if (_activeBackupProgress?.CancelCommand is ICommand cmd && cmd.CanExecute(null))
                    cmd.Execute(null);
                else
                    _scanCts?.Cancel();
            },
            _ => (_scanCts is not null && !_scanCts.IsCancellationRequested) ||
                 (_activeBackupProgress?.CancelCommand?.CanExecute(null) == true));
        RunIncrementalCommand = new RelayCommand(
            _ => StartIncrementalFlow(),
            _ => !IsBurning && SelectedBackupSet is not null);
        RestoreCommand = new RelayCommand(
            _ => StartRestoreFlow(),
            _ => !IsBurning && SelectedBackupSet is not null);
        EditBackupSetCommand = new RelayCommand(
            _ => StartEditFlow(),
            _ => !IsBurning && SelectedBackupSet is not null);
        ChangeDestinationCommand = new RelayCommand(
            _ => _ = ChangeDestinationAsync(),
            _ => !IsBurning && SelectedBackupSet?.JobOptions?.TargetDirectory is not null);
        CopyBackupSetCommand = new RelayCommand(
            _ => _ = CopyBackupSetAsync(),
            _ => !IsBurning && SelectedBackupSet is not null);
        OrphanedDirsCommand = new RelayCommand(
            _ => StartOrphanedDirsFlow(),
            _ => !IsBurning && SelectedBackupSet is not null);
        FindFileCommand = new RelayCommand(
            _ => StartFindFileFlow());
        HomeCommand = new RelayCommand(_ => GoHome());
        BackupCoverageCommand = new RelayCommand(
            _ => StartBackupCoverageFlow(),
            _ => SelectedBackupSet is not null);
        LargestFilesCommand = new RelayCommand(
            _ => StartLargestFilesFlow(),
            _ => !IsBurning && SelectedBackupSet is not null);
        ExportBackupSetCommand = new RelayCommand(
            _ => _ = ExportBackupSetAsync(),
            _ => SelectedBackupSet is not null);
        ImportBackupSetCommand = new RelayCommand(
            _ => _ = ImportBackupSetAsync());

        // Per-set action commands (take BackupSet via CommandParameter).
        SetCheckCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; _ = CheckSizeAsync(); } },
            o => !IsBurning && _checkingSizeSetId is null);
        AbortCheckCommand = new RelayCommand(
            _ => _checkSizeCts?.Cancel(),
            _ => _checkingSizeSetId is not null);
        SetBackupCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartIncrementalFlow(); } },
            o => !IsBurning);
        SetModifyCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartEditFlow(); } },
            o => !IsBurning);
        SetRestoreCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartRestoreFlow(); } },
            o => !IsBurning);
        SetOrphanedDirsCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartOrphanedDirsFlow(); } },
            o => !IsBurning);
        SetCoverageCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartBackupCoverageFlow(); } },
            o => o is BackupSet);
        SetLargestFilesCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartLargestFilesFlow(); } },
            o => !IsBurning);
        SetCopyCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; _ = CopyBackupSetAsync(); } },
            o => !IsBurning);
        SetChangeDestCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; _ = ChangeDestinationAsync(); } },
            o => !IsBurning && o is BackupSet bs && bs.JobOptions?.TargetDirectory is not null);
        SetExportCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; _ = ExportBackupSetAsync(); } },
            o => o is BackupSet);
        SetDeleteCommand = new RelayCommand(
            o => { if (o is BackupSet s) _ = DeleteBackupSetAsync(s); },
            o => !IsBurning && o is BackupSet);
        InstallServiceCommand = new RelayCommand(_ => InstallService());
        UninstallServiceCommand = new RelayCommand(_ => UninstallService());
        StartServiceCommand = new RelayCommand(_ => StartService());
        StopServiceCommand = new RelayCommand(_ => StopService());

        // Simulated burner failure injection (--simulate-burner only).
        SimFileFailureCommand = new RelayCommand(
            _ => { if (_simulatedBurner is not null) _simulatedBurner.FileFailureProbability = 1.0; },
            _ => _simulatedBurner is not null && IsBurning);
        SimCatastrophicFailureCommand = new RelayCommand(
            _ => { if (_simulatedBurner is not null) _simulatedBurner.CatastrophicFailureAtPercent = 0; },
            _ => _simulatedBurner is not null && IsBurning);
        SimEraseFailureCommand = new RelayCommand(
            _ => { if (_simulatedBurner is not null) _simulatedBurner.SimulateEraseFail = true; },
            _ => _simulatedBurner is not null && IsBurning);

        RefreshServiceStatus();

        // Wire up tray service events for background monitoring notifications.
        if (_trayService is not null)
        {
            _trayService.BackupSuggested += reason =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    BackgroundStatusText = $"Background: {reason}";
                });
            };
        }

        DetectRecorders();
        _ = LoadBackupSetsAsync();
    }

    // --- Properties ---

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string BackgroundStatusText
    {
        get => _backgroundStatusText;
        set => SetProperty(ref _backgroundStatusText, value);
    }

    public int RecorderCount
    {
        get => _recorderCount;
        set => SetProperty(ref _recorderCount, value);
    }

    /// <summary>The view currently displayed in the main content area. Null = home screen.</summary>
    public ViewModelBase? CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public ObservableCollection<BackupSet> BackupSets { get; }

    public BackupSet? SelectedBackupSet
    {
        get => _selectedBackupSet;
        set => SetProperty(ref _selectedBackupSet, value);
    }

    /// <summary>True while a backup (burn or directory copy) is in progress.</summary>
    public bool IsBurning
    {
        get => _isBurning;
        private set => SetProperty(ref _isBurning, value);
    }

    /// <summary>The in-progress or just-completed backup, shown in the inline progress panel.</summary>
    public BurnProgressViewModel? ActiveBackupProgress
    {
        get => _activeBackupProgress;
        private set
        {
            if (SetProperty(ref _activeBackupProgress, value))
                OnPropertyChanged(nameof(HasActiveBackupProgress));
        }
    }

    /// <summary>True when there's an active backup progress panel to display.</summary>
    public bool HasActiveBackupProgress => _activeBackupProgress is not null;

    // --- Commands ---

    public ICommand NewBackupSetCommand { get; }
    public ICommand EditBackupSetCommand { get; }
    public ICommand ChangeDestinationCommand { get; }
    public ICommand CopyBackupSetCommand { get; }
    public ICommand RunIncrementalCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand OrphanedDirsCommand { get; }
    public ICommand FindFileCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand BackupCoverageCommand { get; }
    public ICommand LargestFilesCommand { get; }
    public ICommand ExportBackupSetCommand { get; }
    public ICommand ImportBackupSetCommand { get; }

    /// <summary>Cancels the scan phase of a backup (before burn starts).</summary>
    public ICommand CancelScanCommand { get; }

    /// <summary>Aborts the current backup — delegates to scan CTS during scanning, or
    /// to <see cref="BurnProgressViewModel.CancelCommand"/> during the burn phase.</summary>
    public ICommand AbortBackupCommand { get; }

    // Per-set action commands (take BackupSet as CommandParameter)
    public ICommand SetCheckCommand { get; }
    public ICommand AbortCheckCommand { get; }
    public ICommand SetBackupCommand { get; }
    public ICommand SetModifyCommand { get; }
    public ICommand SetRestoreCommand { get; }
    public ICommand SetOrphanedDirsCommand { get; }
    public ICommand SetCoverageCommand { get; }
    public ICommand SetLargestFilesCommand { get; }
    public ICommand SetCopyCommand { get; }
    public ICommand SetChangeDestCommand { get; }
    public ICommand SetExportCommand { get; }
    public ICommand SetDeleteCommand { get; }
    /// <summary>ID of the backup set currently being backed up, or null.</summary>
    public int? RunningBackupSetId
    {
        get => _runningBackupSetId;
        private set => SetProperty(ref _runningBackupSetId, value);
    }

    /// <summary>
    /// The ID of the backup set currently being size-checked, or <c>null</c>
    /// if no check is in progress.  The XAML swaps "Check Size" to
    /// "Abort Check" for the matching row.
    /// </summary>
    public int? CheckingSizeSetId
    {
        get => _checkingSizeSetId;
        private set => SetProperty(ref _checkingSizeSetId, value);
    }

    // --- Worker Service management ---

    public ICommand InstallServiceCommand { get; }
    public ICommand UninstallServiceCommand { get; }
    public ICommand StartServiceCommand { get; }
    public ICommand StopServiceCommand { get; }

    // Simulated burner failure injection (--simulate-burner only).
    public bool IsSimulatedBurner => _simulatedBurner is not null;
    public ICommand SimFileFailureCommand { get; }
    public ICommand SimCatastrophicFailureCommand { get; }
    public ICommand SimEraseFailureCommand { get; }

    /// <summary>Current service state for UI binding.</summary>
    public ServiceState ServiceStatus { get; private set; }

    public string ServiceStatusText
    {
        get => _serviceStatusText;
        private set => SetProperty(ref _serviceStatusText, value);
    }

    /// <summary>True if the service is not installed and we can find the worker exe.</summary>
    public bool CanInstallService => ServiceStatus == ServiceState.NotInstalled
                                     && Services.WorkerServiceHelper.FindWorkerExe() is not null;

    public bool CanUninstallService => ServiceStatus is ServiceState.Stopped or ServiceState.Running;
    public bool CanStartService => ServiceStatus == ServiceState.Stopped;
    public bool CanStopService => ServiceStatus == ServiceState.Running;

    // -------------------------------------------------------------------
    // Flow 1: New Backup Set
    //   Source Selection → Backup Job Config → Burn Progress → Done
    // -------------------------------------------------------------------

    private async void StartNewBackupFlow()
    {
        try
        {
            var newSet = await _catalog.CreateBackupSetAsync(new BackupSet
            {
                Name = $"Backup {DateTime.Now:yyyy-MM-dd}",
                SourceRoots = [],
                CreatedUtc = DateTime.UtcNow,
            });

            // Don't reload the list — the new set stays invisible until the
            // user explicitly clicks Save.  If they close without saving,
            // the close handler deletes the temporary DB record.
            _unsavedNewSetId = newSet.Id;
            SelectedBackupSet = newSet;
            StartEditFlow();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to create backup set: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------
    // Flow 1b: Edit Backup Set (modify-only — no wizard, no Plan/Start)
    // -------------------------------------------------------------------

    private async void StartEditFlow()
    {
        if (SelectedBackupSet is null) return;

        var backupSet = SelectedBackupSet;

        // Show a wait cursor while loading — the dialog won't appear until ready.
        Mouse.OverrideCursor = Cursors.Wait;

        // ---------------------------------------------------------------
        // Phase 1: async data loading (dialog not yet visible)
        // ---------------------------------------------------------------

        // Kick off the two slow I/O operations in parallel on the thread pool:
        // 1. Catalog query (synchronous SQL under the hood)
        // 2. Drive enumeration (DriveInfo.GetDrives + TotalSize — slow for network drives)
        var catalogTask = Task.Run(() =>
        {
            try { return _catalog.GetLatestVersionInfoAsync(backupSet.Id).GetAwaiter().GetResult(); }
            catch { return null as Dictionary<string, Core.Models.FileVersionInfo>; }
        });
        var drivesTask = Task.Run(() => SourceSelectionViewModel.EnumerateDrives());

        await Task.WhenAll(catalogTask, drivesTask);

        var catalogInfo = catalogTask.Result;
        var drives = drivesTask.Result;

        var sourceSelection = new SourceSelectionViewModel(catalogInfo, drives,
            fileHashCache: _fileHashCache, scanner: _scanner);
        sourceSelection.IsEditMode = true;

        // Restore backup set settings from saved state.
        sourceSelection.SetName = backupSet.Name;
        RestoreSourceSettings(sourceSelection, backupSet.JobOptions);

        // Selection restore and size computation are deferred to after the
        // dialog is visible (see PostShowInitAsync below).  The view shows a
        // "Restoring selections..." overlay while this runs.
        CancellationTokenSource? autoCheckCts = null;
        bool planCheckReady = false;

        // Enable Save if selections already exist; for new empty sets, the user
        // must check at least one box first.
        if (backupSet.SourceSelections is { Count: > 0 } || backupSet.SourceRoots.Count > 0)
            sourceSelection.HasSelection = true;
        sourceSelection.ShowLargestFiles = true;

        // Show catalog summary so the user knows files are already tracked
        // (e.g. from a previous seed or backup).
        if (catalogInfo is { Count: > 0 })
        {
            long totalBytes = 0;
            foreach (var fvi in catalogInfo.Values)
                totalBytes += fvi.SizeBytes;
            sourceSelection.SeedResult =
                $"{catalogInfo.Count:N0} files ({FormatBytes(totalBytes)}) in catalog.";
        }

        // Helper: sync all VM settings into the BackupSet and write to DB.
        async Task SaveAllAsync()
        {
            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();
            await Task.Run(() => _catalog.UpdateBackupSetAsync(backupSet));
        }

        // Register a pending save so settings are persisted on dialog close.
        _pendingSettingsSave = SaveAllAsync;

        // ---------------------------------------------------------------
        // Phase 2: create dialog, set content, then show (invisible → reveal)
        // ---------------------------------------------------------------

        var dialog = new BackupSetEditorWindow
        {
            Owner = Application.Current.MainWindow,
            Title = $"Modify \u2014 {backupSet.Name}",
        };
        _editorWindow = dialog;

        // Prompt before closing if there are unsaved changes.
        // For new sets: "Discard?"  For existing sets: "Save before closing?"
        dialog.Closing += (_, e) =>
        {
            if (!sourceSelection.HasUnsavedChanges)
                return;

            if (_unsavedNewSetId is not null)
            {
                var result = MessageBox.Show(
                    "This backup set hasn't been saved yet.\n\nDiscard it?",
                    "Unsaved Backup Set",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    e.Cancel = true;
            }
            else
            {
                var result = MessageBox.Show(
                    "You have unsaved changes.\n\nSave before closing?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                    e.Cancel = true;
                else if (result == MessageBoxResult.No)
                    _pendingSettingsSave = null; // skip auto-save
            }
        };

        dialog.Closed += async (_, _) =>
        {
            // Stop any background PlanAsync scan.
            autoCheckCts?.Cancel();

            if (_unsavedNewSetId is int unsavedId)
            {
                // User closed without saving a new set — discard the
                // temporary DB record so no orphan appears in the catalog.
                _unsavedNewSetId = null;
                _pendingSettingsSave = null;
                try { await _catalog.DeleteBackupSetAsync(unsavedId); }
                catch { /* best effort */ }
            }
            else if (_pendingSettingsSave is not null)
            {
                // Existing set — save all settings on close.
                await _pendingSettingsSave();
                _pendingSettingsSave = null;
            }
            _editorWindow = null;
            await LoadBackupSetsAsync();
        };

        // Save button: persist everything and show confirmation.
        sourceSelection.SaveRequested += async () =>
        {
            try
            {
                await SaveAllAsync();
                _unsavedNewSetId = null; // committed — don't delete on close
                StatusText = $"Backup set \"{sourceSelection.SetName}\" saved. {DateTime.Now:HH:mm:ss}";
                sourceSelection.SaveStatusText = "Saved";
                await LoadBackupSetsAsync();
                SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id);
                dialog.Title = $"Modify \u2014 {sourceSelection.SetName}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save: {ex.Message}";
                sourceSelection.SaveStatusText = "Save failed";
            }
        };

        sourceSelection.CancelRequested += () => dialog.Close();

        // "Seed from Existing Backup" button: imports files from an existing
        // mirror-format backup directory (e.g. backup4all mirror) into the
        // catalog so future incremental backups only copy new/changed files.
        sourceSelection.SeedFromExistingRequested += async () =>
        {
            // Sync and save current settings first.
            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();
            await Task.Run(() => _catalog.UpdateBackupSetAsync(backupSet));

            string? dir = sourceSelection.TargetDirectory;
            if (sourceSelection.CreateSubdirectory
                && !string.IsNullOrWhiteSpace(sourceSelection.SubdirectoryName))
                dir = Path.Combine(dir!, sourceSelection.SubdirectoryName.Trim());

            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                sourceSelection.SeedResult = "Destination directory does not exist.";
                return;
            }

            long lastSeedUpdate = 0;
            int lastSkippedExisting = 0;
            var seedSw = System.Diagnostics.Stopwatch.StartNew();
            var scanProgress = new Progress<ScanProgress>(sp =>
            {
                // Track the most recent skipped count so the post-completion
                // message can show "X already in catalog" even after the
                // throttled progress reports stop firing.
                lastSkippedExisting = sp.FilesSkipped;

                long now = seedSw.ElapsedMilliseconds;
                if (now - lastSeedUpdate < ProgressUpdateIntervalMs)
                    return;
                lastSeedUpdate = now;

                string current = string.IsNullOrEmpty(sp.CurrentDirectory) ? ""
                    : $"\n{sp.CurrentDirectory}";
                string skipped = sp.FilesSkipped > 0
                    ? $", {sp.FilesSkipped:N0} already in catalog"
                    : "";
                sourceSelection.SeedResult =
                    $"Importing... {sp.FilesFound:N0} files ({FormatBytes(sp.TotalBytes)}){skipped}{current}";
            });

            bool skipHash = sourceSelection.SeedSkipHashing;
            var seedCt = sourceSelection.SeedCancellationToken;
            int count = await Task.Run(() =>
                _directoryBackupService.SeedFromExistingDirectoryAsync(
                    backupSet.Id, dir, scanProgress, seedCt,
                    skipHashing: skipHash));

            if (count > 0)
            {
                string skippedSuffix = lastSkippedExisting > 0
                    ? $" ({lastSkippedExisting:N0} already in catalog were skipped)"
                    : "";
                sourceSelection.SeedResult =
                    $"Imported {count:N0} files{skippedSuffix}. Future backups will be incremental.";

                // Check source tree nodes matching the seeded directory structure
                // so the user sees which drives/directories are covered.
                await ApplySeedSelectionsAsync(sourceSelection, dir!);

                sourceSelection.HasSelection = true;
            }
            else if (lastSkippedExisting > 0)
            {
                sourceSelection.SeedResult =
                    $"Nothing new to import — all {lastSkippedExisting:N0} files were already in the catalog.";
            }
            else
            {
                sourceSelection.SeedResult = "No files found to import.";
            }
        };

        // Shared helper: cancel any in-progress PlanAsync and re-run after
        // a debounce.  Used by both selection changes and exclusion changes.
        void TriggerPlanReCheck()
        {
            autoCheckCts?.Cancel();
            if (!planCheckReady) return;
            autoCheckCts = new CancellationTokenSource();
            var ct = autoCheckCts.Token;
            _ = DebouncedPlanCheckAsync(ct);
        }

        async Task DebouncedPlanCheckAsync(CancellationToken token)
        {
            try
            {
                // Longer debounce — PlanAsync is expensive, wait for the
                // user to finish toggling checkboxes or typing patterns.
                await Task.Delay(1000, token);
                sourceSelection.BuildSizeReport();
                await RunPlanCheckInEditorAsync(sourceSelection, backupSet, token);
            }
            catch (OperationCanceledException) { }
        }

        // Auto-save source selections to the database whenever the user
        // toggles a checkbox.  Debounced because cascading parent→child
        // changes fire the callback many times for a single click.
        CancellationTokenSource? saveDebounce = null;
        sourceSelection.SelectionChanged += () =>
        {
            // Don't auto-save while restoring saved selections — the tree
            // is in a partially-restored state and GetSelections() would
            // produce incomplete data, overwriting the real saved state.
            if (sourceSelection.IsApplyingSelections) return;

            saveDebounce?.Cancel();
            saveDebounce = new CancellationTokenSource();
            var ct = saveDebounce.Token;
            _ = DebouncedSaveAsync(ct);

            async Task DebouncedSaveAsync(CancellationToken token)
            {
                try
                {
                    await Task.Delay(300, token);
                    // Sync ALL settings (including JobOptions) so the
                    // full BackupSet write never overwrites with stale data.
                    SyncSettingsToJobOptions(backupSet, sourceSelection);
                    backupSet.SourceSelections = sourceSelection.GetSelections();
                    await Task.Run(() => _catalog.UpdateBackupSetAsync(backupSet));
                }
                catch (OperationCanceledException) { }
            }

            // Re-run PlanAsync so the size report reflects the new selection.
            TriggerPlanReCheck();
        };

        // Re-run PlanAsync when the user edits tier set file patterns
        // (exclusion/inclusion lists) so the "To back up" line updates.
        sourceSelection.ExclusionSettingsChanged += TriggerPlanReCheck;

        // Open Largest Files in a separate window so the modify dialog
        // stays visible.  Only one instance at a time.
        sourceSelection.LargestFilesRequested += async () =>
        {
            if (_largestFilesWindow is not null)
            {
                _largestFilesWindow.Activate();
                return;
            }

            // Flush any pending debounced save so the scan sees current state.
            saveDebounce?.Cancel();
            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();
            await Task.Run(() => _catalog.UpdateBackupSetAsync(backupSet));

            int estimatedCount = 0;
            try { estimatedCount = await _catalog.GetFileCountForBackupSetAsync(backupSet.Id); }
            catch { }

            var vm = new LargestFilesViewModel(_scanner, _catalog, backupSet, estimatedCount);

            var lfWindow = new BackupSetEditorWindow
            {
                Owner = dialog,
                Title = $"Largest Files \u2014 {backupSet.Name}",
                SizeToContent = SizeToContent.Manual,
                Height = 600,
            };

            _largestFilesWindow = lfWindow;

            vm.DoneRequested += () => lfWindow.Close();

            vm.SaveRequested += async () =>
            {
                try
                {
                    SyncSettingsToJobOptions(backupSet, sourceSelection);
                    backupSet.SourceSelections = sourceSelection.GetSelections();
                    await Task.Run(() => _catalog.UpdateBackupSetAsync(backupSet));
                    vm.SaveStatusText = "Saved";
                }
                catch (Exception ex)
                {
                    vm.SaveStatusText = $"Save failed: {ex.Message}";
                }
            };

            lfWindow.Closed += (_, _) =>
            {
                vm.CancelScan();
                _largestFilesWindow = null;

                // LargestFiles may have modified ExcludedExtensions on the
                // backup set; those changes are persisted directly on the
                // BackupSet.JobOptions and don't need syncing back here.
            };

            lfWindow.SetEditorContent(vm);
            lfWindow.Show();
        };

        // Set content BEFORE showing so layout happens on the real view.
        // Show() is needed for WPF to have a handle/PresentationSource.
        // The window starts at Opacity 0 and reveals itself after layout
        // completes inside SetEditorContent (which also clears the wait cursor).
        dialog.SetEditorContent(sourceSelection);
        dialog.Show();

        StatusText = $"Editing backup set \"{backupSet.Name}\".";

        // ---------------------------------------------------------------
        // Phase 3: restore selections and compute sizes (dialog visible)
        // ---------------------------------------------------------------
        // The view shows a "Restoring selections..." overlay (bound to
        // IsApplyingSelections) while this runs.  This avoids blocking
        // the UI for seconds before the window appears.

        if (backupSet.SourceSelections is { Count: > 0 })
            await sourceSelection.ApplySelectionsAsync(backupSet.SourceSelections);

        // Selections are now restored — re-sort so size-based ordering uses
        // the correct effective sizes (during initial load, children had
        // IsSelected = false so GetEffectiveSize returned -1 for everything).
        if (sourceSelection.CurrentSortColumn == SortColumn.Size)
        {
            foreach (var root in sourceSelection.Roots)
                root.SortChildren();
        }

        // For existing sets, mark clean AFTER selections are restored so
        // the restore itself doesn't count as a user change.
        if (_unsavedNewSetId is null)
            sourceSelection.MarkClean();

        _ = PostShowInitAsync();

        async Task PostShowInitAsync()
        {
            await sourceSelection.ComputeAllUnknownSizesAsync();
            planCheckReady = true;
            autoCheckCts = new CancellationTokenSource();
            await RunPlanCheckInEditorAsync(sourceSelection, backupSet, autoCheckCts.Token);
        }
    }

    // -------------------------------------------------------------------
    // Change destination path for a directory-mode backup set
    // -------------------------------------------------------------------

    private async Task ChangeDestinationAsync()
    {
        if (SelectedBackupSet?.JobOptions is null) return;

        var backupSet = SelectedBackupSet;
        string oldPath = backupSet.JobOptions.TargetDirectory ?? "";

        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Select new destination for \"{backupSet.Name}\"",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (!string.IsNullOrWhiteSpace(oldPath) && System.IO.Directory.Exists(oldPath))
            dialog.SelectedPath = oldPath;

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        string newPath = dialog.SelectedPath;
        if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
            return;

        // Confirm and explain what this does.
        var confirm = MessageBox.Show(
            $"Change destination from:\n{oldPath}\n\nTo:\n{newPath}\n\n" +
            "This relocates where LithicBackup looks for your existing backup " +
            "files. Use this when your backup drive has changed drive letters " +
            "or mount points and the files are already at the new path.\n\n" +
            "If you want to start a fresh backup to a new empty location, " +
            "click No and create a new backup set instead.",
            "Relocate Backup Destination",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            // Update the saved job options.
            backupSet.JobOptions.TargetDirectory = newPath;
            await _catalog.UpdateBackupSetAsync(backupSet);

            // Update disc record labels so they reflect the new path.
            var discs = await _catalog.GetDiscsForBackupSetAsync(backupSet.Id);
            foreach (var disc in discs)
            {
                if (string.Equals(disc.Label, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    disc.Label = newPath;
                    await _catalog.UpdateDiscAsync(disc);
                }
            }

            StatusText = $"Destination relocated: {oldPath} → {newPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to change destination: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------
    // Copy backup set
    // -------------------------------------------------------------------

    private async Task CopyBackupSetAsync()
    {
        if (SelectedBackupSet is null) return;

        var src = SelectedBackupSet;

        try
        {
            // Deep-copy job options so the new set is independent.
            JobOptions? copiedOptions = null;
            if (src.JobOptions is not null)
            {
                copiedOptions = new JobOptions
                {
                    ZipMode = src.JobOptions.ZipMode,
                    FilesystemType = src.JobOptions.FilesystemType,
                    CapacityOverrideBytes = src.JobOptions.CapacityOverrideBytes,
                    VerifyAfterBurn = src.JobOptions.VerifyAfterBurn,
                    VerifyAfterBackup = src.JobOptions.VerifyAfterBackup,
                    IncludeCatalogOnDisc = src.JobOptions.IncludeCatalogOnDisc,
                    AllowFileSplitting = src.JobOptions.AllowFileSplitting,
                    EnableFileDeduplication = src.JobOptions.EnableFileDeduplication,
                    EnableDeduplication = src.JobOptions.EnableDeduplication,
                    DeduplicationBlockSize = src.JobOptions.DeduplicationBlockSize,
                    RetentionTiers = src.JobOptions.RetentionTiers.Select(t => new VersionRetentionTier
                    {
                        MaxAge = t.MaxAge,
                        MaxVersions = t.MaxVersions,
                    }).ToList(),
                    TierSets = src.JobOptions.TierSets.Select(ts => new VersionTierSet
                    {
                        Name = ts.Name,
                        Tiers = ts.Tiers.Select(t => new VersionRetentionTier
                        {
                            MaxAge = t.MaxAge,
                            MaxVersions = t.MaxVersions,
                        }).ToList(),
                        FilePatterns = [.. ts.FilePatterns],
                        FileExemptPatterns = [.. ts.FileExemptPatterns],
                    }).ToList(),
                    TargetDirectory = src.JobOptions.TargetDirectory,
                    CreateSubdirectory = src.JobOptions.CreateSubdirectory,
                    SubdirectoryName = src.JobOptions.SubdirectoryName,
                    ExcludedExtensions = [.. src.JobOptions.ExcludedExtensions],
                    Schedule = src.JobOptions.Schedule is not null
                        ? new BackupSchedule
                        {
                            Enabled = src.JobOptions.Schedule.Enabled,
                            Mode = src.JobOptions.Schedule.Mode,
                            IntervalHours = src.JobOptions.Schedule.IntervalHours,
                            DailyHour = src.JobOptions.Schedule.DailyHour,
                            DailyMinute = src.JobOptions.Schedule.DailyMinute,
                            DebounceSeconds = src.JobOptions.Schedule.DebounceSeconds,
                        }
                        : null,
                };
            }

            var newSet = await _catalog.CreateBackupSetAsync(new BackupSet
            {
                Name = $"Copy of {src.Name}",
                SourceRoots = [.. src.SourceRoots],
                SourceSelections = src.SourceSelections,
                JobOptions = copiedOptions,
                MaxIncrementalDiscs = src.MaxIncrementalDiscs,
                DefaultMediaType = src.DefaultMediaType,
                DefaultFilesystemType = src.DefaultFilesystemType,
                CapacityOverrideBytes = src.CapacityOverrideBytes,
                CreatedUtc = DateTime.UtcNow,
            });

            await LoadBackupSetsAsync();

            // Select the new copy.
            SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == newSet.Id);
            StatusText = $"Created \"{newSet.Name}\" from \"{src.Name}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to copy backup set: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------
    // Delete Backup Set
    // -------------------------------------------------------------------

    private async Task DeleteBackupSetAsync(BackupSet backupSet)
    {
        var result = MessageBox.Show(
            $"Permanently delete \"{backupSet.Name}\"?\n\n" +
            "This removes the backup set and all its catalog records " +
            "(disc entries, file records, etc.) from the database.\n\n" +
            "Files already written to disc or directory are not affected.",
            "Delete Backup Set",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            await _catalog.DeleteBackupSetAsync(backupSet.Id);
            await LoadBackupSetsAsync();

            if (SelectedBackupSet?.Id == backupSet.Id)
                SelectedBackupSet = null;

            StatusText = $"Deleted \"{backupSet.Name}\".";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to delete backup set: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------
    // Check Size (run PlanAsync without starting backup)
    // -------------------------------------------------------------------

    private async Task CheckSizeAsync()
    {
        if (SelectedBackupSet is null) return;

        var backupSet = SelectedBackupSet;
        var sources = backupSet.SourceSelections;
        if (sources is null or { Count: 0 })
        {
            StatusText = "No sources configured — open Modify to set up this backup set.";
            return;
        }

        var opts = backupSet.JobOptions;
        if (opts is null)
        {
            StatusText = "No job options saved — open Modify to configure this backup set.";
            return;
        }

        // Ask upfront whether to also check duplicates (only when dedup is on).
        bool alsoCheckDuplicates = false;
        if ((opts.EnableFileDeduplication || opts.EnableDeduplication) && _fileHashCache is not null)
        {
            var answer = MessageBox.Show(
                "Also scan for duplicate files?\n\n" +
                "Hashes are cached, so this also speeds up the next backup.",
                "Check Size",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            alsoCheckDuplicates = answer == MessageBoxResult.Yes;
        }

        string? effectiveTargetDir = opts.TargetDirectory;
        if (effectiveTargetDir is not null && opts.CreateSubdirectory
            && !string.IsNullOrWhiteSpace(opts.SubdirectoryName))
        {
            effectiveTargetDir = Path.Combine(effectiveTargetDir, opts.SubdirectoryName.Trim());
        }

        var job = new BackupJob
        {
            BackupSetId = backupSet.Id,
            Sources = sources,
            ZipMode = opts.ZipMode,
            FilesystemType = opts.FilesystemType,
            CapacityOverrideBytes = opts.CapacityOverrideBytes,
            VerifyAfterBurn = opts.VerifyAfterBurn,
            VerifyAfterBackup = opts.VerifyAfterBackup,
            IncludeCatalogOnDisc = opts.IncludeCatalogOnDisc,
            AllowFileSplitting = opts.AllowFileSplitting,
            TargetDirectory = effectiveTargetDir,
            CreateSubdirectory = opts.CreateSubdirectory,
            SubdirectoryName = opts.CreateSubdirectory ? opts.SubdirectoryName?.Trim() : null,
            EnableFileDeduplication = opts.EnableFileDeduplication,
            EnableDeduplication = opts.EnableDeduplication,
            DeduplicationBlockSize = opts.DeduplicationBlockSize > 0
                ? opts.DeduplicationBlockSize : 64 * 1024,
            ExcludedExtensions = opts.ExcludedExtensions,
            RetentionTiers = opts.RetentionTiers,
            TierSets = opts.TierSets,
        };

        bool isDir = job.TargetDirectory is not null;

        _checkSizeCts = new CancellationTokenSource();
        var ct = _checkSizeCts.Token;
        CheckingSizeSetId = backupSet.Id;
        CommandManager.InvalidateRequerySuggested();

        StatusText = $"Checking \"{backupSet.Name}\"...";

        try
        {
            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            long lastScanUpdate = 0;
            var scanProgress = new Progress<ScanProgress>(sp =>
            {
                long now = scanSw.ElapsedMilliseconds;
                if (now - lastScanUpdate >= ProgressUpdateIntervalMs)
                {
                    lastScanUpdate = now;
                    StatusText = $"Checking \"{backupSet.Name}\"... {sp.FilesFound:N0} files scanned";
                }
            });

            int totalFiles;
            long totalBytes;

            if (isDir && _directoryBackupService is not null)
            {
                var (diff, bytes, files) = await Task.Run(
                    () => _directoryBackupService.PlanAsync(job, ct, scanProgress));
                totalFiles = files;
                totalBytes = bytes;

                int newCount = diff.NewFiles.Count;
                int changedCount = diff.ChangedFiles.Count;
                int deletedCount = diff.DeletedFiles.Count;

                string msg = totalFiles == 0
                    ? $"\"{backupSet.Name}\": nothing to back up — all files are current."
                    : $"\"{backupSet.Name}\": {totalFiles:N0} file(s) to back up ({FormatBytes(totalBytes)}) " +
                      $"— {newCount:N0} new, {changedCount:N0} changed, {deletedCount:N0} deleted";

                // Check free space.
                if (totalFiles > 0 && effectiveTargetDir is not null)
                {
                    try
                    {
                        string pathRoot = Path.GetPathRoot(effectiveTargetDir) ?? effectiveTargetDir;
                        var driveInfo = new DriveInfo(pathRoot);
                        if (driveInfo.IsReady)
                        {
                            long free = driveInfo.AvailableFreeSpace;
                            if (totalBytes > free)
                                msg += $" — \u26A0 only {FormatBytes(free)} free!";
                        }
                    }
                    catch { }
                }

                StatusText = msg;
            }
            else if (!isDir)
            {
                var plan = await Task.Run(
                    () => _orchestrator.PlanAsync(job, ct, scanProgress));
                totalFiles = plan.Diff.NewFiles.Count + plan.Diff.ChangedFiles.Count;
                totalBytes = plan.TotalBytes;

                StatusText = totalFiles == 0
                    ? $"\"{backupSet.Name}\": nothing to back up — all files are current."
                    : $"\"{backupSet.Name}\": {totalFiles:N0} file(s) to back up ({FormatBytes(totalBytes)}), " +
                      $"{plan.TotalDiscsRequired} disc(s) required";
            }
            else
            {
                StatusText = "Directory backup service not available.";
            }

            // Phase 2: duplicate analysis (same CTS, same flag).
            if (alsoCheckDuplicates)
                await RunDuplicateAnalysisAsync(backupSet, ct);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Check aborted.";
        }
        catch (Exception ex)
        {
            StatusText = $"Check failed: {ex.Message}";
        }
        finally
        {
            // Flush any hashes computed before abort/completion so they
            // survive to the next run even if we didn't reach the end.
            try { _fileHashCache?.Flush(); } catch { }

            CheckingSizeSetId = null;
            _checkSizeCts?.Dispose();
            _checkSizeCts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // -------------------------------------------------------------------
    // Duplicate analysis (called from CheckSizeAsync when user opts in)
    // -------------------------------------------------------------------

    /// <summary>
    /// Scan files for duplicates by grouping by size, then hashing same-size
    /// candidates. Exceptions propagate to the caller (which owns the
    /// <see cref="CheckingSizeSetId"/> flag and CTS).
    /// </summary>
    private async Task RunDuplicateAnalysisAsync(BackupSet backupSet, CancellationToken ct)
    {
        if (_fileHashCache is null) return;

        var sources = backupSet.SourceSelections;
        var opts = backupSet.JobOptions;
        if (sources is null or { Count: 0 } || opts is null) return;

        StatusText = $"Scanning \"{backupSet.Name}\" for duplicates...";

        // Build exclusion filter matching what the backup service uses.
        var job = new BackupJob
        {
            Sources = sources,
            ExcludedExtensions = opts.ExcludedExtensions,
            TierSets = opts.TierSets,
        };
        var excludeFilter = DirectoryBackupService.BuildExclusionFilter(job);

        // Scan files.
        var scanned = await Task.Run(
            () => _scanner.ScanAsync(sources, progress: null, ct, excludeFilter));
        ct.ThrowIfCancellationRequested();

        if (scanned.Count == 0)
        {
            StatusText = $"\"{backupSet.Name}\": no files found.";
            return;
        }

        long totalRawSize = scanned.Sum(f => f.SizeBytes);
        StatusText = $"Scanning \"{backupSet.Name}\" for duplicates... " +
            $"{scanned.Count:N0} files, grouping by size...";

        // Group by size — only groups with 2+ files need hashing.
        var sizeGroups = scanned
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() >= 2)
            .ToList();

        int candidateFiles = sizeGroups.Sum(g => g.Count());
        int uniqueSizeFiles = scanned.Count - candidateFiles;

        // Hash candidates on a background thread.
        int filesHashed = 0;
        int filesProcessed = 0;
        long lastProgressTick = 0;
        var hashMap = new Dictionary<string, List<ScannedFile>>();
        string setName = backupSet.Name;

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
                            StatusText = $"Scanning \"{setName}\" for duplicates... " +
                                $"hashing candidate {n + 1:N0} of {candidateFiles:N0}" +
                                (h > 0 ? $" ({h:N0} new)" : ""));
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
                            var bytes = await System.Security.Cryptography.SHA256
                                .HashDataAsync(stream, ct);
                            hash = Convert.ToHexString(bytes).ToLowerInvariant();

                            _fileHashCache.Set(
                                file.FullPath, file.SizeBytes, file.LastWriteUtc, hash);
                            filesHashed++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
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

        // Compute savings.
        long bytesSaved = 0;
        int duplicateFiles = 0;
        foreach (var (_, files) in hashMap)
        {
            if (files.Count <= 1) continue;
            for (int i = 1; i < files.Count; i++)
            {
                bytesSaved += files[i].SizeBytes;
                duplicateFiles++;
            }
        }

        if (duplicateFiles == 0)
        {
            StatusText = $"\"{backupSet.Name}\": {scanned.Count:N0} files " +
                $"({FormatBytes(totalRawSize)}), no duplicates found.";
        }
        else
        {
            StatusText = $"\"{backupSet.Name}\": {scanned.Count:N0} files " +
                $"({FormatBytes(totalRawSize)}), {duplicateFiles:N0} duplicates " +
                $"({FormatBytes(bytesSaved)} saveable with file-level dedup)" +
                (filesHashed > 0 ? $"  [{filesHashed:N0} hashed, {candidateFiles - filesHashed:N0} cached]" : "");
        }
    }

    /// <summary>
    /// Run PlanAsync against the current editor state and show accurate
    /// new/changed/deleted counts in <see cref="SourceSelectionViewModel.SizeCalculationResult"/>.
    /// Cancellable — pass a token that is cancelled on selection change or dialog close.
    /// </summary>
    private async Task RunPlanCheckInEditorAsync(
        SourceSelectionViewModel sourceVm, BackupSet backupSet, CancellationToken ct)
    {
        // Sync current UI state so the job reflects the latest settings.
        SyncSettingsToJobOptions(backupSet, sourceVm);
        backupSet.SourceSelections = sourceVm.GetSelections();

        var opts = backupSet.JobOptions;
        var sources = backupSet.SourceSelections;
        if (opts is null || sources is null or { Count: 0 })
            return;

        string? effectiveTargetDir = opts.TargetDirectory;
        if (effectiveTargetDir is not null && opts.CreateSubdirectory
            && !string.IsNullOrWhiteSpace(opts.SubdirectoryName))
            effectiveTargetDir = Path.Combine(effectiveTargetDir, opts.SubdirectoryName.Trim());

        var job = new BackupJob
        {
            BackupSetId = backupSet.Id,
            Sources = sources,
            ZipMode = opts.ZipMode,
            FilesystemType = opts.FilesystemType,
            CapacityOverrideBytes = opts.CapacityOverrideBytes,
            VerifyAfterBurn = opts.VerifyAfterBurn,
            VerifyAfterBackup = opts.VerifyAfterBackup,
            IncludeCatalogOnDisc = opts.IncludeCatalogOnDisc,
            AllowFileSplitting = opts.AllowFileSplitting,
            TargetDirectory = effectiveTargetDir,
            CreateSubdirectory = opts.CreateSubdirectory,
            SubdirectoryName = opts.CreateSubdirectory ? opts.SubdirectoryName?.Trim() : null,
            EnableFileDeduplication = opts.EnableFileDeduplication,
            EnableDeduplication = opts.EnableDeduplication,
            DeduplicationBlockSize = opts.DeduplicationBlockSize > 0
                ? opts.DeduplicationBlockSize : 64 * 1024,
            ExcludedExtensions = opts.ExcludedExtensions,
            RetentionTiers = opts.RetentionTiers,
            TierSets = opts.TierSets,
        };

        bool isDir = job.TargetDirectory is not null;

        // Capture the quick report as the base text; scanning progress appends to it.
        string baseReport = sourceVm.SizeCalculationResult;
        string scanningLine = "Scanning for changes...";
        sourceVm.SizeCalculationResult = string.IsNullOrEmpty(baseReport)
            ? scanningLine
            : baseReport + "\n" + scanningLine;

        try
        {
            ct.ThrowIfCancellationRequested();

            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            long lastUpdate = 0;
            var scanProgress = new Progress<ScanProgress>(sp =>
            {
                long now = scanSw.ElapsedMilliseconds;
                if (now - lastUpdate >= ProgressUpdateIntervalMs)
                {
                    lastUpdate = now;
                    string line = $"Scanning for changes... {sp.FilesFound:N0} files checked";
                    sourceVm.SizeCalculationResult = string.IsNullOrEmpty(baseReport)
                        ? line : baseReport + "\n" + line;
                }
            });

            string resultLine;

            if (isDir && _directoryBackupService is not null)
            {
                var (diff, totalBytes, totalFiles) = await Task.Run(
                    () => _directoryBackupService.PlanAsync(job, ct, scanProgress));

                if (totalFiles == 0)
                {
                    resultLine = "All selected files are backed up and up to date.";
                }
                else
                {
                    resultLine = $"To back up: {totalFiles:N0} file(s) ({FormatBytes(totalBytes)}) \u2014 " +
                        $"{diff.NewFiles.Count:N0} new, {diff.ChangedFiles.Count:N0} changed, {diff.DeletedFiles.Count:N0} deleted";

                    // Free space warning.
                    if (effectiveTargetDir is not null)
                    {
                        try
                        {
                            string pathRoot = Path.GetPathRoot(effectiveTargetDir) ?? effectiveTargetDir;
                            var driveInfo = new DriveInfo(pathRoot);
                            if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < totalBytes)
                                resultLine += $"\n\u26A0 Only {FormatBytes(driveInfo.AvailableFreeSpace)} free \u2014 need {FormatBytes(totalBytes)}";
                        }
                        catch { }
                    }
                }
            }
            else if (!isDir)
            {
                var plan = await Task.Run(
                    () => _orchestrator.PlanAsync(job, ct, scanProgress));
                int totalFiles = plan.Diff.NewFiles.Count + plan.Diff.ChangedFiles.Count;

                if (totalFiles == 0)
                    resultLine = "All selected files are backed up and up to date.";
                else
                    resultLine = $"To back up: {totalFiles:N0} file(s) ({FormatBytes(plan.TotalBytes)}), " +
                        $"{plan.TotalDiscsRequired} disc(s) required";
            }
            else
            {
                return;
            }

            // Replace scanning indicator with accurate result.
            sourceVm.SizeCalculationResult = string.IsNullOrEmpty(baseReport)
                ? resultLine : baseReport + "\n" + resultLine;
        }
        catch (Exception)
        {
            // Cancelled or failed — restore base report without scanning indicator.
            sourceVm.SizeCalculationResult = baseReport;
        }
    }

    // -------------------------------------------------------------------
    // Flow 2: Incremental Backup (existing backup set)
    //   Builds a BackupJob from saved options, plans, and starts
    //   immediately — no configuration page.
    // -------------------------------------------------------------------

    private async void StartIncrementalFlow()
    {
        if (SelectedBackupSet is null)
            return;

        var backupSet = SelectedBackupSet;
        var opts = backupSet.JobOptions ?? new JobOptions();

        // Prefer the full saved selection tree; fall back to root-only reconstruction.
        List<SourceSelection> sources;
        if (backupSet.SourceSelections is { Count: > 0 })
        {
            sources = backupSet.SourceSelections;
        }
        else
        {
            sources = backupSet.SourceRoots
                .Select(root => new SourceSelection
                {
                    Path = root,
                    IsDirectory = true,
                    IsSelected = true,
                    AutoIncludeNewSubdirectories = true,
                })
                .ToList();
        }

        // Build the BackupJob directly from saved JobOptions.
        string? effectiveTargetDir = opts.TargetDirectory;
        if (effectiveTargetDir is not null && opts.CreateSubdirectory
            && !string.IsNullOrWhiteSpace(opts.SubdirectoryName))
        {
            effectiveTargetDir = Path.Combine(effectiveTargetDir, opts.SubdirectoryName.Trim());
        }

        var job = new BackupJob
        {
            BackupSetId = backupSet.Id,
            Sources = sources,
            ZipMode = opts.ZipMode,
            FilesystemType = opts.FilesystemType,
            CapacityOverrideBytes = opts.CapacityOverrideBytes,
            VerifyAfterBurn = opts.VerifyAfterBurn,
            VerifyAfterBackup = opts.VerifyAfterBackup,
            IncludeCatalogOnDisc = opts.IncludeCatalogOnDisc,
            AllowFileSplitting = opts.AllowFileSplitting,
            TargetDirectory = effectiveTargetDir,
            CreateSubdirectory = opts.CreateSubdirectory,
            SubdirectoryName = opts.CreateSubdirectory ? opts.SubdirectoryName?.Trim() : null,
            EnableFileDeduplication = opts.EnableFileDeduplication,
            EnableDeduplication = opts.EnableDeduplication,
            DeduplicationBlockSize = opts.DeduplicationBlockSize > 0
                ? opts.DeduplicationBlockSize : 64 * 1024,
            ExcludedExtensions = opts.ExcludedExtensions,
            RetentionTiers = opts.RetentionTiers,
            TierSets = opts.TierSets,
        };

        bool isDir = job.TargetDirectory is not null;

        // Mark the set as running immediately so the per-set buttons
        // switch from Backup/Restore/Modify to Pause/Abort right away.
        // Clear any stale progress panel from a previous run.
        ActiveBackupProgress = null;
        IsBurning = true;
        RunningBackupSetId = backupSet.Id;
        _scanCts = new CancellationTokenSource();
        var scanToken = _scanCts.Token;
        StatusText = $"Scanning \"{backupSet.Name}\"...";

        // Show a wait cursor until the burn progress panel appears.  Scan +
        // plan can take many seconds on large sets, and without this the
        // user has no immediate visual confirmation that the click landed.
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            // Throttled scan progress reporter.  The `scanning` flag is
            // cleared after planning so late-arriving Progress callbacks
            // (queued on the dispatcher) don't overwrite StartBurn's status.
            bool scanning = true;
            var lastScanUpdate = 0L;
            var scanSw = System.Diagnostics.Stopwatch.StartNew();
            var scanProgress = new Progress<ScanProgress>(sp =>
            {
                if (!scanning) return;
                long now = scanSw.ElapsedMilliseconds;
                if (now - lastScanUpdate >= ProgressUpdateIntervalMs)
                {
                    lastScanUpdate = now;
                    StatusText = $"Scanning \"{backupSet.Name}\"... {sp.FilesFound:N0} files scanned ({FormatBytes(sp.TotalBytes)})";
                }
            });

            BackupPlan plan;
            if (isDir)
            {
                if (_directoryBackupService is null)
                {
                    StatusText = "Directory backup service not available.";
                    IsBurning = false;
                    RunningBackupSetId = null;
                    return;
                }

                var (diff, totalBytes, totalFiles) = await Task.Run(
                    () => _directoryBackupService.PlanAsync(job, scanToken, scanProgress));

                if (totalFiles == 0)
                {
                    StatusText = "Nothing to back up — all files are already current.";
                    IsBurning = false;
                    RunningBackupSetId = null;
                    return;
                }

                StatusText = $"{totalFiles:N0} file(s) to back up ({FormatBytes(totalBytes)})";

                plan = new BackupPlan
                {
                    Job = job,
                    Diff = diff,
                    DiscAllocations = [],
                    TotalDiscsRequired = 0,
                    TotalBytes = totalBytes,
                };

                // Check free space before starting.
                if (!CheckFreeSpaceBeforeBackup(job.TargetDirectory!, totalBytes))
                {
                    IsBurning = false;
                    RunningBackupSetId = null;
                    return;
                }
            }
            else
            {
                plan = await Task.Run(
                    () => _orchestrator.PlanAsync(job, scanToken, scanProgress));

                int totalFiles = plan.Diff.NewFiles.Count + plan.Diff.ChangedFiles.Count;
                if (totalFiles == 0)
                {
                    StatusText = "Nothing to back up — all files are already current.";
                    IsBurning = false;
                    RunningBackupSetId = null;
                    return;
                }

                StatusText = $"{totalFiles:N0} file(s) to back up ({FormatBytes(plan.TotalBytes)})";
            }

            // Stop scan-progress callbacks from overwriting burn status.
            scanning = false;
            StartBurn(plan);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Backup cancelled.";
            IsBurning = false;
            RunningBackupSetId = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Backup failed: {ex.GetType().Name}: {ex.Message}";
            IsBurning = false;
            RunningBackupSetId = null;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            // Clear the wait cursor.  By this point either the burn progress
            // panel is visible (StartBurn assigned ActiveBackupProgress) or we
            // bailed out with an error / cancellation / nothing-to-do.
            Mouse.OverrideCursor = null;
        }
    }

    // -------------------------------------------------------------------
    // Shared: Job Config → Plan → Burn
    // -------------------------------------------------------------------

    private void ShowJobConfig(List<SourceSelection> sources, int? backupSetId,
        SourceSelectionViewModel? sourceSettings = null,
        BackupSetEditorWindow? dialog = null)
    {
        var jobConfig = new BackupJobViewModel(sources, _orchestrator, _burner, _directoryBackupService);

        if (backupSetId.HasValue && SelectedBackupSet is not null)
        {
            jobConfig.SetName = SelectedBackupSet.Name;
            RestoreJobOptions(jobConfig, SelectedBackupSet.JobOptions);
        }

        // Settings from the source selection page take precedence over
        // saved options, so apply them after RestoreJobOptions.
        if (sourceSettings is not null)
            ApplySourceSettings(jobConfig, sourceSettings);

        // Save the backup set when planning completes.
        jobConfig.PlanCompleted += async job =>
        {
            try
            {
                backupSetId = await SaveBackupSetAsync(
                    backupSetId, jobConfig.SetName, sources, job, jobConfig.BuildSchedule());
                StatusText = $"Backup set \"{jobConfig.SetName}\" saved. {DateTime.Now:HH:mm:ss}";
                await LoadBackupSetsAsync();
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save backup set: {ex.Message}";
            }
        };

        if (dialog is not null)
        {
            // Editing in dialog — navigation stays within the dialog window.
            dialog.SetEditorContent(jobConfig);
            jobConfig.BackRequested += StartEditFlow;
            jobConfig.StartRequested += plan =>
            {
                _pendingSettingsSave = null; // PlanCompleted already saved — don't overwrite
                dialog.Close();
                plan.Job.BackupSetId = backupSetId;
                StartBurn(plan);
            };
        }
        else
        {
            // Inline in main window (new backup set or incremental backup).
            CurrentView = jobConfig;
            jobConfig.BackRequested += backupSetId.HasValue
                ? () => StartEditFlow()
                : StartNewBackupFlow;
            jobConfig.StartRequested += plan =>
            {
                plan.Job.BackupSetId = backupSetId;
                StartBurn(plan);
            };
        }

        StatusText = "Configure your backup options, then click Plan Backup.";
    }

    /// <summary>
    /// Restore saved job options into a BackupJobViewModel.
    /// </summary>
    private static void RestoreJobOptions(BackupJobViewModel vm, JobOptions? opts)
    {
        if (opts is null)
            return;

        vm.ZipMode = opts.ZipMode;
        vm.FilesystemType = opts.FilesystemType;
        vm.VerifyAfterBurn = opts.VerifyAfterBurn;
        vm.VerifyAfterBackup = opts.VerifyAfterBackup;
        vm.IncludeCatalogOnDisc = opts.IncludeCatalogOnDisc;
        vm.AllowFileSplitting = opts.AllowFileSplitting;
        vm.EnableFileDeduplication = opts.EnableFileDeduplication;
        vm.EnableDeduplication = opts.EnableDeduplication;

        if (opts.CapacityOverrideBytes.HasValue)
        {
            double gb = opts.CapacityOverrideBytes.Value / (1024.0 * 1024 * 1024);
            vm.CapacityOverrideGb = gb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (opts.DeduplicationBlockSize > 0)
            vm.DeduplicationBlockSizeKb = (opts.DeduplicationBlockSize / 1024).ToString();

        if (opts.TargetDirectory is not null)
        {
            vm.IsDirectoryMode = true;
            vm.TargetDirectoryPath = opts.TargetDirectory;
        }

        vm.CreateSubdirectory = opts.CreateSubdirectory;
        if (opts.SubdirectoryName is not null)
            vm.SubdirectoryName = opts.SubdirectoryName;

        if (opts.ExcludedExtensions.Count > 0)
            vm.ExcludedExtensions = BackupJobViewModel.FormatExclusionPatterns(opts.ExcludedExtensions);

        // Restore tier sets.
        if (opts.TierSets.Count > 0)
        {
            vm.TierSets.Clear();
            foreach (var ts in opts.TierSets)
            {
                var tsVm = TierSetViewModel.FromModel(ts, ts.Name is "Default" or "None");
                vm.TierSets.Add(tsVm);
            }
        }
        else if (opts.RetentionTiers.Count > 0)
        {
            // Backward compat: convert flat RetentionTiers to "Default" tier set.
            vm.TierSets.Clear();
            var defaultModel = new VersionTierSet { Name = "Default", Tiers = [.. opts.RetentionTiers] };
            vm.TierSets.Add(TierSetViewModel.FromModel(defaultModel, isBuiltIn: true));
            vm.TierSets.Add(TierSetViewModel.FromModel(new VersionTierSet { Name = "None" }, isBuiltIn: true));
        }

        // Restore legacy RetentionTiers from the "Default" tier set.
        var defSet = vm.TierSets.FirstOrDefault(t => t.Name == "Default");
        if (defSet is not null)
        {
            vm.RetentionTiers.Clear();
            foreach (var tier in defSet.Tiers)
            {
                var model = tier.ToModel();
                var tierVm = RetentionTierViewModel.FromModel(model);
                tierVm.RemoveRequested += t => vm.RetentionTiers.Remove(t);
                vm.RetentionTiers.Add(tierVm);
            }
        }

        // Restore schedule.
        if (opts.Schedule is { Enabled: true } sched)
        {
            vm.ScheduleEnabled = true;
            vm.ScheduleMode = sched.Mode;
            vm.ScheduleIntervalHours = sched.IntervalHours.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            vm.ScheduleDailyHour = sched.DailyHour;
            vm.ScheduleDailyMinute = sched.DailyMinute;
            vm.ScheduleDebounceSeconds = sched.DebounceSeconds.ToString();
        }
    }

    /// <summary>
    /// After seeding, check the source tree nodes that correspond to the
    /// directories found in the mirror.  For each drive-prefix subdirectory
    /// in the mirror (C, D, …), expand the matching drive node and check
    /// the top-level subdirectories that exist in the mirror so the user
    /// sees exactly which directories are covered.
    /// </summary>
    private static async Task ApplySeedSelectionsAsync(
        SourceSelectionViewModel sourceSelection, string mirrorDir)
    {
        if (sourceSelection.RootNode is null) return;

        var rootDir = new DirectoryInfo(mirrorDir);
        if (!rootDir.Exists) return;

        foreach (var driveDir in rootDir.EnumerateDirectories())
        {
            // Same filter as SeedFromExistingDirectoryAsync: accept single/
            // double-letter names, skip _prev/_blocks/etc.
            if (driveDir.Name.StartsWith('_') || driveDir.Name.Length > 2)
                continue;

            string driveRoot = driveDir.Name + @":\";
            var driveNode = sourceSelection.RootNode.Children.FirstOrDefault(c =>
                string.Equals(c.Path, driveRoot, StringComparison.OrdinalIgnoreCase));
            if (driveNode is null)
                continue;

            // Load and expand the drive so its children are visible.
            await driveNode.EnsureChildrenLoadedAsync();
            driveNode.IsExpanded = true;

            // Check subdirectories that have files in the mirror.
            bool anyChecked = false;
            foreach (var subDir in driveDir.EnumerateDirectories())
            {
                if (subDir.Name.StartsWith('_'))
                    continue;

                string subPath = Path.Combine(driveRoot, subDir.Name);
                var childNode = driveNode.Children.FirstOrDefault(c =>
                    string.Equals(c.Path.TrimEnd('\\'), subPath.TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase));
                if (childNode is not null)
                {
                    childNode.IsSelected = true;
                    anyChecked = true;
                }
            }

            // If the mirror only contains loose files directly under the drive
            // prefix (no subdirectories), check the drive itself.
            if (!anyChecked && driveDir.EnumerateFiles().Any())
                driveNode.IsSelected = true;
        }

        sourceSelection.RefreshHasSelection();
    }

    /// <summary>
    /// Restore saved job options into the source selection view's settings section.
    /// </summary>
    private static void RestoreSourceSettings(SourceSelectionViewModel vm, JobOptions? opts)
    {
        if (opts is null) return;

        // Target mode + directory.
        if (opts.TargetDirectory is not null)
        {
            vm.IsDirectoryMode = true;
            vm.TargetDirectory = opts.TargetDirectory;
        }
        vm.CreateSubdirectory = opts.CreateSubdirectory;
        if (opts.SubdirectoryName is not null)
            vm.SubdirectoryName = opts.SubdirectoryName;

        // Disc options.
        vm.ZipMode = opts.ZipMode;
        vm.FilesystemType = opts.FilesystemType;
        vm.VerifyAfterBurn = opts.VerifyAfterBurn;
        vm.IncludeCatalogOnDisc = opts.IncludeCatalogOnDisc;
        vm.AllowFileSplitting = opts.AllowFileSplitting;
        if (opts.CapacityOverrideBytes.HasValue)
        {
            double gb = opts.CapacityOverrideBytes.Value / (1024.0 * 1024 * 1024);
            vm.CapacityOverrideGb = gb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Directory options.
        vm.EnableFileDeduplication = opts.EnableFileDeduplication;
        vm.EnableBlockDeduplication = opts.EnableDeduplication;
        if (opts.DeduplicationBlockSize > 0)
            vm.BlockSizeKb = (opts.DeduplicationBlockSize / 1024).ToString();

        // Excluded-from-backup glob patterns (set level).
        vm.ExcludedExtensions = BackupJobViewModel.FormatExclusionPatterns(opts.ExcludedExtensions);

        // Retention tier sets.
        if (opts.TierSets.Count > 0)
        {
            vm.LoadTierSets(opts.TierSets);
        }
        else if (opts.RetentionTiers.Count > 0)
        {
            // Backward compat: old data has a flat RetentionTiers list.
            // Convert it to the "Default" tier set.
            var tierSets = new List<VersionTierSet>
            {
                new() { Name = "Default", Tiers = [.. opts.RetentionTiers] },
                new() { Name = "None", Tiers = [] },
            };
            vm.LoadTierSets(tierSets);
        }

        // Schedule.
        if (opts.Schedule is { Enabled: true } sched)
        {
            vm.ScheduleEnabled = true;
            vm.ScheduleMode = sched.Mode;
            vm.ScheduleIntervalHours = sched.IntervalHours.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            vm.ScheduleDailyHour = sched.DailyHour;
            vm.ScheduleDailyMinute = sched.DailyMinute;
            vm.ScheduleDebounceSeconds = sched.DebounceSeconds.ToString();
        }
    }

    /// <summary>
    /// Write the current source selection settings back into the backup set's
    /// <see cref="JobOptions"/> so they survive a dialog close without Plan.
    /// This is the inverse of <see cref="RestoreSourceSettings"/>.
    /// </summary>
    private static void SyncSettingsToJobOptions(BackupSet backupSet, SourceSelectionViewModel vm)
    {
        var opts = backupSet.JobOptions ?? new JobOptions();

        // Name.
        backupSet.Name = vm.SetName;

        // Source roots — derive from the current tree selections so orphaned-
        // directory detection and other consumers always have an up-to-date
        // flat list of covered root paths.
        var selections = vm.GetSelections();
        backupSet.SourceRoots = selections.Select(s => s.Path).ToList();

        // Target mode + directory.
        if (vm.IsDirectoryMode)
            opts.TargetDirectory = vm.TargetDirectory;
        opts.CreateSubdirectory = vm.CreateSubdirectory;
        opts.SubdirectoryName = vm.SubdirectoryName;

        // Disc options.
        opts.ZipMode = vm.ZipMode;
        opts.FilesystemType = vm.FilesystemType;
        opts.VerifyAfterBurn = vm.VerifyAfterBurn;
        opts.IncludeCatalogOnDisc = vm.IncludeCatalogOnDisc;
        opts.AllowFileSplitting = vm.AllowFileSplitting;
        if (double.TryParse(vm.CapacityOverrideGb,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double gb) && gb > 0)
            opts.CapacityOverrideBytes = (long)(gb * 1024 * 1024 * 1024);
        else
            opts.CapacityOverrideBytes = null;

        // Directory options.
        opts.EnableFileDeduplication = vm.EnableFileDeduplication;
        opts.EnableDeduplication = vm.EnableBlockDeduplication;
        if (int.TryParse(vm.BlockSizeKb, out int blockKb) && blockKb > 0)
            opts.DeduplicationBlockSize = blockKb * 1024;

        // Excluded-from-backup glob patterns (set level).
        opts.ExcludedExtensions = BackupJobViewModel.ParseExclusionPatterns(vm.ExcludedExtensions);

        // Tier sets.
        opts.TierSets = vm.TierSets.Select(ts => ts.ToModel()).ToList();
        var defaultTs = vm.TierSets.FirstOrDefault(t => t.Name == "Default");
        opts.RetentionTiers = defaultTs is not null
            ? defaultTs.Tiers.Select(t => t.ToModel()).ToList()
            : [];

        // Schedule.
        if (vm.ScheduleEnabled)
        {
            opts.Schedule = new BackupSchedule
            {
                Enabled = true,
                Mode = vm.ScheduleMode,
                IntervalHours = double.TryParse(vm.ScheduleIntervalHours,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var h) ? h : 24,
                DailyHour = vm.ScheduleDailyHour,
                DailyMinute = vm.ScheduleDailyMinute,
                DebounceSeconds = int.TryParse(vm.ScheduleDebounceSeconds, out var s) ? s : 60,
            };
        }
        else if (opts.Schedule is not null)
        {
            opts.Schedule.Enabled = false;
        }

        backupSet.JobOptions = opts;
    }

    /// <summary>
    /// Transfer all settings from the source selection view into the backup job view.
    /// Called after RestoreJobOptions so source selection values take precedence.
    /// </summary>
    private static void ApplySourceSettings(BackupJobViewModel job, SourceSelectionViewModel src)
    {
        // Identity.
        if (!string.IsNullOrEmpty(src.SetName))
            job.SetName = src.SetName;

        // Target mode + directory.
        job.IsDirectoryMode = src.IsDirectoryMode;
        if (src.IsDirectoryMode)
        {
            job.TargetDirectoryPath = src.TargetDirectory;
            job.CreateSubdirectory = src.CreateSubdirectory;
            job.SubdirectoryName = src.SubdirectoryName;
        }

        // Disc options.
        job.ZipMode = src.ZipMode;
        job.FilesystemType = src.FilesystemType;
        job.CapacityOverrideGb = src.CapacityOverrideGb;
        job.VerifyAfterBurn = src.VerifyAfterBurn;
        job.IncludeCatalogOnDisc = src.IncludeCatalogOnDisc;
        job.AllowFileSplitting = src.AllowFileSplitting;

        // Directory options.
        job.EnableFileDeduplication = src.EnableFileDeduplication;
        job.EnableDeduplication = src.EnableBlockDeduplication;
        job.DeduplicationBlockSizeKb = src.BlockSizeKb;

        // Excluded-from-backup glob patterns (carried forward to the job page,
        // which has its own editor for the same value).
        job.ExcludedExtensions = src.ExcludedExtensions;

        // Retention tier sets.
        job.TierSets.Clear();
        foreach (var srcTs in src.TierSets)
        {
            var tsVm = TierSetViewModel.FromModel(srcTs.ToModel(), srcTs.IsBuiltIn);
            job.TierSets.Add(tsVm);
        }

        // Also populate the legacy RetentionTiers from the "Default" tier set
        // for backward compatibility with the backup service.
        job.RetentionTiers.Clear();
        var defaultTs = src.TierSets.FirstOrDefault(t => t.Name == "Default");
        if (defaultTs is not null)
        {
            foreach (var srcTier in defaultTs.Tiers)
            {
                var model = srcTier.ToModel();
                var tierVm = RetentionTierViewModel.FromModel(model);
                tierVm.RemoveRequested += t => job.RetentionTiers.Remove(t);
                job.RetentionTiers.Add(tierVm);
            }
        }

        // Schedule.
        job.ScheduleEnabled = src.ScheduleEnabled;
        job.ScheduleMode = src.ScheduleMode;
        job.ScheduleIntervalHours = src.ScheduleIntervalHours;
        job.ScheduleDailyHour = src.ScheduleDailyHour;
        job.ScheduleDailyMinute = src.ScheduleDailyMinute;
        job.ScheduleDebounceSeconds = src.ScheduleDebounceSeconds;
    }

    /// <summary>
    /// Create or update a backup set with the full source selection tree and job options.
    /// </summary>
    private async Task<int> SaveBackupSetAsync(
        int? existingId, string name, List<SourceSelection> sources,
        BackupJob job, BackupSchedule? schedule)
    {
        var jobOptions = new JobOptions
        {
            ZipMode = job.ZipMode,
            FilesystemType = job.FilesystemType,
            CapacityOverrideBytes = job.CapacityOverrideBytes,
            VerifyAfterBurn = job.VerifyAfterBurn,
            VerifyAfterBackup = job.VerifyAfterBackup,
            IncludeCatalogOnDisc = job.IncludeCatalogOnDisc,
            AllowFileSplitting = job.AllowFileSplitting,
            EnableFileDeduplication = job.EnableFileDeduplication,
            EnableDeduplication = job.EnableDeduplication,
            DeduplicationBlockSize = job.DeduplicationBlockSize,
            RetentionTiers = job.RetentionTiers,
            TierSets = job.TierSets.Select(ts => new VersionTierSet
            {
                Name = ts.Name,
                Tiers = ts.Tiers.Select(t => new VersionRetentionTier
                {
                    MaxAge = t.MaxAge,
                    MaxVersions = t.MaxVersions,
                }).ToList(),
                FilePatterns = [.. ts.FilePatterns],
                FileExemptPatterns = [.. ts.FileExemptPatterns],
            }).ToList(),
            TargetDirectory = job.TargetDirectory,
            CreateSubdirectory = job.CreateSubdirectory,
            SubdirectoryName = job.SubdirectoryName,
            ExcludedExtensions = job.ExcludedExtensions,
            Schedule = schedule,
        };

        if (existingId.HasValue)
        {
            var existing = await _catalog.GetBackupSetAsync(existingId.Value);
            if (existing is not null)
            {
                existing.Name = name;
                existing.SourceRoots = sources.Select(s => s.Path).ToList();
                existing.SourceSelections = sources;
                existing.JobOptions = jobOptions;
                existing.DefaultFilesystemType = job.FilesystemType;
                existing.CapacityOverrideBytes = job.CapacityOverrideBytes;
                await _catalog.UpdateBackupSetAsync(existing);
                return existing.Id;
            }
        }

        var newSet = await _catalog.CreateBackupSetAsync(new BackupSet
        {
            Name = name,
            SourceRoots = sources.Select(s => s.Path).ToList(),
            SourceSelections = sources,
            JobOptions = jobOptions,
            DefaultMediaType = job.TargetDirectory is not null ? MediaType.Directory : MediaType.Unknown,
            DefaultFilesystemType = job.FilesystemType,
            CapacityOverrideBytes = job.CapacityOverrideBytes,
            CreatedUtc = DateTime.UtcNow,
        });
        return newSet.Id;
    }

    /// <summary>
    /// Check whether the target drive has enough free space for the planned
    /// backup.  Returns <c>true</c> to proceed, <c>false</c> to abort.
    /// When space is insufficient a confirmation dialog lets the user
    /// continue anyway (partial backup) or cancel.
    /// </summary>
    private bool CheckFreeSpaceBeforeBackup(string targetDirectory, long requiredBytes)
    {
        try
        {
            string pathRoot = System.IO.Path.GetPathRoot(targetDirectory) ?? targetDirectory;
            var driveInfo = new System.IO.DriveInfo(pathRoot);
            if (!driveInfo.IsReady)
                return true; // can't check — let it proceed

            long freeSpace = driveInfo.AvailableFreeSpace;
            if (freeSpace >= requiredBytes)
                return true; // plenty of space

            string driveLetter = pathRoot.TrimEnd('\\');
            var result = MessageBox.Show(
                $"The target drive ({driveLetter}) only has {FormatBytes(freeSpace)} free, " +
                $"but the backup needs {FormatBytes(requiredBytes)}.\n\n" +
                $"The backup will run out of space and may be incomplete.\n\n" +
                $"Start anyway?",
                "Insufficient Disk Space",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                StatusText = "Backup cancelled — not enough free space.";
                return false;
            }
        }
        catch { /* can't check — let it proceed */ }
        return true;
    }

    private void StartBurn(BackupPlan plan)
    {
        // Reset simulated failure modes so each burn starts clean.
        if (_simulatedBurner is not null)
        {
            _simulatedBurner.FileFailureProbability = 0;
            _simulatedBurner.CatastrophicFailureAtPercent = null;
            _simulatedBurner.SimulateEraseFail = false;
        }

        bool isDir = plan.Job.TargetDirectory is not null;
        var progressVm = new BurnProgressViewModel { IsDirectoryMode = isDir };
        CurrentView = null;                     // return to home screen
        ActiveBackupProgress = progressVm;      // show inline progress panel
        IsBurning = true;
        RunningBackupSetId = plan.Job.BackupSetId;

        progressVm.DoneRequested += () =>
        {
            ActiveBackupProgress = null;        // dismiss the progress panel
            RunningBackupSetId = null;
            _ = LoadBackupSetsAsync();
        };

        if (isDir)
        {
            StatusText = "";
            _ = ExecuteDirectoryBackupAsync(plan, progressVm);
        }
        else
        {
            StatusText = "";
            _ = ExecuteBurnAsync(plan, progressVm);
        }
    }

    private async Task ExecuteBurnAsync(BackupPlan plan, BurnProgressViewModel progressVm)
    {
        var cts = progressVm.StartBurn();

        // Let the UI render the progress view before starting heavy work.
        await Task.Yield();

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                progressVm.OnBackupProgress(p);
                StatusText = $"Burning disc {p.CurrentDisc}/{p.TotalDiscs} — {p.OverallPercentage:F0}%";
            });

            // Files that fail to copy are automatically skipped so the
            // backup can run unattended.  Failures are collected and shown
            // in the completion view.
            var result = await Task.Run(
                () => _orchestrator.ExecuteAsync(plan, progress, onFailure: null, cts.Token));

            string detail = $"Discs written: {result.DiscsWritten}\n" +
                            $"Data written: {FormatBytes(result.BytesWritten)}";

            progressVm.CompleteBurn(result.Success, "", detail, result.FailedFiles);
            StatusText = result.Success ? "Backup completed." : "Backup completed with errors.";
        }
        catch (OperationCanceledException)
        {
            progressVm.CompleteBurn(false, "Cancelled by user.");
            StatusText = "Backup cancelled.";
        }
        catch (Exception ex)
        {
            progressVm.CompleteBurn(false, ex.Message);
            StatusText = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsBurning = false;
            RunningBackupSetId = null;
        }
    }

    // -------------------------------------------------------------------
    // Flow 2b: Directory Backup
    // -------------------------------------------------------------------

    private async Task ExecuteDirectoryBackupAsync(BackupPlan plan, BurnProgressViewModel progressVm)
    {
        var cts = progressVm.StartBurn();

        // Let the UI render the progress view before starting heavy work.
        await Task.Yield();

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                progressVm.OnBackupProgress(p);
                StatusText = $"{p.OverallPercentage:F0}% complete";
            });

            // Files that fail to copy are automatically skipped so the
            // backup can run unattended.  Failures are collected and shown
            // in the completion view.
            var result = await Task.Run(() => _directoryBackupService.ExecuteAsync(
                plan.Job,
                plan.Job.TargetDirectory!,
                plan.Job.RetentionTiers.Count > 0 ? plan.Job.RetentionTiers : VersionRetentionService.DefaultTiers,
                progress,
                cts.Token,
                progressVm.PauseEvent,
                onFailure: null,
                plan.Diff));

            string detail = $"Data written: {FormatBytes(result.BytesWritten)}";

            progressVm.CompleteBurn(result.Success, "", detail, result.FailedFiles);
            StatusText = result.Success ? "Directory backup completed." : "Directory backup completed with errors.";
        }
        catch (OperationCanceledException)
        {
            progressVm.CompleteBurn(false, "Cancelled by user.");
            StatusText = "Directory backup cancelled.";
        }
        catch (Exception ex)
        {
            progressVm.CompleteBurn(false, ex.Message);
            StatusText = $"Directory backup failed: {ex.Message}";
        }
        finally
        {
            IsBurning = false;
            RunningBackupSetId = null;
        }
    }

    // -------------------------------------------------------------------
    // Flow 3: Restore Files
    // -------------------------------------------------------------------

    private void StartRestoreFlow()
    {
        if (SelectedBackupSet is null)
            return;

        var restoreVm = new RestoreViewModel(_catalog, _restoreService, SelectedBackupSet);

        restoreVm.DoneRequested += GoHome;

        CurrentView = restoreVm;
        StatusText = $"Restore files from \"{SelectedBackupSet.Name}\".";
    }

    // -------------------------------------------------------------------
    // Flow 4: Cleanup (orphaned catalog records + optional destination scan)
    // -------------------------------------------------------------------

    private async void StartOrphanedDirsFlow()
    {
        if (SelectedBackupSet is null)
            return;

        // Catalog load + classification can take several seconds on large
        // backup sets.  Show a wait cursor for the entire load so the user
        // gets immediate feedback that the click landed; the view itself is
        // displayed right away so the in-progress phase counters are visible.
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var vm = new OrphanedDirectoriesViewModel(_catalog, SelectedBackupSet);
            vm.DoneRequested += GoHome;
            CurrentView = vm;
            StatusText = "Review files and directories that can be cleaned up.";
            await vm.WaitForLoadAsync();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // -------------------------------------------------------------------
    // Flow 5: Find File (cross-set search)
    // -------------------------------------------------------------------

    private void StartFindFileFlow()
    {
        var vm = new FindFileViewModel(_catalog);
        vm.DoneRequested += GoHome;

        vm.RestoreRequested += async setId =>
        {
            var backupSet = await _catalog.GetBackupSetAsync(setId);
            if (backupSet is null)
            {
                StatusText = "Backup set no longer exists.";
                return;
            }

            var restoreVm = new RestoreViewModel(_catalog, _restoreService, backupSet);
            restoreVm.DoneRequested += GoHome;
            CurrentView = restoreVm;
            StatusText = $"Restore files from \"{backupSet.Name}\".";
        };

        CurrentView = vm;
        StatusText = "Search for files or directories across all backup sets.";
    }

    // -------------------------------------------------------------------
    // Flow 6: Backup Coverage
    // -------------------------------------------------------------------

    private void StartBackupCoverageFlow()
    {
        if (SelectedBackupSet is null) return;

        var vm = new BackupCoverageViewModel(_catalog, _scanner, SelectedBackupSet);
        vm.DoneRequested += GoHome;

        CurrentView = vm;
        StatusText = $"Analyzing backup coverage for \"{SelectedBackupSet.Name}\".";
    }

    // -------------------------------------------------------------------
    // Flow 7: Largest Source Files
    // -------------------------------------------------------------------

    private async void StartLargestFilesFlow()
    {
        if (SelectedBackupSet is null) return;
        await ShowLargestFilesAsync(SelectedBackupSet, GoHome);
    }

    private async Task<LargestFilesViewModel> ShowLargestFilesAsync(
        BackupSet backupSet, Action onDone,
        Action<ViewModelBase>? setView = null)
    {
        setView ??= vm => CurrentView = vm;

        // Fast scalar count from the catalog for the progress bar.
        int estimatedCount = 0;
        try { estimatedCount = await _catalog.GetFileCountForBackupSetAsync(backupSet.Id); }
        catch { }

        var vm = new LargestFilesViewModel(_scanner, _catalog, backupSet, estimatedCount);
        vm.DoneRequested += onDone;

        setView(vm);
        StatusText = $"Scanning source files for \"{backupSet.Name}\".";
        return vm;
    }

    // -------------------------------------------------------------------
    // Export / Import Backup Set
    // -------------------------------------------------------------------

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private async Task ExportBackupSetAsync()
    {
        if (SelectedBackupSet is null) return;

        var exported = ExportedBackupSet.FromBackupSet(SelectedBackupSet);

        var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = $"Export \"{SelectedBackupSet.Name}\"",
            Filter = "Backup Set Config (*.lithic)|*.lithic|JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = SanitizeFilename(SelectedBackupSet.Name) + ".lithic",
            DefaultExt = ".lithic",
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        try
        {
            var json = JsonSerializer.Serialize(exported, _jsonOptions);
            await File.WriteAllTextAsync(dialog.FileName, json);
            StatusText = $"Exported \"{SelectedBackupSet.Name}\" to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private async Task ImportBackupSetAsync()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Import Backup Set",
            Filter = "Backup Set Config (*.lithic)|*.lithic|JSON files (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var exported = JsonSerializer.Deserialize<ExportedBackupSet>(json, _jsonOptions);
            if (exported is null)
            {
                StatusText = "Import failed: file is empty or invalid.";
                return;
            }

            var newSet = exported.ToBackupSet();
            await _catalog.CreateBackupSetAsync(newSet);
            await LoadBackupSetsAsync();

            SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == newSet.Id);
            StatusText = $"Imported \"{newSet.Name}\" from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (JsonException)
        {
            StatusText = "Import failed: the file is not a valid backup set configuration.";
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    // -------------------------------------------------------------------
    // Worker Service management
    // -------------------------------------------------------------------

    private void RefreshServiceStatus()
    {
        ServiceStatus = Services.WorkerServiceHelper.GetStatus();
        ServiceStatusText = ServiceStatus switch
        {
            ServiceState.NotInstalled => "Worker Service: not installed",
            ServiceState.Stopped => "Worker Service: stopped",
            ServiceState.Running => "Worker Service: running",
            ServiceState.StartPending => "Worker Service: starting...",
            ServiceState.StopPending => "Worker Service: stopping...",
            _ => "Worker Service: unknown",
        };
        OnPropertyChanged(nameof(ServiceStatus));
        OnPropertyChanged(nameof(CanInstallService));
        OnPropertyChanged(nameof(CanUninstallService));
        OnPropertyChanged(nameof(CanStartService));
        OnPropertyChanged(nameof(CanStopService));
    }

    /// <summary>
    /// Poll until the service leaves a pending state or the timeout expires.
    /// Keeps the UI updated while waiting.
    /// </summary>
    private async Task WaitForServiceReadyAsync(int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            RefreshServiceStatus();
            if (ServiceStatus is not ServiceState.StartPending
                and not ServiceState.StopPending)
                return;

            await Task.Delay(500);
        }
        RefreshServiceStatus();
    }

    private async void InstallService()
    {
        var workerPath = Services.WorkerServiceHelper.FindWorkerExe();
        if (workerPath is null)
        {
            StatusText = "Cannot find LithicBackup.Worker.exe.";
            return;
        }

        StatusText = "Installing Worker Service (UAC prompt)...";
        if (Services.WorkerServiceHelper.Install(workerPath))
        {
            // Auto-start after install.
            Services.WorkerServiceHelper.Start();
            RefreshServiceStatus();
            await WaitForServiceReadyAsync();
            StatusText = ServiceStatus == ServiceState.Running
                ? "Worker Service installed and started."
                : "Worker Service installed.";
        }
        else
        {
            StatusText = "Service installation cancelled or failed.";
            RefreshServiceStatus();
        }
    }

    private void UninstallService()
    {
        StatusText = "Uninstalling Worker Service (UAC prompt)...";
        if (Services.WorkerServiceHelper.Uninstall())
            StatusText = "Worker Service uninstalled.";
        else
            StatusText = "Service uninstall cancelled or failed.";

        RefreshServiceStatus();
    }

    private async void StartService()
    {
        StatusText = "Starting Worker Service (UAC prompt)...";
        if (Services.WorkerServiceHelper.Start())
        {
            RefreshServiceStatus();
            await WaitForServiceReadyAsync();
            StatusText = ServiceStatus == ServiceState.Running
                ? "Worker Service started."
                : "Failed to start service.";
        }
        else
        {
            StatusText = "Failed to start service.";
            RefreshServiceStatus();
        }
    }

    private async void StopService()
    {
        StatusText = "Stopping Worker Service (UAC prompt)...";
        if (Services.WorkerServiceHelper.Stop())
        {
            RefreshServiceStatus();
            await WaitForServiceReadyAsync();
            StatusText = ServiceStatus == ServiceState.Stopped
                ? "Worker Service stopped."
                : "Failed to stop service.";
        }
        else
        {
            StatusText = "Failed to stop service.";
            RefreshServiceStatus();
        }
    }

    // -------------------------------------------------------------------
    // Navigation
    // -------------------------------------------------------------------

    private void GoHome()
    {
        CurrentView = null;
        StatusText = RecorderCount switch
        {
            0 => "No disc recorders detected",
            1 => "1 disc recorder detected",
            _ => $"{RecorderCount} disc recorders detected",
        };
    }

    // -------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------

    private void DetectRecorders()
    {
        try
        {
            var ids = _burner.GetRecorderIds();
            _recorderCount = ids.Count;
            _statusText = ids.Count switch
            {
                0 => "No disc recorders detected",
                1 => "1 disc recorder detected",
                _ => $"{ids.Count} disc recorders detected",
            };
        }
        catch
        {
            _statusText = "Could not query disc recorders";
        }
    }

    private async Task LoadBackupSetsAsync()
    {
        try
        {
            var sets = await _catalog.GetAllBackupSetsAsync();
            BackupSets.Clear();
            foreach (var set in sets)
                BackupSets.Add(set);
        }
        catch
        {
            StatusText = "Failed to load backup sets";
        }
    }

    private static string FormatBytes(long bytes) => $"{bytes:N0}";
}

/// <summary>Simple ICommand implementation for MVVM binding.</summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
