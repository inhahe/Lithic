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
    private readonly ICatalogFreeRestoreService _catalogFreeRestoreService;
    private readonly DirectoryBackupService _directoryBackupService;
    private readonly Services.TrayService? _trayService;
    private readonly SwitchableDiscBurner? _switchableBurner;
    private readonly SimulatedDiscBurner? _simulatedBurner;
    private readonly FileHashCache? _fileHashCache;
    private readonly IDestinationResolver? _destinationResolver;
    private readonly ISourceResolver? _sourceResolver;
    private readonly UserSettings _settings;

    private string _statusText = "Ready";
    private string _backgroundStatusText = "";
    private int _recorderCount;
    private ViewModelBase? _currentView;
    private BackupSet? _selectedBackupSet;
    private string _serviceStatusText = "";
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
        ICatalogFreeRestoreService catalogFreeRestoreService,
        DirectoryBackupService directoryBackupService,
        Services.TrayService? trayService = null,
        FileHashCache? fileHashCache = null,
        IDestinationResolver? destinationResolver = null,
        UserSettings? settings = null,
        ISourceResolver? sourceResolver = null)
    {
        _settings = settings ?? new UserSettings();
        _catalog = catalog;
        _burner = burner;
        _scanner = scanner;
        _orchestrator = orchestrator;
        _restoreService = restoreService;
        _catalogFreeRestoreService = catalogFreeRestoreService;
        _directoryBackupService = directoryBackupService;
        _trayService = trayService;
        _fileHashCache = fileHashCache;
        _destinationResolver = destinationResolver;
        _sourceResolver = sourceResolver;
        _switchableBurner = burner as SwitchableDiscBurner;
        _simulatedBurner = _switchableBurner?.Simulated;

        BackupSets = [];

        // Creating a new set is always allowed — concurrent backups mean there
        // is no global "busy" state to block on.
        NewBackupSetCommand = new RelayCommand(
            _ => StartNewBackupFlow());
        // Abort the run on a specific row: during the burn/copy phase delegate
        // to that row's progress CancelCommand; during the scan phase cancel its
        // scan token. Parameter is the row VM.
        AbortBackupCommand = new RelayCommand(
            o =>
            {
                if (o is not BackupSetRowViewModel row) return;
                if (row.Progress?.CancelCommand is ICommand cmd && cmd.CanExecute(null))
                    cmd.Execute(null);
                else
                    row.ScanCts?.Cancel();
            },
            o => o is BackupSetRowViewModel row &&
                 ((row.ScanCts is { IsCancellationRequested: false }) ||
                  (row.Progress?.CancelCommand?.CanExecute(null) == true)));
        CancelScanCommand = new RelayCommand(
            o => (o as BackupSetRowViewModel)?.ScanCts?.Cancel(),
            o => o is BackupSetRowViewModel row && row.ScanCts is { IsCancellationRequested: false });

        // Selected-set commands (used by menus/other views; operate on the
        // currently selected set's row).
        RunIncrementalCommand = new RelayCommand(
            _ => { if (SelectedRow is { } r) StartIncrementalFlow(r); },
            _ => SelectedRow is { IsRunning: false });
        RestoreCommand = new RelayCommand(
            _ => StartRestoreFlow(),
            _ => SelectedBackupSet is not null);
        EditBackupSetCommand = new RelayCommand(
            _ => StartEditFlow(),
            _ => SelectedBackupSet is not null);
        ChangeDestinationCommand = new RelayCommand(
            _ => _ = ChangeDestinationAsync(),
            _ => SelectedBackupSet?.JobOptions?.TargetDirectory is not null);
        CopyBackupSetCommand = new RelayCommand(
            _ => _ = CopyBackupSetAsync(),
            _ => SelectedBackupSet is not null);
        OrphanedDirsCommand = new RelayCommand(
            _ => StartOrphanedDirsFlow(),
            _ => SelectedBackupSet is not null);
        FindFileCommand = new RelayCommand(
            _ => StartFindFileFlow());
        CatalogFreeRestoreCommand = new RelayCommand(
            _ => StartCatalogFreeRestoreFlow());
        HomeCommand = new RelayCommand(_ => GoHome());
        BackupCoverageCommand = new RelayCommand(
            _ => StartBackupCoverageFlow(),
            _ => SelectedBackupSet is not null);
        LargestFilesCommand = new RelayCommand(
            _ => StartLargestFilesFlow(),
            _ => SelectedBackupSet is not null);
        ExportBackupSetCommand = new RelayCommand(
            _ => _ = ExportBackupSetAsync(),
            _ => SelectedBackupSet is not null);
        ImportBackupSetCommand = new RelayCommand(
            _ => _ = ImportBackupSetAsync());

        // Per-set action commands (take the row VM via CommandParameter). Each
        // gate is now per-row, so one set running never disables another's
        // buttons.
        SetCheckCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; _ = CheckSizeAsync(r); } },
            o => o is BackupSetRowViewModel r && !r.IsChecking && !r.IsRunning);
        AbortCheckCommand = new RelayCommand(
            o => (o as BackupSetRowViewModel)?.CheckCts?.Cancel(),
            o => o is BackupSetRowViewModel r && r.IsChecking);
        SetBackupCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartIncrementalFlow(r); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetReviewCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; _ = RunIncrementalFlowAsync(r, forceReview: true); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetModifyCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartEditFlow(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetRestoreCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartRestoreFlow(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetOrphanedDirsCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartOrphanedDirsFlow(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetCoverageCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartBackupCoverageFlow(); } },
            o => o is BackupSetRowViewModel);
        SetVerifyIntegrityCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartVerifyIntegrityFlow(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetLargestFilesCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartLargestFilesFlow(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetCopyCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; _ = CopyBackupSetAsync(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetChangeDestCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; _ = ChangeDestinationAsync(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning && r.Model.JobOptions?.TargetDirectory is not null);
        SetRemapSourceDriveCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; _ = RemapSourceDriveAsync(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        SetExportCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; _ = ExportBackupSetAsync(); } },
            o => o is BackupSetRowViewModel);
        SetDeleteCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) _ = DeleteBackupSetAsync(r.Model); },
            o => o is BackupSetRowViewModel r && !r.IsRunning);
        InstallServiceCommand = new RelayCommand(_ => InstallService());
        UninstallServiceCommand = new RelayCommand(_ => UninstallService());
        StartServiceCommand = new RelayCommand(_ => StartService());
        StopServiceCommand = new RelayCommand(_ => StopService());

        // Simulated burner failure injection (--test-mode only). These are
        // the *while-burning* triggers — momentary actions pressed during a live
        // burn to inject an error that can occur mid-write. They target whichever
        // set is currently burning. (The *pre-burn* conditions — no recorder, no
        // media, erase fails — are armed ahead of time via the toggle properties
        // below.)
        SimFileFailureCommand = new RelayCommand(
            _ => { if (_simulatedBurner is not null) _simulatedBurner.FileFailureProbability = 1.0; },
            _ => _simulatedBurner is not null && AnyRunning);
        SimCatastrophicFailureCommand = new RelayCommand(
            _ => { if (_simulatedBurner is not null) _simulatedBurner.CatastrophicFailureAtPercent = 0; },
            _ => _simulatedBurner is not null && AnyRunning);
        SimVerifyFailureCommand = new RelayCommand(
            _ => { if (_simulatedBurner is not null) _simulatedBurner.SimulateVerifyFailure = true; },
            _ => _simulatedBurner is not null && AnyRunning);

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

    public ObservableCollection<BackupSetRowViewModel> BackupSets { get; }

    public BackupSet? SelectedBackupSet
    {
        get => _selectedBackupSet;
        set => SetProperty(ref _selectedBackupSet, value);
    }

    /// <summary>True if any backup set is currently scanning, burning, or copying.</summary>
    public bool AnyRunning => BackupSets.Any(r => r.IsRunning);

    // --- Commands ---

    public ICommand NewBackupSetCommand { get; }
    public ICommand EditBackupSetCommand { get; }
    public ICommand ChangeDestinationCommand { get; }
    public ICommand CopyBackupSetCommand { get; }
    public ICommand RunIncrementalCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand OrphanedDirsCommand { get; }
    public ICommand FindFileCommand { get; }
    public ICommand CatalogFreeRestoreCommand { get; }
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
    public ICommand SetReviewCommand { get; }
    public ICommand SetModifyCommand { get; }
    public ICommand SetRestoreCommand { get; }
    public ICommand SetOrphanedDirsCommand { get; }
    public ICommand SetCoverageCommand { get; }
    public ICommand SetVerifyIntegrityCommand { get; }
    public ICommand SetLargestFilesCommand { get; }
    public ICommand SetCopyCommand { get; }
    public ICommand SetChangeDestCommand { get; }
    public ICommand SetRemapSourceDriveCommand { get; }
    public ICommand SetExportCommand { get; }
    public ICommand SetDeleteCommand { get; }

    // --- Worker Service management ---

    public ICommand InstallServiceCommand { get; }
    public ICommand UninstallServiceCommand { get; }
    public ICommand StartServiceCommand { get; }
    public ICommand StopServiceCommand { get; }

    // Simulated burner failure injection (--test-mode only).
    public bool IsTestMode => _switchableBurner is not null;

    /// <summary>
    /// When on, the single shared burner routes to the simulated burner; when
    /// off, to the real hardware. Bound to the "use simulated burner" checkbox
    /// that gates the home-screen test controls. Defaults to off so that, even
    /// in test mode, real hardware is used until the user opts in.
    /// </summary>
    public bool UseSimulatedBurner
    {
        get => _switchableBurner?.UseSimulated ?? false;
        set
        {
            if (_switchableBurner is not null && _switchableBurner.UseSimulated != value)
            {
                _switchableBurner.UseSimulated = value;
                OnPropertyChanged();
            }
        }
    }

    // While-burning triggers (momentary; pressed during a live burn).
    public ICommand SimFileFailureCommand { get; }
    public ICommand SimCatastrophicFailureCommand { get; }
    public ICommand SimVerifyFailureCommand { get; }

    // Pre-burn conditions (armed ahead of time via toggle buttons on the home
    // screen). Unlike the while-burning triggers, these persist until toggled
    // off — they are NOT cleared when a burn starts — so they reliably fire on
    // the next backup's planning / burn-start phase.
    public bool SimNoRecorder
    {
        get => _simulatedBurner?.SimulateNoRecorder ?? false;
        set { if (_simulatedBurner is not null) { _simulatedBurner.SimulateNoRecorder = value; OnPropertyChanged(); } }
    }
    public bool SimNoMedia
    {
        get => _simulatedBurner?.SimulateNoMedia ?? false;
        set { if (_simulatedBurner is not null) { _simulatedBurner.SimulateNoMedia = value; OnPropertyChanged(); } }
    }
    public bool SimEraseFail
    {
        get => _simulatedBurner?.SimulateEraseFail ?? false;
        set { if (_simulatedBurner is not null) { _simulatedBurner.SimulateEraseFail = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// When on, the simulated burner recreates the real directory tree and
    /// filenames on the shelf but stores a tiny hash+size stub in each file
    /// instead of the real bytes — keeps the shelf tiny while mirroring the true
    /// structure. Restore from such discs can't reconstruct content, so leave
    /// this off when testing restore or block-dedup round-trips.
    /// </summary>
    public bool SimMetadataOnly
    {
        get => _simulatedBurner is not null && !_simulatedBurner.StoreFileContents;
        set { if (_simulatedBurner is not null) { _simulatedBurner.StoreFileContents = !value; OnPropertyChanged(); } }
    }

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
                SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id)?.Model;
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

        // "Clear Backup History" button: wipes the catalog record of what's
        // been backed up for this set (discs + file entries), keeping all
        // settings.  The next backup then treats every source file as new.
        sourceSelection.ClearHistoryRequested += async () =>
        {
            var confirm = MessageBox.Show(
                $"Clear the backup history for \"{sourceSelection.SetName}\"?\n\n" +
                "This deletes the catalog records of which files have been backed " +
                "up (disc entries and file records). Settings, sources, and schedule " +
                "are kept.\n\n" +
                "The next backup will treat every source file as new. Files already " +
                "written to the destination are not deleted.",
                "Clear Backup History",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                await Task.Run(() => _catalog.ClearBackupSetCatalogAsync(backupSet.Id));
                sourceSelection.ClearHistoryResult =
                    "Backup history cleared. The next backup will re-copy everything.";
                StatusText = $"Cleared backup history for \"{sourceSelection.SetName}\".";
                await LoadBackupSetsAsync();
                SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id)?.Model;
            }
            catch (Exception ex)
            {
                sourceSelection.ClearHistoryResult = $"Failed to clear history: {ex.Message}";
                StatusText = $"Failed to clear backup history: {ex.Message}";
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
            // Update the saved job options. The new path may live on a
            // different volume, so discard the old volume identity and let the
            // resolver re-capture it for the new path (the resolver's backfill
            // branch fires when DestinationVolumeId is null).
            backupSet.JobOptions.TargetDirectory = newPath;
            backupSet.JobOptions.DestinationVolumeId = null;
            backupSet.JobOptions.DestinationSubpath = null;
            _destinationResolver?.Resolve(backupSet.JobOptions);
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
    // Remap source drive
    // -------------------------------------------------------------------

    /// <summary>
    /// Remap a source drive letter in the catalog: the data that used to live on
    /// one drive (e.g. <c>E:\</c>) now lives on another (e.g. <c>F:\</c>) with the
    /// same directory structure. Rewrites the source drive letter in every
    /// catalog record's <c>SourcePath</c> and in the set's own source
    /// configuration, so future backups treat the already-backed-up files as
    /// present instead of re-copying everything. The destination backup files are
    /// deliberately not moved — they migrate naturally as source files change.
    /// </summary>
    private async Task RemapSourceDriveAsync()
    {
        if (SelectedBackupSet is null) return;
        var backupSet = SelectedBackupSet;

        // Collect the distinct source drive letters currently in the set.
        var sourceDrives = new SortedSet<char>();
        foreach (var root in backupSet.SourceRoots)
            if (DriveLetterOf(root) is char c) sourceDrives.Add(c);
        if (backupSet.SourceSelections is not null)
            CollectSelectionDrives(backupSet.SourceSelections, sourceDrives);

        if (sourceDrives.Count == 0)
        {
            MessageBox.Show(
                "This backup set has no drive-letter source paths to remap.",
                "Remap Source Drive", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Per-drive catalog record counts (for the preview) and the ready drives
        // available as remap targets (any present drive that isn't already one of
        // the set's own sources). The drive enumeration can be slow, so run it off
        // the UI thread.
        Dictionary<char, int> counts;
        List<char> targetDrives;
        try
        {
            counts = new Dictionary<char, int>();
            foreach (var d in sourceDrives)
                counts[d] = await _catalog.CountFilesUnderSourcePrefixAsync(backupSet.Id, $"{d}:\\");

            targetDrives = await Task.Run(() =>
                DriveInfo.GetDrives()
                    .Where(dr => dr.IsReady && dr.DriveType != DriveType.CDRom)
                    .Select(dr => char.ToUpperInvariant(dr.Name[0]))
                    .Where(c => !sourceDrives.Contains(c))
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList());
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to prepare source-drive remap: {ex.Message}";
            return;
        }

        if (targetDrives.Count == 0)
        {
            MessageBox.Show(
                "No other ready drive is available to remap to. Connect the drive " +
                "that now holds the source data (with the same directory structure) " +
                "and try again.",
                "Remap Source Drive", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var vm = new RemapSourceDriveViewModel(sourceDrives.ToList(), targetDrives, counts);
        var dialog = new RemapSourceDriveDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = vm,
        };
        if (dialog.ShowDialog() != true) return;

        if (vm.SourceDriveLetter is not char oldDrive || vm.TargetDriveLetter is not char newDrive)
            return;
        if (oldDrive == newDrive) return;

        var confirm = MessageBox.Show(
            $"Remap source drive {oldDrive}: → {newDrive}: for \"{backupSet.Name}\"?\n\n" +
            $"{counts.GetValueOrDefault(oldDrive):N0} catalog record(s) recorded under " +
            $"{oldDrive}:\\ will be treated as living under {newDrive}:\\ going forward, so " +
            "future backups won't re-copy files that already exist.\n\n" +
            $"Use this only when the data that was on {oldDrive}: now lives on {newDrive}: " +
            "with the same directory structure. The destination backup files are not moved.",
            "Remap Source Drive", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            int updated = await _catalog.RemapSourcePathPrefixAsync(
                backupSet.Id, $"{oldDrive}:\\", $"{newDrive}:\\");

            // Mirror the change in the set's own source configuration so scans,
            // watchers and future selections use the new drive too.
            for (int i = 0; i < backupSet.SourceRoots.Count; i++)
                backupSet.SourceRoots[i] = RemapDriveInPath(backupSet.SourceRoots[i], oldDrive, newDrive);
            if (backupSet.SourceSelections is not null)
                RemapDriveInSelections(backupSet.SourceSelections, oldDrive, newDrive);

            await _catalog.UpdateBackupSetAsync(backupSet);
            StatusText = $"Remapped source drive {oldDrive}: → {newDrive}: ({updated:N0} record(s) updated).";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to remap source drive: {ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Uppercase drive letter of a path like <c>E:\foo</c>, or null.</summary>
    private static char? DriveLetterOf(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])
            ? char.ToUpperInvariant(path[0])
            : null;

    /// <summary>Add every drive letter present in a selection subtree.</summary>
    private static void CollectSelectionDrives(IEnumerable<SourceSelection> nodes, SortedSet<char> drives)
    {
        foreach (var n in nodes)
        {
            if (DriveLetterOf(n.Path) is char c) drives.Add(c);
            if (n.Children.Count > 0) CollectSelectionDrives(n.Children, drives);
        }
    }

    /// <summary>
    /// Replace the drive letter of <paramref name="path"/> when it matches
    /// <paramref name="oldDrive"/> (case-insensitive), preserving the rest.
    /// </summary>
    private static string RemapDriveInPath(string path, char oldDrive, char newDrive) =>
        path.Length >= 2 && path[1] == ':' && char.ToUpperInvariant(path[0]) == oldDrive
            ? newDrive + path[1..]
            : path;

    private static void RemapDriveInSelections(IEnumerable<SourceSelection> nodes, char oldDrive, char newDrive)
    {
        foreach (var n in nodes)
        {
            n.Path = RemapDriveInPath(n.Path, oldDrive, newDrive);
            if (n.Children.Count > 0) RemapDriveInSelections(n.Children, oldDrive, newDrive);
        }
    }

    // -------------------------------------------------------------------
    // Copy backup set
    // -------------------------------------------------------------------

    private async Task CopyBackupSetAsync()
    {
        if (SelectedBackupSet is null) return;

        var src = SelectedBackupSet;

        // Ask the user which parts of the set to carry over.
        var vm = new DuplicateBackupSetViewModel(src.Name, src.JobOptions?.TargetDirectory);
        var dialog = new DuplicateBackupSetDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = vm,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // Build the new job options according to the chosen options.
            // Start from a deep copy of settings (or defaults when settings
            // aren't being duplicated), then gate the target directory and
            // schedule on their own checkboxes.
            JobOptions? copiedOptions = null;
            if (src.JobOptions is not null)
            {
                // Each group of options is gated on its own checkbox; anything
                // not copied falls back to a fresh JobOptions' defaults.
                copiedOptions = new JobOptions();

                if (vm.CopySettings)
                {
                    copiedOptions.ZipMode = src.JobOptions.ZipMode;
                    copiedOptions.FilesystemType = src.JobOptions.FilesystemType;
                    copiedOptions.CapacityOverrideBytes = src.JobOptions.CapacityOverrideBytes;
                    copiedOptions.VerifyAfterBurn = src.JobOptions.VerifyAfterBurn;
                    copiedOptions.VerifyAfterBackup = src.JobOptions.VerifyAfterBackup;
                    copiedOptions.IncludeCatalogOnDisc = src.JobOptions.IncludeCatalogOnDisc;
                    copiedOptions.AllowFileSplitting = src.JobOptions.AllowFileSplitting;
                    copiedOptions.EnableFileDeduplication = src.JobOptions.EnableFileDeduplication;
                    copiedOptions.EnableDeduplication = src.JobOptions.EnableDeduplication;
                    copiedOptions.DeduplicationBlockSize = src.JobOptions.DeduplicationBlockSize;
                }

                if (vm.CopyTierSets)
                {
                    copiedOptions.RetentionTiers = src.JobOptions.RetentionTiers.Select(t => new VersionRetentionTier
                    {
                        MaxAge = t.MaxAge,
                        MaxVersions = t.MaxVersions,
                    }).ToList();
                    copiedOptions.TierSets = src.JobOptions.TierSets.Select(ts => new VersionTierSet
                    {
                        Name = ts.Name,
                        Tiers = ts.Tiers.Select(t => new VersionRetentionTier
                        {
                            MaxAge = t.MaxAge,
                            MaxVersions = t.MaxVersions,
                        }).ToList(),
                        FilePatterns = [.. ts.FilePatterns],
                        FileExemptPatterns = [.. ts.FileExemptPatterns],
                    }).ToList();
                }

                if (vm.CopyExclusionPatterns)
                    copiedOptions.ExcludedExtensions = [.. src.JobOptions.ExcludedExtensions];

                // Destination is set directly in the dialog (pre-filled from the
                // source). Carry the source's subdirectory shaping only when the
                // destination is unchanged from the original target. Store null
                // (not empty) for a blank destination so the Worker treats the
                // set as inactive until a real destination is chosen.
                copiedOptions.TargetDirectory =
                    string.IsNullOrWhiteSpace(vm.TargetDirectory) ? null : vm.TargetDirectory.Trim();
                if (vm.KeepsOriginalTarget)
                {
                    copiedOptions.CreateSubdirectory = src.JobOptions.CreateSubdirectory;
                    copiedOptions.SubdirectoryName = src.JobOptions.SubdirectoryName;
                }

                // Schedule is its own opt-in.
                if (vm.CopySchedule && src.JobOptions.Schedule is not null)
                {
                    copiedOptions.Schedule = new BackupSchedule
                    {
                        Enabled = src.JobOptions.Schedule.Enabled,
                        Mode = src.JobOptions.Schedule.Mode,
                        IntervalHours = src.JobOptions.Schedule.IntervalHours,
                        DailyHour = src.JobOptions.Schedule.DailyHour,
                        DailyMinute = src.JobOptions.Schedule.DailyMinute,
                        DebounceSeconds = src.JobOptions.Schedule.DebounceSeconds,
                    };
                }
            }

            var newSet = await _catalog.CreateBackupSetAsync(new BackupSet
            {
                Name = vm.Name.Trim(),
                SourceRoots = vm.CopySourceSelections ? [.. src.SourceRoots] : [],
                SourceSelections = vm.CopySourceSelections ? src.SourceSelections : null,
                JobOptions = copiedOptions,
                MaxIncrementalDiscs = vm.CopySettings ? src.MaxIncrementalDiscs : new BackupSet().MaxIncrementalDiscs,
                DefaultMediaType = vm.CopySettings ? src.DefaultMediaType : new BackupSet().DefaultMediaType,
                DefaultFilesystemType = vm.CopySettings ? src.DefaultFilesystemType : new BackupSet().DefaultFilesystemType,
                CapacityOverrideBytes = vm.CopySettings ? src.CapacityOverrideBytes : null,
                CreatedUtc = DateTime.UtcNow,
            });

            // Optionally carry over the record of what's already backed up.
            // Only valid (and only offered) when the destination is unchanged.
            if (vm.CopyBackupHistory && vm.KeepsOriginalTarget)
            {
                await _catalog.CopyBackupSetCatalogAsync(src.Id, newSet.Id);
            }

            await LoadBackupSetsAsync();

            // Select the new copy.
            SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == newSet.Id)?.Model;
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

    private async Task CheckSizeAsync(BackupSetRowViewModel row)
    {
        var backupSet = row.Model;
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
            MemoryBudget = _settings.MemoryBudget,
        };

        bool isDir = job.TargetDirectory is not null;

        row.CheckCts = new CancellationTokenSource();
        var ct = row.CheckCts.Token;
        row.IsChecking = true;

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

            row.IsChecking = false;
            row.CheckCts?.Dispose();
            row.CheckCts = null;
        }
    }

    // -------------------------------------------------------------------
    // Duplicate analysis (called from CheckSizeAsync when user opts in)
    // -------------------------------------------------------------------

    /// <summary>
    /// Scan files for duplicates by grouping by size, then hashing same-size
    /// candidates. Exceptions propagate to the caller (which owns the
    /// row's IsChecking flag and CheckCts).
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
            MemoryBudget = _settings.MemoryBudget,
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

    /// <summary>
    /// Status message when a planned backup has no new or changed files.
    /// Distinguishes "genuinely up to date" from "the source is gone" — when
    /// every previously-backed-up file is now missing from the source (e.g. the
    /// source folder was moved, renamed, or deleted), saying "all files are
    /// already current" is misleading. The set name isn't included because this
    /// shows on the set's own row.
    /// </summary>
    private static string BuildNothingToBackUpMessage(int deletedCount) =>
        deletedCount > 0
            ? $"No new or changed files. {deletedCount:N0} previously-backed-up file(s) are no longer in the source — if the source folder was moved or renamed, update the set's source."
            : "Nothing to back up — all files are already current.";

    /// <summary>
    /// Record the outcome of a run that did nothing (no new/changed files, or a
    /// missing source). Rather than presenting a dismissible completion panel,
    /// the message is stored on the row's persistent result line (shown beneath
    /// the "N source(s) · Last: …" subtitle) and the row returns straight to
    /// idle — no Dismiss button to click.
    /// </summary>
    private void ShowNoOpCompletion(BackupSetRowViewModel row, string message)
    {
        row.Progress = null;
        row.IsRunning = false;
        row.LastResultIsError = false;
        row.LastResultText = message;
        CurrentView = null;                   // return to the home screen
        StatusText = "";
    }

    private async void StartIncrementalFlow(BackupSetRowViewModel row)
        => await RunIncrementalFlowAsync(row, forceReview: false);

    /// <summary>
    /// Scan a set and start its backup.  When <paramref name="forceReview"/> is
    /// true (or the destination lacks free space for the full delta), the
    /// post-scan file-review dialog is shown first so the user can inspect and
    /// deselect files before the backup runs.
    /// </summary>
    private async Task RunIncrementalFlowAsync(
        BackupSetRowViewModel row, bool forceReview)
    {
        if (row.IsRunning)
            return;

        var backupSet = row.Model;
        backupSet.JobOptions ??= new JobOptions();
        var opts = backupSet.JobOptions;

        // Follow the set's source drives across any Windows drive-letter
        // reassignment (rewriting source paths in place), and warn about — or,
        // if nothing is reachable, abort on — source locations that aren't
        // currently connected, instead of silently backing up nothing.
        if (_sourceResolver is not null)
        {
            var sr = _sourceResolver.Resolve(backupSet);

            if (sr.MetadataChanged)
                await _catalog.UpdateBackupSetAsync(backupSet);

            if (!sr.AnyAvailable)
            {
                MessageBox.Show(
                    $"None of the source locations for \"{backupSet.Name}\" are currently available:\n\n"
                    + string.Join("\n", sr.MissingSources)
                    + "\n\nThe backup was not started. Reconnect the source drive(s) and try again.",
                    "Sources unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText = $"Sources for \"{backupSet.Name}\" are not available.";
                return;
            }

            if (sr.MissingSources.Count > 0)
            {
                var choice = MessageBox.Show(
                    $"Some source locations for \"{backupSet.Name}\" are not currently available "
                    + "and will be skipped:\n\n"
                    + string.Join("\n", sr.MissingSources)
                    + "\n\nContinue backing up the available sources?",
                    "Some sources unavailable",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                {
                    StatusText = $"Backup of \"{backupSet.Name}\" cancelled \u2014 sources unavailable.";
                    return;
                }
            }

            if (sr.LetterChanges.Count > 0)
                StatusText = $"Source drive(s) moved: {string.Join(", ", sr.LetterChanges)}. Updated automatically.";
        }

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

        // Resolve the destination's stable volume identity to a live path,
        // following any drive-letter reassignment Windows may have made since
        // the set was last used. Updates opts.TargetDirectory in place and
        // backfills the volume identity for pre-feature sets.
        if (_destinationResolver is not null && opts.TargetDirectory is not null)
        {
            var resolution = _destinationResolver.Resolve(opts);

            if (resolution.MetadataChanged)
                await _catalog.UpdateBackupSetAsync(backupSet);

            if (!resolution.IsConnected)
            {
                StatusText = $"Destination drive for \"{backupSet.Name}\" is not connected " +
                    $"({resolution.PreviousPath ?? "unknown path"}).";
                return;
            }

            if (resolution.LetterChanged)
            {
                StatusText = $"Destination drive moved: \"{resolution.PreviousPath}\" \u2192 " +
                    $"\"{resolution.LivePath}\". Updated automatically.";
            }
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
            MemoryBudget = _settings.MemoryBudget,
        };

        bool isDir = job.TargetDirectory is not null;

        // Mark THIS set as running immediately so its per-set buttons switch
        // from Backup/Restore/Modify to Pause/Abort right away. Other rows are
        // untouched — they remain fully interactive and can start their own
        // backups concurrently. No app-wide wait cursor: the scan phase shows
        // an inline "Scanning…" status on this row only.
        row.Progress = null;
        row.LastResultText = "";            // clear any prior run's result line
        row.IsRunning = true;
        row.ScanCts = new CancellationTokenSource();
        var scanToken = row.ScanCts.Token;
        row.RunningStatusText = $"Scanning \"{backupSet.Name}\"\u2026";
        StatusText = $"Scanning \"{backupSet.Name}\"...";

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
                    var text = $"Scanning \"{backupSet.Name}\"... {sp.FilesFound:N0} files scanned ({FormatBytes(sp.TotalBytes)})";
                    row.RunningStatusText = text;
                    StatusText = text;
                }
            });

            BackupPlan plan;
            if (isDir)
            {
                if (_directoryBackupService is null)
                {
                    StatusText = "Directory backup service not available.";
                    row.IsRunning = false;
                    return;
                }

                var (diff, totalBytes, totalFiles) = await Task.Run(
                    () => _directoryBackupService.PlanAsync(job, scanToken, scanProgress));

                if (totalFiles == 0)
                {
                    scanning = false;
                    ShowNoOpCompletion(row,
                        BuildNothingToBackUpMessage(diff.DeletedFiles.Count));
                    return;
                }

                StatusText = $"{totalFiles:N0} file(s) to back up ({FormatBytes(totalBytes)})";

                // Determine whether to pause for the post-scan review dialog:
                // always when explicitly requested, or automatically when the
                // destination can't fit the full delta.
                var (hasFreeInfo, freeBytes) = GetDestinationFreeSpace(job.TargetDirectory!);
                bool insufficientSpace = hasFreeInfo && freeBytes < totalBytes;

                if (forceReview || insufficientSpace)
                {
                    // Stop scan-progress callbacks from overwriting the review.
                    scanning = false;

                    var review = ShowBackupReviewDialog(
                        backupSet.Name, diff, totalBytes, freeBytes, hasFreeInfo,
                        triggeredByLowSpace: insufficientSpace);

                    if (review is null)
                    {
                        // User cancelled the backup from the review dialog.
                        row.Progress = null;
                        row.IsRunning = false;
                        row.LastResultIsError = insufficientSpace;
                        row.LastResultText = insufficientSpace
                            ? "Backup cancelled — not enough free space on the destination."
                            : "Backup cancelled.";
                        StatusText = insufficientSpace
                            ? "Backup cancelled — not enough free space."
                            : $"\"{backupSet.Name}\": backup cancelled.";
                        return;
                    }

                    diff = review.Value.Diff;
                    totalBytes = review.Value.TotalBytes;

                    // Persist source removals if the user opted in.
                    if (review.Value.RemovedPaths.Count > 0)
                    {
                        await PersistSourceRemovalsAsync(backupSet, review.Value.RemovedPaths);
                        // Reflect the new exclusions in the running job so this
                        // very run also honors them (belt-and-suspenders — the
                        // diff is already filtered to the selected files).
                        job.ExcludedExtensions = backupSet.JobOptions!.ExcludedExtensions;
                    }

                    int selectedFiles = diff.NewFiles.Count + diff.ChangedFiles.Count;
                    if (selectedFiles == 0)
                    {
                        ShowNoOpCompletion(row,
                            BuildNothingToBackUpMessage(diff.DeletedFiles.Count));
                        return;
                    }

                    StatusText = $"{selectedFiles:N0} file(s) to back up ({FormatBytes(totalBytes)})";
                }

                plan = new BackupPlan
                {
                    Job = job,
                    Diff = diff,
                    DiscAllocations = [],
                    TotalDiscsRequired = 0,
                    TotalBytes = totalBytes,
                };

                // Final free-space gate — ALWAYS re-check, reading the
                // destination's free space fresh right before writing and
                // comparing it against the (possibly review-reduced) total.
                // This is the re-check after the user deselects items in the
                // review dialog: the earlier scan-time figure is stale once the
                // selection changes, so confirm the trimmed set actually fits
                // now. CheckFreeSpaceBeforeBackup only prompts when it still
                // doesn't fit, and lets the user override with "Start anyway".
                if (!CheckFreeSpaceBeforeBackup(job.TargetDirectory!, totalBytes))
                {
                    row.Progress = null;
                    row.IsRunning = false;
                    row.LastResultIsError = true;
                    row.LastResultText = "Backup cancelled — not enough free space on the destination.";
                    StatusText = "Backup cancelled — not enough free space.";
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
                    scanning = false;
                    ShowNoOpCompletion(row,
                        BuildNothingToBackUpMessage(plan.Diff.DeletedFiles.Count));
                    return;
                }

                StatusText = $"{totalFiles:N0} file(s) to back up ({FormatBytes(plan.TotalBytes)})";
            }

            // Stop scan-progress callbacks from overwriting burn status.
            scanning = false;
            StartBurn(row, plan);
        }
        catch (OperationCanceledException)
        {
            StatusText = $"\"{backupSet.Name}\": backup cancelled.";
            row.IsRunning = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Backup failed: {ex.GetType().Name}: {ex.Message}";
            row.IsRunning = false;
        }
        finally
        {
            row.ScanCts?.Dispose();
            row.ScanCts = null;
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
                StartBurnForSavedSet(plan, backupSetId);
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
                StartBurnForSavedSet(plan, backupSetId);
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

        // Restore schedule. Load all fields whenever a schedule exists (even if
        // disabled) so the editor reflects the stored Mode/interval/debounce.
        // Guarding on Enabled==true would drop the stored Mode and let the UI
        // defaults (Off + Interval) silently overwrite it on the next save.
        if (opts.Schedule is { } sched)
        {
            vm.ScheduleEnabled = sched.Enabled;
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

        // Schedule. Load all fields whenever a schedule exists (even if
        // disabled) so the editor reflects the stored Mode/interval/debounce.
        // Guarding on Enabled==true would drop the stored Mode and let the UI
        // defaults (Off + Interval) silently overwrite it on the next save.
        if (opts.Schedule is { } sched)
        {
            vm.ScheduleEnabled = sched.Enabled;
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
    /// <summary>
    /// Query the available free space on the drive hosting a directory-mode
    /// destination.  Returns (false, 0) when the drive can't be inspected
    /// (not ready, path error) so callers can skip the space-based prompts.
    /// </summary>
    private static (bool HasInfo, long FreeBytes) GetDestinationFreeSpace(string targetDirectory)
    {
        try
        {
            string pathRoot = System.IO.Path.GetPathRoot(targetDirectory) ?? targetDirectory;
            var driveInfo = new System.IO.DriveInfo(pathRoot);
            if (!driveInfo.IsReady)
                return (false, 0);
            return (true, driveInfo.AvailableFreeSpace);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>
    /// The outcome of the post-scan review dialog: the (possibly filtered) diff
    /// to back up, its total byte size, and glob exclusion patterns to persist
    /// for any paths the user chose to remove from the set's sources.
    /// </summary>
    private readonly record struct BackupReviewOutcome(
        BackupDiff Diff, long TotalBytes, List<string> RemovedPaths);

    /// <summary>
    /// Show the tristate post-scan review dialog modally.  Returns the filtered
    /// backup (files the user left selected) plus any source-removal patterns,
    /// or <c>null</c> if the user cancelled the backup.
    /// </summary>
    private BackupReviewOutcome? ShowBackupReviewDialog(
        string setName, BackupDiff diff, long totalBytes,
        long freeBytes, bool hasFreeInfo, bool triggeredByLowSpace)
    {
        var vm = new BackupReviewViewModel(
            setName, diff, totalBytes, freeBytes, hasFreeInfo, triggeredByLowSpace);

        var win = new Views.BackupSetEditorWindow
        {
            Owner = _editorWindow ?? Application.Current.MainWindow,
            Title = $"Review Files \u2014 {setName}",
        };

        vm.ProceedRequested += () => { try { win.DialogResult = true; } catch { win.Close(); } };
        vm.CancelRequested += () => { try { win.DialogResult = false; } catch { win.Close(); } };

        win.SetEditorContent(vm);
        win.ShowDialog();

        if (!vm.Confirmed)
            return null;

        var selected = vm.SelectedFilePaths();

        var filteredNew = diff.NewFiles.Where(f => selected.Contains(f.FullPath)).ToList();
        var filteredChanged = diff.ChangedFiles.Where(f => selected.Contains(f.FullPath)).ToList();

        long filteredBytes = 0;
        foreach (var f in filteredNew) filteredBytes += f.SizeBytes;
        foreach (var f in filteredChanged) filteredBytes += f.SizeBytes;

        var filteredDiff = new BackupDiff
        {
            NewFiles = filteredNew,
            ChangedFiles = filteredChanged,
            DeletedFiles = diff.DeletedFiles,
        };

        var removed = new List<string>();
        if (vm.RemoveDeselectedFromSources)
        {
            // Exclude ONLY the individual files the user actually saw and
            // deselected in the review — never a "dir\*" glob for a deselected
            // directory. The review lists only the current backup delta, so a
            // directory glob could exclude source files that live in that
            // directory but weren't shown here (already-backed-up / unchanged
            // files). Per-file exclusions (a full path is honoured as an exact
            // path pattern by DirectoryBackupService.BuildExclusionFilter)
            // guarantee we only remove what the user looked at. To stop backing
            // up a whole folder going forward, the user uses Modify, whose tree
            // shows the folder's full contents.
            foreach (var path in vm.DeselectedFiles())
            {
                if (!string.IsNullOrEmpty(path))
                    removed.Add(path);
            }
        }

        return new BackupReviewOutcome(filteredDiff, filteredBytes, removed);
    }

    /// <summary>
    /// Add source-removal exclusion patterns to a set's job options and persist
    /// them, so future backups also skip the paths the user deselected.
    /// </summary>
    private async Task PersistSourceRemovalsAsync(BackupSet set, List<string> patterns)
    {
        set.JobOptions ??= new JobOptions();
        var excl = set.JobOptions.ExcludedExtensions;
        foreach (var p in patterns)
        {
            if (!excl.Contains(p, StringComparer.OrdinalIgnoreCase))
                excl.Add(p);
        }
        try { await Task.Run(() => _catalog.UpdateBackupSetAsync(set)); }
        catch { /* best effort — the run still honours the filtered diff */ }
    }

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
            string message =
                $"The target drive ({driveLetter}) only has {FormatBytes(freeSpace)} free, " +
                $"but the backup needs {FormatBytes(requiredBytes)}.\n\n" +
                $"The backup will run out of space and may be incomplete.\n\n" +
                $"Start anyway?";

            // Show the prompt owned by (and activated over) the main window.
            // An ownerless MessageBox can open behind the app and never take
            // focus, so the backup appears frozen on "Scanning…" while it
            // silently waits for a click the user can't see.
            var owner = _editorWindow ?? Application.Current.MainWindow;
            owner?.Activate();
            var result = owner is not null
                ? MessageBox.Show(owner, message, "Insufficient Disk Space",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(message, "Insufficient Disk Space",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return false;
        }
        catch { /* can't check — let it proceed */ }
        return true;
    }

    /// <summary>
    /// Entry point for the new-set / edit-dialog flow, where planning is done by
    /// a <see cref="BackupJobViewModel"/> rather than the inline scan phase. The
    /// set has already been saved (so its row exists in <see cref="BackupSets"/>);
    /// we resolve that row, mark it running, and hand off to <see cref="StartBurn"/>.
    /// </summary>
    private void StartBurnForSavedSet(BackupPlan plan, int? backupSetId)
    {
        var row = backupSetId.HasValue ? RowFor(backupSetId.Value) : null;
        if (row is null)
        {
            StatusText = "Unable to locate the saved backup set to run.";
            CurrentView = null;
            return;
        }
        if (row.IsRunning)
            return;

        row.Progress = null;
        row.IsRunning = true;
        StartBurn(row, plan);
    }

    private void StartBurn(BackupSetRowViewModel row, BackupPlan plan)
    {
        // Reset the *while-burning* triggers so each burn starts clean — these
        // are meant to be pressed live during the burn, not carried over from a
        // previous run. The *pre-burn* toggles (no recorder / no media / erase
        // fails) are intentionally left untouched: the user arms them ahead of
        // time and they must survive into this run to fire.
        if (_simulatedBurner is not null)
        {
            _simulatedBurner.FileFailureProbability = 0;
            _simulatedBurner.CatastrophicFailureAtPercent = null;
            _simulatedBurner.SimulateVerifyFailure = false;
        }

        bool isDir = plan.Job.TargetDirectory is not null;
        var progressVm = new BurnProgressViewModel { IsDirectoryMode = isDir };
        CurrentView = null;                     // return to home screen
        row.Progress = progressVm;              // show this row's inline progress panel
        // row.IsRunning is already true from the scan phase.

        progressVm.DoneRequested += () =>
        {
            // Carry the just-finished outcome onto the row's persistent result
            // line so it stays visible after the panel is dismissed.
            row.LastResultIsError = progressVm.HasFailedFiles
                || progressVm.StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase);
            row.LastResultText = progressVm.StatusText;
            row.Progress = null;                // dismiss this row's progress panel
            row.IsRunning = false;
            _ = LoadBackupSetsAsync();
        };

        if (isDir)
        {
            StatusText = "";
            _ = ExecuteDirectoryBackupAsync(row, plan, progressVm);
        }
        else
        {
            StatusText = "";
            _ = ExecuteBurnAsync(row, plan, progressVm);
        }
    }

    /// <summary>
    /// Builds the per-file failure callback handed to the backup services. On a
    /// failure it shows a <see cref="FailureDialog"/> <em>modelessly</em> (via
    /// <c>Show()</c>, not <c>ShowDialog()</c>) and awaits the user's choice via a
    /// <see cref="TaskCompletionSource{TResult}"/>. Because the window is
    /// modeless, a prompt for one running set never disables the window or the
    /// other concurrent backups — only the set that hit the failure pauses while
    /// it waits for an answer. The callback may be invoked from a background
    /// thread (the backup runs under <c>Task.Run</c>), so all UI work is
    /// marshalled onto the dispatcher.
    /// </summary>
    private FailureCallback CreateFailureCallback(bool isDirectoryMode)
    {
        return (filePath, error, category) =>
        {
            var tcs = new TaskCompletionSource<FailureDecision>();

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var vm = new FailureDialogViewModel
                {
                    FilePath = filePath,
                    ErrorMessage = error,
                    IsDirectoryMode = isDirectoryMode,
                    ErrorCategory = category,
                };
                var dialog = new FailureDialog
                {
                    DataContext = vm,
                    Owner = Application.Current.MainWindow,
                };

                bool chosen = false;
                dialog.ActionChosen += action =>
                {
                    chosen = true;
                    tcs.TrySetResult(new FailureDecision { Action = action });
                };
                // If the user closes the window without picking a button,
                // default to Skip so the backup can continue rather than hang.
                dialog.Closed += (_, _) =>
                {
                    if (!chosen)
                        tcs.TrySetResult(new FailureDecision { Action = BurnFailureAction.Skip });
                };

                dialog.Show();
            });

            return tcs.Task;
        };
    }

    private async Task ExecuteBurnAsync(BackupSetRowViewModel row, BackupPlan plan, BurnProgressViewModel progressVm)
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

            // When a file fails, prompt the user (Skip/Retry/Zip/Abort) via a
            // modeless popup so other concurrent backups stay interactive.
            var result = await Task.Run(
                () => _orchestrator.ExecuteAsync(
                    plan, progress, CreateFailureCallback(isDirectoryMode: false), cts.Token));

            string detail = $"Discs written: {result.DiscsWritten}\n" +
                            $"Data written: {FormatBytes(result.BytesWritten)}";

            progressVm.CompleteBurn(result.Success, "", detail, result.FailedFiles);
            StatusText = result.Success ? "Backup completed." : "Backup completed with errors.";

            // Clean run with nothing to review → finalize straight to the row's
            // persistent result line (no "Dismiss" to click). When files failed,
            // leave the panel up so its failed-file list / export stays available.
            if (result.Success && result.FailedFiles.Count == 0)
                progressVm.RequestDone();
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
        // The row stays IsRunning until the user dismisses the completed panel
        // (BurnProgressViewModel.DoneRequested), which clears row state and
        // reloads the set list. Other rows are unaffected throughout.
    }

    // -------------------------------------------------------------------
    // Flow 2b: Directory Backup
    // -------------------------------------------------------------------

    private async Task ExecuteDirectoryBackupAsync(BackupSetRowViewModel row, BackupPlan plan, BurnProgressViewModel progressVm)
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

            // Directory (non-optical) backups do not prompt on per-file
            // failures: a failing file is logged and skipped automatically so
            // the backup runs unattended, and the failed files are surfaced in
            // the completion summary for review afterward. (Optical burns still
            // prompt — see ExecuteBurnAsync — because a failure there may need a
            // zip/skip-disc decision before the disc is committed.)
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

            // Clean run with nothing to review → finalize straight to the row's
            // persistent result line (no "Dismiss" to click). When files failed,
            // leave the panel up so its failed-file list / export stays available.
            if (result.Success && result.FailedFiles.Count == 0)
                progressVm.RequestDone();
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
        // The row stays IsRunning until the user dismisses the completed panel
        // (see ExecuteBurnAsync note above).
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
    // Flow 5b: Catalog-free (disaster-recovery) restore
    // -------------------------------------------------------------------

    private void StartCatalogFreeRestoreFlow()
    {
        var vm = new CatalogFreeRestoreViewModel(_catalogFreeRestoreService);
        vm.DoneRequested += GoHome;

        CurrentView = vm;
        StatusText = "Rebuild files directly from a backup folder, without the catalog.";
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
    // Flow 6b: Verify Integrity
    // -------------------------------------------------------------------

    private void StartVerifyIntegrityFlow()
    {
        if (SelectedBackupSet is null) return;

        var vm = new VerifyIntegrityViewModel(_catalog, _scanner, SelectedBackupSet);
        vm.DoneRequested += GoHome;

        CurrentView = vm;
        StatusText = $"Verifying backup integrity for \"{SelectedBackupSet.Name}\".";
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

            SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == newSet.Id)?.Model;
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

            // Merge rather than rebuild: a reload can happen while another set is
            // mid-backup (concurrent runs), so we must preserve the live run
            // state (IsRunning / Progress / CTS) of any row that is currently
            // working. Match by Id, update the model in place, and only drop
            // rows whose set no longer exists.
            var existing = BackupSets.ToDictionary(r => r.Id);
            var desiredIds = new HashSet<int>(sets.Select(s => s.Id));

            // Remove rows for deleted sets (never remove a running row).
            for (int i = BackupSets.Count - 1; i >= 0; i--)
            {
                if (!desiredIds.Contains(BackupSets[i].Id) && !BackupSets[i].IsRunning)
                    BackupSets.RemoveAt(i);
            }

            // Add new rows and refresh existing ones, in catalog order.
            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                if (existing.TryGetValue(set.Id, out var row))
                {
                    row.UpdateModel(set);
                    int currentIndex = BackupSets.IndexOf(row);
                    if (currentIndex != i && i < BackupSets.Count)
                        BackupSets.Move(currentIndex, i);
                }
                else
                {
                    var newRow = new BackupSetRowViewModel(set);
                    if (i <= BackupSets.Count)
                        BackupSets.Insert(i, newRow);
                    else
                        BackupSets.Add(newRow);
                }
            }
        }
        catch
        {
            StatusText = "Failed to load backup sets";
        }
    }

    /// <summary>Find the row VM wrapping the given backup set, if loaded.</summary>
    private BackupSetRowViewModel? RowFor(int setId) =>
        BackupSets.FirstOrDefault(r => r.Id == setId);

    /// <summary>Find the row VM wrapping the currently-selected set, if any.</summary>
    private BackupSetRowViewModel? SelectedRow =>
        SelectedBackupSet is null ? null : RowFor(SelectedBackupSet.Id);

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
