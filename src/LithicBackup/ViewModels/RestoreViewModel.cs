using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the file restore view. Shows backup sets, their files,
/// and allows the user to restore selected files.
/// </summary>
public class RestoreViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IRestoreService _restoreService;

    private BackupSet? _selectedBackupSet;
    private string _destinationPath = "";
    private bool _isLoading;
    private bool _isRestoring;
    private bool _isComplete;
    private string _statusText = "Select a backup set to see available files.";
    private string _resultDetail = "";
    private double _restorePercentage;
    private string _currentFile = "";
    private string _progressText = "";
    private CancellationTokenSource? _cts;

    /// <summary>Fired when the user clicks "Done" or "Close".</summary>
    public event Action? DoneRequested;

    public RestoreViewModel(
        ICatalogRepository catalog,
        IRestoreService restoreService,
        BackupSet backupSet)
    {
        _catalog = catalog;
        _restoreService = restoreService;
        _selectedBackupSet = backupSet;
        RestorableFiles = [];
        SelectedFiles = [];

        BrowseCommand = new RelayCommand(_ => BrowseDestination());
        RestoreCommand = new RelayCommand(_ => _ = RestoreAsync(), _ => CanRestore());
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRestoring);
        DoneCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        // Load files immediately for the given backup set.
        _ = LoadRestorableFilesAsync(backupSet.Id);
    }

    // --- Properties ---

    public BackupSet SelectedBackupSet => _selectedBackupSet!;

    public ObservableCollection<RestorableFileViewModel> RestorableFiles { get; }

    /// <summary>Files the user has selected for restore.</summary>
    public ObservableCollection<RestorableFileViewModel> SelectedFiles { get; }

    public string DestinationPath
    {
        get => _destinationPath;
        set => SetProperty(ref _destinationPath, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsRestoring
    {
        get => _isRestoring;
        set => SetProperty(ref _isRestoring, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ResultDetail
    {
        get => _resultDetail;
        set => SetProperty(ref _resultDetail, value);
    }

    public double RestorePercentage
    {
        get => _restorePercentage;
        set => SetProperty(ref _restorePercentage, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        set => SetProperty(ref _currentFile, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    // --- Commands ---

    public ICommand BrowseCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DoneCommand { get; }

    // --- Logic ---

    private async Task LoadRestorableFilesAsync(int backupSetId)
    {
        IsLoading = true;
        StatusText = "Loading files from catalog...";
        RestorableFiles.Clear();
        SelectedFiles.Clear();

        try
        {
            var files = await _restoreService.GetRestorableFilesAsync(backupSetId);

            // Group by source path, show latest version.
            var latestByPath = files
                .GroupBy(f => f.Record.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(f => f.Record.BackedUpUtc).First())
                .OrderBy(f => f.Record.SourcePath)
                .ToList();

            foreach (var file in latestByPath)
            {
                var vm = new RestorableFileViewModel(file);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RestorableFileViewModel.IsSelected))
                        UpdateSelectedFiles();
                };
                RestorableFiles.Add(vm);
            }

            StatusText = $"{RestorableFiles.Count:N0} files available for restore.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load files: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateSelectedFiles()
    {
        SelectedFiles.Clear();
        foreach (var f in RestorableFiles.Where(f => f.IsSelected))
            SelectedFiles.Add(f);
    }

    private bool CanRestore()
    {
        return !IsRestoring
               && SelectedFiles.Count > 0
               && !string.IsNullOrWhiteSpace(DestinationPath);
    }

    private void BrowseDestination()
    {
        // Use WinForms FolderBrowserDialog since WPF doesn't have one built in.
        // This avoids adding a NuGet dependency.
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select destination folder for restored files",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DestinationPath = dialog.SelectedPath;
        }
    }

    private async Task RestoreAsync()
    {
        if (!CanRestore())
            return;

        _cts = new CancellationTokenSource();
        IsRestoring = true;
        IsComplete = false;
        StatusText = "Restoring files...";
        ResultDetail = "";

        try
        {
            var filesToRestore = SelectedFiles
                .Select(vm => vm.File)
                .ToList();

            var progress = new Progress<RestoreProgress>(p =>
            {
                CurrentFile = p.CurrentFile;
                RestorePercentage = p.Percentage;
                ProgressText = $"{p.FilesCompleted}/{p.TotalFiles} files " +
                               $"({FormatBytes(p.BytesCompleted)}/{FormatBytes(p.TotalBytes)})";
            });

            var result = await _restoreService.RestoreAsync(
                filesToRestore, DestinationPath, progress, _cts.Token);

            StatusText = result.Success
                ? "Restore completed successfully."
                : "Restore completed with errors.";

            ResultDetail = $"Files restored: {result.FilesRestored:N0}\n" +
                           $"Data restored: {FormatBytes(result.BytesRestored)}";

            if (result.Errors.Count > 0)
            {
                ResultDetail += $"\n\nErrors ({result.Errors.Count}):";
                foreach (var error in result.Errors.Take(10))
                    ResultDetail += $"\n  - {error}";
                if (result.Errors.Count > 10)
                    ResultDetail += $"\n  ... and {result.Errors.Count - 10} more";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Restore cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsRestoring = false;
            IsComplete = true;
            _cts?.Dispose();
            _cts = null;
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

/// <summary>
/// ViewModel for a single restorable file with a selection checkbox.
/// </summary>
public class RestorableFileViewModel : ViewModelBase
{
    private bool _isSelected;

    public RestorableFileViewModel(RestorableFile file)
    {
        File = file;
    }

    public RestorableFile File { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // Convenience properties for binding.
    public string SourcePath => File.Record.SourcePath;
    public string DiscLabel => File.Disc.Label;
    public long SizeBytes => File.Record.SizeBytes;
    public DateTime BackedUpUtc => File.Record.BackedUpUtc;
    public bool IsZipped => File.Record.IsZipped;
    public bool IsSplit => File.Record.IsSplit;
}
