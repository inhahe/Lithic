using System.Diagnostics;
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

    public ICommand CancelCommand => new RelayCommand(_ => Cancel(), _ => IsBurning);
    public ICommand DoneCommand => new RelayCommand(_ => DoneRequested?.Invoke(), _ => IsComplete);

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
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
    /// </summary>
    public void OnBackupProgress(BackupProgress progress)
    {
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
    }

    /// <summary>Creates and returns a CancellationTokenSource for this operation.</summary>
    public CancellationTokenSource StartBurn()
    {
        _cts = new CancellationTokenSource();
        IsBurning = true;
        IsComplete = false;
        StatusText = IsDirectoryMode ? "Copying files..." : "Burning...";
        _stopwatch.Restart();
        return _cts;
    }

    public void CompleteBurn(bool success, string message, string detail = "")
    {
        _stopwatch.Stop();
        IsBurning = false;
        IsComplete = true;
        ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);
        RemainingText = "00:00";
        string verb = IsDirectoryMode ? "Backup" : "Burn";
        StatusText = success ? $"{verb} completed successfully." : $"{verb} failed: {message}";
        ResultDetail = detail;
        _cts?.Dispose();
        _cts = null;
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
