using System.Collections.ObjectModel;
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

        NewBackupSetCommand = new RelayCommand(_ => StartNewBackupFlow());
        RunIncrementalCommand = new RelayCommand(
            _ => StartIncrementalFlow(),
            _ => SelectedBackupSet is not null);
        RestoreCommand = new RelayCommand(_ => StartRestoreFlow());
        OrphanedDirsCommand = new RelayCommand(
            _ => StartOrphanedDirsFlow(),
            _ => SelectedBackupSet is not null);

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

    // --- Commands ---

    public ICommand NewBackupSetCommand { get; }
    public ICommand RunIncrementalCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand OrphanedDirsCommand { get; }

    // -------------------------------------------------------------------
    // Flow 1: New Backup Set
    //   Source Selection → Backup Job Config → Burn Progress → Done
    // -------------------------------------------------------------------

    private void StartNewBackupFlow()
    {
        var sourceSelection = new SourceSelectionViewModel();

        sourceSelection.NextRequested += sources => ShowJobConfig(sources, backupSetId: null);
        sourceSelection.CancelRequested += GoHome;

        // Default to "has selection" so Next is available immediately
        // (the user will check boxes before clicking).
        sourceSelection.HasSelection = true;

        CurrentView = sourceSelection;
        StatusText = "Select the files and directories to back up.";
    }

    // -------------------------------------------------------------------
    // Flow 2: Incremental Backup (existing backup set)
    // -------------------------------------------------------------------

    private void StartIncrementalFlow()
    {
        if (SelectedBackupSet is null)
            return;

        // Prefer the full saved selection tree; fall back to root-only reconstruction.
        List<SourceSelection> sources;
        if (SelectedBackupSet.SourceSelections is { Count: > 0 })
        {
            sources = SelectedBackupSet.SourceSelections;
        }
        else
        {
            sources = SelectedBackupSet.SourceRoots
                .Select(root => new SourceSelection
                {
                    Path = root,
                    IsDirectory = true,
                    IsSelected = true,
                    AutoIncludeNewSubdirectories = true,
                })
                .ToList();
        }

        ShowJobConfig(sources, SelectedBackupSet.Id);
    }

    // -------------------------------------------------------------------
    // Shared: Job Config → Plan → Burn
    // -------------------------------------------------------------------

    private void ShowJobConfig(List<SourceSelection> sources, int? backupSetId)
    {
        var jobConfig = new BackupJobViewModel(sources, _orchestrator, _burner, _directoryBackupService);

        if (backupSetId.HasValue && SelectedBackupSet is not null)
        {
            jobConfig.SetName = SelectedBackupSet.Name;
            RestoreJobOptions(jobConfig, SelectedBackupSet.JobOptions);
        }

        jobConfig.BackRequested += StartNewBackupFlow;

        // Save the backup set when planning completes.
        jobConfig.PlanCompleted += async job =>
        {
            try
            {
                backupSetId = await SaveBackupSetAsync(
                    backupSetId, jobConfig.SetName, sources, job);
                StatusText = $"Backup set \"{jobConfig.SetName}\" saved. {DateTime.Now:HH:mm:ss}";
                await LoadBackupSetsAsync();
            }
            catch (Exception ex)
            {
                StatusText = $"Failed to save backup set: {ex.Message}";
            }
        };

        jobConfig.StartRequested += plan =>
        {
            // Patch the backup set ID onto the plan's job.
            plan.Job.BackupSetId = backupSetId;
            StartBurn(plan);
        };

        CurrentView = jobConfig;
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

        // Restore retention tiers.
        if (opts.RetentionTiers.Count > 0)
        {
            vm.RetentionTiers.Clear();
            foreach (var tier in opts.RetentionTiers)
            {
                var tierVm = RetentionTierViewModel.FromModel(tier);
                tierVm.RemoveRequested += t => vm.RetentionTiers.Remove(t);
                vm.RetentionTiers.Add(tierVm);
            }
        }
    }

    /// <summary>
    /// Create or update a backup set with the full source selection tree and job options.
    /// </summary>
    private async Task<int> SaveBackupSetAsync(
        int? existingId, string name, List<SourceSelection> sources, BackupJob job)
    {
        var jobOptions = new JobOptions
        {
            ZipMode = job.ZipMode,
            FilesystemType = job.FilesystemType,
            CapacityOverrideBytes = job.CapacityOverrideBytes,
            VerifyAfterBurn = job.VerifyAfterBurn,
            IncludeCatalogOnDisc = job.IncludeCatalogOnDisc,
            AllowFileSplitting = job.AllowFileSplitting,
            EnableFileDeduplication = job.EnableFileDeduplication,
            EnableDeduplication = job.EnableDeduplication,
            DeduplicationBlockSize = job.DeduplicationBlockSize,
            RetentionTiers = job.RetentionTiers,
            TargetDirectory = job.TargetDirectory,
            ExcludedExtensions = job.ExcludedExtensions,
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
        CurrentView = progressVm;

        progressVm.DoneRequested += () =>
        {
            GoHome();
            _ = LoadBackupSetsAsync();
        };

        if (isDir)
        {
            StatusText = "Copying files...";
            _ = ExecuteDirectoryBackupAsync(plan, progressVm);
        }
        else
        {
            StatusText = "Burning...";
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

                    if (dialogResult == true)
                    {
                        tcs.SetResult(new FailureDecision
                        {
                            Action = vm.ChosenAction,
                            ApplyToAllOnDisc = vm.ApplyToAll,
                        });
                    }
                    else
                    {
                        // Dialog closed without choosing -- default to Skip.
                        tcs.SetResult(new FailureDecision
                        {
                            Action = BurnFailureAction.Skip,
                            ApplyToAllOnDisc = false,
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

            if (result.FailedFiles.Count > 0)
            {
                detail += $"\nFailed files: {result.FailedFiles.Count}";
                foreach (var f in result.FailedFiles.Take(10))
                    detail += $"\n  - {f.Path}: {f.Error}";
                if (result.FailedFiles.Count > 10)
                    detail += $"\n  ... and {result.FailedFiles.Count - 10} more";
            }

            progressVm.CompleteBurn(result.Success, "", detail);
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
            StatusText = "Backup failed.";
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
                StatusText = $"Copying files — {p.OverallPercentage:F0}%";
            });

            // Run the backup on a background thread so the UI stays responsive.
            var result = await Task.Run(() => _directoryBackupService.ExecuteAsync(
                plan.Job,
                plan.Job.TargetDirectory!,
                plan.Job.RetentionTiers.Count > 0 ? plan.Job.RetentionTiers : VersionRetentionService.DefaultTiers,
                progress,
                cts.Token));

            string detail = $"Data written: {FormatBytes(result.BytesWritten)}";

            if (result.FailedFiles.Count > 0)
            {
                detail += $"\nFailed files: {result.FailedFiles.Count}";
                foreach (var f in result.FailedFiles.Take(10))
                    detail += $"\n  - {f.Path}: {f.Error}";
                if (result.FailedFiles.Count > 10)
                    detail += $"\n  ... and {result.FailedFiles.Count - 10} more";
            }

            progressVm.CompleteBurn(result.Success, "", detail);
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
            StatusText = "Directory backup failed.";
        }
    }

    // -------------------------------------------------------------------
    // Flow 3: Restore Files
    // -------------------------------------------------------------------

    private void StartRestoreFlow()
    {
        var restoreVm = new RestoreViewModel(_catalog, _restoreService, BackupSets);

        restoreVm.DoneRequested += GoHome;

        CurrentView = restoreVm;
        StatusText = "Select a backup set and files to restore.";
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
