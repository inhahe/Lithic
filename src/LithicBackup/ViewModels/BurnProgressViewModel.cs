using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the burn progress view. Displays real-time progress
/// during a disc burn or directory backup operation.
/// </summary>
public class BurnProgressViewModel : ViewModelBase
{
    private int _currentDisc;
    private int _totalDiscs;
    private string _currentFile = "";
    private double _discPercentage;
    private double _overallPercentage;
    private string _bytesWrittenText = "";
    private string _elapsedText = "00:00";
    private string _remainingText = "--:--";
    private string _statusText = "Preparing...";
    private bool _isBurning;
    private bool _isComplete;
    private string _resultDetail = "";
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _stopwatch = new();
    private bool _isDirectoryMode;
    private double _currentFilePercentage;
    private string _currentFileSizeText = "";
    private bool _isPaused;
    private string _statusBeforePause = "";
    private long _lastUiUpdateMs;

    /// <summary>
    /// Signaling primitive shared with the backup service. When reset (paused),
    /// the file-copy loop blocks until set (resumed) or the cancellation token fires.
    /// </summary>
    public ManualResetEventSlim PauseEvent { get; } = new(true);

    /// <summary>Fired when the user clicks "Done" after burn completes.</summary>
    public event Action? DoneRequested;

    public int CurrentDisc
    {
        get => _currentDisc;
        set => SetProperty(ref _currentDisc, value);
    }

    public int TotalDiscs
    {
        get => _totalDiscs;
        set => SetProperty(ref _totalDiscs, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        set => SetProperty(ref _currentFile, value);
    }

    public double DiscPercentage
    {
        get => _discPercentage;
        set => SetProperty(ref _discPercentage, value);
    }

    public double OverallPercentage
    {
        get => _overallPercentage;
        set => SetProperty(ref _overallPercentage, value);
    }

    public string BytesWrittenText
    {
        get => _bytesWrittenText;
        set => SetProperty(ref _bytesWrittenText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        set => SetProperty(ref _elapsedText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        set => SetProperty(ref _remainingText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsBurning
    {
        get => _isBurning;
        set => SetProperty(ref _isBurning, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    public string ResultDetail
    {
        get => _resultDetail;
        set => SetProperty(ref _resultDetail, value);
    }

    /// <summary>Files that failed during the backup, displayed in a scrollable list.</summary>
    public ObservableCollection<FailedFile> FailedFiles { get; } = [];

    /// <summary>True when there are any failed files to display.</summary>
    public bool HasFailedFiles => FailedFiles.Count > 0;

    /// <summary>When true, this is a directory backup (not a disc burn).</summary>
    public bool IsDirectoryMode
    {
        get => _isDirectoryMode;
        set => SetProperty(ref _isDirectoryMode, value);
    }

    /// <summary>Progress percentage for the current file (0-100).</summary>
    public double CurrentFilePercentage
    {
        get => _currentFilePercentage;
        set => SetProperty(ref _currentFilePercentage, value);
    }

    /// <summary>Formatted bytes text for the current file (e.g. "45.2 MB / 512.0 MB").</summary>
    public string CurrentFileSizeText
    {
        get => _currentFileSizeText;
        set => SetProperty(ref _currentFileSizeText, value);
    }

    /// <summary>Whether the backup is currently paused.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value))
                OnPropertyChanged(nameof(PauseResumeLabel));
        }
    }

    /// <summary>Label for the pause/resume toggle button.</summary>
    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";

    public ICommand CancelCommand => new RelayCommand(_ => Cancel(), _ => IsBurning);
    public ICommand DoneCommand => new RelayCommand(_ => DoneRequested?.Invoke(), _ => IsComplete);
    public ICommand PauseResumeCommand => new RelayCommand(
        _ => { if (IsPaused) Resume(); else Pause(); },
        _ => IsBurning && IsDirectoryMode);
    public ICommand CopyFailedFilesCommand => new RelayCommand(
        _ => CopyFailedFilesToClipboard(), _ => HasFailedFiles);
    public ICommand ExportFailedFilesCommand => new RelayCommand(
        _ => ExportFailedFilesToFile(), _ => HasFailedFiles);

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    private void Pause()
    {
        PauseEvent.Reset();
        _statusBeforePause = StatusText;
        StatusText = "Paused";
        _stopwatch.Stop();
        IsPaused = true;
    }

    private void Resume()
    {
        PauseEvent.Set();
        StatusText = _statusBeforePause;
        _stopwatch.Start();
        IsPaused = false;
    }

    /// <summary>
    /// Update the view from a <see cref="BurnProgress"/> event.
    /// Called on the UI thread via IProgress marshaling.
    /// </summary>
    public void OnBurnProgress(BurnProgress progress)
    {
        DiscPercentage = progress.Percentage;
        BytesWrittenText = $"{FormatBytes(progress.BytesWritten)} / {FormatBytes(progress.TotalBytes)}";
        ElapsedText = FormatTimeSpan(progress.Elapsed);
        RemainingText = progress.EstimatedRemaining.HasValue
            ? FormatTimeSpan(progress.EstimatedRemaining.Value)
            : "--:--";
        CurrentFile = progress.CurrentFile;
    }

    /// <summary>
    /// Update the view from a <see cref="BackupProgress"/> event.
    /// Throttled to avoid excessive UI redraw (see <see cref="ProgressUpdateIntervalMs"/>).
    /// Status-message-only reports (planning phases) are never throttled.
    /// </summary>
    public void OnBackupProgress(BackupProgress progress)
    {
        // Status-message-only reports (e.g. "Verifying backup...") always go through.
        if (!string.IsNullOrEmpty(progress.StatusMessage))
        {
            StatusText = progress.StatusMessage;
            CurrentFile = progress.CurrentFile;
            return;
        }

        // Throttle data-progress updates.
        long nowMs = _stopwatch.ElapsedMilliseconds;
        if (nowMs - _lastUiUpdateMs < ProgressUpdateIntervalMs)
            return;
        _lastUiUpdateMs = nowMs;

        CurrentDisc = progress.CurrentDisc;
        TotalDiscs = progress.TotalDiscs;
        OverallPercentage = progress.OverallPercentage;
        CurrentFile = progress.CurrentFile;
        BytesWrittenText = $"{FormatBytes(progress.BytesWrittenTotal)} / {FormatBytes(progress.BytesTotalAll)}";

        // Always update elapsed from our own stopwatch so it works for
        // both disc burns and directory backups.
        ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);

        // Estimate remaining time from byte throughput.
        if (progress.BytesWrittenTotal > 0 && progress.BytesTotalAll > 0)
        {
            double elapsedSec = _stopwatch.Elapsed.TotalSeconds;
            if (elapsedSec > 0.5) // need at least half a second of data
            {
                double bytesPerSec = progress.BytesWrittenTotal / elapsedSec;
                long bytesRemaining = progress.BytesTotalAll - progress.BytesWrittenTotal;
                if (bytesRemaining > 0 && bytesPerSec > 0)
                {
                    RemainingText = FormatTimeSpan(
                        TimeSpan.FromSeconds(bytesRemaining / bytesPerSec));
                }
                else
                {
                    RemainingText = "00:00";
                }
            }
        }

        // Per-file progress (updated during large file copies).
        if (progress.CurrentFileTotalBytes > 0)
        {
            CurrentFilePercentage = (double)progress.CurrentFileBytesWritten
                / progress.CurrentFileTotalBytes * 100;
            CurrentFileSizeText = $"{FormatBytes(progress.CurrentFileBytesWritten)} / {FormatBytes(progress.CurrentFileTotalBytes)}";
        }
        else
        {
            CurrentFilePercentage = 0;
            CurrentFileSizeText = "";
        }

        // If the burn layer has its own per-disc progress, use it for disc percentage.
        if (progress.DiscBurnProgress is not null)
        {
            DiscPercentage = progress.DiscBurnProgress.Percentage;
        }

        StatusText = "Copying files...";
    }

    /// <summary>Creates and returns a CancellationTokenSource for this operation.</summary>
    public CancellationTokenSource StartBurn()
    {
        _cts = new CancellationTokenSource();
        PauseEvent.Set();
        IsPaused = false;
        IsBurning = true;
        IsComplete = false;
        StatusText = IsDirectoryMode ? "Copying files..." : "Burning...";
        _stopwatch.Restart();
        return _cts;
    }

    public void CompleteBurn(
        bool success, string message, string detail = "",
        IReadOnlyList<FailedFile>? failedFiles = null)
    {
        PauseEvent.Set();
        IsPaused = false;
        _stopwatch.Stop();
        IsBurning = false;
        IsComplete = true;
        ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);
        RemainingText = "00:00";
        string verb = IsDirectoryMode ? "Backup" : "Burn";
        StatusText = success ? $"{verb} completed successfully." : $"{verb} failed: {message}";
        ResultDetail = detail;

        FailedFiles.Clear();
        if (failedFiles is not null)
        {
            foreach (var f in failedFiles)
                FailedFiles.Add(f);
        }
        OnPropertyChanged(nameof(HasFailedFiles));

        _cts?.Dispose();
        _cts = null;
    }

    private string FormatFailedFilesList()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Failed/skipped files: {FailedFiles.Count}");
        sb.AppendLine();
        sb.AppendLine("Path\tError\tAction");
        foreach (var f in FailedFiles)
            sb.AppendLine($"{f.Path}\t{f.Error}\t{f.ActionTaken}");
        return sb.ToString();
    }

    private void CopyFailedFilesToClipboard()
    {
        Clipboard.SetText(FormatFailedFilesList());
    }

    private void ExportFailedFilesToFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export failed/skipped files",
            Filter = "Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = $"failed-files-{DateTime.Now:yyyy-MM-dd-HHmmss}",
        };

        if (dialog.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dialog.FileName, FormatFailedFilesList());
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

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
