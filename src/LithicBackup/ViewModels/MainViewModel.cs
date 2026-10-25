using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Burning;
using LithicBackup.Infrastructure.Diagnostics;
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

    /// <summary>The backup settings every account shares, read again whenever they
    /// are saved (by this app or another account's) and kept while unreadable.</summary>
    private readonly MachineSettingsReader _machineSettings = new();

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

    /// <summary>
    /// Opens and tracks the non-modal task windows (Cleanup, Restore, Verify,
    /// ...). Each task owns a window, so nothing in the main window can discard
    /// work in progress - which is exactly what used to happen when a backup
    /// starting reset CurrentView out from under a running Cleanup scan.
    /// </summary>
    private readonly Services.TaskWindowManager _taskWindows;
    private Window? _largestFilesWindow;
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

        // The row VMs are the authority on whether a set is mid-run, and the
        // main window is resolved lazily because it does not exist yet here.
        _taskWindows = new Services.TaskWindowManager(
            describeRunningWork: setId => RowFor(setId) is { IsRunning: true } r
                ? r.BusyActivity is { } activity
                    ? $"this set is {activity} right now, so its catalog is actively being written"
                    : "a backup of this set is running right now, so it is actively "
                      + "writing the catalog and the destination"
                : null,
            mainWindow: () => Application.Current?.MainWindow);

        BackupSets = [];
        // While a row is working - a backup, a seed, clearing its history, a
        // cleanup - the Worker gives way to it (ICatalogRepository.HoldSetInApp).
        BackupSets.CollectionChanged += OnBackupSetRowsChanged;

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
        // Not while the set is backing up in this window: the export reads the
        // set's database, and a read here shares the backup's connection.
        ExportBackupSetCommand = new RelayCommand(
            _ => _ = ExportBackupSetAsync(),
            _ => SelectedBackupSet is not null && SelectedRow is not { IsRunning: true } and not { IsExporting: true });
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
            o => o is BackupSetRowViewModel r && !r.IsRunning && !r.IsExporting);
        SetDeleteCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) _ = DeleteBackupSetAsync(r.Model); },
            o => o is BackupSetRowViewModel r && !r.IsRunning && !r.IsExporting);
        SetResumeAutomationCommand = new RelayCommand(
            o => { if (o is BackupSetRowViewModel r) _ = ResumeAutomatedBackupsAsync(r.Model); },
            o => o is BackupSetRowViewModel { IsAutomationHeld: true });
        SetStopBackgroundWorkCommand = new RelayCommand(
            o => (o as BackupSetRowViewModel)?.StopBackgroundWork?.Invoke(),
            o => o is BackupSetRowViewModel { StopBackgroundWork: not null });
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

    /// <summary>Release a set's <see cref="JobOptions.HoldAutomatedBackups"/> without running a backup.</summary>
    public ICommand SetResumeAutomationCommand { get; }

    /// <summary>Stop the read-only work shown on a row's <see cref="BackupSetRowViewModel.BackgroundWorkText"/> line.</summary>
    public ICommand SetStopBackgroundWorkCommand { get; }

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

    /// <summary>
    /// Overwrite <paramref name="target"/>'s persisted fields with
    /// <paramref name="source"/>'s, keeping the <paramref name="target"/> instance.
    /// Every field the catalog stores is copied, so a value changed underneath
    /// this window cannot survive in memory to be saved back later.
    /// </summary>
    private static void CopyPersistedFields(BackupSet source, BackupSet target)
    {
        target.Name = source.Name;
        target.SourceRoots = source.SourceRoots;
        target.MaxIncrementalDiscs = source.MaxIncrementalDiscs;
        target.DefaultMediaType = source.DefaultMediaType;
        target.DefaultFilesystemType = source.DefaultFilesystemType;
        target.CapacityOverrideBytes = source.CapacityOverrideBytes;
        target.SourceSelections = source.SourceSelections;
        target.JobOptions = source.JobOptions;
        target.CreatedUtc = source.CreatedUtc;
        target.LastBackupUtc = source.LastBackupUtc;
    }

    /// <summary>
    /// A deep, independent copy of <paramref name="set"/>: no list or object is
    /// shared with the original, so editing the copy cannot change the original.
    /// Round-trips through System.Text.Json with default options - exactly how the
    /// catalog stores the nested fields - so the copy is as faithful as a save and
    /// a reload.
    /// </summary>
    internal static BackupSet CloneBackupSet(BackupSet set)
        => JsonSerializer.Deserialize<BackupSet>(JsonSerializer.SerializeToUtf8Bytes(set))
           ?? throw new InvalidOperationException("A backup set could not be copied.");

    /// <summary>
    /// Re-read <paramref name="set"/> from the catalog into the SAME instance, so
    /// every holder of that reference sees current values. Anything that writes a
    /// set from memory must do this first: a copy read an hour ago otherwise puts
    /// back whatever has changed since (see the selection-widening entry in
    /// known-issues.md). A failed read is reported, not thrown, and the caller
    /// carries on with the copy it has.
    /// </summary>
    private async Task RefreshFromCatalogAsync(BackupSet set, string beforeWhat)
    {
        try
        {
            var fresh = await _catalog.GetBackupSetAsync(set.Id);
            if (fresh is not null)
                CopyPersistedFields(fresh, set);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not refresh \"{set.Name}\" from the catalog "
                         + $"before {beforeWhat}: {ex.Message}";
        }
    }

    /// <summary>
    /// Apply one Largest Files include/exclude toggle to the editor's exclusion
    /// list (one pattern per line, as the editor shows it). Returns the text
    /// unchanged when the toggle changes nothing, so it raises no edit.
    /// </summary>
    internal static string ApplyExclusionToggle(string patternsText, string pattern, bool excluded)
    {
        var list = BackupJobViewModel.ParseExclusionPatterns(patternsText);
        bool present = list.Contains(pattern, StringComparer.OrdinalIgnoreCase);

        if (excluded == present)
            return patternsText;

        if (excluded)
            list.Add(pattern);
        else
            list.RemoveAll(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase));

        return BackupJobViewModel.FormatExclusionPatterns(list);
    }

    private async void StartEditFlow()
    {
        if (SelectedBackupSet is null) return;

        // The set as the rest of the app holds it: the row, SelectedBackupSet, any
        // window opened on it. The editor never edits this instance.
        var shared = SelectedBackupSet;

        // Edit what the catalog says NOW, not what this window last read. The
        // editor used to edit the in-memory copy as-is, so anything that changed
        // the catalog underneath - the Worker pinning a new folder, a repair done
        // by hand - was displayed stale and then written straight back over. That
        // is how a corrected source selection reverted within minutes.
        await RefreshFromCatalogAsync(shared, "editing");

        // ...and edit a PRIVATE copy of it. Nothing done in the editor is saved
        // until the user clicks Save, or answers Yes when closing, and Cancel has
        // to be able to throw all of it away. Edits made on the shared instance
        // would outlive a Cancel in memory - the row, and every later write made
        // from SelectedBackupSet, would still carry them. SaveAllAsync copies what
        // it saved back into `shared`.
        var backupSet = CloneBackupSet(shared);

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

        // Set once the editor window has closed. Work it started (a seed) can
        // still be running, and must not touch the closed editor's tree.
        bool editorClosed = false;

        // Exactly what the last save wrote, frozen. The post-close reconcile
        // compares against this rather than backupSet, which the live size check
        // keeps syncing with whatever is on screen - saved or not.
        BackupSet? savedState = null;

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

        // Same idea for edits to entries whose folder no longer exists ("not found"
        // rows, Forget), which are counted apart from ChangedSelectionPaths so the
        // reconcile never offers to purge a deleted folder's backups.
        int cleanMissingEditMark = 0;

        // Completes once the deferred selection restore (Phase 3, below) has
        // finished.  The restore runs asynchronously AFTER the dialog is shown,
        // during which GetSelections() would return a partial/empty tree.  Every
        // path that reads the selection to save it (SaveAllAsync — used by the
        // Save button, Yes at the close prompt and saving before a seed) or to
        // hand it on (Largest Files) awaits this first, so a fast click can never
        // write a half-restored tree over the real saved sources.  It is completed
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

        // The retention settings as the editor will write them, before any edit:
        // after a save, a different signature offers to remove the versions the
        // new settings no longer keep. Taken through the editor, not from the
        // stored options, because a save writes the tier sets from the editor: a
        // round trip that changed nothing must compare equal.
        string retentionBefore = RetentionSignature(EditorJobOptions(backupSet, sourceSelection));

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
            cleanMissingEditMark = sourceSelection.MissingFolderEditCount;
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

        // The one way the editor writes the set. Called by the Save button, by Yes
        // at the close prompt, and by "save first" before a seed - never on its own.
        async Task SaveAllAsync()
        {
            // Never read GetSelections() from a still-restoring tree — wait for
            // the deferred restore to finish so we persist the real selection,
            // not a partial/empty snapshot.  Completes instantly once restore is
            // done (or immediately for new sets).
            await selectionRestored.Task;

            // Nor from a half-propagated one: drain any deferred checkbox
            // propagation and in-flight node work first. The Save button already
            // does this before it gets here; the other two callers do not.
            await sourceSelection.FlushPendingEditsAsync();

            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();
            await Task.Run(async () =>
            {
                await CarryStoredHoldAsync(backupSet);
                await _catalog.UpdateBackupSetAsync(backupSet);
            });
            AfterSave();
        }

        // The automated-backup hold is not an editor setting (seeding, Clear
        // Backup History, a completed backup and the row's Resume link write it
        // straight to the catalog), so a save writes back whatever the catalog
        // holds now. Writing the editor's copy instead re-paused a set whose hold
        // was released while the editor was open, and could release one that was
        // set meanwhile.
        async Task CarryStoredHoldAsync(BackupSet editorCopy)
        {
            if (editorCopy.JobOptions is null)
                return;
            var stored = await _catalog.GetBackupSetAsync(editorCopy.Id);
            editorCopy.JobOptions.HoldAutomatedBackups = stored?.JobOptions?.HoldAutomatedBackups ?? false;
        }

        // Yes at the close prompt during File > Exit. The process will not wait for
        // an async save to finish, and with nothing auto-saved any more, that
        // would lose the whole edit. So write before returning - the UI-thread part
        // first, then the row update, waited for off the UI thread's context.
        void SaveBeforeExit()
        {
            // Nothing restored yet means nothing the user could have changed.
            if (!selectionRestored.Task.IsCompleted)
                return;

            // Drains the checkbox settle synchronously; an in-flight folder load
            // cannot be waited for here, and is the one thing this may miss.
            _ = sourceSelection.FlushPendingEditsAsync();

            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();
            Task.Run(async () =>
            {
                await CarryStoredHoldAsync(backupSet);
                await _catalog.UpdateBackupSetAsync(backupSet);
            }).GetAwaiter().GetResult();
            AfterSave();
        }

        // Before a save that moves the set's backup folder (its destination,
        // "Create subdirectory" or the subfolder's name) while some of its files
        // are recorded outside the new one: nothing moves them, so from then on
        // the set would be refused - its backups held, nothing deleted from it -
        // until they are in one folder again. Asked here, where the user can still
        // back out, rather than learnt at the next backup. Not for the save at
        // File > Exit, which cannot ask; the refusal covers it.
        //
        // The question, or null when there is none to ask. The editor's fields are
        // read on the UI thread, the catalog off it.
        async Task<string?> BackupFolderMoveQuestionAsync()
        {
            string? newRoot = EditorJobOptions(backupSet, sourceSelection).BackupRoot();
            if (newRoot is null)
                return null;
            string? elsewhere = await Task.Run(async () =>
            {
                string? oldRoot = (await _catalog.GetBackupSetAsync(backupSet.Id))?.JobOptions?.BackupRoot();
                if (oldRoot is null || JobOptions.SameFolderOnAnyDrive(oldRoot, newRoot))
                    return null;
                return await _catalog.FindLiveRecordsOutsideAsync(backupSet.Id, newRoot);
            });
            return elsewhere is null ? null
                : $"Some of this set's backed-up files are recorded in\n    {elsewhere}\n"
                  + $"and this change makes its backup folder\n    {newRoot}\n\n"
                  + "LithicBackup does not move backed-up files. Until they are all in one folder again, the set "
                  + "will not be backed up and nothing will be deleted from it.\n\n"
                  + "If you moved the backup to another drive or folder, use Change Destination on the set's "
                  + "right-click menu instead, which records where its files now are. To start a fresh backup "
                  + "in the new folder, clear the set's backup history after saving.\n\n"
                  + "Save this change anyway?";
        }

        // Ask it, on the UI thread. True to go ahead with the save.
        static bool ConfirmBackupFolderMove(string? question)
            => question is null
               || MessageBox.Show(question, "Change Backup Folder", MessageBoxButton.YesNo,
                      MessageBoxImage.Warning) == MessageBoxResult.Yes;

        void AfterSave()
        {
            savedThisSession = true;
            savedState = CloneBackupSet(backupSet);

            // Let the rest of the app see what is now saved; the editor's own copy
            // stays private. Both the instance the editor was opened from and, if
            // the main window has since swapped in another copy of this set, that.
            CopyPersistedFields(CloneBackupSet(backupSet), shared);
            if (SelectedBackupSet is { } selected && selected.Id == backupSet.Id
                && !ReferenceEquals(selected, shared))
                CopyPersistedFields(CloneBackupSet(backupSet), selected);

            // The persisted state is now the new "clean" baseline, so a later
            // stray dirty event (or the still-populated ChangedSelectionPaths from
            // the toggles we just saved) followed by a close won't re-prompt to
            // save changes that are already on disk.
            settingsBaseline = SnapshotEditorSettings(backupSet, sourceSelection);
            cleanSelectionMark = sourceSelection.ChangedSelectionPaths.Count;
            cleanMissingEditMark = sourceSelection.MissingFolderEditCount;
            sourceSelection.MarkClean();
        }

        // ---------------------------------------------------------------
        // Phase 2: create dialog, set content, then show (invisible → reveal)
        // ---------------------------------------------------------------

        var dialog = new BackupSetEditorWindow
        {
            Owner = Application.Current.MainWindow,
            Title = $"Modify \u2014 {backupSet.Name}",
        };
        _editorWindow = dialog;

        // Closing the editor never saves by itself. How it closes decides:
        //   Save button   → SaveRequested has saved everything; closes silently.
        //   Cancel button → discards (after a confirm, if anything changed).
        //   ✕ / Alt+F4    → nothing changed: closes and writes nothing.
        //                   changed: "Save before closing?"
        //                     Yes    → save, then close
        //                     No     → close without saving (a new set's
        //                              temporary record is deleted)
        //                     Cancel → stay open
        // It used to save on EVERY close, prompt or no prompt, and to auto-save
        // each checkbox change 300 ms after it was made - so No and Cancel threw
        // away nothing but settings, and a stray click reached the Worker.

        // Set by the Cancel button: close and throw the edits away, no prompt.
        bool discardRequested = false;

        // Set by Yes at the close prompt: the Closed handler saves.
        bool saveOnClose = false;

        // Whether anything genuinely changed since the last save. The event-based
        // flag never misses a real change but fires on programmatic churn, so it is
        // confirmed with the two precise, cheap signals: a genuine selection toggle
        // (ChangedSelectionPaths grew past the last clean mark) or a genuine setting
        // edit (the settings snapshot differs from the baseline). Neither walks the
        // whole selection tree, so this stays fast on huge sets. New sets have no
        // baseline and always count as changed: there is an unsaved record either way.
        bool HasRealUnsavedChanges()
        {
            if (!sourceSelection.HasUnsavedChanges)
                return false;
            if (_unsavedNewSetId is not null || settingsBaseline is null)
                return true;
            bool selectionChanged = sourceSelection.ChangedSelectionPaths.Count > cleanSelectionMark
                                    || sourceSelection.MissingFolderEditCount > cleanMissingEditMark;
            bool settingsChanged = SnapshotEditorSettings(backupSet, sourceSelection) != settingsBaseline;
            return selectionChanged || settingsChanged;
        }

        dialog.Closing += (_, e) =>
        {
            // A forced shutdown (the upgrade installer's signal, or Windows ending
            // the session) must not be held up by a modal prompt - the installer
            // waits only a bounded time for LithicBackup.exe to exit. Nor may it
            // save edits nobody confirmed, so they are dropped, exactly as for No.
            // (A new set's temporary record is deleted by the Closed handler.)
            if (Application.Current is App { IsForcedShutdown: true })
                return;

            // Cancel button: it has already confirmed, if there was anything to lose.
            if (discardRequested)
                return;

            // Nothing to save - including right after the Save button, which saved
            // and refreshed the baseline before calling Close().
            if (!HasRealUnsavedChanges())
                return;

            bool isNewSet = _unsavedNewSetId is not null;
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
                if (Application.Current is App { IsExiting: true })
                {
                    // File > Exit: the Closed handler saves at once (SaveBeforeExit),
                    // which leaves no time to ask anything more. For a new set,
                    // clear the "unsaved new" marker so it is kept, not deleted.
                    saveOnClose = true;
                    _unsavedNewSetId = null;
                }
                else
                {
                    // Otherwise exactly what the Save button does - asking first
                    // if the save would move the backup folder - and closed by it,
                    // once Closing has returned: that asks the catalog, which is not
                    // to be waited for here on the UI thread. A refusal or a failed
                    // save leaves the editor open with the edits in it.
                    e.Cancel = true;
                    dialog.Dispatcher.BeginInvoke(new Func<Task>(SaveAndCloseAsync));
                }
            }
            // No: close without saving. A new set keeps _unsavedNewSetId, so the
            // Closed handler deletes its temporary record.
        };

        dialog.Closed += async (_, _) =>
        {
            editorClosed = true;

            // Stop any background PlanAsync scan.
            autoCheckCts?.Cancel();

            if (_unsavedNewSetId is int unsavedId)
            {
                // A new set that was never saved - Cancel, No at the prompt, or a
                // forced shutdown. Delete the temporary record so no orphan
                // appears in the catalog.
                _unsavedNewSetId = null;
                try { await _catalog.DeleteBackupSetAsync(unsavedId); }
                catch { /* best effort */ }
            }
            else if (saveOnClose)
            {
                // Yes at the close prompt. Guard the save: this is an async-void
                // event handler, so an unhandled exception here would tear down
                // the app. On failure savedThisSession stays false, so the
                // reconcile below is correctly skipped.
                try
                {
                    if (Application.Current is App { IsExiting: true })
                        SaveBeforeExit();
                    else
                        await SaveAllAsync();
                }
                catch (Exception ex)
                {
                    StatusText = $"Failed to save on close: {ex.Message}";
                }
            }

            // Stop this editor's directory-size scheduler - after the save above,
            // which still reads the tree. Nothing used to stop it, so its worker
            // carried on enumerating whole volumes against a closed window - one
            // was found still going after two days - while its closures kept the
            // entire node tree alive and every reopen added another scheduler
            // over the same cache file.
            sourceSelection.Dispose();

            _editorWindow = null;

            // Work this editor started outlives it, visibly. A seed or a history
            // clear already holds the set's row (BeginRowWork). A duplicate
            // analysis - read-only, so it blocks nothing - moves to a line of its
            // own on the row. (Calculate size stops with the window: its only
            // output is this editor's size column, and what it computed is cached.)
            var editedRow = RowFor(backupSet.Id);
            CarryDedupAnalysisToRow(sourceSelection, editedRow);
            if (editedRow?.BusyActivity is { } stillBusy)
                StatusText = $"\"{editedRow.Name}\" is still {stillBusy}; its progress is on its row in the list.";

            await LoadBackupSetsAsync();

            // If the edit actually saved, reconcile the destination with the new
            // source selection: offer to purge copies of removed folders and to
            // back up newly added ones.  Skipped entirely when nothing was saved.
            // Uses savedState - what was written - not backupSet, which can also
            // hold later edits that were then discarded.  Whether the reconcile
            // runs at all — and whether it prompts first — is governed inside
            // ReconcileDestinationAfterEditAsync by the ReconcileMode setting.
            // Not while a seed or history clear is still writing the catalog: the
            // purge it can offer marks records deleted under them, and a backup
            // of added folders cannot run until they finish. Folders added are
            // then picked up by the next backup, and copies of removed ones are
            // left for Cleanup.
            // Then, if the retention settings changed, offer to remove the
            // earlier versions they no longer keep - after the sources, whose
            // removal can take some of those versions with it.
            if (savedThisSession && savedState is not null)
            {
                if (RowFor(backupSet.Id)?.BusyActivity is { } busy)
                    StatusText = $"Saved \"{savedState.Name}\". It is still {busy}, so folders you added "
                                 + "will be backed up by its next backup, and backed-up copies of folders you "
                                 + "removed, and any earlier versions its retention settings no longer keep, "
                                 + "are left for Cleanup.";
                else
                {
                    await ReconcileDestinationAfterEditAsync(
                        savedState, originalSelections, sourceSelection.ChangedSelectionPaths);
                    await OfferToRemoveVersionsNoLongerKeptAsync(savedState, retentionBefore);
                }
            }
        };

        // Save button: persist everything, then close the editor — matching the
        // Settings dialog, where Save commits and dismisses in one action. Yes at
        // the close prompt does the same.
        sourceSelection.SaveRequested += SaveAndCloseAsync;
        async Task SaveAndCloseAsync()
        {
            try
            {
                if (!ConfirmBackupFolderMove(await BackupFolderMoveQuestionAsync()))
                {
                    sourceSelection.SaveStatusText = "Not saved";
                    return;
                }
                await SaveAllAsync();
                _unsavedNewSetId = null; // committed — don't delete on close
                StatusText = $"Backup set \"{sourceSelection.SetName}\" saved. {DateTime.Now:HH:mm:ss}";
                await LoadBackupSetsAsync();
                SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id)?.Model;

                // Close on save. SaveAllAsync just refreshed the dirty baseline, so
                // the Closing handler sees a clean state and won't re-prompt to save.
                dialog.Close();
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save: {ex.Message}";
                sourceSelection.SaveStatusText = "Save failed";
            }
        }

        // Cancel discards. It used to just call Close(), which asked "Save before
        // closing?" - an odd question for a button labelled Cancel, and moot for
        // the selection, which had already been auto-saved.
        sourceSelection.CancelRequested += () =>
        {
            if (HasRealUnsavedChanges())
            {
                var confirm = MessageBox.Show(
                    _unsavedNewSetId is not null
                        ? "Discard this new backup set?"
                        : "Discard your changes to this backup set?\n\n"
                          + "Nothing you changed since opening it (or since your "
                          + "last save) will be kept.",
                    "Discard Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            discardRequested = true;
            dialog.Close();
        };

        // "Seed from Existing Backup" button: imports files from an existing
        // mirror-format backup directory (e.g. backup4all mirror) into the
        // catalog so future incremental backups only copy new/changed files.
        sourceSelection.SeedFromExistingRequested += async () =>
        {
            // After the deferred restore, so the editor's state is the real one.
            await selectionRestored.Task;

            // Seeding records every file it finds as ALREADY BACKED UP, to the
            // destination shown in this editor, and writes those records to the
            // catalog at once - Cancel cannot take them back. Discarding an
            // unsaved destination afterwards would leave the catalog saying files
            // are backed up somewhere the saved set does not point, and the next
            // backup would skip them. So with unsaved changes, seeding asks to
            // save first. It used to save them without asking.
            if (HasRealUnsavedChanges())
            {
                var answer = MessageBox.Show(
                    (_unsavedNewSetId is not null
                        ? "This backup set hasn't been saved yet."
                        : "You have unsaved changes to this backup set.")
                    + "\n\nSeeding records the files it finds as already backed up "
                    + "to the destination shown here, and Cancel can't undo that, so "
                    + "the set has to be saved first.\n\nSave now and seed?",
                    "Save Before Seeding",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK)
                    return;

                try
                {
                    if (!ConfirmBackupFolderMove(await BackupFolderMoveQuestionAsync()))
                    {
                        sourceSelection.SeedResult = "Not saved, so nothing was imported.";
                        return;
                    }
                    await SaveAllAsync();
                    _unsavedNewSetId = null; // committed - Cancel must not delete it now
                }
                catch (Exception ex)
                {
                    sourceSelection.SeedResult = $"Save failed, so nothing was imported: {ex.Message}";
                    return;
                }
            }

            string? dir = JobOptions.BackupRootFor(sourceSelection.TargetDirectory,
                sourceSelection.CreateSubdirectory, sourceSelection.SubdirectoryName);

            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                sourceSelection.SeedResult = "Destination directory does not exist.";
                return;
            }

            // The editor is not modal: a backup can have been started on this
            // set's row since it opened, and seeding under a running backup would
            // record files that backup is in the middle of writing.
            var row = RowFor(backupSet.Id);
            if (row is { IsRunning: true })
            {
                sourceSelection.SeedResult = row.BusyActivity is { } busy
                    ? $"This set is {busy} already."
                    : "This set is backing up. Seed it when the backup has finished.";
                return;
            }

            // Hold automated backups until the user runs one. A full backup the
            // Worker started mid-seed would find every file the seed had not
            // reached yet missing from the catalog and copy it all again. The
            // editor's Save keeps the stored hold (CarryStoredHoldAsync).
            try
            {
                await SetAutomationHoldAsync(backupSet.Id, hold: true);
            }
            catch (Exception ex)
            {
                sourceSelection.SeedResult = $"Could not pause automated backups, so nothing was imported: {ex.Message}";
                return;
            }

            // Whether the set already chose its sources. After a seed, the folders
            // found in the backup are ticked so a set being CREATED from a mirror
            // shows what it covers - but ticking them in a set that already has a
            // selection widened it (C:\Users\me\Documents became all of C:\Users),
            // waiting for the next Save. Seeding is how an imported set catches up
            // with its destination, so that was the common case, not an edge one.
            bool hadSelection = backupSet.SourceSelections is { Count: > 0 } || backupSet.SourceRoots.Count > 0;

            // From here the seed holds the set's row the way a backup does: its
            // progress shows there, Abort stops it, and the set cannot be backed
            // up, modified, restored or deleted until it ends - whether or not this
            // editor is still open: a backup started mid-seed would copy every
            // file the seed had not reached yet. The editor's own Cancel stops it
            // too (the tokens are linked).
            using var rowCts = CancellationTokenSource.CreateLinkedTokenSource(
                sourceSelection.SeedCancellationToken);
            BeginRowWork(row, "seeding", $"Seeding from {dir}…", rowCts);

            long lastSeedUpdate = 0;
            var seedSw = System.Diagnostics.Stopwatch.StartNew();
            var scanProgress = new Progress<ScanProgress>(sp =>
            {
                long now = seedSw.ElapsedMilliseconds;
                if (now - lastSeedUpdate < ProgressCadence.IntervalMs)
                    return;
                lastSeedUpdate = now;

                string current = string.IsNullOrEmpty(sp.CurrentDirectory) ? ""
                    : $"\n{sp.CurrentDirectory}";
                string skipped = sp.FilesSkipped > 0
                    ? $", {sp.FilesSkipped:N0} already in catalog"
                    : "";
                sourceSelection.SeedResult =
                    $"Importing... {sp.FilesFound:N0} files and earlier versions ({FormatBytes(sp.TotalBytes)} bytes){skipped}{current}";
                if (row is not null && row.BusyActivity == "seeding")
                    row.RunningStatusText =
                        $"Seeding from {dir}: {sp.FilesFound:N0} files and earlier versions recorded{skipped}{current}";
            });

            bool skipHash = sourceSelection.SeedSkipHashing;
            DirectoryBackupService.SeedOutcome outcome;
            try
            {
                await WaitForBackgroundRunAsync(row, backupSet.Id, $"Seeding from {dir}", rowCts.Token);
                outcome = await Task.Run(() =>
                    _directoryBackupService.SeedFromExistingDirectoryAsync(
                        backupSet.Id, dir, scanProgress, rowCts.Token,
                        skipHashing: skipHash));
            }
            catch (OperationCanceledException)
            {
                // Whole paths were committed as it went, so nothing is half-done.
                EndRowWork(row,
                    "Seed stopped. What it had recorded is kept; seeding again carries on from there.",
                    isError: false);
                CrashLogger.Log(null, $"GUI: seeding \"{backupSet.Name}\" from {dir} was stopped.");
                await LoadBackupSetsAsync();
                throw; // the editor, if still open, says "Import cancelled."
            }
            catch (Exception ex)
            {
                EndRowWork(row, $"Seed failed: {ex.Message}", isError: true);
                CrashLogger.Log(ex, $"GUI: seeding \"{backupSet.Name}\" from {dir}");
                await LoadBackupSetsAsync();
                throw;
            }

            CrashLogger.Log(null,
                $"GUI: seeded \"{backupSet.Name}\" from {dir}: {outcome.Files:N0} file(s), "
                + $"{outcome.PreviousVersions:N0} earlier version(s) (of which {outcome.MovedToHistory:N0} older "
                + $"cop(ies) moved from beside a newer one into the history), {outcome.SkippedExisting:N0} already in "
                + $"the catalog, {outcome.DuplicateCopies:N0} stored cop(ies) left untracked, "
                + $"{outcome.Unrecognized:N0} unrecognised file(s) left alone.");

            bool imported = outcome.Files + outcome.PreviousVersions > 0;
            string result;
            if (imported)
            {
                var parts = new List<string>();
                if (outcome.PreviousVersions > 0)
                    parts.Add($"{outcome.PreviousVersions:N0} earlier versions");
                if (outcome.MovedToHistory > 0)
                    parts.Add($"{outcome.MovedToHistory:N0} older copies found beside a newer one were moved into "
                              + "the version history");
                if (outcome.SkippedExisting > 0)
                    parts.Add($"{outcome.SkippedExisting:N0} files already in the catalog were skipped");
                if (outcome.DuplicateCopies > 0)
                    parts.Add($"{outcome.DuplicateCopies:N0} stored copies could not be recorded and were left as "
                              + "they are; Cleanup's destination scan lists them as untracked");
                string detail = parts.Count > 0 ? $" ({string.Join("; ", parts)})" : "";
                result = $"Imported {outcome.Files:N0} files{detail}. Run a backup next: it copies only "
                         + "what changed, and resumes automated backups.";
            }
            else if (outcome.SkippedExisting > 0)
            {
                result = $"Nothing new to import — all {outcome.SkippedExisting:N0} files were already in the catalog.";
            }
            else
            {
                result = "No files found to import.";
            }

            // Release the row before anything else can fail.
            sourceSelection.SeedResult = result;
            EndRowWork(row, "Seeded: " + result, isError: false);

            // A set created from a mirror: tick the folders found in it so the
            // user sees what it covers. Never in a set that has its sources, and
            // not in an editor that has been closed meanwhile.
            if (imported && !hadSelection && !editorClosed)
            {
                try
                {
                    await ApplySeedSelectionsAsync(sourceSelection, dir!);
                    sourceSelection.HasSelection = true;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, $"GUI: ticking the seeded folders of \"{backupSet.Name}\"");
                }
            }

            await LoadBackupSetsAsync();
        };

        // "Clear Backup History" button: wipes the catalog record of what's
        // been backed up for this set (discs + file entries), keeping all
        // settings.  The next backup then treats every source file as new.
        sourceSelection.ClearHistoryRequested += async () =>
        {
            // Not under a running backup or seed (the editor is not modal): the
            // delete would race the records they are writing.
            var row = RowFor(backupSet.Id);
            if (row is { IsRunning: true })
            {
                sourceSelection.ClearHistoryResult = row.BusyActivity is { } busy
                    ? $"This set is {busy}. Clear its history when that has finished."
                    : "This set is backing up. Clear its history when the backup has finished.";
                return;
            }

            var confirm = MessageBox.Show(
                $"Clear the backup history for \"{sourceSelection.SetName}\"?\n\n" +
                "This deletes the catalog records of which files have been backed " +
                "up (disc entries and file records). Settings, sources, and schedule " +
                "are kept.\n\n" +
                "The next backup will treat every source file as new. Files already " +
                "written to the destination are not deleted.\n\n" +
                "Automated backups for this set pause until you run a backup yourself, " +
                "so you can seed it from the destination first.",
                "Clear Backup History",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            // Deleting millions of records takes a while; hold the row meanwhile,
            // as the seed does, so nothing can back the set up half-cleared.
            BeginRowWork(row, "clearing its history", "Clearing backup history…", cancel: null);
            try
            {
                await WaitForBackgroundRunAsync(row, backupSet.Id, "Clearing backup history");

                // Hold first: with an empty catalog, the Worker's next run would
                // be a full re-copy over the destination before the user had a
                // chance to seed it. (It would also be refused there now, but only
                // while the destination holds files at this set's paths.)
                await SetAutomationHoldAsync(backupSet.Id, hold: true);

                await Task.Run(() => _catalog.ClearBackupSetCatalogAsync(backupSet.Id));
                sourceSelection.ClearHistoryResult =
                    "Backup history cleared. The next backup will re-copy everything, unless you seed first.";
                StatusText = $"Cleared backup history for \"{sourceSelection.SetName}\".";
                EndRowWork(row, "Backup history cleared. Seed from the destination, or back up to copy everything again.",
                    isError: false);
                await LoadBackupSetsAsync();
                SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id)?.Model;
            }
            catch (Exception ex)
            {
                sourceSelection.ClearHistoryResult = $"Failed to clear history: {ex.Message}";
                StatusText = $"Failed to clear backup history: {ex.Message}";
                EndRowWork(row, $"Clearing the backup history failed: {ex.Message}", isError: true);
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

        // NOTHING is written to the catalog while the editor is open - see the
        // close rules above the Closing handler. This used to auto-save every
        // checkbox change 300 ms after it was made, which made Cancel and "No"
        // meaningless for the selection and let one stray click reach the Worker:
        // a click on the tick-box column's header while it is mixed selects every
        // drive in full. The live size estimate needs no write; it reads the
        // editor's in-memory copy.
        sourceSelection.SelectionChanged += () =>
        {
            // The tree is still being restored - nothing the user did.
            if (sourceSelection.IsApplyingSelections) return;

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

            // Hand Largest Files the editor's CURRENT state, in memory only: it
            // scans backupSet's selection and exclusions directly and never
            // re-reads the catalog. This used to save the whole set first, which
            // wrote the user's in-progress edits just for opening a window. Wait
            // for the deferred restore so it isn't handed a half-restored tree.
            await selectionRestored.Task;
            await sourceSelection.FlushPendingEditsAsync();
            SyncSettingsToJobOptions(backupSet, sourceSelection);
            backupSet.SourceSelections = sourceSelection.GetSelections();

            int estimatedCount = 0;
            try { estimatedCount = await _catalog.GetFileCountForBackupSetAsync(backupSet.Id); }
            catch { }

            // Opened from the editor, Largest Files is part of this editing session.
            // Each include/exclude toggle becomes an edit of the editor's own
            // exclusion list: shown there, counted as an unsaved change, saved by
            // Save and dropped by Cancel. Toggles used to be written to the catalog
            // on every click without ever reaching that list, so the editor's next
            // save put the old list back and the exclusions silently vanished.
            var vm = new LargestFilesViewModel(
                _scanner, _catalog, backupSet, estimatedCount, deferPersistence: true);
            vm.ExclusionToggled += (pattern, excluded) =>
            {
                sourceSelection.ExcludedExtensions = ApplyExclusionToggle(
                    sourceSelection.ExcludedExtensions, pattern, excluded);
                TriggerPlanReCheck();
            };

            var lfWindow = new BackupSetEditorWindow
            {
                Owner = dialog,
                Title = $"Largest Files \u2014 {backupSet.Name}",
                SizeToContent = SizeToContent.Manual,
                Height = 600,
            };

            _largestFilesWindow = lfWindow;

            vm.DoneRequested += () => lfWindow.Close();

            lfWindow.Closed += (_, _) =>
            {
                vm.CancelScan();
                _largestFilesWindow = null;
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
            // restore to finish.
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
                        sourceSelection.ChangedSelectionPaths.Count > cleanSelectionMark
                        || sourceSelection.MissingFolderEditCount > cleanMissingEditMark;
                    bool settingsChanged = refreshed != settingsBaseline;
                    if (!selectionChanged && !settingsChanged)
                    {
                        // No real edit — clear first-render churn and fold any benign
                        // settling into the clean baseline.
                        sourceSelection.MarkClean();
                        settingsBaseline = refreshed;
                        cleanSelectionMark = sourceSelection.ChangedSelectionPaths.Count;
                        cleanMissingEditMark = sourceSelection.MissingFolderEditCount;
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
                var catalogInfo = await Task.Run(async () =>
                {
                    try { return await _catalog.GetLatestVersionInfoAsync(backupSet.Id); }
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
        // added - a save that changed only settings. Skip all the catalog work
        // below in that case.
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
            async (progress, ct) =>
            {
                // Removed: currently-active catalog files whose source path was
                // covered before the edit but isn't covered any more.  Rather
                // than load the whole (potentially ~1M-row) file table, query
                // only the subtrees the user actually toggled this session: every
                // file whose inclusion changed sits at or under one of those
                // toggled nodes (a removal can only be caused by unchecking that
                // node or an ancestor of it), so scoping the reads to them is
                // both correct and far cheaper.
                var rem = await ComputeRemovedFilesTargetedAsync(
                    backupSet.Id, originalSelections, newSelections, changedPaths,
                    progress, ct);

                // Added: new covering entries not already covered by an old one.
                //
                // CollectCoveredEntries, NOT CollectSelectedRoots: the latter
                // coarsens a selected FILE to its containing directory, so one
                // ticked file at D:\registrybackup.reg produced the root "D:\",
                // whose minimal-root reduction then dropped every other D:\…
                // entry as already covered. The diff therefore reported ZERO
                // added roots no matter how many folders were ticked, and this
                // flow silently did nothing — observed with a 12 GB folder.
                var oldRoots = SourceSelection.CollectCoveredEntries(originalSelections);
                var newRoots = SourceSelection.CollectCoveredEntries(newSelections);
                var added = newRoots
                    .Where(r => !IsCoveredBy(oldRoots, r))
                    .ToList();

                return (Removed: rem, AddedRoots: added);
            },
            // Read-only: it inspects the catalog and the selection trees and
            // changes nothing, so there is no reason to disable every other
            // window while it runs.
            modal: false);

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

        // Say so when the scan found nothing. A toggle that nets out to no change
        // (untick a folder, tick it again before closing) legitimately produces
        // removed=0 and added=0, and staying silent after a visible scan is
        // indistinguishable from the app having ignored the edit -- which is
        // exactly how the genuine bug above was experienced.
        if (removed.Count == 0 && addedRoots.Count == 0)
            StatusText =
                $"No coverage changes to reconcile for \"{backupSet.Name}\" — "
                + "nothing was added or dropped.";
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
    private async Task<List<FileRecord>> ComputeRemovedFilesTargetedAsync(
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
            return await ComputeRemovedFilesFullAsync(backupSetId, originalSelections, newSelections, progress, ct);

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
        var cadence = new ProgressCadence();
        foreach (var path in roots)
        {
            ct.ThrowIfCancellationRequested();

            // Matches the path itself and every descendant, so it works whether
            // the toggled node was a file or a directory.
            var records = await _catalog.GetFileRecordsUnderDirectoryAsync(backupSetId, path, ct);

            foreach (var f in records)
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;

                if (progress is not null && cadence.IsDue())
                    progress.Report($"Checked {checkedCount:N0} files, {removed.Count:N0} to remove\u2026");

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
    /// Whole-table fallback for <see cref="ComputeRemovedFilesTargetedAsync"/> used
    /// when the change can't be localised (e.g. a bulk deselect-all).  Loads
    /// every catalog file and keeps those covered before the edit but not after.
    /// </summary>
    private async Task<List<FileRecord>> ComputeRemovedFilesFullAsync(
        int backupSetId,
        IReadOnlyList<Core.Models.SourceSelection> originalSelections,
        IReadOnlyList<Core.Models.SourceSelection> newSelections,
        IProgress<ProgressReport>? progress,
        CancellationToken ct)
    {
        progress?.Report("Loading backup catalog\u2026");
        var loading = new ProgressCadence();
        var all = await _catalog.GetAllFilesForBackupSetAsync(backupSetId, ct,
            progress is null ? null : new SyncProgress<int>(read =>
            {
                if (loading.IsDue())
                    progress.Report($"Loading backup catalog\u2026 {read:N0} records read");
            }));

        var removed = new List<FileRecord>();
        long checkedCount = 0;
        int total = all.Count;
        var cadence = new ProgressCadence();
        foreach (var f in all)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;

            if (progress is not null && cadence.IsDue())
            {
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
        // Asked before the review, which would otherwise end in a refusal: with
        // records in another folder, the deletion below - in the backup folder -
        // would retire their records and leave their files behind. It reads only
        // the catalog, so the answer holds through the review; whether the drive
        // is there is asked after it, since it can be plugged in meanwhile.
        string? targetDir = backupSet.JobOptions?.BackupRoot();
        if (targetDir is not null && await RecordsElsewhereRefusalAsync(backupSet, targetDir) is { } elsewhereMessage)
        {
            MessageBox.Show(elsewhereMessage, "Backup Folder Changed", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = "Left the removed-source files in place: some of the set's files are recorded in another folder.";
            return;
        }

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

        // Refuse if the set HAS a destination but it isn't reachable. Marking the
        // catalog without deleting the files would free nothing and would drop the
        // rows out of every catalog-side view (they classify active files only),
        // leaving the copies on the drive with nothing pointing at them. Same rule
        // as Cleanup's purge; see DestinationFilePurger.IsAvailable.
        if (targetDir is not null && !Services.DestinationFilePurger.IsAvailable(targetDir))
        {
            StatusText = $"Destination not connected ({targetDir}) — left the removed-source "
                + "files in place. Connect it and remove them via Cleanup.";
            return;
        }

        // Hold the set's row, as Cleanup's purge does: while it is working the
        // Worker starts nothing on the set and stops what it is running, and this
        // waits for that run to go, so no copy is written at a path this deletes.
        // Not while something else works on the set; Cleanup can do it later.
        var row = RowFor(backupSet.Id);
        if (row is { IsWorking: true })
        {
            StatusText = $"\"{backupSet.Name}\" is {row.BusyActivity ?? "backing up"} right now, so the "
                + "removed-source files were left on the destination. Remove them via Cleanup when that has finished.";
            return;
        }
        BeginRowWork(row, RemovingDroppedSources, "Removing the backed-up files of the sources you dropped…", cancel: null);
        try
        {
            await WaitForBackgroundRunAsync(row, backupSet.Id, "Removing the files of dropped sources");
            await PurgeRemovedSourcesAsync(backupSet, removed, byPath, targetDir);
        }
        finally
        {
            EndRowWork(row, StatusText, isError: false);
        }
        await LoadBackupSetsAsync();
    }

    private const string RemovingDroppedSources = "removing the files of dropped sources";

    /// <summary>
    /// The removal itself, for <see cref="PromptAndPurgeRemovedAsync"/>, with the
    /// set held: the records of <paramref name="removed"/> retired and their
    /// stored files deleted, except what another file still needs. Its outcome
    /// goes to <see cref="StatusText"/>.
    /// </summary>
    private async Task PurgeRemovedSourcesAsync(
        BackupSet backupSet, List<FileRecord> removed,
        List<(string SourcePath, long SizeBytes)> byPath, string? targetDir)
    {
        var sourcePaths = byPath.Select(p => p.SourcePath).ToList();
        // Every version's destination file must be deleted, de-duplicated so a
        // shared disc path is never deleted (or counted) twice.
        var discPaths = new HashSet<string>(
            removed.Select(f => f.DiscPath.Replace('/', '\\')),
            StringComparer.OrdinalIgnoreCase);

        int catPurged = 0, filesDeleted = 0, delFailures = 0, alreadyAbsent = 0, keptForReferences = 0;
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
            var (_, purge) = await Views.ProgressDialog.RunAsync<(int Cat, int Files, int Fail, long Bytes, int Absent, int Kept)>(
                Application.Current.MainWindow,
                "Removing files from destination",
                "Deleting backed-up copies of the removed folders\u2026",
                cancellable: false,
                async (progress, ct) =>
                {
                    // Catalog marks inside a single transaction (mirrors Cleanup).
                    // Loop per source path so the marking shows live "x of y"
                    // progress (throttled to ProgressCadence.IntervalMs) instead of a
                    // single static "Updating catalog…" that looks frozen while
                    // thousands of rows are updated — the user observed exactly this.
                    var cadence = new ProgressCadence();
                    var tx = await _catalog.BeginTransactionAsync(backupSet.Id);
                    int purged = 0, kept = 0;
                    // Every live record of the removed paths, by id, with its stored file.
                    var candidates = new Dictionary<long, string>();
                    try
                    {
                        for (int i = 0; i < sourcePaths.Count; i++)
                        {
                            var path = sourcePaths[i];
                            // Always report the final item so a "N/N" lands before
                            // the disk-delete phase takes over; throttle the rest.
                            if (cadence.IsDue() || i == sourcePaths.Count - 1)
                            {
                                int pct = sourcePaths.Count == 0
                                    ? 100 : (int)((i + 1) * 100L / sourcePaths.Count);
                                progress.Report(new ProgressReport(
                                    $"Updating catalog {i + 1:N0}/{sourcePaths.Count:N0} ({pct}%): "
                                    + path,
                                    pct));
                            }

                            foreach (var r in await _catalog.GetFileRecordsByPathAsync(backupSet.Id, path))
                                if (!r.IsDeleted)
                                    candidates[r.Id] = r.DiscPath;
                        }

                        // All at once, so a copy one removed file holds for another's
                        // reference is judged against every removal. A copy a file
                        // still in the sources needs for its .fileref stays, with its
                        // file: removing a folder must not orphan a file outside it.
                        var keptIds = await _catalog.RetireRecordsKeepingNeededCopiesAsync(
                            backupSet.Id, candidates.Keys.ToList());
                        purged = candidates.Count - keptIds.Count;
                        kept = keptIds.Count;
                        foreach (long id in keptIds)
                            discPaths.Remove(candidates[id].Replace('/', '\\'));
                        // A stored file a live record still points at is never
                        // deleted (another set path's version filed there, say),
                        // as in Cleanup's purge.
                        if (discPaths.Count > 0)
                            discPaths.ExceptWith(await _catalog.FindDiscPathsInUseAsync(backupSet.Id, discPaths.ToList()));
                        tx.Complete();
                    }
                    finally
                    {
                        tx.Dispose();
                    }

                    // Physical deletion outside the tx so file errors can't roll
                    // the catalog back.  Skipped entirely if no destination is set.
                    int fd = 0, ff = 0, absent = 0;
                    long bytes = 0;
                    if (targetDir is not null)
                    {
                        var (d, f, b, a) = Services.DestinationFilePurger
                            .DeleteFilesAndSweep(targetDir, discPaths, progress, sweepWholeTree: false);
                        fd = d; ff = f; bytes = b; absent = a;

                        // Repair any references left stale by the deletions (a
                        // plain copy other rows referenced may now be gone),
                        // exactly as manual Cleanup's reconcile step does.
                        progress.Report("Repairing catalog references\u2026");
                        var reconcile = new CatalogReconcileService(_catalog);
                        var report = await reconcile.AnalyzeAsync(backupSet.Id, targetDir, progress, ct);
                        if (report.HasChanges)
                            await reconcile.ApplyAsync(backupSet.Id, report, targetDir, progress, ct);
                    }
                    return (purged, fd, ff, bytes, absent, kept);
                });

            (catPurged, filesDeleted, delFailures, bytesFreed, alreadyAbsent, keptForReferences) = purge;
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
        if (alreadyAbsent > 0)
            parts.Add($"{alreadyAbsent:N0} already absent");
        if (keptForReferences > 0)
            parts.Add($"kept {keptForReferences:N0} cop{(keptForReferences == 1 ? "y" : "ies")} that duplicate-file "
                      + "references of other files still need");
        StatusText = parts.Count > 0
            ? "Removed sources: " + string.Join(", ", parts) + "."
            : "No destination files needed removal.";
    }

    // -------------------------------------------------------------------
    // After a retention change: offer to remove the versions no longer kept
    // -------------------------------------------------------------------

    private const string RemovingVersionsNotKept = "removing earlier versions";

    /// <summary>
    /// After a saved edit that changed the set's retention settings: find the
    /// earlier versions the new settings no longer keep
    /// (<see cref="VersionRetentionService.FindVersionsNotKept"/>, as Cleanup's
    /// Excess Versions) and offer, in the review tree, to delete them now. The
    /// backup's own retention pass removes a tier's excess only at the next full
    /// backup, and never an emptied Default's versions; this is the moment the
    /// user is thinking about them. Never throws: it runs from the editor's close
    /// handler, where an exception would end the app.
    /// </summary>
    /// <param name="set">The set as saved.</param>
    /// <param name="retentionBefore"><see cref="RetentionSignature"/> of the editor as it opened.</param>
    private async Task OfferToRemoveVersionsNoLongerKeptAsync(BackupSet set, string retentionBefore)
    {
        var opts = set.JobOptions;
        if (opts is null || RetentionSignature(opts) == retentionBefore)
            return;
        if (opts.BackupRoot() is null)
            return; // A disc set keeps no history folder.

        try
        {
            // A set with no backup history has no versions to offer (a new set,
            // saved with tiers of its own).
            if (!await _catalog.HasFileRecordsAsync(set.Id))
                return;

            // Not while something else works on the set - a backup the source
            // reconcile just started, say: the scan can build the index under the
            // set's write lock, and Remove would be refused after the review.
            if (RowFor(set.Id) is { IsWorking: true } busyRow)
            {
                StatusText = $"Saved \"{set.Name}\". It is {busyRow.BusyActivity ?? "backing up"} right now, so the "
                             + "earlier versions its new retention settings no longer keep are left for Cleanup.";
                return;
            }

            // Where the backup is now, as the backup flow finds it: a drive can
            // have moved to another letter since the destination was saved.
            string? root = LiveBackupRoot(opts);
            if (root is null || !Services.DestinationFilePurger.IsAvailable(root))
            {
                StatusText = $"Saved \"{set.Name}\". Its destination ({opts.BackupRoot()}) is not connected, so the "
                             + "earlier versions its new retention settings no longer keep are left for Cleanup.";
                return;
            }
            if (await RecordsElsewhereRefusalAsync(set, root) is not null)
            {
                StatusText = $"Saved \"{set.Name}\". Some of its backed-up files are recorded in another folder than "
                             + $"{root}, so no earlier versions were offered for removal; backing it up explains.";
                return;
            }

            await OfferAndRemoveVersionsNotKeptAsync(set, opts, root);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"GUI: looking for earlier versions \"{set.Name}\" no longer keeps");
            StatusText = $"Could not look for the earlier versions \"{set.Name}\" no longer keeps: {ex.Message}. "
                         + "Cleanup's Excess Versions can remove them.";
        }
    }

    /// <summary>
    /// The set's backup folder where its drive is now: followed through the
    /// destination resolver, on a copy of the options, so nothing is saved.
    /// Null when the drive is not connected.
    /// </summary>
    private string? LiveBackupRoot(JobOptions options)
    {
        if (_destinationResolver is null)
            return options.BackupRoot();
        var copy = JsonSerializer.Deserialize<JobOptions>(JsonSerializer.Serialize(options))!;
        var resolution = _destinationResolver.Resolve(copy);
        return resolution is { IsConnected: true, LivePath: { } live } ? options.BackupRoot(live) : null;
    }

    /// <summary>
    /// The scan, the review and the removal, for
    /// <see cref="OfferToRemoveVersionsNoLongerKeptAsync"/>.
    /// </summary>
    /// <param name="opts">The options the versions are judged by: the set's as saved.</param>
    /// <param name="root">The backup folder, found connected.</param>
    private async Task OfferAndRemoveVersionsNotKeptAsync(BackupSet set, JobOptions opts, string root)
    {
        var (completed, notKept) = await Views.ProgressDialog.RunAsync(
            Application.Current.MainWindow,
            "Retention settings changed",
            $"Looking for earlier versions \"{set.Name}\" no longer keeps…",
            cancellable: true,
            async (progress, ct) =>
            {
                var previous = await _catalog.GetActivePreviousVersionsAsync(set.Id,
                    onIndexing: () => progress.Report(
                        "Indexing this set's earlier versions. This happens once; on a large set it takes a minute or two…"),
                    ct);
                progress.Report($"Checking {previous.Count:N0} earlier versions against the new settings…");
                var found = VersionRetentionService.FindVersionsNotKept(previous, opts, DateTime.UtcNow);
                // As in Cleanup: the last copy of content a duplicate-file
                // reference still needs is kept, and not listed.
                if (found.Count > 0)
                {
                    var needed = await _catalog.GetNeededContentCopiesAsync(
                        set.Id, found.Select(r => r.Id).ToList(), ct: ct);
                    found.RemoveAll(r => needed.Contains(r.Id));
                }
                return found;
            },
            // Read-only, apart from building the index once.
            modal: false);

        if (!completed)
        {
            StatusText = "Cancelled. The earlier versions the new retention settings no longer keep were left in place.";
            return;
        }
        if (notKept.Count == 0)
        {
            StatusText = $"Saved \"{set.Name}\". No earlier versions need removing under its new retention settings.";
            return;
        }

        var dialog = new ReviewTreeDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = ReviewTreeViewModel.ForVersionsNotKept(notKept
                .GroupBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Sum(r => r.SizeBytes), g.Count()))
                .ToList()),
        };
        if (dialog.ShowDialog() != true)
        {
            StatusText = $"Left the earlier versions \"{set.Name}\" no longer keeps; Cleanup's Excess Versions "
                         + "can remove them later.";
            return;
        }

        // The review can take a while; the drive may have gone, or something
        // started on the set.
        if (!Services.DestinationFilePurger.IsAvailable(root))
        {
            StatusText = $"Destination not connected ({root}), so the earlier versions were left in place. "
                         + "Connect it and remove them via Cleanup's Excess Versions.";
            return;
        }
        var row = RowFor(set.Id);
        if (row is { IsWorking: true })
        {
            StatusText = $"\"{set.Name}\" is {row.BusyActivity ?? "backing up"} right now, so the earlier versions "
                         + "were left in place. Remove them via Cleanup's Excess Versions when that has finished.";
            return;
        }

        // Hold the set's row, as the removal of dropped sources does: the Worker
        // starts nothing on the set and stops what it is running, and this waits
        // for that run to go.
        string outcome = "";
        bool failed = false;
        BeginRowWork(row, RemovingVersionsNotKept,
            "Removing the earlier versions its retention settings no longer keep…", cancel: null);
        try
        {
            await WaitForBackgroundRunAsync(row, set.Id, "Removing earlier versions");
            (outcome, failed) = await PurgeVersionsNoLongerKeptAsync(set.Id, notKept, root, RetentionSignature(opts));
        }
        finally
        {
            EndRowWork(row, outcome, failed);
        }
        StatusText = outcome;
        await LoadBackupSetsAsync();
    }

    /// <summary>
    /// The removal itself, for <see cref="OfferToRemoveVersionsNoLongerKeptAsync"/>,
    /// with the set held: <paramref name="versions"/> retired and their stored
    /// files deleted, except a copy a duplicate-file reference still needs and a
    /// file a live record still points at.
    /// </summary>
    /// <param name="versions">The earlier versions found and confirmed, as read then.</param>
    /// <param name="root">The folder the set's backup lives in.</param>
    /// <param name="judgedBy">
    /// <see cref="RetentionSignature"/> of the settings <paramref name="versions"/>
    /// were found under. If the set's settings are different by the time of the
    /// removal (saved again from another editor meanwhile), nothing is removed:
    /// the list was made for settings the set no longer has.
    /// </param>
    /// <returns>What was done, for the status bar and the set's row; whether it failed.</returns>
    private async Task<(string Outcome, bool Failed)> PurgeVersionsNoLongerKeptAsync(
        int setId, IReadOnlyList<FileRecord> versions, string root, string judgedBy)
    {
        (int Retired, int Deleted, int Failures, long Bytes, int Absent, int Kept) result;
        try
        {
            // Not cancellable: the user confirmed the deletion, and a half-done
            // purge leaves the catalog and the destination out of step.
            (_, result) = await Views.ProgressDialog.RunAsync<(int, int, int, long, int, int)>(
                Application.Current.MainWindow,
                "Removing earlier versions",
                "Deleting the earlier versions the retention settings no longer keep…",
                cancellable: false,
                async (progress, ct) =>
                {
                    // The guard below finds a reference's content by hash: give one to
                    // any plain copy about to go that has none (a seed with Skip
                    // hashing records them so), as the backup's retention pass does.
                    var unhashed = versions
                        .Where(v => string.IsNullOrEmpty(v.Hash) && !v.IsFileRef && !v.IsDeduped)
                        .Select(v => v.Id).ToList();
                    if (unhashed.Count > 0)
                    {
                        progress.Report("Checking copies that duplicate-file references may need…");
                        await ContentAnchors.HashUnhashedAnchorsAsync(_catalog, setId, root, unhashed, ct);
                    }

                    progress.Report("Updating the catalog…");
                    var discPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int retired, kept;
                    var tx = await _catalog.BeginTransactionAsync(setId, ct,
                        onWaitingForOtherProcess: () => progress.Report(
                            "Waiting for the background backup of this set to pause…"));
                    try
                    {
                        // As late as it can be checked: the settings live in the
                        // master catalog, outside this lock.
                        var current = await _catalog.GetBackupSetAsync(setId, ct);
                        if (RetentionSignature(current?.JobOptions) != judgedBy)
                            return (-1, 0, 0, 0L, 0, 0);
                        // The records as they are now, under the write lock: one retired
                        // since is left alone, and one a rename has moved since is
                        // deleted where it now is, not where the scan found it.
                        var live = await _catalog.GetLiveDiscPathsAsync(setId, versions.Select(v => v.Id).ToList(), ct);
                        // A copy a reference still needs stays live, with its file.
                        var keptIds = await _catalog.RetireRecordsKeepingNeededCopiesAsync(setId, live.Keys.ToList(), ct: ct);
                        foreach (var (id, discPath) in live)
                            if (!keptIds.Contains(id))
                                discPaths.Add(discPath.Replace('/', '\\'));
                        // Nor a file another live record still points at.
                        if (discPaths.Count > 0)
                            discPaths.ExceptWith(await _catalog.FindDiscPathsInUseAsync(setId, discPaths.ToList(), ct));
                        retired = live.Count - keptIds.Count;
                        kept = keptIds.Count;
                        tx.Complete();
                    }
                    finally
                    {
                        tx.Dispose();
                    }

                    // Outside the transaction, so a file that cannot be deleted
                    // cannot roll the catalog back; it shows up later as a
                    // catalog-deleted file in Cleanup's destination scan.
                    var (deleted, failures, bytes, absent) = Services.DestinationFilePurger.DeleteFilesAndSweep(
                        root, discPaths, progress, sweepWholeTree: false);
                    return (retired, deleted, failures, bytes, absent, kept);
                });
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"GUI: removing earlier versions of set {setId}");
            return ($"Removing the earlier versions failed: {ex.Message}", true);
        }

        var (retiredCount, deletedCount, failureCount, bytesFreed, absentCount, keptCount) = result;
        if (retiredCount < 0)
            return ("The set's retention settings were changed again before the removal, so nothing was "
                    + "removed. Save them again to be offered the versions they no longer keep.", false);
        var parts = new List<string>
        {
            $"removed {retiredCount:N0} earlier version{(retiredCount == 1 ? "" : "s")}",
        };
        if (deletedCount > 0)
            parts.Add($"deleted {deletedCount:N0} file{(deletedCount == 1 ? "" : "s")} ({FormatBytes(bytesFreed)} bytes freed)");
        if (failureCount > 0)
            parts.Add($"{failureCount:N0} could not be deleted (Cleanup's destination scan lists them)");
        if (absentCount > 0)
            parts.Add($"{absentCount:N0} already gone");
        if (keptCount > 0)
            parts.Add($"kept {keptCount:N0} that duplicate-file references now need");
        return (char.ToUpperInvariant(parts[0][0]) + parts[0][1..]
                + (parts.Count > 1 ? ": " + string.Join(", ", parts.Skip(1)) : "") + ".",
            failureCount > 0);
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
        var (completed, scan) = await Views.ProgressDialog.RunAsync(
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
                long alreadyCovered = 0;
                long coveredBytes = 0;
                var cadence = new ProgressCadence();
                foreach (var (path, size) in EnumerateFilesUnderRoots(addedRoots))
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;

                    if (cadence.IsDue())
                        progress.Report($"Scanned {scanned:N0} files, {result.Count:N0} new\u2026");

                    if (!SourceSelection.IsPathIncluded(newSelections, path))
                        continue;

                    if (SourceSelection.IsPathIncluded(originalSelections, path))
                    {
                        // Counted, not discarded: this is the case that used to
                        // end in a silent return. See below.
                        alreadyCovered++;
                        coveredBytes += size;
                        continue;
                    }

                    result.Add((path, size));
                }
                return (Added: result, Scanned: scanned,
                        AlreadyCovered: alreadyCovered, CoveredBytes: coveredBytes);
            },
            // Read-only walk of the added folders — nothing to protect by
            // disabling the rest of the app.
            modal: false);

        // Cancelled the scan — leave the added folders for the next backup.
        if (!completed)
            return;

        var addedFiles = scan.Added;

        if (addedFiles.Count == 0)
        {
            // NEVER return silently here. The user has just watched a scan run;
            // saying nothing reads as "the app ignored my 12 GB".
            //
            // The interesting case is AlreadyCovered > 0, and it comes from two
            // predicates that disagree by design:
            //   CollectSelectedRoots (which decided a root was ADDED) collects
            //     only fully-selected (IsSelected == true) nodes;
            //   IsPathIncluded (which decides a file is NEW) returns true for an
            //     unlisted descendant of a PARTIALLY-selected directory whose
            //     auto-include-new is on.
            // So ticking a folder under an auto-include parent yields a new root
            // whose every file was already considered covered. Nothing is lost —
            // the next run backs them up — but the user has to be told that,
            // and offered the run they plainly wanted.
            if (scan.AlreadyCovered > 0)
            {
                var answer = MessageBox.Show(
                    $"The {scan.AlreadyCovered:N0} file(s) ({FormatBytes(scan.CoveredBytes)}) in the "
                    + "folder(s) you added were already covered by this set's existing selection — a "
                    + "parent folder is set to include new subfolders automatically — so they are not "
                    + "listed as newly added.\n\n"
                    + "They will be backed up by the next run. Back up now?",
                    "Updating backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);

                if (answer == MessageBoxResult.Yes)
                {
                    var coveredRow = BackupSets.FirstOrDefault(s => s.Id == backupSet.Id);
                    if (coveredRow is not null)
                        _ = RunIncrementalFlowAsync(coveredRow, forceReview: false);
                    else
                        StatusText = "Couldn't start backup — the set is no longer listed.";
                }
                else
                {
                    StatusText =
                        $"{scan.AlreadyCovered:N0} file(s) in the added folder(s) were already "
                        + "covered — they'll be included in the next backup.";
                }
                return;
            }

            StatusText = scan.Scanned == 0
                ? "The folder(s) you added contain no files yet — nothing to back up."
                : $"Scanned {scan.Scanned:N0} file(s) in the added folder(s); none of them are "
                  + "covered by the set's selection, so there is nothing to back up.";
            return;
        }

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

        // Back up exactly the files just enumerated, rather than re-scanning every
        // source to rediscover them.
        _ = RunIncrementalFlowAsync(
            row, forceReview: false,
            onlyThesePaths: addedFiles.Select(f => f.SourcePath).ToList());
    }

    /// <summary>
    /// Recursively enumerate the files (with sizes) under a set of directory
    /// roots, following links as the scan does - but not into themselves - and
    /// swallowing per-directory access errors so one unreadable folder can't
    /// abort the walk.
    /// </summary>
    internal static IEnumerable<(string Path, long Size)> EnumerateFilesUnderRoots(
        IEnumerable<string> roots)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var links = new LinkLoopGuard();
        foreach (var r in roots)
        {
            // An entry may be a file: CollectCoveredEntries returns a selected
            // file as itself rather than as its containing directory.
            if (File.Exists(r))
            {
                long fsize = 0;
                try { fsize = new FileInfo(r).Length; } catch { }
                yield return (r, fsize);
            }
            else if (visited.Add(r))
            {
                links.Seed(r);
                stack.Push(r);
            }
        }

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            var subdirs = new List<DirectoryInfo>();
            try { subdirs.AddRange(new DirectoryInfo(dir).EnumerateDirectories()); }
            catch { /* unreadable — skip */ }
            foreach (var sd in subdirs)
            {
                if (links.MayEnter(sd.FullName, sd.Attributes) && visited.Add(sd.FullName))
                    stack.Push(sd.FullName);
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

    /// <summary>
    /// Relabel a set's discs for Change Destination, so they name the folders
    /// their files are now in.
    /// </summary>
    /// <param name="oldRoot">The set's backup folder before the move.</param>
    /// <param name="oldDestination">Its destination before the move: with "Create
    /// subdirectory", the folder above the backup folder, where an older Worker
    /// wrote its runs.</param>
    /// <param name="newRoot">The backup folder after it.</param>
    /// <param name="newDestination">The destination after it.</param>
    /// <remarks>
    /// A label is matched to the old folders on any drive letter, as the guard
    /// that refuses a set with records elsewhere matches it
    /// (<see cref="JobOptions.SameFolderOnAnyDrive"/>): labels written before
    /// the drive changed letter would otherwise stay behind, and that guard
    /// would then refuse the set for them.
    /// </remarks>
    public static async Task RelabelDiscsAfterMoveAsync(ICatalogRepository catalog, int setId,
        string? oldRoot, string oldDestination, string? newRoot, string newDestination)
    {
        var changed = new List<DiscRecord>();
        foreach (var disc in await catalog.GetDiscsForBackupSetAsync(setId))
        {
            string? relabel =
                JobOptions.SameFolderOnAnyDrive(disc.Label, oldRoot) ? newRoot
                : JobOptions.SameFolderOnAnyDrive(disc.Label, oldDestination) ? newDestination
                : null;
            if (relabel is not null && relabel != disc.Label)
            {
                disc.Label = relabel;
                changed.Add(disc);
            }
        }
        if (changed.Count == 0)
            return;

        // All or none: a set has a disc record per run (tens of thousands), and
        // labels half moved would leave it refused for records "elsewhere". One
        // transaction is also one write, not one per disc.
        var tx = await catalog.BeginTransactionAsync(setId);
        try
        {
            foreach (var disc in changed)
                await catalog.UpdateDiscAsync(disc);
            tx.Complete();
        }
        finally
        {
            tx.Dispose();
        }
    }

    private async Task ChangeDestinationAsync()
    {
        if (SelectedBackupSet?.JobOptions is null) return;

        var backupSet = SelectedBackupSet;

        // It writes the set's settings back below, so start from the catalog's
        // copy: the editor no longer edits this instance, so after a save made
        // there it can be behind.
        await RefreshFromCatalogAsync(backupSet, "changing its destination");
        if (backupSet.JobOptions is null) return;

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
            // A disc is labelled with the folder its run wrote to: the backup's
            // own folder (BackupRoot), or for a set with the "create subdirectory"
            // option backed up by an older Worker, the destination itself.
            string? oldRoot = backupSet.JobOptions.BackupRoot();

            // The labels first, all or none. If that fails, nothing has changed;
            // if saving the options below then fails, doing it again finds the
            // labels moved already and saves the options. The other order could
            // leave the options moved and the labels not: the set refused, for
            // records in another folder.
            await RelabelDiscsAfterMoveAsync(_catalog, backupSet.Id,
                oldRoot, oldPath, backupSet.JobOptions.BackupRoot(newPath), newPath);

            // Update the saved job options. The new path may live on a
            // different volume, so discard the old volume identity and let the
            // resolver re-capture it for the new path (the resolver's backfill
            // branch fires when DestinationVolumeId is null).
            backupSet.JobOptions.TargetDirectory = newPath;
            backupSet.JobOptions.DestinationVolumeId = null;
            backupSet.JobOptions.DestinationSubpath = null;
            _destinationResolver?.Resolve(backupSet.JobOptions);
            // Destination only - never the selection.
            await _catalog.UpdateBackupSetMetadataAsync(backupSet);

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

        // It rewrites the whole set below, selection included, so start from the
        // catalog's copy - see ChangeDestinationAsync.
        await RefreshFromCatalogAsync(backupSet, "remapping its source drive");

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

        // Its open windows (Cleanup, Coverage, Verify...) may be reading it: the
        // delete waits for a read under way, and closing them stops theirs.
        if (!_taskWindows.CloseForSet(backupSet.Id))
        {
            StatusText = $"\"{backupSet.Name}\" was not deleted: a window open on it was kept open.";
            return;
        }
        StatusText = $"Deleting \"{backupSet.Name}\"…";

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
            // Report a FAILED delete in a dialog, not only the status bar.
            // The user just confirmed a permanent, destructive action in a modal
            // dialog; if it then fails, having the set quietly reappear with the
            // sole explanation on a status line nobody is looking at reads as
            // "delete is broken", not "delete failed, and here is why". That is
            // precisely how the FOREIGN KEY failure on legacy Discs rows stayed
            // invisible across several attempts.
            StatusText = $"Failed to delete backup set: {ex.Message}";
            CrashLogger.Log(ex, $"Delete of backup set {backupSet.Id} (\"{backupSet.Name}\")");
            MessageBox.Show(
                $"Could not delete \"{backupSet.Name}\".\n\n{ex.Message}",
                "Delete Backup Set",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        var job = BuildJob(backupSet, opts, sources);
        string? effectiveTargetDir = job.TargetDirectory;

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
                if (now - lastScanUpdate >= ProgressCadence.IntervalMs)
                {
                    lastScanUpdate = now;
                    StatusText = $"Checking \"{backupSet.Name}\"... {sp.FilesFound:N0} files scanned, "
                        + $"{sp.DirectoriesScanned:N0} folders";
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
    /// <summary>
    /// A backup job for <paramref name="backupSet"/> from its saved options and
    /// <paramref name="sources"/>, with the backup settings every account shares as
    /// they are now (memory budget, disc staging), and the target folder including
    /// the set's subfolder.
    /// </summary>
    /// <remarks>
    /// The shared settings are read again if they have been saved since the last
    /// job, by this app or another account's; while the file cannot be read, the
    /// settings last read stay, and the status line says so once.
    /// </remarks>
    private BackupJob BuildJob(BackupSet backupSet, JobOptions opts, List<SourceSelection> sources)
    {
        string? targetDir = opts.BackupRoot();

        if (_machineSettings.Refresh())
            StatusText = $"The backup settings in {MachineSettings.DefaultPath} could not be read; "
                         + "backups go on with the ones last read.";
        var machineSettings = _machineSettings.Current;

        return new BackupJob
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
            TargetDirectory = targetDir,
            EnableFileDeduplication = opts.EnableFileDeduplication,
            EnableDeduplication = opts.EnableDeduplication,
            DeduplicationBlockSize = opts.DeduplicationBlockSize > 0
                ? opts.DeduplicationBlockSize : 64 * 1024,
            ExcludedExtensions = opts.ExcludedExtensions,
            RetentionTiers = opts.RetentionTiers,
            TierSets = opts.TierSets,
            MemoryBudget = machineSettings.MemoryBudget,
            MemoryBudgetWrittenUtc = _machineSettings.CurrentWrittenUtc,
            StagingMode = machineSettings.DiscStagingMode,
        };
    }

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
        var progressCadence = new ProgressCadence();
        var hashMap = new Dictionary<string, List<ScannedFile>>();
        string setName = backupSet.Name;

        await Task.Run(async () =>
        {
            foreach (var group in sizeGroups)
            {
                foreach (var file in group)
                {
                    ct.ThrowIfCancellationRequested();

                    // Rate-limited progress, at the shared cadence.
                    if (progressCadence.IsDue())
                    {
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

        var job = BuildJob(backupSet, opts, sources);
        string? effectiveTargetDir = job.TargetDirectory;

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
                if (now - lastUpdate >= ProgressCadence.IntervalMs)
                {
                    lastUpdate = now;
                    // Directories as well as files: FilesFound counts only files
                    // that will be backed up, so crossing a large excluded tree
                    // pins it at one number for a long time and reads as a hang.
                    // The directory count is what shows the scan is alive.
                    string line =
                        $"Scanning for changes... {sp.FilesFound:N0} files checked, "
                        + $"{sp.DirectoriesScanned:N0} folders";
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
        CrashLogger.Log(null, $"GUI: nothing to back up for \"{row.Name}\": {message}");
        // Deliberately does NOT touch the current view. Task flows own their own
        // windows now; a backup finishing must never reach over and close work
        // the user is in the middle of, which is exactly what resetting the view
        // here used to do.
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
    /// <summary>
    /// Copy an explicit list of files to the destination, with no scan.
    ///
    /// <para>Uses <c>DirectoryBackupService.ExecuteTargetedAsync</c> — the same
    /// entry point continuous backup uses for USN-reported changes — which still
    /// applies the set's exclusion rules and still compares each candidate against
    /// the catalog, so it copies only what genuinely needs copying. It just does
    /// not go looking for candidates.</para>
    ///
    /// <para>Deliberately scoped: this backs up the listed files and nothing else.
    /// Other changes pending elsewhere in the set are left to continuous backup
    /// and the scheduled full runs, and the prompt that leads here says so.</para>
    /// </summary>
    private async Task RunTargetedBackupAsync(
        BackupSetRowViewModel row, BackupJob job, IReadOnlyList<string> paths)
    {
        if (job.TargetDirectory is null)
        {
            // Optical sets plan whole discs; there is no "copy these few files".
            StatusText = "Backing up just the added files needs a folder destination — "
                + "run a normal backup instead.";
            return;
        }

        var targetDir = job.TargetDirectory;
        var tiers = job.RetentionTiers;

        CrashLogger.Log(null,
            $"GUI: targeted backup for \"{row.Name}\" → {targetDir}: "
            + $"{paths.Count:N0} file(s) from a source-selection edit.");

        // The same inline progress panel a normal backup gets: percent, bytes
        // done of total, time remaining, the file being copied and its own
        // progress, Pause and Cancel, and the failed-files list at the end. This
        // run used to sit behind a small dialog that only ever said "Copying N
        // file(s) to …", because no progress reporter was passed down at all.
        var progressVm = new BurnProgressViewModel { IsDirectoryMode = true };
        string? resultText = null;   // the row's result line, once there is one

        row.LastResultText = "";
        row.RunningStatusText = "";
        row.Progress = progressVm;
        row.IsRunning = true;

        progressVm.DoneRequested += () =>
        {
            row.LastResultIsError = progressVm.HasFailedFiles
                || progressVm.StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase);
            row.LastResultText = WithAdvisory(resultText ?? progressVm.StatusText, progressVm);
            CrashLogger.Log(null,
                $"GUI: targeted backup for \"{row.Name}\" finished"
                + (row.LastResultIsError ? " WITH ERRORS" : "") + $": {row.LastResultText}");
            row.Progress = null;
            row.IsRunning = false;
            _ = LoadBackupSetsAsync();
        };

        var cts = progressVm.StartBurn();
        progressVm.OnBackupProgress(new BackupProgress
        {
            StatusMessage = $"Backing up {paths.Count:N0} added file(s)…",
        });

        // Let the panel render before the work starts.
        await Task.Yield();

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                progressVm.OnBackupProgress(p);
                // Reports with byte totals carry the backup's percentage; a
                // phase's report carries only its own count, shown on the panel.
                if (p.BytesTotalAll > 0)
                    StatusText = $"Backing up added files for \"{row.Name}\": {p.OverallPercentage:F0}%";
            });

            var result = await Task.Run(() => _directoryBackupService.ExecuteTargetedAsync(
                job, targetDir, paths, tiers, cts.Token, progress, progressVm.PauseEvent));

            int failed = result.FailedFiles.Count;
            resultText = failed > 0
                ? $"Backed up {FormatBytes(result.BytesWritten)} of added files, {failed:N0} failed."
                : $"Backed up {paths.Count:N0} added file(s) ({FormatBytes(result.BytesWritten)}).";
            progressVm.CompleteBurn(result.Success, "",
                $"Data written: {FormatBytes(result.BytesWritten)}", result.FailedFiles);
            StatusText = resultText;

            // A clean run goes straight to the row's result line; with failures
            // the panel stays up so the list of failed files can be reviewed.
            if (result.Success && failed == 0)
                progressVm.RequestDone();
        }
        catch (OperationCanceledException ex)
        {
            var cancelled = ex as BackupCanceledException;
            resultText = cancelled is { CopyFinished: true }
                ? "Cancelled after the copy — the added files are backed up; what follows it (verifying, pruning old versions) was cut short."
                : "Cancelled — some added files may not be backed up yet.";
            progressVm.CompleteBurn(false, "Cancelled by user.",
                failedFiles: cancelled?.FailedFiles, cancelled: true);
            StatusText = resultText;
            if (!progressVm.HasFailedFiles)
                progressVm.RequestDone();
        }
        catch (Exception ex)
        {
            resultText = $"Backing up the added files failed: {ex.Message}";
            progressVm.CompleteBurn(false, ex.Message);
            StatusText = resultText;
            CrashLogger.Log(ex, $"GUI: targeted backup for \"{row.Name}\" threw.");
        }
    }

    /// <param name="onlyThesePaths">
    /// When supplied, back up EXACTLY these files instead of scanning the whole
    /// selection. Used by the post-edit "you added folders" flow, which has just
    /// walked those folders and already knows every file — re-deriving the same
    /// list by scanning every source costs a median of 699 s on a real set (max
    /// 18,539 s measured), for an answer already in hand. Everything above this
    /// point still runs: drive-letter following, source/destination availability
    /// and job construction are identical either way.
    /// </param>
    private async Task RunIncrementalFlowAsync(
        BackupSetRowViewModel row, bool forceReview,
        IReadOnlyList<string>? onlyThesePaths = null)
    {
        if (row.IsRunning)
        {
            // The row's Backup link is hidden while it runs, but other paths (the
            // offers made after an edit) reach here too, and must not just do
            // nothing after the user said yes.
            if (row.BusyActivity is { } activity)
            {
                MessageBox.Show(
                    $"\"{row.Name}\" is {activity} right now, so it can't be backed up "
                    + "yet. Back it up when that has finished.",
                    "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        var backupSet = row.Model;

        // Back up what the catalog says NOW - same reasoning as StartEditFlow.
        // This window's copy can predate a change made elsewhere (the Worker
        // pinning a folder, a selection corrected by hand), and everything below
        // works from it: the plan, the source-drive remap, and that remap's full
        // write, which would otherwise put the stale selection back.
        await RefreshFromCatalogAsync(backupSet, "backing up");

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

            // Volume identity lives in JobOptions. Metadata write: this runs at
            // the start of every GUI backup, from whatever copy of the set this
            // window holds, which may predate a selection change made elsewhere.
            if (resolution.MetadataChanged)
                await _catalog.UpdateBackupSetMetadataAsync(backupSet);

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
        var job = BuildJob(backupSet, opts, sources);

        bool isDir = job.TargetDirectory is not null;

        // Records written to a folder other than the backup folder: a backup here
        // would lose track of their earlier versions. Refused, and said why.
        if (isDir && await RecordsElsewhereRefusalAsync(backupSet, job.TargetDirectory!) is { } elsewhereMessage)
        {
            MessageBox.Show(elsewhereMessage, "Backup Folder Changed", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText = $"\"{backupSet.Name}\" was not backed up: its backed-up files are recorded in another folder.";
            return;
        }

        // No history, but the destination already holds a backup at this set's
        // paths (an imported or re-created set, a cleared history): ask before
        // copying everything again over it. Before the scan, which on a large set
        // takes minutes to say nothing the answer depends on.
        if (isDir && !await ConfirmBackupOverUntrackedDestinationAsync(backupSet, sources, job.TargetDirectory!))
            return;

        // That prompt is modal only to its window, and the editor is not: a seed
        // can have taken the row while it was up.
        if (row.IsRunning)
        {
            if (row.BusyActivity is { } busyNow)
                MessageBox.Show(
                    $"\"{row.Name}\" is {busyNow} right now, so it can't be backed up "
                    + "yet. Back it up when that has finished.",
                    "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Targeted run: the caller already knows exactly which files to copy, so
        // skip the scan-and-diff entirely.
        if (onlyThesePaths is not null)
        {
            await RunTargetedBackupAsync(row, job, onlyThesePaths);
            return;
        }

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
                if (now - lastScanUpdate >= ProgressCadence.IntervalMs)
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
                    // A full check that found everything backed up: the user has
                    // run a backup, as far as a hold on automated ones goes.
                    await AfterManualFullBackupAsync(row, nothingToBackUp: true);
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
        var opts = EditorJobOptions(backupSet, vm);
        // Not an editor setting: seeding and Clear Backup History write it to the
        // catalog themselves, and Save keeps whatever is stored. Left in, a set
        // opened while held would differ from its edits by it.
        opts.HoldAutomatedBackups = false;
        return JsonSerializer.Serialize(new { Name = vm.SetName, JobOptions = opts }, _jsonOptions);
    }

    /// <summary>
    /// The options a save would write from the editor as it stands, on a copy of
    /// the set's: everything the editor shows comes from it, exactly as
    /// <see cref="SyncSettingsToJobOptions"/> writes it.
    /// </summary>
    private JobOptions EditorJobOptions(BackupSet backupSet, SourceSelectionViewModel vm)
    {
        var opts = backupSet.JobOptions is null
            ? new JobOptions()
            : JsonSerializer.Deserialize<JobOptions>(
                JsonSerializer.Serialize(backupSet.JobOptions, _jsonOptions),
                _jsonOptions) ?? new JobOptions();
        ApplyVmSettingsToJobOptions(opts, vm);
        return opts;
    }

    /// <summary>
    /// A set's retention settings as one comparable string: its tier sets (tiers,
    /// patterns, exemptions) and an old set's flat tiers. Two equal strings keep
    /// exactly the same versions.
    /// </summary>
    private static string RetentionSignature(JobOptions? options) =>
        JsonSerializer.Serialize(new { options?.TierSets, options?.RetentionTiers });

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
        // Exclusion patterns live in JobOptions - never rewrite the selection here.
        try { await Task.Run(() => _catalog.UpdateBackupSetMetadataAsync(set)); }
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

        // Log the lifecycle of a GUI-initiated backup to the same daily log the
        // Worker writes to.
        //
        // Only the Worker used to log its runs, so a backup started from the GUI
        // left no trace on disk at all. When one of them coincided with a task
        // window disappearing, there was no record of it having started, let
        // alone when it finished - the cause had to be inferred from the code
        // rather than read off a log line. Anything that can move while the user
        // is not watching should say so somewhere durable.
        string destination = isDir
            ? plan.Job.TargetDirectory ?? "(no destination)"
            : $"{plan.TotalDiscsRequired} disc(s)";
        CrashLogger.Log(null,
            $"GUI: starting backup for \"{row.Name}\" \u2192 {destination}: "
            + $"{plan.Diff.NewFiles.Count + plan.Diff.ChangedFiles.Count:N0} file(s), "
            + $"{plan.TotalBytes:N0} bytes "
            + $"({plan.Diff.NewFiles.Count:N0} new, {plan.Diff.ChangedFiles.Count:N0} changed, "
            + $"{plan.Diff.DeletedFiles.Count:N0} deleted).");

        var progressVm = new BurnProgressViewModel { IsDirectoryMode = isDir };
        // The row's progress panel lives on the home screen, but starting a
        // backup must not NAVIGATE there: doing so silently destroyed whatever
        // task the user had open, including multi-hour Cleanup scans. Task
        // flows are separate windows now, so the panel simply appears behind
        // whatever else is open.
        row.Progress = progressVm;              // show this row's inline progress panel
        // row.IsRunning is already true from the scan phase.

        progressVm.DoneRequested += () =>
        {
            // Carry the just-finished outcome onto the row's persistent result
            // line so it stays visible after the panel is dismissed.
            row.LastResultIsError = progressVm.HasFailedFiles
                || progressVm.StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase);
            row.LastResultText = WithAdvisory(progressVm.StatusText, progressVm);

            CrashLogger.Log(null,
                $"GUI: backup for \"{row.Name}\" finished"
                + (row.LastResultIsError ? " WITH ERRORS" : "")
                + $": {row.LastResultText}");
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
                // Reports with byte totals carry the backup's percentage. A phase
                // before or after the copy reports only its own count, shown on
                // the panel, and would put "0% complete" up while a backup verifies.
                if (p.BytesTotalAll > 0)
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

            // The run finished (individual failed files aside): release any hold
            // on automated backups before the row reloads.
            await AfterManualFullBackupAsync(row, nothingToBackUp: false);

            string detail = $"Data written: {FormatBytes(result.BytesWritten)}";

            progressVm.CompleteBurn(result.Success, "", detail, result.FailedFiles);
            StatusText = result.Success ? "Directory backup completed." : "Directory backup completed with errors.";

            // Clean run with nothing to review → finalize straight to the row's
            // persistent result line (no "Dismiss" to click). When files failed,
            // leave the panel up so its failed-file list / export stays available.
            if (result.Success && result.FailedFiles.Count == 0)
                progressVm.RequestDone();
        }
        catch (OperationCanceledException ex)
        {
            // Files that failed before the cancel are listed as after any run.
            progressVm.CompleteBurn(false, "Cancelled by user.",
                failedFiles: (ex as BackupCanceledException)?.FailedFiles, cancelled: true);
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

        var set = SelectedBackupSet;
        _taskWindows.Show(
            Services.TaskKinds.Restore, set.Id, set.Name,
            () => new RestoreViewModel(_catalog, _restoreService, set),
            (vm, close) => ((RestoreViewModel)vm).DoneRequested += close);
        StatusText = $"Restore files from \"{set.Name}\".";
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
        //
        // Its purge holds the set's row the way a seed does (see RowWorkHooks):
        // the row shows each stage and refuses Backup, Modify, Restore and Delete
        // until it ends, whether or not the Cleanup window is still open: a
        // backup started from the row mid-purge would write records and files
        // the purge is in the middle of retiring and deleting.
        const string CleaningActivity = "being cleaned up";
        _taskWindows.Show(
            Services.TaskKinds.Cleanup, fresh.Id, fresh.Name,
            () => new OrphanedDirectoriesViewModel(_catalog, fresh)
            {
                RowWork = new RowWorkHooks
                {
                    DescribeBusy = () => RowFor(setId) is { IsWorking: true } r
                        ? r.BusyActivity ?? "backing up"
                        : null,
                    Begin = cts =>
                    {
                        BeginRowWork(RowFor(setId), CleaningActivity, "Cleaning up…", cts);
                        CommandManager.InvalidateRequerySuggested();
                    },
                    Progress = text =>
                    {
                        if (RowFor(setId) is { BusyActivity: CleaningActivity } r)
                            r.RunningStatusText = text;
                    },
                    End = (text, isError) =>
                    {
                        if (RowFor(setId) is { BusyActivity: CleaningActivity } r)
                            EndRowWork(r, text, isError);
                        CommandManager.InvalidateRequerySuggested();
                    },
                },
            },
            (vm, close) => ((OrphanedDirectoriesViewModel)vm).DoneRequested += close);
        StatusText = "Review files and directories that can be cleaned up.";
    }

    // -------------------------------------------------------------------
    // Flow 5: Find File (cross-set search)
    // -------------------------------------------------------------------

    private void StartFindFileFlow()
    {
        FindFileViewModel? created = null;

        _taskWindows.Show(
            Services.TaskKinds.FindFile, null, null,
            () =>
            {
                created = new FindFileViewModel(_catalog);
                return created;
            },
            (vm, close) => ((FindFileViewModel)vm).DoneRequested += close);

        // Null when the window was already open and simply got focused - its
        // hand-off is already wired, so there is nothing to subscribe again.
        if (created is null)
            return;

        // Restoring from a search result opens Restore in ITS OWN window rather
        // than replacing the search, so the result list survives the restore.
        created.RestoreRequested += async setId =>
        {
            var backupSet = await _catalog.GetBackupSetAsync(setId);
            if (backupSet is null)
            {
                StatusText = "Backup set no longer exists.";
                return;
            }

            _taskWindows.Show(
                Services.TaskKinds.Restore, backupSet.Id, backupSet.Name,
                () => new RestoreViewModel(_catalog, _restoreService, backupSet),
                (vm, close) => ((RestoreViewModel)vm).DoneRequested += close);
            StatusText = $"Restore files from \"{backupSet.Name}\".";
        };

        StatusText = "Search for files or directories across all backup sets.";
    }

    // -------------------------------------------------------------------
    // Flow 5b: Catalog-free (disaster-recovery) restore
    // -------------------------------------------------------------------

    private void StartCatalogFreeRestoreFlow()
    {
        _taskWindows.Show(
            Services.TaskKinds.CatalogFreeRestore, null, null,
            () => new CatalogFreeRestoreViewModel(_catalogFreeRestoreService),
            (vm, close) => ((CatalogFreeRestoreViewModel)vm).DoneRequested += close);
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

        var set = SelectedBackupSet;
        _taskWindows.Show(
            Services.TaskKinds.Coverage, set.Id, set.Name,
            () => new BackupCoverageViewModel(_catalog, _scanner, set, estimator),
            (vm, close) => ((BackupCoverageViewModel)vm).DoneRequested += close);
        StatusText = $"Analyzing backup coverage for \"{set.Name}\".";
    }

    // -------------------------------------------------------------------
    // Flow 6b: Verify Integrity
    // -------------------------------------------------------------------

    private void StartVerifyIntegrityFlow()
    {
        if (SelectedBackupSet is null) return;

        var set = SelectedBackupSet;
        _taskWindows.Show(
            Services.TaskKinds.Verify, set.Id, set.Name,
            () => new VerifyIntegrityViewModel(_catalog, _scanner, set),
            (vm, close) => ((VerifyIntegrityViewModel)vm).DoneRequested += close);
        StatusText = $"Verifying backup integrity for \"{set.Name}\".";
    }

    // -------------------------------------------------------------------
    // Flow 6c: Test Disc (integrity-test one optical disc + re-burn repairs)
    // -------------------------------------------------------------------

    private void StartTestDiscFlow()
    {
        if (SelectedBackupSet is null) return;

        var set = SelectedBackupSet;
        _taskWindows.Show(
            Services.TaskKinds.TestDisc, set.Id, set.Name,
            () => new TestDiscViewModel(_catalog, _restoreService, _orchestrator, _burner, set),
            (vm, close) => ((TestDiscViewModel)vm).DoneRequested += close);
        StatusText = $"Test a backup disc from \"{set.Name}\".";
    }

    // -------------------------------------------------------------------
    // Flow 7: Largest Source Files
    // -------------------------------------------------------------------

    private async void StartLargestFilesFlow()
    {
        if (SelectedBackupSet is null) return;

        // Ask BEFORE constructing: the view model starts a full source scan as
        // soon as it exists, so building one only to find the window already
        // open would run that scan for nothing.
        if (_taskWindows.TryFocusExisting(Services.TaskKinds.LargestFiles, SelectedBackupSet.Id))
            return;

        // ShowLargestFilesAsync already takes a "where do I put the view?" hook
        // (the set editor uses it to host this inline); pass one that opens a
        // task window instead of replacing the main view.
        var set = SelectedBackupSet;
        Views.TaskWindow? window = null;
        await ShowLargestFilesAsync(
            set,
            onDone: () => window?.Close(),
            setView: vm => window = _taskWindows.Show(
                Services.TaskKinds.LargestFiles, set.Id, set.Name,
                () => vm,
                (created, close) => ((LargestFilesViewModel)created).DoneRequested += close));
    }

    /// <param name="setView">
    /// Where to put the view. Required: both callers host it explicitly (a task
    /// window from the main screen, inline from the set editor), and defaulting
    /// to the main window's CurrentView is what this refactor removed.
    /// </param>
    private async Task<LargestFilesViewModel> ShowLargestFilesAsync(
        BackupSet backupSet, Action onDone,
        Action<ViewModelBase> setView)
    {

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

    /// <summary>
    /// Export a set to a <c>.lithic</c> file, with its backup history unless the
    /// user leaves it out. See <see cref="BackupSetTransfer"/> for the format, and
    /// for why the history is what makes an exported set useful on another
    /// computer: without it the set starts out knowing nothing about the backup
    /// already on its destination.
    /// </summary>
    private async Task ExportBackupSetAsync()
    {
        if (SelectedBackupSet is null) return;
        var set = SelectedBackupSet;

        var row = RowFor(set.Id);
        if (row?.IsRunning == true)
        {
            StatusText = $"\"{set.Name}\" is backing up; export it when the backup has finished.";
            return;
        }
        if (row?.IsExporting == true)
        {
            StatusText = $"\"{set.Name}\" is being exported already.";
            return;
        }

        // Export what the catalog holds now, not this window's older copy.
        await RefreshFromCatalogAsync(set, "exporting");

        bool hasHistory = false;
        long historyBytes = 0;
        try
        {
            hasHistory = await _catalog.HasFileRecordsAsync(set.Id);
            if (hasHistory)
                historyBytes = _catalog.GetSetHistorySizeBytes(set.Id);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"Export of \"{set.Name}\": could not inspect its history.");
        }

        var options = new ExportBackupSetViewModel(set.Name, hasHistory, historyBytes);
        var optionsDialog = new ExportBackupSetDialog
        {
            Owner = Application.Current.MainWindow,
            DataContext = options,
        };
        if (optionsDialog.ShowDialog() != true)
            return;

        var dialog = new System.Windows.Forms.SaveFileDialog
        {
            Title = $"Export \"{set.Name}\"",
            Filter = "Backup Set (*.lithic)|*.lithic|All files (*.*)|*.*",
            FileName = SanitizeFilename(set.Name) + ".lithic",
            DefaultExt = ".lithic",
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        bool withHistory = hasHistory && options.IncludeHistory;
        string file = dialog.FileName;
        CrashLogger.Log(null, $"GUI: exporting \"{set.Name}\" to {file} "
            + (withHistory ? $"with its backup history (~{historyBytes:N0} bytes)." : "(settings only)."));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        StatusText = $"Exporting \"{set.Name}\" to {file}…";
        try
        {
            // A window of its own, with the step, a bar and Cancel: a history is
            // gigabytes and takes minutes, and the file appears only once complete.
            // Modeless: it only reads the catalog, so the rest of the app stays
            // usable, except deleting or exporting this set (IsExporting).
            bool completed;
            if (row is not null)
                row.IsExporting = true;
            try
            {
                completed = await Views.ProgressDialog.RunAsync(
                    Application.Current.MainWindow,
                    $"Export \"{set.Name}\"",
                    $"Exporting \"{set.Name}\" to {file}",
                    cancellable: true,
                    (progress, ct) => BackupSetTransfer.ExportAsync(_catalog, set, file, withHistory, progress, ct),
                    modal: false);
            }
            finally
            {
                if (row is not null)
                    row.IsExporting = false;
            }

            if (!completed)
            {
                StatusText = $"Export of \"{set.Name}\" cancelled; no file was written.";
                CrashLogger.Log(null, $"GUI: export of \"{set.Name}\" cancelled after {clock.Elapsed:hh\\:mm\\:ss}.");
                return;
            }

            long size = new FileInfo(file).Length;
            StatusText = withHistory
                ? $"Exported \"{set.Name}\" with its backup history to {file} ({FormatBytes(size)} bytes)."
                : $"Exported \"{set.Name}\" (settings only) to {file}.";
            CrashLogger.Log(null, $"GUI: exported \"{set.Name}\" to {file} ({size:N0} bytes) in {clock.Elapsed:hh\\:mm\\:ss}.");

            var answer = MessageBox.Show(
                $"Exported \"{set.Name}\" "
                + (withHistory ? "with its backup history" : "(settings only)")
                + $" to\n\n{file}\n\n{FormatBytes(size)} bytes. Show it in File Explorer?",
                "Export Backup Set", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            CrashLogger.Log(ex, $"Export of \"{set.Name}\" to {file}");
            MessageBox.Show($"Could not export \"{set.Name}\".\n\n{ex.Message}",
                "Export Backup Set", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ImportBackupSetAsync()
    {
        var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "Import Backup Set",
            Filter = "Backup Set (*.lithic)|*.lithic|JSON files (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        await ImportFromFileAsync(dialog.FileName);
    }

    /// <summary>One prompt at a time when several export files are opened at once.</summary>
    private readonly SemaphoreSlim _openedFilesGate = new(1, 1);

    /// <summary>
    /// Export files opened with Lithic Backup (double-clicked in Explorer, or
    /// handed over by a second launch): for each, show what it holds and ask
    /// whether to add it as a backup set. One at a time, in order.
    /// </summary>
    public async Task OfferOpenedFilesAsync(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            await _openedFilesGate.WaitAsync();
            try
            {
                await OfferImportAsync(path);
            }
            finally
            {
                _openedFilesGate.Release();
            }
        }
    }

    /// <summary>
    /// The window to own a prompt the user did not open from the window in front
    /// of them, such as an opened file's offer: the main window, unless a dialog
    /// open over it has disabled it, and then that dialog. A message box owned by
    /// a disabled window enables it again on closing, under the still-open dialog.
    /// </summary>
    internal static Window? PromptOwner()
    {
        var main = Application.Current.MainWindow;
        if (main is null || IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(main).Handle))
            return main;
        return Application.Current.Windows.OfType<Window>().LastOrDefault(w =>
                   w.IsVisible && IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(w).Handle))
               ?? main;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private async Task OfferImportAsync(string path)
    {
        var owner = PromptOwner();
        ExportedBackupSet settings;
        bool hasHistory;
        try
        {
            (settings, hasHistory) = await BackupSetTransfer.ReadSummaryAsync(path);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"Opening {path}");
            MessageBox.Show(owner,
                $"{Path.GetFileName(path)} is not a Lithic Backup set export, or could not be read.\n\n{ex.Message}",
                "Open Backup Set", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string destination = string.IsNullOrWhiteSpace(settings.JobOptions?.TargetDirectory)
            ? "(none - a disc set, or not chosen yet)"
            : settings.JobOptions!.TargetDirectory!;
        string history = hasHistory
            ? "included - it carries on from its existing backup"
            : "not included (settings only)";
        bool nameTaken = BackupSets.Any(r => string.Equals(r.Name, settings.Name, StringComparison.OrdinalIgnoreCase));

        var answer = MessageBox.Show(owner,
            $"Add \"{settings.Name}\" as a backup set?\n\n"
            + $"File: {path}\n"
            + $"Destination: {destination}\n"
            + $"Backup history: {history}\n"
            + (nameTaken ? $"\nYou already have a backup set named \"{settings.Name}\"; this adds a second one.\n" : "")
            + "\nIts automated backups start paused until you run a backup yourself.",
            "Open Backup Set",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText = $"Did not add \"{settings.Name}\" from {Path.GetFileName(path)}.";
            return;
        }

        await ImportFromFileAsync(path);
    }

    /// <summary>Import an export file as a new backup set, and say what it brought.</summary>
    private async Task ImportFromFileAsync(string fileName)
    {
        try
        {
            CrashLogger.Log(null, $"GUI: importing {fileName}.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            StatusText = $"Importing {Path.GetFileName(fileName)}…";
            // A window with the step, a bar and Cancel: a history holds millions
            // of records. Modal, as it adds a set to the catalog. A cancel leaves
            // no set behind (the import is all or nothing).
            var (completed, result) = await Views.ProgressDialog.RunAsync(
                PromptOwner(),
                "Import Backup Set",
                $"Importing {fileName}",
                cancellable: true,
                (progress, ct) => BackupSetTransfer.ImportAsync(_catalog, fileName, progress, ct));
            if (!completed)
            {
                // The import creates its set before the history goes in and
                // deletes it again on a cancel, and the list may have been
                // reloaded (by a backup finishing) in between.
                await LoadBackupSetsAsync();
                StatusText = $"Import of {Path.GetFileName(fileName)} cancelled; no backup set was added.";
                CrashLogger.Log(null, $"GUI: import of {fileName} cancelled after {clock.Elapsed:hh\\:mm\\:ss}.");
                return;
            }
            CrashLogger.Log(null, $"GUI: imported \"{result.Set.Name}\" from {fileName} "
                + $"({result.FileRecords:N0} file records) in {clock.Elapsed:hh\\:mm\\:ss}.");
            var newSet = result.Set;
            await LoadBackupSetsAsync();
            SelectedBackupSet = BackupSets.FirstOrDefault(s => s.Id == newSet.Id)?.Model;

            StatusText = result.IncludedHistory
                ? $"Imported \"{newSet.Name}\" with its backup history ({result.FileRecords:N0} file records)."
                : $"Imported \"{newSet.Name}\" (settings only, no backup history).";

            bool scheduled = newSet.JobOptions?.Schedule?.Enabled == true;
            string held = scheduled
                ? "\n\nIts automated backups are paused until you run a backup yourself, so nothing "
                  + "is written to its destination before you have checked it."
                : "";
            string message = result.IncludedHistory
                ? $"Imported \"{newSet.Name}\" with its backup history "
                  + $"({result.FileRecords:N0} file records).\n\n"
                  + "Before backing up, open it (Modify) and check that its destination and "
                  + "sources are where they are on this computer." + held
                : $"Imported \"{newSet.Name}\". The file holds its settings only, not its backup "
                  + "history, so the set starts out knowing nothing about what is already backed up.\n\n"
                  + "If its destination already holds this set's backup, open it (Modify) and use "
                  + "Seed from Existing Backup on the General tab before backing up. Otherwise the "
                  + "first backup copies everything again." + held;
            MessageBox.Show(message, "Import Backup Set", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            await LoadBackupSetsAsync();   // as for a cancel: a set may have come and gone
            StatusText = $"Import failed: {ex.Message}";
            CrashLogger.Log(ex, $"Import of {fileName}");
            MessageBox.Show($"Could not import {Path.GetFileName(fileName)}.\n\n{ex.Message}",
                "Import Backup Set", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -------------------------------------------------------------------
    // Automated-backup hold (JobOptions.HoldAutomatedBackups)
    // -------------------------------------------------------------------

    /// <summary>
    /// Set or clear a set's automated-backup hold in the catalog, from a fresh
    /// read so nothing else in the stored set is rewritten from an older copy.
    /// Returns the set as stored, or null if it no longer exists. A set with no
    /// saved options has no schedule to hold and is left alone.
    /// </summary>
    /// <param name="markBackedUp">
    /// Also stamp <see cref="BackupSet.LastBackupUtc"/> with now: a full check that
    /// found every file already backed up has backed the set up, though a backup
    /// with nothing to copy writes nothing. The Worker times its next whole-tree
    /// scan from that stamp when a hold ends, and a set imported with its history
    /// carries the old computer's, so stamping only an empty one left the release
    /// followed by a redundant full scan.
    /// </param>
    private async Task<BackupSet?> SetAutomationHoldAsync(int setId, bool hold, bool markBackedUp = false)
    {
        var fresh = await _catalog.GetBackupSetAsync(setId);
        if (fresh is null)
            return null;

        bool changed = false;
        if (fresh.JobOptions is { } opts && opts.HoldAutomatedBackups != hold)
        {
            opts.HoldAutomatedBackups = hold;
            changed = true;
        }
        if (markBackedUp)
        {
            fresh.LastBackupUtc = DateTime.UtcNow;
            changed = true;
        }

        if (changed)
            await _catalog.UpdateBackupSetMetadataAsync(fresh);
        return fresh;
    }

    /// <summary>
    /// Hold a set's row for work, other than a backup, that writes its catalog
    /// (a seed, clearing its history): the row shows <paramref name="status"/> in
    /// place of its actions, as during a backup's scan, so the set cannot be
    /// backed up, modified, restored or deleted until <see cref="EndRowWork"/>.
    /// With <paramref name="cancel"/>, the row's Abort link stops it.
    /// </summary>
    private static void BeginRowWork(
        BackupSetRowViewModel? row, string activity, string status, CancellationTokenSource? cancel)
    {
        if (row is null)
            return;
        row.LastResultText = "";
        row.LastResultIsError = false;
        row.Progress = null;
        row.BusyActivity = activity;
        row.RunningStatusText = status;
        row.ScanCts = cancel;
        row.IsRunning = true;
    }

    /// <summary>
    /// An editor closed while its duplicate analysis was running: show the
    /// analysis's progress on the set's row, with Stop, and its summary on the
    /// row's result line when it ends, rather than hashing on with nothing on
    /// screen. Read-only, so the set's actions stay available.
    /// </summary>
    private static void CarryDedupAnalysisToRow(SourceSelectionViewModel editor, BackupSetRowViewModel? row)
    {
        if (row is null || !editor.IsAnalyzingDedup)
            return;

        static string Summary(string text)
        {
            string flat = string.Join(" · ", text.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return flat.StartsWith("Dedup analysis", StringComparison.OrdinalIgnoreCase)
                ? flat
                : "Duplicate analysis: " + flat;
        }

        row.BackgroundWorkText = Summary(editor.DedupAnalysisResult);
        row.StopBackgroundWork = () =>
        {
            if (editor.CancelDedupCommand.CanExecute(null))
                editor.CancelDedupCommand.Execute(null);
        };

        System.ComponentModel.PropertyChangedEventHandler? onChanged = null;
        onChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(SourceSelectionViewModel.DedupAnalysisResult) && editor.IsAnalyzingDedup)
            {
                row.BackgroundWorkText = Summary(editor.DedupAnalysisResult);
            }
            else if (e.PropertyName == nameof(SourceSelectionViewModel.IsAnalyzingDedup) && !editor.IsAnalyzingDedup)
            {
                // The final text is set before IsAnalyzingDedup clears.
                editor.PropertyChanged -= onChanged;
                row.BackgroundWorkText = "";
                row.StopBackgroundWork = null;
                row.LastResultIsError = editor.DedupAnalysisResult.Contains("failed", StringComparison.OrdinalIgnoreCase);
                row.LastResultText = Summary(editor.DedupAnalysisResult);
                CommandManager.InvalidateRequerySuggested();
            }
        };
        editor.PropertyChanged += onChanged;
    }

    /// <summary>
    /// Wait until the Worker has stopped any backup of the set it was running.
    /// Holding the set's row tells it to stop (<see cref="SyncAppHold"/>) and it
    /// does within a second; work that writes the set's catalog - a seed, clearing
    /// its history - waits for that before writing, as a cleanup does, or its
    /// first writes would land beside the Worker's last.
    /// </summary>
    /// <param name="what">What is waiting, for the row's status line.</param>
    private async Task WaitForBackgroundRunAsync(
        BackupSetRowViewModel? row, int setId, string what, CancellationToken ct = default)
    {
        if (!_catalog.IsSetBeingBackedUpInBackground(setId))
            return;
        if (row is not null)
            row.RunningStatusText = $"{what}: waiting for the background service to pause its backup of this set…";
        while (_catalog.IsSetBeingBackedUpInBackground(setId))
            await Task.Delay(250, ct);
    }

    /// <summary>Holds on sets whose row is working, by set id (see <see cref="SyncAppHold"/>).</summary>
    private readonly Dictionary<int, IDisposable> _appHolds = [];

    private void OnBackupSetRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // A move is the same row at another index: nothing to hold or let go.
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            return;
        if (e.OldItems is not null)
            foreach (BackupSetRowViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                ReleaseAppHold(row.Id);
            }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            lock (_appHolds)
                foreach (int id in _appHolds.Keys.Where(id => RowFor(id) is null).ToList())
                    ReleaseAppHold(id);
        if (e.NewItems is not null)
            foreach (BackupSetRowViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnRowPropertyChanged;
                SyncAppHold(row);
            }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not BackupSetRowViewModel row)
            return;
        if (e.PropertyName == nameof(BackupSetRowViewModel.Progress) && row.Progress is { } progress)
            progress.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is nameof(BurnProgressViewModel.IsComplete) or nameof(BurnProgressViewModel.IsPaused))
                    SyncAppHold(row);
            };
        if (e.PropertyName is nameof(BackupSetRowViewModel.IsRunning) or nameof(BackupSetRowViewModel.Progress))
            SyncAppHold(row);
    }

    /// <summary>
    /// Hold the set against the Worker while its row is working
    /// (<see cref="BackupSetRowViewModel.IsWorking"/>), and let go when it stops -
    /// or pauses: a paused backup commits and gives the set's write lock up so
    /// that other writers can go on, and the Worker is one. Resuming takes the set
    /// back, and the Worker stops what it started within a second.
    /// </summary>
    private void SyncAppHold(BackupSetRowViewModel row)
    {
        bool working = row.IsWorking && row.Progress is not { IsPaused: true };
        lock (_appHolds)
        {
            if (working && !_appHolds.ContainsKey(row.Id))
                _appHolds[row.Id] = _catalog.HoldSetInApp(row.Id);
            else if (!working)
                ReleaseAppHold(row.Id);
        }
    }

    private void ReleaseAppHold(int setId)
    {
        lock (_appHolds)
            if (_appHolds.Remove(setId, out var hold))
                hold.Dispose();
    }

    /// <summary>Let the Worker carry on with every set: the app is exiting.</summary>
    public void ReleaseAppHolds()
    {
        lock (_appHolds)
            foreach (int id in _appHolds.Keys.ToList())
                ReleaseAppHold(id);
    }

    /// <summary>
    /// A finished run's result line, with what its panel advised (that creating
    /// files on the destination was pathologically slow, say) - which would
    /// otherwise go with the panel.
    /// </summary>
    private static string WithAdvisory(string result, BurnProgressViewModel progressVm)
        => string.IsNullOrEmpty(progressVm.AdvisoryText) ? result : $"{result} {progressVm.AdvisoryText}";

    /// <summary>Release a row held by <see cref="BeginRowWork"/>, leaving <paramref name="result"/> on it.</summary>
    private static void EndRowWork(BackupSetRowViewModel? row, string result, bool isError)
    {
        if (row is null)
            return;
        row.IsRunning = false;
        row.BusyActivity = null;
        row.RunningStatusText = "";
        row.ScanCts = null;
        row.LastResultIsError = isError;
        row.LastResultText = result;
    }

    /// <summary>The row's "Resume now" link.</summary>
    private async Task ResumeAutomatedBackupsAsync(BackupSet set)
    {
        try
        {
            await SetAutomationHoldAsync(set.Id, hold: false);
            await LoadBackupSetsAsync();
            StatusText = $"Automated backups for \"{set.Name}\" resumed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not resume automated backups for \"{set.Name}\": {ex.Message}";
        }
    }

    /// <summary>
    /// A full directory backup started from the app has finished - it copied
    /// files, or found nothing to copy. That is the user having run a backup, so
    /// release any hold on automated backups.
    /// </summary>
    private async Task AfterManualFullBackupAsync(BackupSetRowViewModel row, bool nothingToBackUp)
    {
        try
        {
            var stored = await SetAutomationHoldAsync(row.Id, hold: false, markBackedUp: nothingToBackUp);
            if (stored is not null)
                row.UpdateModel(stored);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"GUI: could not release the automated-backup hold on \"{row.Name}\".");
        }
    }

    /// <summary>
    /// Before backing up a directory set: if its catalog is empty but its
    /// destination already holds backed-up files where it writes, say so and let
    /// the user choose. Returns whether to go ahead.
    /// </summary>
    /// <remarks>
    /// A set imported onto a new computer without its history would otherwise
    /// back up here without a word, copying every file again over the copies
    /// already on the drive, with the version history beside them invisible to
    /// the set. Seeding records what is there instead; "back up anyway" remains
    /// for when copying it all again is really what the user wants.
    /// </remarks>
    /// <summary>
    /// Why the set must not be written to or deleted from in
    /// <paramref name="backupRoot"/>, or null when it may: live records of its
    /// catalog belong to another folder
    /// (<see cref="ICatalogRepository.FindLiveRecordsOutsideAsync"/>). A backup
    /// there cannot find those copies, so each changed file's earlier versions
    /// lose their files, and a purge there retires records without deleting them.
    /// A check that fails refuses too.
    /// </summary>
    private async Task<string?> RecordsElsewhereRefusalAsync(BackupSet set, string backupRoot)
    {
        string? elsewhere;
        try
        {
            elsewhere = await _catalog.FindLiveRecordsOutsideAsync(set.Id, backupRoot);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"GUI: checking where the files of \"{set.Name}\" are recorded");
            return $"Could not check where the backed-up files of \"{set.Name}\" are recorded: {ex.Message}";
        }
        return elsewhere is null ? null
            : $"Some of \"{set.Name}\"'s backed-up files are recorded in\n    {elsewhere}\n"
              + $"but its backup folder is now\n    {backupRoot}\n\n"
              + "Working there would lose track of them and their earlier versions, so nothing was done.\n\n"
              + RecordsElsewhere.Advice(elsewhere, backupRoot);
    }

    private async Task<bool> ConfirmBackupOverUntrackedDestinationAsync(
        BackupSet backupSet, IReadOnlyList<SourceSelection> sources, string targetDirectory)
    {
        DirectoryBackupService.UntrackedBackup? found;
        try
        {
            // What the selection covers, not the watch roots, which take in whole
            // partial folders that auto-include new ones: probing all of such a
            // folder would find another set's files in a shared destination.
            var roots = SourceSelection.CollectCoveredEntries(sources);
            if (roots.Count == 0)
                roots = backupSet.SourceRoots.ToList();
            found = await _directoryBackupService.FindUntrackedBackupAsync(backupSet.Id, roots, targetDirectory);
        }
        catch (Exception ex)
        {
            // The user asked for this backup; an unanswerable check does not stop it.
            CrashLogger.Log(ex, $"GUI: could not check whether the destination of \"{backupSet.Name}\" "
                                + "already holds a backup.");
            return true;
        }

        if (found is null)
            return true;

        const int shown = 4;
        string examples = string.Join("\n", found.OccupiedPaths.Take(shown).Select(p => "    " + p))
            + (found.OccupiedPaths.Count > shown ? $"\n    … and {found.OccupiedPaths.Count - shown} more" : "");

        var answer = MessageBox.Show(
            $"\"{backupSet.Name}\" has no backup history, but its destination already holds "
            + $"backed-up files where this set would write them:\n\n{examples}\n\n"
            + "Backing up now would copy every file again on top of them"
            + (found.HasVersionHistory
                ? ", and the earlier versions kept there would not be part of this set's history "
                  + "(Restore couldn't see them)."
                : ".")
            + "\n\nTo carry on from that backup instead, record what's already there with "
            + "Seed from Existing Backup, on the General tab of the set's editor.\n\n"
            + "Yes: open the editor\nNo: back up anyway\nCancel: do nothing",
            "Destination Already Holds a Backup",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                SelectedBackupSet = backupSet;
                StartEditFlow();
                return false;
            case MessageBoxResult.No:
                CrashLogger.Log(null,
                    $"GUI: backing up \"{backupSet.Name}\" with no history over the existing backup at "
                    + $"{targetDirectory}, as the user chose.");
                return true;
            default:
                StatusText = $"Backup of \"{backupSet.Name}\" cancelled.";
                return false;
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
    /// down so the installer can replace the running files. If the release has no
    /// installer asset, falls back to opening the release page in the browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately NOT elevated.</b> This used to ShellExecute <c>msiexec /i</c>
    /// with the <c>runas</c> verb, on the theory that the <c>SignalLithicShutdown</c>
    /// custom action runs in the client process and therefore needed the client to be
    /// elevated in order to SCM-stop the LocalSystem Worker service. That premise was
    /// wrong: the action is scheduled in the <c>InstallExecuteSequence</c>, which
    /// Windows Installer runs in its own LocalSystem server process in session 0, so
    /// the SCM stop already succeeds from an ordinary double-click. Elevating here
    /// bought nothing.
    /// </para>
    /// <para>
    /// It also actively caused harm, which is why this is not merely tidying.
    /// Elevating the client process means the wizard's "Launch Lithic Backup" exit
    /// checkbox — which is ticked by default and runs in that same client — starts
    /// the GUI with High integrity. The app's manifest is <c>asInvoker</c>, so it had
    /// no business running elevated, and it then stayed elevated for its whole
    /// lifetime. At the NEXT upgrade, Restart Manager (driven from an ordinary
    /// unelevated client) is blocked by UIPI from closing a High-integrity window, so
    /// the fallback that would otherwise have quietly closed the app failed too and
    /// the upgrade died with error 1611. See the MSI-upgrade entry in
    /// <c>known-issues.md</c>.
    /// </para>
    /// <para>
    /// Windows Installer still elevates itself at <c>InstallInitialize</c>, so the
    /// user sees exactly one UAC prompt either way — it is just raised by the
    /// installer instead of by us, and the client (and anything it launches) stays at
    /// the user's normal integrity level.
    /// </para>
    /// <para>
    /// Closing the GUI below, and the custom action's signals, cover the manual
    /// double-click-the-MSI path where we control neither.
    /// </para>
    /// </remarks>
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

            // No Verb = "runas": the installer raises its own UAC prompt at
            // InstallInitialize, and elevating the client here would leave the GUI it
            // launches afterwards running elevated. See the remarks above.
            var isMsi = installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
            var psi = isMsi
                ? new System.Diagnostics.ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\"")
                : new System.Diagnostics.ProcessStartInfo(installerPath);
            psi.UseShellExecute = true;

            try
            {
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — the user dismissed a prompt (the installer's own
                // elevation request can surface here when the shell raises it).
                StatusText = "Update cancelled.";
                return;
            }

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
    /// <summary>
    /// Close every open task window. Called when the main window closes: the app
    /// runs with ShutdownMode.OnExplicitShutdown, so a task window left open
    /// would keep the process alive with no way back to the main UI.
    /// </summary>
    public void CloseTaskWindows() => _taskWindows.CloseAll();

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
