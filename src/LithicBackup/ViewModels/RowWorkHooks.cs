namespace LithicBackup.ViewModels;

/// <summary>
/// How a task window does work that holds its set's row in the main window
/// (see MainViewModel.BeginRowWork): the row shows the progress and refuses
/// Backup, Modify, Restore and Delete until it ends. Cleanup's purge uses it.
/// </summary>
public sealed class RowWorkHooks
{
    /// <summary>What the set's row is doing right now ("backing up", "seeding"), or null when idle.</summary>
    public required Func<string?> DescribeBusy { get; init; }

    /// <summary>Hold the row; its Abort link cancels <c>cancel</c>.</summary>
    public required Action<CancellationTokenSource> Begin { get; init; }

    /// <summary>Show a progress line on the held row.</summary>
    public required Action<string> Progress { get; init; }

    /// <summary>Release the row, leaving a result line on it.</summary>
    public required Action<string, bool> End { get; init; }
}
