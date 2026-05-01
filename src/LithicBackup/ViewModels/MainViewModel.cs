using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
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

    private string _statusText = "Ready";
    private int _recorderCount;
    private ViewModelBase? _currentView;
    private BackupSet? _selectedBackupSet;
    private bool _isBurning;
    private BurnProgressViewModel? _activeBackupProgress;
    private string _serviceStatusText = "";
    private int? _runningBackupSetId;
    private CancellationTokenSource? _scanCts;
    private BackupSetEditorWindow? _editorWindow;
    private Window? _largestFilesWindow;
    private Func<Task>? _pendingSettingsSave;

    public MainViewModel(
        ICatalogRepository catalog,
        IDiscBurner burner,
        IFileScanner scanner,
        IBackupOrchestrator orchestrator,
        IRestoreService restoreService,
        DirectoryBackupService directoryBackupService,
        Services.TrayService? trayService = null)
    {
        _catalog = catalog;
        _burner = burner;
        _scanner = scanner;
        _orchestrator = orchestrator;
        _restoreService = restoreService;
        _directoryBackupService = directoryBackupService;
        _trayService = trayService;

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
        SetBackupCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartIncrementalFlow(); } },
            o => !IsBurning);
        SetModifyCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartEditFlow(); } },
            o => !IsBurning);
        SetRestoreCommand = new RelayCommand(
            o => { if (o is BackupSet s) { SelectedBackupSet = s; StartRestoreFlow(); } },
            o => !IsBurning);
        InstallServiceCommand = new RelayCommand(_ => InstallService());
        UninstallServiceCommand = new RelayCommand(_ => UninstallService());
        StartServiceCommand = new RelayCommand(_ => StartService());
        StopServiceCommand = new RelayCommand(_ => StopService());

        RefreshServiceStatus();

        // Wire up tray service events for background monitoring notifications.
        if (_trayService is not null)
        {
            _trayService.BackupSuggested += reason =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = $"Background: {reason}";
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
    public ICommand SetBackupCommand { get; }
    public ICommand SetModifyCommand { get; }
    public ICommand SetRestoreCommand { get; }
    /// <summary>ID of the backup set currently being backed up, or null.</summary>
    public int? RunningBackupSetId
    {
        get => _runningBackupSetId;
        private set => SetProperty(ref _runningBackupSetId, value);
    }

    // --- Worker Service management ---

    public ICommand InstallServiceCommand { get; }
    public ICommand UninstallServiceCommand { get; }
    public ICommand StartServiceCommand { get; }
    public ICommand StopServiceCommand { get; }

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

    private void StartNewBackupFlow()
    {
        var sourceSelection = new SourceSelectionViewModel();
        sourceSelection.SetName = $"Backup {DateTime.Now:yyyy-MM-dd}";

        sourceSelection.NextRequested += sources =>
            ShowJobConfig(sources, backupSetId: null, sourceSelection);
        sourceSelection.CancelRequested += GoHome;

        // Default to "has selection" so Next is available immediately
        // (the user will check boxes before clicking).
        sourceSelection.HasSelection = true;

        CurrentView = sourceSelection;
        StatusText = "Select the files and directories to back up.";
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

        Dictionary<string, Core.Models.FileVersionInfo>? catalogInfo = null;
        try
        {
            catalogInfo = await _catalog.GetLatestVersionInfoAsync(backupSet.Id);
        }
        catch { }

        var sourceSelection = new SourceSelectionViewModel(catalogInfo);
        sourceSelection.IsEditMode = true;

        // Restore backup set settings from saved state.
        sourceSelection.SetName = backupSet.Name;
        RestoreSourceSettings(sourceSelection, backupSet.JobOptions);

        // Restore saved selections into the treeview.
        // Must await so the tree is fully populated before the dialog is shown —
        // otherwise nodes expand/check after the window is visible, causing
        // visible layout shifts.
        if (backupSet.SourceSelections is { Count: > 0 })
            await sourceSelection.ApplySelectionsAsync(backupSet.SourceSelections);

        // Enable the Save button immediately — selections exist in a saved set.
        sourceSelection.HasSelection = true;
        sourceSelection.ShowLargestFiles = true;

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
        dialog.Closed += async (_, _) =>
        {
            // Save all settings when the dialog closes (Close / X button).
            // Await the write so LoadBackupSetsAsync sees current data.
            if (_pendingSettingsSave is not null)
            {
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
                StatusText = $"Backup set \"{sourceSelection.SetName}\" saved. {DateTime.Now:HH:mm:ss}";
                sourceSelection.SaveStatusText = "Saved";
                await LoadBackupSetsAsync();
                dialog.Title = $"Modify \u2014 {sourceSelection.SetName}";
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save: {ex.Message}";
                sourceSelection.SaveStatusText = "Save failed";
            }
        };

        sourceSelection.CancelRequested += () => dialog.Close();

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
        };

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

                // Refresh exclusion patterns — LargestFiles may have added
                // or removed full-path entries in ExcludedExtensions.
                if (backupSet.JobOptions?.ExcludedExtensions is { Count: > 0 } excl)
                    sourceSelection.ExcludedPatterns =
                        BackupJobViewModel.FormatExclusionPatterns(excl);
                else
                    sourceSelection.ExcludedPatterns = "";
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
                var tsVm = new TierSetViewModel(ts.Name, ts.Name is "Default" or "None");
                foreach (var tier in ts.Tiers)
                {
                    var tierVm = RetentionTierViewModel.FromModel(tier);
                    tierVm.RemoveRequested += t => tsVm.Tiers.Remove(t);
                    tsVm.Tiers.Add(tierVm);
                }
                vm.TierSets.Add(tsVm);
            }
        }
        else if (opts.RetentionTiers.Count > 0)
        {
            // Backward compat: convert flat RetentionTiers to "Default" tier set.
            vm.TierSets.Clear();
            var defaultTs = new TierSetViewModel("Default", isBuiltIn: true);
            foreach (var tier in opts.RetentionTiers)
            {
                var tierVm = RetentionTierViewModel.FromModel(tier);
                tierVm.RemoveRequested += t => defaultTs.Tiers.Remove(t);
                defaultTs.Tiers.Add(tierVm);
            }
            vm.TierSets.Add(defaultTs);
            vm.TierSets.Add(new TierSetViewModel("None", isBuiltIn: true));
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
    /// Restore saved job options into the source selection view's settings section.
    /// </summary>
    private static void RestoreSourceSettings(SourceSelectionViewModel vm, JobOptions? opts)
    {
        if (opts is null) return;

        // Exclusions.
        if (opts.ExcludedExtensions.Count > 0)
            vm.ExcludedPatterns = BackupJobViewModel.FormatExclusionPatterns(opts.ExcludedExtensions);

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

        // Exclusions.
        opts.ExcludedExtensions = !string.IsNullOrWhiteSpace(vm.ExcludedPatterns)
            ? BackupJobViewModel.ParseExclusionPatterns(vm.ExcludedPatterns)
            : [];

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

        // Exclusions.
        job.ExcludedExtensions = src.ExcludedPatterns;

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

        // Retention tier sets.
        job.TierSets.Clear();
        foreach (var srcTs in src.TierSets)
        {
            var tsVm = new TierSetViewModel(srcTs.Name, srcTs.IsBuiltIn);
            foreach (var srcTier in srcTs.Tiers)
            {
                var model = srcTier.ToModel();
                var tierVm = RetentionTierViewModel.FromModel(model);
                tierVm.RemoveRequested += t => tsVm.Tiers.Remove(t);
                tsVm.Tiers.Add(tierVm);
            }
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

    private void StartBurn(BackupPlan plan)
    {
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

            FailureCallback onFailure = async (filePath, error) =>
            {
                var tcs = new TaskCompletionSource<FailureDecision>();

                // Must show the dialog on the UI thread.
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var vm = new FailureDialogViewModel
                    {
                        FilePath = filePath,
                        ErrorMessage = error,
                    };

                    var dialog = new FailureDialog
                    {
                        DataContext = vm,
                        Owner = Application.Current.MainWindow,
                    };

                    bool? dialogResult = dialog.ShowDialog();

                    // Re-activate the main window so focus isn't lost to the desktop.
                    Application.Current.MainWindow?.Activate();

                    if (dialogResult == true)
                    {
                        tcs.SetResult(new FailureDecision
                        {
                            Action = vm.ChosenAction,
                        });
                    }
                    else
                    {
                        // Dialog closed without choosing — default to Skip.
                        tcs.SetResult(new FailureDecision
                        {
                            Action = BurnFailureAction.Skip,
                        });
                    }
                });

                return await tcs.Task;
            };

            // Run the backup on a background thread so the UI stays responsive.
            // The Progress<T> callback marshals back to the UI thread automatically,
            // and the onFailure callback uses Dispatcher.InvokeAsync for dialogs.
            var result = await Task.Run(
                () => _orchestrator.ExecuteAsync(plan, progress, onFailure, cts.Token));

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

            FailureCallback onFailure = async (filePath, error) =>
            {
                var tcs = new TaskCompletionSource<FailureDecision>();

                // Must show the dialog on the UI thread.
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var vm = new FailureDialogViewModel
                    {
                        FilePath = filePath,
                        ErrorMessage = error,
                        IsDirectoryMode = true,
                    };

                    var dialog = new FailureDialog
                    {
                        DataContext = vm,
                        Owner = Application.Current.MainWindow,
                    };

                    bool? dialogResult = dialog.ShowDialog();

                    // Re-activate the main window so focus isn't lost to the desktop.
                    Application.Current.MainWindow?.Activate();

                    if (dialogResult == true)
                    {
                        tcs.SetResult(new FailureDecision
                        {
                            Action = vm.ChosenAction,
                        });
                    }
                    else
                    {
                        // Dialog closed without choosing — default to Skip.
                        tcs.SetResult(new FailureDecision
                        {
                            Action = BurnFailureAction.Skip,
                        });
                    }
                });

                return await tcs.Task;
            };

            // Run the backup on a background thread so the UI stays responsive.
            // The Progress<T> callback marshals back to the UI thread automatically,
            // and the onFailure callback uses Dispatcher.InvokeAsync for dialogs.
            var result = await Task.Run(() => _directoryBackupService.ExecuteAsync(
                plan.Job,
                plan.Job.TargetDirectory!,
                plan.Job.RetentionTiers.Count > 0 ? plan.Job.RetentionTiers : VersionRetentionService.DefaultTiers,
                progress,
                cts.Token,
                progressVm.PauseEvent,
                onFailure,
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
    // Flow 4: Orphaned Directories
    // -------------------------------------------------------------------

    private void StartOrphanedDirsFlow()
    {
        if (SelectedBackupSet is null)
            return;

        var vm = new OrphanedDirectoriesViewModel(_catalog, SelectedBackupSet);
        vm.DoneRequested += GoHome;

        CurrentView = vm;
        StatusText = "Review directories no longer in the backup sources.";
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

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
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
