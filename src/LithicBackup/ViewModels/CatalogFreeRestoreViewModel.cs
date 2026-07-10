using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the catalog-free (disaster-recovery) restore view. Unlike the
/// normal restore, this picks no backup set — the catalog is assumed lost — and
/// works purely from a backup destination directory, reconstructing every file
/// into an output directory via <see cref="ICatalogFreeRestoreService"/>.
/// </summary>
public class CatalogFreeRestoreViewModel : ViewModelBase
{
    private readonly ICatalogFreeRestoreService _service;

    private string _backupPath = "";
    private string _outputPath = "";
    private bool _isRestoring;
    private bool _isComplete;
    private string _statusText =
        "Select a backup folder and an output folder, then click Restore. " +
        "This rebuilds your files directly from the backup, without the catalog.";
    private string _resultDetail = "";
    private double _restorePercentage;
    private string _currentFile = "";
    private string _progressText = "";
    private CancellationTokenSource? _cts;

    /// <summary>Fired when the user clicks "Done" or "Close".</summary>
    public event Action? DoneRequested;

    public CatalogFreeRestoreViewModel(ICatalogFreeRestoreService service)
    {
        _service = service;

        BrowseBackupCommand = new RelayCommand(_ => BrowseBackup());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        RestoreCommand = new RelayCommand(_ => _ = RestoreAsync(), _ => CanRestore());
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRestoring);
        DoneCommand = new RelayCommand(_ => DoneRequested?.Invoke());
    }

    // --- Properties ---

    public string BackupPath
    {
        get => _backupPath;
        set => SetProperty(ref _backupPath, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
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

    public ICommand BrowseBackupCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DoneCommand { get; }

    // --- Logic ---

    private bool CanRestore()
        => !IsRestoring
           && !string.IsNullOrWhiteSpace(BackupPath)
           && !string.IsNullOrWhiteSpace(OutputPath);

    private void BrowseBackup()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the backup folder (the backup destination directory)",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            BackupPath = dialog.SelectedPath;
    }

    private void BrowseOutput()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the output folder for the reconstructed files",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OutputPath = dialog.SelectedPath;
    }

    private async Task RestoreAsync()
    {
        if (!CanRestore())
            return;

        if (!Directory.Exists(BackupPath))
        {
            StatusText = "Backup folder does not exist.";
            return;
        }

        if (string.Equals(
                Path.GetFullPath(BackupPath).TrimEnd('\\', '/'),
                Path.GetFullPath(OutputPath).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Output folder must be different from the backup folder.";
            return;
        }

        _cts = new CancellationTokenSource();
        IsRestoring = true;
        IsComplete = false;
        StatusText = "Rebuilding files from the backup...";
        ResultDetail = "";

        try
        {
            var progress = new Progress<RestoreProgress>(p =>
            {
                CurrentFile = p.CurrentFile;
                RestorePercentage = p.Percentage;
                ProgressText = $"{p.FilesCompleted:N0}/{p.TotalFiles:N0} files " +
                               $"({FormatBytes(p.BytesCompleted)}/{FormatBytes(p.TotalBytes)})";
            });

            var result = await Task.Run(
                () => _service.RestoreAsync(BackupPath, OutputPath, progress, _cts.Token),
                _cts.Token);

            StatusText = result.Success
                ? "Restore completed successfully."
                : "Restore completed with errors.";

            ResultDetail = $"Files restored: {result.FilesRestored:N0}\n" +
                           $"Data restored: {FormatBytes(result.BytesRestored)}";

            if (result.Errors.Count > 0)
            {
                ResultDetail += $"\n\nErrors ({result.Errors.Count:N0}):";
                foreach (var error in result.Errors.Take(10))
                    ResultDetail += $"\n  - {error}";
                if (result.Errors.Count > 10)
                    ResultDetail += $"\n  ... and {result.Errors.Count - 10:N0} more";
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
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{size:N1} {units[unit]}";
    }
}
