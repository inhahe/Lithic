namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Watches source directories for file changes (for tray mode and live-burn detection).
/// </summary>
public interface IFileSystemMonitor : IDisposable
{
    /// <summary>Raised when a file in a watched directory is created, changed, or deleted.</summary>
    event EventHandler<FileChangeEventArgs> FileChanged;

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

public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed,
}
