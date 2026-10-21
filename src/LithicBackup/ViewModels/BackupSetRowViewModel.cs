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
    /// The set has automated backups switched on but held until a backup is run
    /// from the app (<see cref="JobOptions.HoldAutomatedBackups"/>), so the row
    /// says so and offers to resume them. Not shown for a manual-only set, where
    /// there is nothing to hold.
    /// </summary>
    public bool IsAutomationHeld =>
        Model.JobOptions is { HoldAutomatedBackups: true, Schedule.Enabled: true };

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
        OnPropertyChanged(nameof(IsAutomationHeld));
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

    /// <summary>
    /// Work is under way on the set: <see cref="IsRunning"/>, unless all that is
    /// left is a finished run's panel, which stays up until it is dismissed.
    /// Whatever refuses other work on the set asks this, and so does the hold
    /// against the Worker (<c>MainViewModel.SyncAppHold</c>), which also lets a
    /// paused backup go.
    /// </summary>
    public bool IsWorking => IsRunning && Progress is not { IsComplete: true };

    private string? _busyActivity;
    /// <summary>
    /// What is holding this row in <see cref="IsRunning"/> when it is not a backup:
    /// "seeding", "clearing its history". Null during a backup and when idle.
    /// Work that writes the set's catalog takes the row exactly as a backup does,
    /// so the set's Backup, Modify, Restore and Delete actions are unavailable
    /// until it finishes - a backup started mid-seed copied every file the seed
    /// had not reached yet - and it carries on, visible here, after the editor
    /// that started it is closed.
    /// </summary>
    public string? BusyActivity
    {
        get => _busyActivity;
        set => SetProperty(ref _busyActivity, value);
    }

    private string _backgroundWorkText = "";
    /// <summary>
    /// Progress of read-only work an editor started and was closed during (a
    /// duplicate analysis), shown on its own line with a Stop link. It blocks
    /// nothing. Empty when there is none.
    /// </summary>
    public string BackgroundWorkText
    {
        get => _backgroundWorkText;
        set
        {
            if (SetProperty(ref _backgroundWorkText, value))
                OnPropertyChanged(nameof(HasBackgroundWork));
        }
    }

    /// <summary>True while <see cref="BackgroundWorkText"/> has something to show.</summary>
    public bool HasBackgroundWork => !string.IsNullOrEmpty(_backgroundWorkText);

    /// <summary>Stops the work shown in <see cref="BackgroundWorkText"/>.</summary>
    internal Action? StopBackgroundWork;

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

    private bool _isDestinationFull;
    /// <summary>
    /// True while this set's destination drive is (practically) full. Driven by
    /// the GUI's <c>DestinationSpaceMonitor</c> sweep and refreshed periodically,
    /// so it stays accurate — set when the drive fills, cleared when space is
    /// freed. A persistent warning line on the row reflects it (see
    /// <see cref="DestinationFullText"/>), unlike the one-shot full-drive dialog.
    /// </summary>
    public bool IsDestinationFull
    {
        get => _isDestinationFull;
        set => SetProperty(ref _isDestinationFull, value);
    }

    private string _destinationFullText = "";
    /// <summary>
    /// The persistent "destination drive full" message shown on the row while
    /// <see cref="IsDestinationFull"/> is true (e.g.
    /// "Destination drive J: is full (512 MB free)"). Empty otherwise.
    /// </summary>
    public string DestinationFullText
    {
        get => _destinationFullText;
        set => SetProperty(ref _destinationFullText, value);
    }

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
