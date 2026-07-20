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
    /// <summary>True while a background poll is watching a transient service
    /// state (START_PENDING/STOP_PENDING) so refreshes don't stack multiple
    /// polling loops.  See <see cref="PollWhileServicePendingAsync"/>.</summary>
    private bool _servicePollActive;
    private BackupSetEditorWindow? _editorWindow;
    private Window? _largestFilesWindow;
    private Func<Task>? _pendingSettingsSave;
    private int? _unsavedNewSetId;

    // --- In-app update check (GitHub Releases) ---
    private UpdateInfo? _availableUpdate;
    private bool _updateBannerVisible;
    private string _updateBannerText = "";
    private bool _isCheckingForUpdates;

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
        SetTestDiscCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) { SelectedBackupSet = r.Model; StartTestDiscFlow(); } },
            o => o is BackupSetRowViewModel r && !r.IsRunning
                 && string.IsNullOrWhiteSpace(r.Model.JobOptions?.TargetDirectory));
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

        // In-app update check (GitHub Releases).
        CheckForUpdatesCommand = new RelayCommand(
            _ => _ = CheckForUpdatesAsync(userInitiated: true),
            _ => !_isCheckingForUpdates);
        DownloadUpdateCommand = new RelayCommand(
            _ => _ = DownloadUpdateAsync(),
            _ => _availableUpdate is not null);
        ViewReleaseNotesCommand = new RelayCommand(
            _ => ViewReleaseNotes(),
            _ => _availableUpdate is not null);
        DismissUpdateCommand = new RelayCommand(_ => DismissUpdate());

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
    public ICommand SetTestDiscCommand { get; }
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

    // --- In-app update check (GitHub Releases) ---

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand ViewReleaseNotesCommand { get; }
    public ICommand DismissUpdateCommand { get; }

    /// <summary>The newer release found by the last check, or null if none.</summary>
    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set => SetProperty(ref _availableUpdate, value);
    }

    /// <summary>Whether the "an update is available" banner is shown.</summary>
    public bool UpdateBannerVisible
    {
        get => _updateBannerVisible;
        private set => SetProperty(ref _updateBannerVisible, value);
    }

    /// <summary>Banner caption, e.g. "Lithic Backup 1.0.3 is available (you have 1.0.2).".</summary>
    public string UpdateBannerText
    {
        get => _updateBannerText;
        private set => SetProperty(ref _updateBannerText, value);
    }

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

        // Snapshot the source selection as it stands before any edit.  After the
        // editor closes we diff this against the saved selection to offer to (a)
        // purge destination copies of folders the user removed and (b) back up
        // folders the user added.  GetSelections() builds a brand-new tree and
        // reassigns the property, so holding the original list reference is a
        // valid snapshot.  Gated on an actual save so a discarded edit prompts
        // nothing (see savedThisSession below).
        var originalSelections = backupSet.SourceSelections is { } os
            ? os
            : new List<Core.Models.SourceSelection>();
        bool savedThisSession = false;

        // Baseline for the close prompt's real-change detection, so a no-touch
        // open/close can't pop a bogus "unsaved changes" prompt off the racy
        // event-based _needsSave flag (which fires on programmatic UI churn — lazy
        // settings-tab realization, tree virtualization, catalog/size stamping).
        // Captured once the initial load settles (Phase 3) and refreshed after every
        // save.  Two orthogonal, cheap signals:
        //   • settingsBaseline — a snapshot string of the name + JobOptions settings
        //     (NO whole-tree walk); a real setting edit changes it.
        //   • cleanSelectionMark — the count of user-toggled selection paths as of
        //     the last clean point; ChangedSelectionPaths only grows on genuine
        //     checkbox/auto-include toggles, so a later count above the mark means a
        //     real selection edit.
        // Left null/0 for brand-new sets, which always prompt (real unsaved record).
        string? settingsBaseline = null;
        int cleanSelectionMark = 0;

        // Completes once the deferred selection restore (Phase 3, below) has
        // finished.  The restore runs asynchronously AFTER the dialog is shown,
        // during which GetSelections() would return a partial/empty tree.  Every
        // path that persists the selection (SaveAllAsync — used by both the Save
        // button and the auto-save-on-close — plus the Seed and Largest-Files
        // handlers) awaits this first, so a fast close or click can never write a
        // half-restored tree over the real saved sources.  It is completed
        // unconditionally in Phase 3's finally, so new sets (which have nothing
        // to restore) and error paths still release any waiter.
        var selectionRestored = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Show a wait cursor while loading — the dialog won't appear until ready.
        Mouse.OverrideCursor = Cursors.Wait;

        // ---------------------------------------------------------------
        // Phase 1: async data loading (dialog not yet visible)
        // ---------------------------------------------------------------

        // Enumerate drives (DriveInfo.GetDrives + TotalSize — can be slow for
        // network drives) on the thread pool.  The catalog version query is
        // deliberately NOT loaded here: for large sets it returns ~1M rows and
        // takes several seconds, which would delay the window appearing.  It is
        // loaded in Phase 3 (below), after the dialog is already visible, and
        // applied to the tree via SetCatalogInfo — so the window opens
        // immediately and backup-status badges fill in a moment later.
        var drives = await Task.Run(() => SourceSelectionViewModel.EnumerateDrives());

        var sourceSelection = new SourceSelectionViewModel(catalogInfo: null, drives,
            fileHashCache: _fileHashCache, scanner: _scanner);
        sourceSelection.IsEditMode = true;

        // Mute dirty tracking across the whole programmatic init below (settings
        // restore, selection restore, catalog/size stamping, and the settings
        // panel's first-render binding write-backs).  Without this, that
        // machinery trips the catch-all "mark dirty" handlers, so opening a set
        // and immediately closing it — without touching anything — pops a bogus
        // "You have unsaved changes" prompt.  Re-armed in PostShowInitAsync's
        // finally once init has settled.
        sourceSelection.SuspendDirtyTracking();

        // Selection restore is deferred to Phase 3 (after the window is visible),
        // so mark "applying" NOW — before the tree ever renders — so the include
        // checkboxes stay hidden until their real state loads from the catalog.
        // Otherwise the first frame shows a full column of unchecked boxes, which
        // looks exactly like "all my sources were deleted." Phase 3 clears it in
        // its finally block once selections are restored.
        sourceSelection.IsApplyingSelections = true;

        // Restore backup set settings from saved state.
        sourceSelection.SetName = backupSet.Name;
        RestoreSourceSettings(sourceSelection, backupSet.JobOptions);

        // Capture the clean baseline for the close prompt RIGHT NOW — synchronously,
        // before the dialog is ever shown — so a fast open/close can't beat it.  The
        // old capture point (the Phase 3 ContextIdle pass, after the multi-second
        // selection restore) let a user who opened a set and closed it within ~1.6s
        // hit a still-null baseline plus the true-by-default _needsSave flag → a
        // bogus "unsaved changes" prompt (confirmed in the gui log).  At this point
        // RestoreSourceSettings has set every settings field, and the selection tree
        // hasn't been touched (programmatic restore writes node backing fields
        // directly, never adding to ChangedSelectionPaths), so this IS the saved
        // state.  New sets keep a null baseline (real unsaved record → always prompt).
        if (_unsavedNewSetId is null)
        {
            settingsBaseline = SnapshotEditorSettings(backupSet, sourceSelection);
            cleanSelectionMark = sourceSelection.ChangedSelectionPaths.Count;
        }

        // Selection restore and size computation are deferred to after the
        // dialog is visible (see Phase 3 / PostShowInitAsync below).  The tree's
        // include checkboxes stay hidden until the restore completes so the user
        // never sees a column of unchecked boxes (IsApplyingSelections above).
        CancellationTokenSource? autoCheckCts = null;
        bool planCheckReady = false;

        // Enable Save if selections already exist; for new empty sets, the user
        // must check at least one box first.
        if (backupSet.SourceSelections is { Count: > 0 } || backupSet.SourceRoots.Count > 0)
            sourceSelection.HasSelection = true;
        sourceSelection.ShowLargestFiles = true;

        // Helper: sync all VM settings into the BackupSet and write to DB.
        async Task SaveAllAsync()
        {
            // Never read GetSelections() from a still-restoring tree — wait for
            // the deferred restore to finish so we persist the real selection,
            // not a partial/empty snapshot.  Completes instantly once restore is
            // done (or immediately for new sets).
            await selectionRestored.Task;
            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();
            await Task.Run(() => _catalog.UpdateBackupSetAsync(backupSet));
            savedThisSession = true;

            // The persisted state is now the new "clean" baseline, so a later
            // stray dirty event (or the still-populated ChangedSelectionPaths from
            // the toggles we just saved) followed by a close won't re-prompt to
            // save changes that are already on disk.
            settingsBaseline = SnapshotEditorSettings(backupSet, sourceSelection);
            cleanSelectionMark = sourceSelection.ChangedSelectionPaths.Count;
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

        // Prompt before closing if there are unsaved changes.  A "save before
        // closing?" prompt (Yes / No / Cancel) is used for both new and existing
        // sets — it's more intuitive and safer than a "discard it?" prompt,
        // because the safe action (keep the work by saving) lines up with the
        // default Yes rather than being buried behind a "No".
        //   Yes    → save and close
        //   No     → close without saving (existing: skip auto-save; new: discard
        //            the temporary catalog record)
        //   Cancel → stay open
        dialog.Closing += (_, e) =>
        {
            // A forced shutdown (upgrade installer's Restart-Manager signal or a
            // Windows session end) must not be blocked by a modal prompt: the
            // installer waits only ~15s for LithicBackup.exe to exit, so stalling
            // Application.Shutdown() on this dialog keeps the .exe locked and makes
            // the upgrade fail with "unable to close all requested applications."
            // Let the window close; the Closed handler still runs the pending
            // best-effort save for existing sets (new sets discard their temp
            // record, which is the right call for an unsaved set during shutdown).
            if (Application.Current is App { IsForcedShutdown: true })
                return;

            bool isNewSet = _unsavedNewSetId is not null;

            // Cheap first gate: the event-based dirty flag never *misses* a real
            // change (it errs the other way — firing on programmatic noise), so if
            // it says clean, we truly are and can skip the prompt without any
            // further work.
            if (!sourceSelection.HasUnsavedChanges)
                return;

            // The flag says dirty, but it's prone to false positives.  For an
            // existing set, confirm with the two precise, cheap signals: a genuine
            // selection toggle (ChangedSelectionPaths grew past the last clean mark)
            // or a genuine setting edit (settings snapshot differs from baseline).
            // Neither walks the whole selection tree, so this stays fast even on
            // huge sets.  If neither fired, only programmatic churn dirtied the flag
            // and we must NOT prompt.  (New sets have no baseline and always prompt —
            // there's an unsaved record to persist.)
            if (!isNewSet && settingsBaseline is not null)
            {
                bool selectionChanged = sourceSelection.ChangedSelectionPaths.Count > cleanSelectionMark;
                bool settingsChanged = SnapshotEditorSettings(backupSet, sourceSelection) != settingsBaseline;

                if (!selectionChanged && !settingsChanged)
                    return;
            }

            var result = MessageBox.Show(
                isNewSet
                    ? "This backup set hasn't been saved yet.\n\nSave it before closing?"
                    : "You have unsaved changes.\n\nSave before closing?",
                isNewSet ? "Unsaved Backup Set" : "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
            else if (result == MessageBoxResult.Yes)
            {
                // Save on close.  For a new set, clear the "unsaved new" marker so
                // the Closed handler persists it (via _pendingSettingsSave) instead
                // of deleting the temporary catalog record.
                _unsavedNewSetId = null;
            }
            else // No — close without saving.
            {
                // Existing set: skip the auto-save.  New set: leave
                // _unsavedNewSetId set so the Closed handler discards the
                // temporary record.
                if (!isNewSet)
                    _pendingSettingsSave = null;
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
                // Save all settings on close — the user chose "Save" at the
                // close prompt (existing set, or a new set they kept).  Guard the
                // save: this is an async-void event handler, so an unhandled
                // exception here would tear down the app.  On failure savedThisSession
                // stays false, so the reconcile below is correctly skipped.
                try
                {
                    await _pendingSettingsSave();
                }
                catch (Exception ex)
                {
                    StatusText = $"Failed to save on close: {ex.Message}";
                }
                _pendingSettingsSave = null;
            }
            _editorWindow = null;
            await LoadBackupSetsAsync();

            // If the edit actually saved, reconcile the destination with the new
            // source selection: offer to purge copies of removed folders and to
            // back up newly added ones.  Skipped entirely when nothing was saved
            // (e.g. the user discarded changes on close).  Whether the reconcile
            // runs at all — and whether it prompts first — is governed inside
            // ReconcileDestinationAfterEditAsync by the ReconcileMode setting.
            if (savedThisSession)
                await ReconcileDestinationAfterEditAsync(
                    backupSet, originalSelections, sourceSelection.ChangedSelectionPaths);
        };

        // Save button: persist everything, then close the editor — matching the
        // Settings dialog, where Save commits and dismisses in one action.
        sourceSelection.SaveRequested += async () =>
        {
            try
            {
                await SaveAllAsync();
                _unsavedNewSetId = null; // committed — don't delete on close
                StatusText = $"Backup set \"{sourceSelection.SetName}\" saved. {DateTime.Now:HH:mm:ss}";
                await LoadBackupSetsAsync();
                SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id)?.Model;

                // We just persisted everything, so clear the on-close pending save;
                // otherwise the Closed handler would run SaveAllAsync a second time.
                // (savedThisSession stays true, so the post-close reconcile still runs.)
                _pendingSettingsSave = null;

                // Close on save. SaveAllAsync just refreshed the dirty baseline, so
                // the Closing handler sees a clean state and won't re-prompt to save.
                dialog.Close();
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
            // Sync and save current settings first (after the deferred restore
            // has finished, so GetSelections() reflects the real selection).
            await selectionRestored.Task;
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
            // Wait for the deferred restore first so we don't flush a partial tree.
            await selectionRestored.Task;
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
                    await selectionRestored.Task;
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
        // Phase 3: load catalog, restore selections, compute sizes (dialog visible)
        // ---------------------------------------------------------------
        // While IsApplyingSelections is set, the tree's include/auto-include
        // checkboxes stay hidden (see SourceSelectionView.xaml), so the user never
        // sees a column of unchecked boxes before the real state loads. This keeps
        // it set across the whole restore span (catalog load + selection restore);
        // it was already set at construction, so this is belt-and-suspenders.
        sourceSelection.IsApplyingSelections = true;
        try
        {
            // Restore the checkboxes FIRST, without waiting on the catalog.
            // Selection state comes entirely from the saved model; the catalog
            // dictionary (potentially ~1M rows, several seconds to query) is only
            // needed for backup-status badges and is loaded in the background
            // afterwards (PostShowInitAsync). Restore also only loads the
            // currently-expanded subtrees — collapsed folders restore their
            // sub-selection lazily when expanded (see ApplySelectionAsync). Both
            // of these keep the "checkboxes hidden" window as short as possible.
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

        }
        finally
        {
            sourceSelection.IsApplyingSelections = false;
            // Release any save/seed/largest-files path that was waiting for the
            // restore to finish (including an auto-save queued by a fast close).
            selectionRestored.TrySetResult();
        }

        // Arm dirty tracking (and mark clean for existing sets) as soon as the
        // restore + first render settle — NOT after the potentially multi-second
        // catalog/size computation in PostShowInitAsync below.  The dialog has
        // been visible and interactive since Show() (well before this point), so
        // a user who immediately edits a setting — e.g. the schedule fields —
        // must have that edit tracked.  Previously the resume lived in
        // PostShowInitAsync's finally, so during a slow ComputeAllUnknownSizesAsync
        // the catch-all dirty handlers stayed suspended and those early edits were
        // silently ignored (Save never enabled).  The catalog/size steps that run
        // afterwards raise no dirtying VM event (SetCatalogInfo only touches node
        // badges; ComputeAllUnknownSizesAsync only writes the excluded
        // SelectedSizeText / SizeCalculationResult), so arming before them is safe.
        //
        // Scheduling at ContextIdle lets the tree's Include/Auto-include checkbox
        // columns — revealed by the IsApplyingSelections=false flip just above —
        // flush their first-render Mode=TwoWay write-backs first (Render priority,
        // above ContextIdle).  Those write-backs dirty the set via the ungated
        // selection-settle / AutoIncludeNew paths, so we MarkClean HERE (after they
        // land) rather than earlier; otherwise a no-touch open/close would prompt
        // to save.  New sets stay dirty (there's a real unsaved record to persist).
        //
        // Crucially, AWAIT this ContextIdle pass before kicking off the
        // multi-second PostShowInitAsync catalog/size/plan work below.  That work
        // pumps a continuous stream of higher-than-ContextIdle dispatcher activity,
        // so if it starts first it STARVES this clean-mark for several seconds —
        // during which _needsSave sits at its `true` init-default and a user who
        // opens the set and clicks Cancel within that window gets a bogus "unsaved
        // changes" prompt.  Awaiting keeps the dispatcher quiet until the clean-mark
        // fires (within milliseconds of the dialog appearing, right after the
        // first-render write-backs settle), closing that window.
        await Application.Current.Dispatcher.InvokeAsync(
            () =>
            {
                if (_unsavedNewSetId is null)
                {
                    // This delayed pass clears the racy dirty flag left by init /
                    // first-render write-backs.  But it must NOT clobber a genuine
                    // edit the user made during the (multi-second) load window — e.g.
                    // ticking "Create subdirectory" right after open, which would
                    // otherwise see Save disable ~half a second later when this pass
                    // finally runs.  So only MarkClean when the current state still
                    // matches the Phase-1 baseline (no real change); otherwise leave
                    // _needsSave and the baseline intact so the edit sticks.
                    string refreshed = SnapshotEditorSettings(backupSet, sourceSelection);
                    bool selectionChanged =
                        sourceSelection.ChangedSelectionPaths.Count > cleanSelectionMark;
                    bool settingsChanged = refreshed != settingsBaseline;
                    if (!selectionChanged && !settingsChanged)
                    {
                        // No real edit — clear first-render churn and fold any benign
                        // settling into the clean baseline.
                        sourceSelection.MarkClean();
                        settingsBaseline = refreshed;
                        cleanSelectionMark = sourceSelection.ChangedSelectionPaths.Count;
                    }
                    // Otherwise the user made a genuine edit during the load window —
                    // leave _needsSave and the baseline intact so the edit sticks.
                }
                sourceSelection.ResumeDirtyTracking();
            },
            System.Windows.Threading.DispatcherPriority.ContextIdle).Task;

        _ = PostShowInitAsync();

        async Task PostShowInitAsync()
        {
            try
            {
                // Now that the tree is visible and interactive, load the big catalog
                // dictionary on a background thread and stamp backup-status badges on
                // the already-loaded (visible) nodes. Collapsed folders pick up their
                // badges when expanded, since new child nodes read the shared catalog
                // getter (which SetCatalogInfo populates here).
                var catalogInfo = await Task.Run(() =>
                {
                    try { return _catalog.GetLatestVersionInfoAsync(backupSet.Id).GetAwaiter().GetResult(); }
                    catch { return null as Dictionary<string, Core.Models.FileVersionInfo>; }
                });
                sourceSelection.SetCatalogInfo(catalogInfo);

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

                await sourceSelection.ComputeAllUnknownSizesAsync();
            }
            catch
            {
                // Catalog/size stamping is best-effort — a failure here must not
                // crash the editor.  Dirty tracking is already armed above,
                // independently of this work, so a failure can't leave the dialog
                // unable to detect edits.
            }

            planCheckReady = true;
            autoCheckCts = new CancellationTokenSource();
            await RunPlanCheckInEditorAsync(sourceSelection, backupSet, autoCheckCts.Token);
        }
    }

    // -------------------------------------------------------------------
    // Post-edit destination reconcile: after a *saved* source edit, offer to
    // purge destination copies of removed sources and back up added ones.
    // -------------------------------------------------------------------

    /// <summary>
    /// After a backup set's sources are edited AND saved, diff the saved source
    /// selection against the pre-edit snapshot and offer two independent,
    /// orthogonal follow-ups:
    /// <list type="bullet">
    /// <item><b>Purge</b> — destination copies of files whose source path is no
    /// longer covered are offered for immediate removal (all-or-nothing, via a
    /// read-only review tree) so they don't linger until the next Cleanup.</item>
    /// <item><b>Back up</b> — files under newly-covered source roots are shown in
    /// a read-only preview tree and offered for an immediate incremental backup.</item>
    /// </list>
    /// Removal is detected file-by-file (a file included under the old selection
    /// but not the new one), which correctly handles deselecting one child of a
    /// fully-selected parent.  Addition is first gated at the covering-root level
    /// (so a partial de-selection can never register as an addition), then the
    /// preview enumerates those roots and keeps only files the new selection
    /// covers but the old one didn't.
    /// </summary>
    private async Task ReconcileDestinationAfterEditAsync(
        BackupSet backupSet,
        IReadOnlyList<Core.Models.SourceSelection> originalSelections,
        IReadOnlyCollection<string> changedPaths)
    {
        var newSelections = backupSet.SourceSelections
            ?? new List<Core.Models.SourceSelection>();

        // Fast path: if the source selection is unchanged from when the dialog
        // opened, there is nothing to reconcile — no folders were dropped or
        // added. Skip all catalog work that would otherwise run on EVERY dialog
        // close, since close auto-saves and thus always sets savedThisSession.
        // The comparison uses the same JSON serialization the catalog persists
        // with, so it is conservative: any real change produces different JSON
        // and still runs the reconcile below.
        if (SelectionsEquivalent(originalSelections, newSelections))
            return;

        // Second fast path: the serialized trees differ, but ONLY because of
        // display-only state — the user expanded/collapsed folders while browsing,
        // which ToModel persists as IsExpanded so the tree reopens the same way.
        // Expansion can neither add nor remove a covered file, and every real
        // coverage change (a checkbox toggle, or an auto-include-new toggle) is
        // recorded into changedPaths at the moment it happens (see
        // SourceSelectionViewModel.RequestSelectionSettle and the ApplyAutoIncludeNew
        // -> _recordChangedPath wiring; a bulk select/deselect-all records the
        // virtual root as an empty path).  So if nothing was recorded, the edit
        // touched no selection at all — skip the (potentially huge, hundreds of
        // thousands of files) catalog + filesystem scans that would otherwise fire
        // just because the user opened a few folders to look around.
        if (changedPaths.Count == 0)
            return;

        // A real coverage change was made and saved.  Whether we scan/reconcile
        // now is up to the user's ReconcileMode setting:
        //   Never  — skip; added/removed folders sync on the next full backup.
        //   Always — reconcile silently.
        //   Ask    — prompt first, so a large edit doesn't kick off a long scan
        //            without consent (the default).
        switch (_settings.ReconcileMode)
        {
            case Core.Models.ReconcileAfterEditMode.Never:
                return;
            case Core.Models.ReconcileAfterEditMode.Ask:
                var answer = MessageBox.Show(
                    $"You changed which folders \"{backupSet.Name}\" backs up.\n\n" +
                    "Scan the affected folders now to back up ones you added and " +
                    "remove copies of ones you dropped?\n\n" +
                    "You can skip this and let the changes sync on the next full " +
                    "backup instead.",
                    "Update backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                    return;
                break;
            case Core.Models.ReconcileAfterEditMode.Always:
            default:
                break;
        }

        // Show a cancellable progress dialog (instead of a busy cursor) while we
        // scan the catalog for dropped files: it says exactly what it's doing and
        // reports a live count, and the user can close it to abort the scan —
        // which leaves the destination untouched (nothing is deleted).
        var (completed, scan) = await Views.ProgressDialog.RunAsync(
            Application.Current.MainWindow,
            "Updating backup",
            "Checking for backed-up files no longer covered by the set\u2026",
            cancellable: true,
            (progress, ct) =>
            {
                // Removed: currently-active catalog files whose source path was
                // covered before the edit but isn't covered any more.  Rather
                // than load the whole (potentially ~1M-row) file table, query
                // only the subtrees the user actually toggled this session: every
                // file whose inclusion changed sits at or under one of those
                // toggled nodes (a removal can only be caused by unchecking that
                // node or an ancestor of it), so scoping the reads to them is
                // both correct and far cheaper.
                var rem = ComputeRemovedFilesTargeted(
                    backupSet.Id, originalSelections, newSelections, changedPaths,
                    progress, ct);

                // Added: new covering roots not already covered by an old root.
                var oldRoots = SourceSelection.CollectSelectedRoots(originalSelections);
                var newRoots = SourceSelection.CollectSelectedRoots(newSelections);
                var added = newRoots
                    .Where(r => !IsCoveredBy(oldRoots, r))
                    .ToList();

                return (Removed: rem, AddedRoots: added);
            });

        if (!completed)
        {
            StatusText = "Cancelled — no destination files were changed.";
            return;
        }

        var removed = scan.Removed;
        var addedRoots = scan.AddedRoots;

        // Removal and addition are disjoint (a path can't be both dropped and
        // newly covered), so both prompts may legitimately appear in sequence.
        if (removed.Count > 0)
            await PromptAndPurgeRemovedAsync(backupSet, removed);

        if (addedRoots.Count > 0)
            await PromptAndBackupAddedAsync(backupSet, addedRoots, originalSelections, newSelections);
    }

    /// <summary>
    /// Conservative structural equality for two source-selection trees, used to
    /// decide whether a dialog edit actually changed the selection. Compares the
    /// same JSON form the catalog persists with (<see cref="SqliteCatalogRepository"/>
    /// serializes <c>SourceSelections</c> with a plain <c>JsonSerializer.Serialize</c>),
    /// so identical trees compare equal and any real change compares unequal.
    /// </summary>
    private static bool SelectionsEquivalent(
        IReadOnlyList<Core.Models.SourceSelection> a,
        IReadOnlyList<Core.Models.SourceSelection> b)
    {
        try
        {
            return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        }
        catch
        {
            // If serialization ever fails, fall back to running the reconcile
            // (correctness over the optimisation).
            return false;
        }
    }

    /// <summary>
    /// Find the catalog files dropped by an edit, reading ONLY the subtrees the
    /// user toggled this session instead of the whole file table.  Correct
    /// because a file's inclusion can only change if the user toggled that file
    /// or one of its ancestor directories, so every removed file sits at or
    /// under one of <paramref name="changedPaths"/>.  Each candidate is kept only
    /// when it was included before the edit and is excluded now, so over-scoping
    /// (e.g. a toggled folder that also gained files) never yields false
    /// removals.
    /// </summary>
    private List<FileRecord> ComputeRemovedFilesTargeted(
        int backupSetId,
        IReadOnlyList<Core.Models.SourceSelection> originalSelections,
        IReadOnlyList<Core.Models.SourceSelection> newSelections,
        IReadOnlyCollection<string> changedPaths,
        IProgress<ProgressReport>? progress,
        CancellationToken ct)
    {
        // A bulk toggle can record the virtual "All Drives" root (empty path),
        // which covers everything — e.g. "deselect all".  There is no cheaper
        // way to reconcile a whole-tree change than scanning every backed-up
        // file, so fall back to the full load in that (rare) case.
        if (changedPaths.Any(string.IsNullOrEmpty))
            return ComputeRemovedFilesFull(backupSetId, originalSelections, newSelections, progress, ct);

        // Nothing was recorded as changed, yet the JSON diff flagged a change — so
        // the edit was purely cosmetic (expansion state, which is display-only and
        // can't drop a backed-up file).  Auto-include-new toggles DO record their
        // directory path (see SourceSelectionNodeViewModel.ApplyAutoIncludeNew ->
        // _recordChangedPath), precisely because turning the rule off can evict
        // existing descendants that were covered only by it; so an auto-include
        // change reaches the scan below rather than this no-op early-return.
        if (changedPaths.Count == 0)
            return new List<FileRecord>();

        // Collapse to a minimal set so overlapping subtrees are queried once.
        var roots = MinimizePaths(changedPaths);

        var seen = new HashSet<long>();
        var removed = new List<FileRecord>();
        long checkedCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastReportMs = -ProgressUpdateIntervalMs;   // fire the first report immediately
        foreach (var path in roots)
        {
            ct.ThrowIfCancellationRequested();

            // Matches the path itself and every descendant, so it works whether
            // the toggled node was a file or a directory.
            var records = _catalog
                .GetFileRecordsUnderDirectoryAsync(backupSetId, path)
                .GetAwaiter().GetResult();

            foreach (var f in records)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;

                long nowMs = sw.ElapsedMilliseconds;
                if (progress is not null && nowMs - lastReportMs >= ProgressUpdateIntervalMs)
                {
                    lastReportMs = nowMs;
                    progress.Report($"Checked {checkedCount:N0} files, {removed.Count:N0} to remove\u2026");
                }

                if (f.IsDeleted || !seen.Add(f.Id))
                    continue;
                if (SourceSelection.IsPathIncluded(originalSelections, f.SourcePath)
                    && !SourceSelection.IsPathIncluded(newSelections, f.SourcePath))
                    removed.Add(f);
            }
        }
        progress?.Report($"Checked {checkedCount:N0} files, {removed.Count:N0} to remove.");
        return removed;
    }

    /// <summary>
    /// Whole-table fallback for <see cref="ComputeRemovedFilesTargeted"/> used
    /// when the change can't be localised (e.g. a bulk deselect-all).  Loads
    /// every catalog file and keeps those covered before the edit but not after.
    /// </summary>
    private List<FileRecord> ComputeRemovedFilesFull(
        int backupSetId,
        IReadOnlyList<Core.Models.SourceSelection> originalSelections,
        IReadOnlyList<Core.Models.SourceSelection> newSelections,
        IProgress<ProgressReport>? progress,
        CancellationToken ct)
    {
        progress?.Report("Loading backup catalog\u2026");
        var all = _catalog
            .GetAllFilesForBackupSetAsync(backupSetId)
            .GetAwaiter().GetResult();

        var removed = new List<FileRecord>();
        long checkedCount = 0;
        int total = all.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastReportMs = -ProgressUpdateIntervalMs;   // fire the first report immediately
        foreach (var f in all)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;

            long nowMs = sw.ElapsedMilliseconds;
            if (progress is not null && nowMs - lastReportMs >= ProgressUpdateIntervalMs)
            {
                lastReportMs = nowMs;
                int pct = total == 0 ? 100 : (int)(checkedCount * 100L / total);
                progress.Report(new ProgressReport(
                    $"Checked {checkedCount:N0}/{total:N0} files, {removed.Count:N0} to remove\u2026",
                    pct));
            }

            if (!f.IsDeleted
                && SourceSelection.IsPathIncluded(originalSelections, f.SourcePath)
                && !SourceSelection.IsPathIncluded(newSelections, f.SourcePath))
                removed.Add(f);
        }
        progress?.Report($"Checked {checkedCount:N0} files, {removed.Count:N0} to remove.");
        return removed;
    }

    /// <summary>
    /// Reduce a set of paths to the minimal covering set: drop any path that
    /// equals or lies under another path in the set, so a subtree isn't queried
    /// twice (once for a parent and again for a child).
    /// </summary>
    private static List<string> MinimizePaths(IEnumerable<string> paths)
    {
        var distinct = paths
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();

        var minimal = new List<string>();
        foreach (var path in distinct)
        {
            if (!IsCoveredBy(minimal, path))
                minimal.Add(path);
        }
        return minimal;
    }

    /// <summary>True when <paramref name="path"/> equals one of
    /// <paramref name="roots"/> or lives underneath one of them.</summary>
    private static bool IsCoveredBy(IEnumerable<string> roots, string path)
    {
        foreach (var root in roots)
        {
            if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
                return true;
            var prefix = root.TrimEnd('\\') + "\\";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Show the read-only removal-review tree for files dropped from the set's
    /// sources; on confirmation, mark them deleted in the catalog and physically
    /// delete their destination copies (reusing the vetted Cleanup purge path via
    /// <see cref="Services.DestinationFilePurger"/>), then run a catalog reconcile
    /// so references left stale by the deletions are repaired — exactly as the
    /// manual Cleanup workflow does.
    /// </summary>
    private async Task PromptAndPurgeRemovedAsync(
        BackupSet backupSet, List<FileRecord> removed)
    {
        // One review row per source path; size is the sum across its versions.
        var byPath = removed
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (SourcePath: g.Key, SizeBytes: g.Sum(f => f.SizeBytes)))
            .ToList();

        var reviewVm = ReviewTreeViewModel.ForRemoval(byPath);
        var dialog = new ReviewTreeDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = reviewVm,
        };

        if (dialog.ShowDialog() != true)
        {
            StatusText = "Left removed-source files on the destination "
                + "(remove later via Cleanup).";
            return;
        }

        string? targetDir = backupSet.JobOptions?.TargetDirectory;
        var sourcePaths = byPath.Select(p => p.SourcePath).ToList();
        // Every version's destination file must be deleted, de-duplicated so a
        // shared disc path is never deleted (or counted) twice.
        var discPaths = new HashSet<string>(
            removed.Select(f => f.DiscPath.Replace('/', '\\')),
            StringComparer.OrdinalIgnoreCase);

        int catPurged = 0, filesDeleted = 0, delFailures = 0;
        long bytesFreed = 0;
        try
        {
            // Progress dialog (not a busy cursor) so the deletion + catalog
            // reconcile always shows live status and the window deterministically
            // closes when the work finishes — the old global cursor override
            // could visually "stick" if a long reconcile ran on with no feedback.
            // Not cancellable: the user already confirmed the deletion, and
            // aborting mid-purge would leave the catalog and destination out of
            // step (Cleanup can always finish a partial purge later).
            var (_, purge) = await Views.ProgressDialog.RunAsync<(int Cat, int Files, int Fail, long Bytes)>(
                Application.Current.MainWindow,
                "Removing files from destination",
                "Deleting backed-up copies of the removed folders\u2026",
                cancellable: false,
                (progress, ct) =>
                {
                    // Catalog marks inside a single transaction (mirrors Cleanup).
                    // Loop per source path so the marking shows live "x of y"
                    // progress (throttled to ProgressUpdateIntervalMs) instead of a
                    // single static "Updating catalog…" that looks frozen while
                    // thousands of rows are updated — the user observed exactly this.
                    var progressSw = System.Diagnostics.Stopwatch.StartNew();
                    long lastReportMs = -ProgressUpdateIntervalMs; // fire first report immediately
                    var tx = _catalog.BeginTransactionAsync(backupSet.Id).GetAwaiter().GetResult();
                    int purged = 0;
                    try
                    {
                        for (int i = 0; i < sourcePaths.Count; i++)
                        {
                            var path = sourcePaths[i];
                            long nowMs = progressSw.ElapsedMilliseconds;
                            // Always report the final item so a "N/N" lands before
                            // the disk-delete phase takes over; throttle the rest.
                            if (nowMs - lastReportMs >= ProgressUpdateIntervalMs
                                || i == sourcePaths.Count - 1)
                            {
                                lastReportMs = nowMs;
                                int pct = sourcePaths.Count == 0
                                    ? 100 : (int)((i + 1) * 100L / sourcePaths.Count);
                                progress.Report(new ProgressReport(
                                    $"Updating catalog {i + 1:N0}/{sourcePaths.Count:N0} ({pct}%): "
                                    + Path.GetFileName(path.TrimEnd('\\')),
                                    pct));
                            }

                            purged += _catalog
                                .MarkFilesDeletedBySourcePathsAsync(backupSet.Id, new[] { path })
                                .GetAwaiter().GetResult();
                        }
                        tx.Complete();
                    }
                    finally
                    {
                        tx.Dispose();
                    }

                    // Physical deletion outside the tx so file errors can't roll
                    // the catalog back.  Skipped entirely if no destination is set.
                    int fd = 0, ff = 0;
                    long bytes = 0;
                    if (targetDir is not null)
                    {
                        var (d, f, b) = Services.DestinationFilePurger
                            .DeleteFilesAndSweep(targetDir, discPaths, progress);
                        fd = d; ff = f; bytes = b;

                        // Repair any references left stale by the deletions (a
                        // plain copy other rows referenced may now be gone),
                        // exactly as manual Cleanup's reconcile step does.
                        progress.Report("Repairing catalog references\u2026");
                        var reconcile = new CatalogReconcileService(_catalog);
                        var report = reconcile.AnalyzeAsync(backupSet.Id, targetDir, progress, ct)
                            .GetAwaiter().GetResult();
                        if (report.HasChanges)
                            reconcile.ApplyAsync(backupSet.Id, report, targetDir, progress, ct)
                                .GetAwaiter().GetResult();
                    }
                    return (purged, fd, ff, bytes);
                });

            (catPurged, filesDeleted, delFailures, bytesFreed) = purge;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to remove destination files: {ex.Message}";
            return;
        }

        var parts = new List<string>();
        if (catPurged > 0)
            parts.Add($"purged {catPurged:N0} catalog record{(catPurged == 1 ? "" : "s")}");
        if (filesDeleted > 0)
            parts.Add($"deleted {filesDeleted:N0} file{(filesDeleted == 1 ? "" : "s")} ({FormatBytes(bytesFreed)})");
        if (delFailures > 0)
            parts.Add($"{delFailures:N0} deletion failure{(delFailures == 1 ? "" : "s")}");
        StatusText = parts.Count > 0
            ? "Removed sources: " + string.Join(", ", parts) + "."
            : "No destination files needed removal.";

        await LoadBackupSetsAsync();
    }

    /// <summary>
    /// Show a read-only, sortable preview tree of the files newly covered by the
    /// set's sources (enumerated from disk under the added roots, filtered to the
    /// files the new selection covers but the old one didn't).  On confirmation,
    /// kick off an immediate incremental backup; declining leaves them for the
    /// next scheduled or manual backup.
    /// </summary>
    private async Task PromptAndBackupAddedAsync(
        BackupSet backupSet,
        List<string> addedRoots,
        IReadOnlyList<Core.Models.SourceSelection> originalSelections,
        IReadOnlyList<Core.Models.SourceSelection> newSelections)
    {
        // Cancellable progress dialog while walking the added folders on disk;
        // closing it aborts the scan and skips the add prompt entirely.
        var (completed, addedFiles) = await Views.ProgressDialog.RunAsync(
            Application.Current.MainWindow,
            "Updating backup",
            "Scanning newly added folders\u2026",
            cancellable: true,
            (progress, ct) =>
            {
                // Walk the added roots on disk and keep only files the new
                // selection covers but the old one didn't — the exact inverse of
                // the removal diff, so partial (de)selections are respected.
                var result = new List<(string SourcePath, long SizeBytes)>();
                long scanned = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastReportMs = -ProgressUpdateIntervalMs;
                foreach (var (path, size) in EnumerateFilesUnderRoots(addedRoots))
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;

                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastReportMs >= ProgressUpdateIntervalMs)
                    {
                        lastReportMs = nowMs;
                        progress.Report($"Scanned {scanned:N0} files, {result.Count:N0} new\u2026");
                    }

                    if (SourceSelection.IsPathIncluded(newSelections, path)
                        && !SourceSelection.IsPathIncluded(originalSelections, path))
                        result.Add((path, size));
                }
                return result;
            });

        // Cancelled the scan — leave the added folders for the next backup.
        if (!completed)
            return;

        // Nothing newly covered actually exists on disk (e.g. empty folders were
        // added) — there's nothing to preview or back up right now.
        if (addedFiles.Count == 0)
            return;

        var reviewVm = ReviewTreeViewModel.ForAdditions(addedFiles);
        var dialog = new ReviewTreeDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = reviewVm,
        };

        if (dialog.ShowDialog() != true)
            return;

        var row = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id);
        if (row is null)
        {
            StatusText = "Couldn't start backup — the set is no longer listed.";
            return;
        }
        _ = RunIncrementalFlowAsync(row, forceReview: false);
    }

    /// <summary>
    /// Recursively enumerate the files (with sizes) under a set of directory
    /// roots, skipping reparse points to avoid symlink loops and swallowing
    /// per-directory access errors so one unreadable folder can't abort the walk.
    /// </summary>
    private static IEnumerable<(string Path, long Size)> EnumerateFilesUnderRoots(
        IEnumerable<string> roots)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        foreach (var r in roots)
        {
            // A "root" is always a directory (CollectSelectedRoots maps a selected
            // file to its containing directory), but guard for a stray file path.
            if (File.Exists(r))
            {
                long fsize = 0;
                try { fsize = new FileInfo(r).Length; } catch { }
                yield return (r, fsize);
            }
            else if (visited.Add(r))
            {
                stack.Push(r);
            }
        }

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> subdirs = Array.Empty<string>();
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { /* unreadable — skip */ }
            foreach (var sd in subdirs)
            {
                try
                {
                    if ((File.GetAttributes(sd) & FileAttributes.ReparsePoint) != 0)
                        continue; // don't follow junctions/symlinks
                }
                catch { continue; }
                if (visited.Add(sd))
                    stack.Push(sd);
            }

            IEnumerable<string> files = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir); }
            catch { /* unreadable — skip */ }
            foreach (var f in files)
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                yield return (f, size);
            }
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
                        MaxWaitSeconds = src.JobOptions.Schedule.MaxWaitSeconds,
                        PollIntervalSeconds = src.JobOptions.Schedule.PollIntervalSeconds,
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

            // Remove the row explicitly rather than leaning on LoadBackupSetsAsync's
            // reconcile. That reconcile deliberately KEEPS any row whose IsRunning
            // flag is set (to shield a concurrently-running set from an incidental
            // reload), and IsRunning stays true after a backup completes until the
            // finished progress panel is dismissed. So a set deleted while its row
            // still shows that panel would survive the reconcile forever as a ghost
            // — present in the list with no catalog record behind it. A set the user
            // explicitly deleted must always disappear, so drop its row here and
            // cancel anything still attached to it.
            var row = RowFor(backupSet.Id);
            if (row is not null)
            {
                row.ScanCts?.Cancel();
                if (row.Progress?.CancelCommand is ICommand cancel && cancel.CanExecute(null))
                    cancel.Execute(null);
                BackupSets.Remove(row);
            }

            if (SelectedBackupSet?.Id == backupSet.Id)
                SelectedBackupSet = null;

            // Refresh the rest (ordering, other sets) now that the row is gone.
            await LoadBackupSetsAsync();

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
            StagingMode = _settings.DiscStagingMode,
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
            StagingMode = _settings.DiscStagingMode,
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

    /// <summary>
    /// Plan-time disc-filesystem compatibility gate. Asks the orchestrator how many
    /// of the planned files would be auto-zipped to satisfy the selected format's
    /// name/path limits (the same check the burn applies under
    /// <see cref="ZipMode.IncompatibleOnly"/>). When a <em>significant</em> fraction
    /// would be zipped and the format isn't already UDF, warns the user and offers to
    /// switch this run to UDF (the most permissive format) so the content lands
    /// unzipped. Mutating <paramref name="job"/>'s <c>FilesystemType</c> is enough —
    /// the bin-packing is capacity-based and format-independent, so no re-plan/re-scan
    /// is needed; the burn reads <c>plan.Job.FilesystemType</c> at write time.
    /// </summary>
    /// <returns>
    /// <c>true</c> to proceed with the burn (either nothing significant to warn about,
    /// or the user chose to continue / switch to UDF); <c>false</c> if the user
    /// cancelled the backup from the warning.
    /// </returns>
    private bool WarnAndMaybeSwitchToUdf(
        BackupJob job, BackupPlan plan, string setName, Action stopScanProgress)
    {
        // The significance rules (which ZipMode/format qualify, and the 5%-files /
        // 5%-bytes / 20-files thresholds) live in DiscCompatibilityAdvisor so the
        // headless test harness exercises the exact same decision the user sees.
        var summary = _orchestrator.SummarizeCompatibility(plan, job.FilesystemType);
        if (!DiscCompatibilityAdvisor.ShouldWarn(job.ZipMode, job.FilesystemType, summary))
            return true;

        // Stop scan-progress callbacks from overwriting the dialog's status text.
        stopScanProgress();

        string fmt = job.FilesystemType == FilesystemType.ISO9660 ? "ISO 9660" : "Joliet";
        var message =
            $"{summary.IncompatibleFiles:N0} of {summary.TotalFiles:N0} files "
            + $"({FormatBytes(summary.IncompatibleBytes)}) have names or paths that are "
            + $"incompatible with the {fmt} disc format and will be individually zipped "
            + $"so they fit. Zipping changes how those files land on the disc.\n\n"
            + "Switch this backup to UDF instead? UDF is the most permissive format "
            + "(long Unicode paths, large files) and would let these files be written "
            + "as-is, without zipping.\n\n"
            + "\u2022 Yes \u2014 switch this run to UDF and burn the files unzipped\n"
            + $"\u2022 No \u2014 keep {fmt} and zip the incompatible files\n"
            + "\u2022 Cancel \u2014 don't start the backup";

        var result = MessageBox.Show(
            message,
            "Many files incompatible with the disc format",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        var choice = result switch
        {
            MessageBoxResult.Yes => UdfWarningChoice.SwitchToUdf,
            MessageBoxResult.No => UdfWarningChoice.KeepFormat,
            _ => UdfWarningChoice.Cancel,
        };

        bool proceed = DiscCompatibilityAdvisor.ApplyChoice(job, choice);
        if (choice == UdfWarningChoice.SwitchToUdf)
            StatusText = $"\"{setName}\": switched to UDF for this backup.";
        return proceed;
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
            StagingMode = _settings.DiscStagingMode,
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

                // Plan-time disc-format compatibility check: if a significant fraction
                // of the planned files would be silently auto-zipped to satisfy the
                // selected filesystem's name/path limits, warn the user up front and
                // offer to switch this run to UDF (the most permissive format) so the
                // content lands unzipped. Only meaningful under ZipMode.IncompatibleOnly.
                if (!WarnAndMaybeSwitchToUdf(job, plan, backupSet.Name, () => scanning = false))
                {
                    // User cancelled from the compatibility warning.
                    row.Progress = null;
                    row.IsRunning = false;
                    row.LastResultText = "Backup cancelled.";
                    StatusText = $"\"{backupSet.Name}\": backup cancelled.";
                    return;
                }
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
            vm.ScheduleDailyHour = sched.DailyHour.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            vm.ScheduleDailyMinute = sched.DailyMinute.ToString(
                "D2", System.Globalization.CultureInfo.InvariantCulture);
            vm.ScheduleDebounceSeconds = sched.DebounceSeconds.ToString();
            vm.ScheduleMaxWaitSeconds = sched.MaxWaitSeconds.ToString();
            vm.SchedulePollSeconds = sched.PollIntervalSeconds.ToString();
        }
    }

    /// <summary>
    /// Write the current source selection settings back into the backup set's
    /// <see cref="JobOptions"/> so they survive a dialog close without Plan.
    /// This is the inverse of <see cref="RestoreSourceSettings"/>.
    /// </summary>
    /// <summary>
    /// Produce a string snapshot of the editor's <em>settings</em> — the set name
    /// and everything that maps into <see cref="JobOptions"/> (target, disc/dir
    /// options, exclusions, tier sets, schedule) — but deliberately NOT the source
    /// selection tree.  Two calls compare equal iff no setting changed, so the close
    /// prompt can detect real setting edits without trusting the racy event-based
    /// dirty flag (which fires on programmatic UI churn).
    /// </summary>
    /// <remarks>
    /// Selection changes are tracked separately and far more cheaply by
    /// <see cref="SourceSelectionViewModel.ChangedSelectionPaths"/> (populated only
    /// by genuine user checkbox/auto-include toggles — programmatic restore writes
    /// the backing fields directly), so this snapshot avoids the expensive
    /// whole-tree <c>GetSelections()</c> walk on the dialog-close path.  It builds a
    /// throwaway clone of the set's <see cref="JobOptions"/> so the live set is
    /// never mutated.
    /// </remarks>
    private string SnapshotEditorSettings(BackupSet backupSet, SourceSelectionViewModel vm)
    {
        var opts = backupSet.JobOptions is null
            ? new JobOptions()
            : JsonSerializer.Deserialize<JobOptions>(
                JsonSerializer.Serialize(backupSet.JobOptions, _jsonOptions),
                _jsonOptions) ?? new JobOptions();
        ApplyVmSettingsToJobOptions(opts, vm);
        return JsonSerializer.Serialize(new { Name = vm.SetName, JobOptions = opts }, _jsonOptions);
    }

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

        ApplyVmSettingsToJobOptions(opts, vm);
        backupSet.JobOptions = opts;
    }

    /// <summary>
    /// Populate a <see cref="JobOptions"/> from the editor VM's settings fields —
    /// everything except the name and source roots (which are set/derived by the
    /// caller).  Shared by <see cref="SyncSettingsToJobOptions"/> (the real save)
    /// and <see cref="SnapshotEditorSettings"/> (the close-prompt dirtiness check),
    /// so the two can never drift out of agreement on what counts as a setting.
    /// </summary>
    private static void ApplyVmSettingsToJobOptions(JobOptions opts, SourceSelectionViewModel vm)
    {
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
                DailyHour = int.TryParse(vm.ScheduleDailyHour, out var dh)
                    ? Math.Clamp(dh, 0, 23) : 2,
                DailyMinute = int.TryParse(vm.ScheduleDailyMinute, out var dm)
                    ? Math.Clamp(dm, 0, 59) : 0,
                DebounceSeconds = int.TryParse(vm.ScheduleDebounceSeconds, out var s) ? s : 60,
                MaxWaitSeconds = int.TryParse(vm.ScheduleMaxWaitSeconds, out var mw) && mw > 0 ? mw : 300,
                PollIntervalSeconds = int.TryParse(vm.SchedulePollSeconds, out var p) && p > 0 ? p : 30,
            };
        }
        else if (opts.Schedule is not null)
        {
            opts.Schedule.Enabled = false;
        }
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
            progressVm.CompleteBurn(false, "Cancelled by user.", cancelled: true);
            StatusText = "Backup cancelled.";
            // A user abort with no failed files leaves nothing to review, so
            // finalize straight to the row's persistent result line instead of
            // parking a Dismiss button over an empty panel. If files did fail
            // before the abort, keep the panel up so that list stays available.
            if (!progressVm.HasFailedFiles)
                progressVm.RequestDone();
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
            progressVm.CompleteBurn(false, "Cancelled by user.", cancelled: true);
            StatusText = "Directory backup cancelled.";
            // A user abort with no failed files leaves nothing to review, so
            // finalize straight to the row's persistent result line instead of
            // parking a Dismiss button over an empty panel. If files did fail
            // before the abort, keep the panel up so that list stays available.
            if (!progressVm.HasFailedFiles)
                progressVm.RequestDone();
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

    private void StartOrphanedDirsFlow()
    {
        if (SelectedBackupSet is null)
            return;

        _ = StartOrphanedDirsFlowAsync(SelectedBackupSet.Id);
    }

    private async Task StartOrphanedDirsFlowAsync(int setId)
    {
        // CRITICAL: classify against a FRESH copy of the set reloaded from the
        // catalog, never the live in-memory SelectedBackupSet.  The in-memory
        // copy's SourceSelections can be stale (the worker persists auto-include
        // materialisations out-of-process) or reflect a partial/mid-edit tree.
        // Cleanup decides what to purge by asking whether each catalogued file is
        // still covered by the selection tree (IsDirectoryInSources); handing it a
        // selection tree that is missing branches makes it classify *in-scope*
        // files as "removed from sources" and delete live backups.  That is
        // exactly how a whole drive's backup ("it forgot my C: drive is backed
        // up") got wrongly purged — see the cleanup-mass-purge entry in
        // known-issues.md.  Reloading fresh guarantees the authoritative,
        // fully-persisted tree drives the classification.
        BackupSet? fresh;
        try
        {
            fresh = await _catalog.GetBackupSetAsync(setId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't load the backup set for cleanup:\n\n{ex.Message}",
                "Cleanup", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (fresh is null)
        {
            MessageBox.Show(
                "The backup set could not be found in the catalog.",
                "Cleanup", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // The catalog load + classification can take several seconds on large
        // backup sets, but it runs on a background thread inside the view model
        // (see OrphanedDirectoriesViewModel.LoadAsync).  The view is switched in
        // below and surfaces its own live progress via SummaryText ("Loading
        // catalogue...", then a running record count), so the app stays fully
        // responsive throughout.  We deliberately do NOT set a global wait cursor
        // here: it would falsely signal "busy/unresponsive", and because the load
        // outlives this method it would also linger as a busy pointer if the user
        // navigated away before the load finished.
        var vm = new OrphanedDirectoriesViewModel(_catalog, fresh);
        vm.DoneRequested += GoHome;
        CurrentView = vm;
        StatusText = "Review files and directories that can be cleaned up.";
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

        // Estimator for the opt-in dedup-aware "actual size" pass. The block
        // engine is stateless (it hashes against the destination's _blocks store
        // passed per call), and the file hash cache lets unchanged files skip a
        // re-read on repeat estimates.
        var estimator = new Services.DedupSizeEstimator(
            _catalog,
            new LithicBackup.Infrastructure.Deduplication.BlockDeduplicationEngine(),
            _fileHashCache);

        var vm = new BackupCoverageViewModel(_catalog, _scanner, SelectedBackupSet, estimator);
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
    // Flow 6c: Test Disc (integrity-test one optical disc + re-burn repairs)
    // -------------------------------------------------------------------

    private void StartTestDiscFlow()
    {
        if (SelectedBackupSet is null) return;

        var vm = new TestDiscViewModel(
            _catalog, _restoreService, _orchestrator, _burner, SelectedBackupSet);
        vm.DoneRequested += GoHome;

        CurrentView = vm;
        StatusText = $"Test a backup disc from \"{SelectedBackupSet.Name}\".";
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

        // Self-heal from a transient pending state.  START_PENDING/STOP_PENDING
        // disable every service button (none of the Can* gates accept a pending
        // state), and nothing else re-queries the SCM on its own — so a status
        // read that happens to land on a pending transition (e.g. right after an
        // install/reinstall, or a stale snapshot inherited at startup) would
        // otherwise leave the panel stuck on "starting..."/"stopping..." with all
        // buttons greyed until the user restarts the app.  Kick off a background
        // poll (unless one is already running, including an action's
        // WaitForServiceReadyAsync) that keeps refreshing until the state
        // settles.
        if (ServiceStatus is ServiceState.StartPending or ServiceState.StopPending
            && !_servicePollActive)
            _ = PollWhileServicePendingAsync();
    }

    /// <summary>
    /// Background watchdog that re-queries the service every second while it is
    /// in a transient pending state, so the panel recovers on its own once the
    /// transition finishes (or the generous timeout expires).  Guarded by
    /// <see cref="_servicePollActive"/> so only one loop runs at a time and the
    /// per-iteration <see cref="RefreshServiceStatus"/> call can't re-spawn it.
    /// </summary>
    private async Task PollWhileServicePendingAsync(int timeoutMs = 60000)
    {
        _servicePollActive = true;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(1000);
                RefreshServiceStatus();
                if (ServiceStatus is not ServiceState.StartPending
                    and not ServiceState.StopPending)
                    return;
            }
        }
        finally
        {
            _servicePollActive = false;
        }
    }

    /// <summary>
    /// Poll until the service leaves a pending state or the timeout expires.
    /// Keeps the UI updated while waiting.  Holds the poll guard so it doesn't
    /// race the background watchdog; if it times out while still pending, the
    /// final refresh (guard released) hands off to that watchdog.
    /// </summary>
    private async Task WaitForServiceReadyAsync(int timeoutMs = 5000)
    {
        _servicePollActive = true;
        try
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
        }
        finally
        {
            _servicePollActive = false;
        }
        // Timed out.  Refresh once more with the guard released so that, if the
        // service is still mid-transition, the self-healing watchdog takes over.
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
    // In-app update check (GitHub Releases)
    // -------------------------------------------------------------------

    /// <summary>The running assembly version, coerced to at least x.y.z.</summary>
    private static Version CurrentAppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
            ?? new Version(0, 0, 0);

    /// <summary>
    /// Checks GitHub for a newer release. A background startup check
    /// (<paramref name="userInitiated"/> = false) stays completely silent unless
    /// a new, non-dismissed version is found; a user-initiated check always
    /// reports the outcome (up to date / error) via a message box.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_isCheckingForUpdates) return;
        _isCheckingForUpdates = true;
        CommandManager.InvalidateRequerySuggested();
        if (userInitiated) StatusText = "Checking for updates...";

        try
        {
            var result = await UpdateService.CheckForUpdateAsync(CurrentAppVersion);

            if (result.IsUpdateAvailable && result.Update is { } update)
            {
                // Respect a prior dismissal of this exact version for the silent
                // startup check; an explicit check always shows it.
                if (!userInitiated &&
                    string.Equals(_settings.DismissedUpdateVersion, update.TagName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                AvailableUpdate = update;
                UpdateBannerText =
                    $"Lithic Backup {update.Version} is available " +
                    $"(you have {CurrentAppVersion.ToString(3)}).";
                UpdateBannerVisible = true;
                if (userInitiated) StatusText = "An update is available.";
            }
            else if (result.Failed)
            {
                if (userInitiated)
                {
                    StatusText = "Update check failed.";
                    MessageBox.Show(
                        $"Couldn't check for updates:\n\n{result.Error}",
                        "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                // Up to date.
                if (userInitiated)
                {
                    StatusText = "You're on the latest version.";
                    MessageBox.Show(
                        $"You're running the latest version ({CurrentAppVersion.ToString(3)}).",
                        "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Downloads the release installer (the bare <c>.msi</c>; a legacy <c>.exe</c>
    /// bundle is accepted only as a fallback) and launches it, then shuts this app
    /// down so the installer can replace the running files. Closing the GUI here
    /// means the upgrade never trips the file-in-use check on our own executables;
    /// the MSI's <c>SignalLithicGuiShutdown</c> custom action covers the manual
    /// (double-click-the-MSI-while-running) path too. If the release has no
    /// installer asset, falls back to opening the release page in the browser.
    /// </summary>
    private async Task DownloadUpdateAsync()
    {
        if (AvailableUpdate is not { } update) return;

        if (string.IsNullOrEmpty(update.InstallerDownloadUrl))
        {
            ViewReleaseNotes();
            return;
        }

        try
        {
            StatusText = $"Downloading Lithic Backup {update.Version}...";
            var installerPath = await UpdateService.DownloadInstallerAsync(update);

            StatusText = "Launching installer...";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
            {
                UseShellExecute = true
            });

            // Close the app so the installer can overwrite the running executables.
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText = "Update download failed.";
            MessageBox.Show(
                $"Couldn't download or launch the installer:\n\n{ex.Message}\n\n" +
                "You can download it manually from the release page.",
                "Download Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            ViewReleaseNotes();
        }
    }

    /// <summary>Opens the GitHub release page in the default browser.</summary>
    private void ViewReleaseNotes()
    {
        if (AvailableUpdate is not { } update) return;
        var url = !string.IsNullOrEmpty(update.ReleasePageUrl)
            ? update.ReleasePageUrl
            : $"https://github.com/inhahe/Lithic/releases";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch { /* best-effort; nothing actionable if the browser won't open */ }
    }

    /// <summary>
    /// Hides the banner and remembers this version so the silent startup check
    /// won't nag about it again (a newer release supersedes the dismissal).
    /// </summary>
    private void DismissUpdate()
    {
        if (AvailableUpdate is { } update)
        {
            _settings.DismissedUpdateVersion = update.TagName;
            _settings.Save();
        }
        UpdateBannerVisible = false;
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

    /// <summary>
    /// Apply the latest destination free-space snapshot from the
    /// <c>DestinationSpaceMonitor</c> to the loaded rows: flag each set whose
    /// destination is full and clear the flag on any that recovered (or whose
    /// destination is absent from the snapshot). Must be called on the UI thread.
    /// </summary>
    public void ApplyDestinationSpaceStatus(
        IReadOnlyDictionary<int, Services.DestinationSpaceStatus> statuses)
    {
        foreach (var row in BackupSets)
        {
            if (statuses.TryGetValue(row.Id, out var st) && st.IsFull)
            {
                row.IsDestinationFull = true;
                row.DestinationFullText =
                    $"Destination drive {st.Root} is full ({st.FreeSpaceText} free)";
            }
            else
            {
                row.IsDestinationFull = false;
                row.DestinationFullText = "";
            }
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
