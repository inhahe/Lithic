namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Watches source directories for file changes (for tray mode and live-burn detection).
/// </summary>
public interface IFileSystemMonitor : IDisposable
{
    /// <summary>Raised when a file in a watched directory is created, changed, or deleted.</summary>
    event EventHandler<FileChangeEventArgs> FileChanged;

    /// <summary>
    /// Raised when the underlying watcher could not deliver every change — most
    /// commonly an internal-buffer overflow when too many files changed at once,
    /// so some per-file events were dropped. The consumer must recheck the whole
    /// affected root (e.g. a timestamp/size rescan) rather than trusting the
    /// individual <see cref="FileChanged"/> events, because the change list is
    /// incomplete.
    /// </summary>
    event EventHandler<FileSystemMonitorOverflowEventArgs> Overflow;

    /// <summary>Start watching the specified directories.</summary>
    void Start(IReadOnlyList<string> directories);

    /// <summary>Stop watching all directories.</summary>
    void Stop();
}

public class FileChangeEventArgs : EventArgs
{
    public required string FullPath { get; init; }
    public required FileChangeType ChangeType { get; init; }
}

/// <summary>
/// Signals that per-file change events were lost for a watched root (buffer
/// overflow or a watcher error), so the root must be rechecked wholesale.
/// </summary>
public class FileSystemMonitorOverflowEventArgs : EventArgs
{
    /// <summary>The watched root directory whose change stream became incomplete.</summary>
    public required string Root { get; init; }
}

public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed,
}
