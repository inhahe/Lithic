using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LithicBackup.Core;
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
    private string _statsText = "";
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
    /// The newest data report the throttle held back, applied when the interval
    /// is up (<see cref="_flushTimer"/>). The throttle used to DROP such a report,
    /// and the one dropped could be the report that names a new file: a big file
    /// starting within the interval after a small one left the panel on the small
    /// file's name for as long as the big one took - minutes on a .py, once.
    /// </summary>
    private BackupProgress? _heldBack;
    private DispatcherTimer? _flushTimer;

    /// <summary>
    /// The copy's time-left estimate, kept between reports that cannot refine it
    /// (too early in the copy, or a status report). Null until there is one.
    /// </summary>
    private TimeSpan? _copyRemaining;

    /// <summary>
    /// Stopwatch time and bytes at the copy's first report, so its rate covers the
    /// copy alone and not the phases before it, which on a large run take minutes.
    /// Null until the copy reports.
    /// </summary>
    private long? _copyStartMs;
    private long _copyStartBytes;

    /// <summary>
    /// The phase on screen (<see cref="BackupProgress.PhaseFilesTotal"/>), by name
    /// and total, with the stopwatch time and file count it started at, so its rate
    /// covers that phase alone, and the count it last showed. Null when the panel
    /// is not showing a phase.
    /// </summary>
    private string? _phase;
    private long _phaseTotal;
    private long _phaseStartMs;
    private long _phaseStartDone;
    private long _phaseLastDone;

    /// <summary>
    /// Cancel was clicked: "Cancelling..." stays up until the run ends, whatever
    /// reports are still on their way.
    /// </summary>
    private bool _cancelling;

    /// <summary>
    /// Signaling primitive shared with the backup service. When reset (paused),
    /// the file-copy loop blocks until set (resumed) or the cancellation token fires.
    /// </summary>
    public ManualResetEventSlim PauseEvent { get; } = new(true);

    /// <summary>Fired when the user clicks "Done" after burn completes.</summary>
    public event Action? DoneRequested;

    /// <summary>
    /// Trigger the same finalization as the "Dismiss" button without a click —
    /// used to auto-clear the completed panel after a clean backup so the result
    /// drops straight onto the row's persistent line.
    /// </summary>
    public void RequestDone() => DoneRequested?.Invoke();

    public int CurrentDisc
    {
        get => _currentDisc;
        set => SetProperty(ref _currentDisc, value);
    }

    public int TotalDiscs
    {
        get => _totalDiscs;
        set
        {
            if (SetProperty(ref _totalDiscs, value))
                OnPropertyChanged(nameof(ShowDiscProgress));
        }
    }

    /// <summary>
    /// Whether to show the separate per-disc progress bar. This is only
    /// meaningful for an optical burn that actually spans more than one disc:
    /// for a single-disc burn the per-disc bar just duplicates the overall bar,
    /// and for a directory backup it has no meaning at all. Gating on
    /// <see cref="TotalDiscs"/> &gt; 1 (which defaults to 0) also prevents the
    /// bar from flashing on at initiation before real disc counts arrive.
    /// </summary>
    public bool ShowDiscProgress => IsBurning && !IsDirectoryMode && TotalDiscs > 1;

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

    /// <summary>
    /// The line under the bar: bytes copied of the total, or for a phase that works
    /// through files, how many it gets through a second; then the time so far and
    /// the time left. Empty until a report brings figures.
    /// </summary>
    public string StatsText
    {
        get => _statsText;
        set => SetProperty(ref _statsText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsBurning
    {
        get => _isBurning;
        set
        {
            if (SetProperty(ref _isBurning, value))
                OnPropertyChanged(nameof(ShowDiscProgress));
        }
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
        set
        {
            if (SetProperty(ref _isDirectoryMode, value))
                OnPropertyChanged(nameof(ShowDiscProgress));
        }
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
    public ICommand DismissFailedFilesCommand => new RelayCommand(
        _ => DismissFailedFiles(), _ => HasFailedFiles);

    private void Cancel()
    {
        _cts?.Cancel();
        _cancelling = true;
        StatusText = "Cancelling...";
    }

    private void Pause()
    {
        PauseEvent.Reset();
        _statusBeforePause = StatusText;
        if (!_cancelling)
            StatusText = "Paused";
        _stopwatch.Stop();
        IsPaused = true;
    }

    private void Resume()
    {
        PauseEvent.Set();
        if (!_cancelling)
            StatusText = _statusBeforePause;
        _stopwatch.Start();
        IsPaused = false;
    }

    /// <summary>
    /// Update the view from a <see cref="BackupProgress"/> event.
    /// Throttled to avoid excessive UI redraw (see <see cref="ProgressCadence.IntervalMs"/>).
    /// Status-message reports are never throttled here: the phases that send
    /// them many times over keep to the cadence themselves.
    /// </summary>
    public void OnBackupProgress(BackupProgress progress)
    {
        // Status-message reports (e.g. "Verifying backup...") always go through
        // without throttling.  Also update numeric fields when provided so
        // progress bars reflect the actual state (e.g. 100% after copying).
        if (!string.IsNullOrEmpty(progress.StatusMessage))
        {
            // A status report supersedes any data report still waiting its turn.
            _heldBack = null;

            if (progress.PhaseFilesTotal > 0)
            {
                ApplyPhaseProgress(progress);
                return;
            }

            ShowStatus(progress.StatusMessage);
            CurrentFile = progress.CurrentFile;

            if (progress.BytesTotalAll > 0)
            {
                OverallPercentage = progress.OverallPercentage;
                StatsText = Stats(BytesText(progress), _copyRemaining);
            }
            else if (_phase is not null)
            {
                // A phase's bar and rate would otherwise stay up, stale, under a
                // message that is not about them (waiting for the set's lock, say).
                OverallPercentage = 0;
                StatsText = "";
            }
            _phase = null;
            CurrentFilePercentage = 0;
            CurrentFileSizeText = "";
            return;
        }

        // Throttle data-progress updates - but hold the newest one back rather
        // than dropping it, and show it when the interval is up (see _heldBack).
        long nowMs = _stopwatch.ElapsedMilliseconds;
        long sinceLast = nowMs - _lastUiUpdateMs;
        if (sinceLast < ProgressCadence.IntervalMs)
        {
            _heldBack = progress;
            ScheduleFlush(ProgressCadence.IntervalMs - sinceLast);
            return;
        }

        _heldBack = null;
        ApplyDataProgress(progress, nowMs);
    }

    /// <summary>Show a held-back report once the throttle interval is up.</summary>
    private void ScheduleFlush(long dueInMs)
    {
        if (_flushTimer is null)
        {
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background);
            _flushTimer.Tick += (_, _) =>
            {
                _flushTimer!.Stop();
                if (_heldBack is { } held && IsBurning)
                {
                    _heldBack = null;
                    ApplyDataProgress(held, _stopwatch.ElapsedMilliseconds);
                }
            };
        }

        if (_flushTimer.IsEnabled)
            return;   // already due; it will show whatever is newest by then
        _flushTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, dueInMs));
        _flushTimer.Start();
    }

    private void ApplyDataProgress(BackupProgress progress, long nowMs)
    {
        _lastUiUpdateMs = nowMs;
        _phase = null;

        CurrentDisc = progress.CurrentDisc;
        TotalDiscs = progress.TotalDiscs;
        OverallPercentage = progress.OverallPercentage;
        CurrentFile = progress.CurrentFile;

        // Estimate remaining time from the copy's own byte throughput.
        if (_copyStartMs is null)
        {
            _copyStartMs = nowMs;
            _copyStartBytes = progress.BytesWrittenTotal;
        }
        if (progress.BytesWrittenTotal > 0 && progress.BytesTotalAll > 0)
        {
            double elapsedSec = (nowMs - _copyStartMs.Value) / 1000.0;
            if (elapsedSec > 0.5) // need at least half a second of data
            {
                double bytesPerSec = (progress.BytesWrittenTotal - _copyStartBytes) / elapsedSec;
                long bytesRemaining = progress.BytesTotalAll - progress.BytesWrittenTotal;
                _copyRemaining = bytesRemaining > 0 && bytesPerSec > 0
                    ? Eta(bytesRemaining / bytesPerSec)
                    : TimeSpan.Zero;
            }
        }
        StatsText = Stats(BytesText(progress), _copyRemaining);

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

        // What is being done to the current file: copying, unless the engine
        // says otherwise (e.g. reading it to check for duplicates first).
        ShowStatus(string.IsNullOrEmpty(progress.CurrentFileActivity)
            ? "Copying files..."
            : progress.CurrentFileActivity + "...");
    }

    /// <summary>
    /// Show a report from a phase that works through files, such as a check before
    /// the copy, as "&lt;phase&gt; (done of total)...", with the bar at the phase's
    /// own percentage, the file it is on, and under the bar how many files it gets
    /// through a second and how long it has left.
    /// </summary>
    private void ApplyPhaseProgress(BackupProgress progress)
    {
        long nowMs = _stopwatch.ElapsedMilliseconds;
        long done = progress.PhaseFilesDone, total = progress.PhaseFilesTotal;

        // A new phase - or the same one begun again, which counts from the start
        // or over another total - gets a rate window of its own.
        if (progress.StatusMessage != _phase || total != _phaseTotal || done < _phaseLastDone)
        {
            _phase = progress.StatusMessage;
            _phaseTotal = total;
            _phaseStartMs = nowMs;
            _phaseStartDone = done;
        }
        _phaseLastDone = done;

        ShowStatus($"{progress.StatusMessage} ({done:N0} of {total:N0})...");
        CurrentFile = progress.CurrentFile;
        OverallPercentage = (double)done / total * 100;
        CurrentFilePercentage = 0;
        CurrentFileSizeText = "";

        // A rate says what a percentage cannot: 3,000 files/s against millions
        // is minutes, 20 is days. Measured over this phase alone, and not before
        // a second of it has passed, when the figure would be noise.
        double seconds = (nowMs - _phaseStartMs) / 1000.0;
        long doneThisPhase = done - _phaseStartDone;
        if (seconds >= 1 && doneThisPhase > 0)
        {
            double perSecond = doneThisPhase / seconds;
            // Below one a second, say how long one takes: "0 files/s" next to a
            // moving count reads as stalled.
            string rate = perSecond >= 1
                ? $"{perSecond:N0} files/s"
                : $"{1 / perSecond:N0} s per file";
            StatsText = Stats(rate, Eta((total - done) / perSecond));
        }
        else
        {
            StatsText = Stats(null, null);
        }
    }

    /// <summary>
    /// Put a report's status on screen - except while paused, when "Paused" stays
    /// and the status is kept for <see cref="Resume"/> instead, and while
    /// cancelling, when "Cancelling..." stays. A report already on its way when the
    /// button was clicked must not make the run read as carrying on.
    /// </summary>
    private void ShowStatus(string status)
    {
        if (_cancelling)
            return;
        if (IsPaused)
            _statusBeforePause = status;
        else
            StatusText = status;
    }

    /// <summary>
    /// The line under the bar (<see cref="StatsText"/>).
    /// </summary>
    /// <param name="figures">What has been done, or how fast; null to leave it out.</param>
    /// <param name="remaining">Time left, or null while there is no estimate yet.</param>
    private string Stats(string? figures, TimeSpan? remaining)
    {
        string times = $"{FormatTimeSpan(_stopwatch.Elapsed)} elapsed  ·  ~"
            + (remaining is { } left ? FormatTimeSpan(left) : "--:--") + " remaining";
        return figures is null ? times : $"{figures}  ·  {times}";
    }

    private static string BytesText(BackupProgress progress)
        => $"{FormatBytes(progress.BytesWrittenTotal)} / {FormatBytes(progress.BytesTotalAll)}";

    /// <summary>
    /// <paramref name="seconds"/> as a time-left estimate; null when it is too
    /// large to be one (a rate near zero), which <see cref="TimeSpan"/> would
    /// otherwise reject by throwing.
    /// </summary>
    private static TimeSpan? Eta(double seconds)
        => seconds < TimeSpan.FromDays(999).TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Creates and returns a CancellationTokenSource for this operation.</summary>
    public CancellationTokenSource StartBurn()
    {
        _cts = new CancellationTokenSource();
        PauseEvent.Set();
        IsPaused = false;
        IsBurning = true;
        IsComplete = false;
        StatusText = IsDirectoryMode ? "Copying files..." : "Burning...";
        StatsText = "";
        _copyRemaining = null;
        _copyStartMs = null;
        _phase = null;
        _cancelling = false;
        _lastUiUpdateMs = -ProgressCadence.IntervalMs; // ensure first report gets through
        _stopwatch.Restart();
        return _cts;
    }

    public void CompleteBurn(
        bool success, string message, string detail = "",
        IReadOnlyList<FailedFile>? failedFiles = null,
        bool cancelled = false)
    {
        // Nothing still waiting for the throttle may overwrite the result.
        _heldBack = null;
        _flushTimer?.Stop();

        PauseEvent.Set();
        IsPaused = false;
        _stopwatch.Stop();
        IsBurning = false;
        IsComplete = true;
        string verb = IsDirectoryMode ? "Backup" : "Burn";
        if (cancelled)
            // A user-initiated abort is not a failure — say so plainly rather
            // than surfacing the internal "Cancelled by user." as an error.
            StatusText = $"{verb} cancelled.";
        else if (success && (failedFiles is null or { Count: 0 }))
            StatusText = $"{verb} completed.";
        else if (failedFiles is { Count: > 0 })
            StatusText = $"{verb} completed with errors — {failedFiles.Count:N0} file(s) skipped or failed.";
        else
            StatusText = string.IsNullOrEmpty(message) ? $"{verb} failed." : $"{verb} failed: {message}";
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

        // The Dismiss / failed-file action buttons gate on IsComplete and
        // HasFailedFiles, but RelayCommand only re-evaluates CanExecute when
        // CommandManager raises RequerySuggested. Setting IsComplete makes the
        // Dismiss button visible via its binding, yet its enabled state stays
        // stale until the next stray input event — so it first renders disabled
        // (grey) and a click is swallowed while it re-queries, then works on the
        // second click. Force the requery now so it appears enabled immediately.
        CommandManager.InvalidateRequerySuggested();
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

    /// <summary>Clear the failed/skipped list so its widget disappears.</summary>
    private void DismissFailedFiles()
    {
        FailedFiles.Clear();
        OnPropertyChanged(nameof(HasFailedFiles));
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

    private static string FormatBytes(long bytes) => $"{bytes:N0}";

    private static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
