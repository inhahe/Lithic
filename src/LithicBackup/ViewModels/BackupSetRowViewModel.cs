using System.Threading;
using System.Windows.Input;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// Wraps a <see cref="BackupSet"/> for the home-screen list, adding the
/// per-set runtime state needed so each set can scan, run, and report progress
/// independently of every other set. This is what makes concurrent backups
/// possible: there is no longer a single global "is a backup running" flag —
/// each row tracks its own <see cref="IsRunning"/> / <see cref="Progress"/> /
/// cancellation, so starting one backup never disables or freezes the others.
/// </summary>
public class BackupSetRowViewModel : ViewModelBase
{
    public BackupSetRowViewModel(BackupSet model)
    {
        Model = model;
    }

    /// <summary>The underlying backup-set model.</summary>
    public BackupSet Model { get; private set; }

    // --- Pass-through display properties (the list item IS this VM) ---
    public int Id => Model.Id;
    public string Name => Model.Name;
    public List<string> SourceRoots => Model.SourceRoots;
    public DateTime? LastBackupUtc => Model.LastBackupUtc;

    /// <summary>
    /// Swap in a freshly-loaded model (after a catalog reload) and refresh the
    /// display bindings, without discarding any live run state on this row.
    /// </summary>
    public void UpdateModel(BackupSet model)
    {
        Model = model;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SourceRoots));
        OnPropertyChanged(nameof(LastBackupUtc));
    }

    private bool _isRunning;
    /// <summary>
    /// True from the moment a backup starts (scan phase) until the completed
    /// progress panel is dismissed. While true, the row shows its progress and
    /// hides its idle action buttons; other rows are unaffected.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set { if (SetProperty(ref _isRunning, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private bool _isChecking;
    /// <summary>True while a pre-backup size check is running for this set.</summary>
    public bool IsChecking
    {
        get => _isChecking;
        set { if (SetProperty(ref _isChecking, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private string _runningStatusText = "";
    /// <summary>
    /// Scan-phase status shown inline on the row before the burn/copy progress
    /// panel appears (e.g. "Scanning… 12,345 files").
    /// </summary>
    public string RunningStatusText
    {
        get => _runningStatusText;
        set => SetProperty(ref _runningStatusText, value);
    }

    private BurnProgressViewModel? _progress;
    /// <summary>
    /// Live progress / completion view model for the in-flight or just-finished
    /// run on this set. Null during the scan phase and when idle.
    /// </summary>
    public BurnProgressViewModel? Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value))
                OnPropertyChanged(nameof(HasProgress));
        }
    }

    /// <summary>True once the burn/copy phase has a progress panel to show.</summary>
    public bool HasProgress => _progress is not null;

    private string _lastResultText = "";
    /// <summary>
    /// Persistent one-line summary of this set's most recent run outcome
    /// (e.g. "Nothing to back up" or "Backed up 1,234 files"). Shown appended
    /// to the idle status line so the result stays visible without a dismissable
    /// panel. Empty when the set has never run this session.
    /// </summary>
    public string LastResultText
    {
        get => _lastResultText;
        set
        {
            if (SetProperty(ref _lastResultText, value))
                OnPropertyChanged(nameof(HasLastResult));
        }
    }

    /// <summary>True when there is a last-result summary to display.</summary>
    public bool HasLastResult => !string.IsNullOrEmpty(_lastResultText);

    private bool _lastResultIsError;
    /// <summary>True when <see cref="LastResultText"/> describes a failure (for styling).</summary>
    public bool LastResultIsError
    {
        get => _lastResultIsError;
        set => SetProperty(ref _lastResultIsError, value);
    }

    // --- Cancellation handles, owned by MainViewModel's run logic ---

    /// <summary>Cancels this set's scan/plan phase (before the burn starts).</summary>
    internal CancellationTokenSource? ScanCts;

    /// <summary>Cancels this set's in-progress size check.</summary>
    internal CancellationTokenSource? CheckCts;
}
