using LithicBackup.Core.Interfaces;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// <see cref="IFileSystemMonitor"/> implementation using <see cref="FileSystemWatcher"/>.
/// </summary>
public class FileSystemMonitorImpl : IFileSystemMonitor
{
    private readonly List<FileSystemWatcher> _watchers = [];

    /// <summary>
    /// Largest buffer Windows allows for a <see cref="FileSystemWatcher"/> (64 KB).
    /// Using the maximum minimizes how often a burst of changes overflows and drops
    /// events; overflows that still happen are surfaced via <see cref="Overflow"/>.
    /// </summary>
    private const int MaxInternalBufferSize = 64 * 1024;

    public event EventHandler<FileChangeEventArgs>? FileChanged;

    public event EventHandler<FileSystemMonitorOverflowEventArgs>? Overflow;

    public void Start(IReadOnlyList<string> directories)
    {
        Stop();

        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir))
                continue;

            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                InternalBufferSize = MaxInternalBufferSize,
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, e) => OnChange(e.FullPath, FileChangeType.Created);
            watcher.Changed += (_, e) => OnChange(e.FullPath, FileChangeType.Modified);
            watcher.Deleted += (_, e) => OnChange(e.FullPath, FileChangeType.Deleted);
            watcher.Renamed += (_, e) => OnChange(e.FullPath, FileChangeType.Renamed);

            // Any Error — most importantly InternalBufferOverflowException — means
            // some change events were lost. We can't know which files those were,
            // so signal the owner to recheck this entire root. `dir` is captured
            // per-iteration (C# foreach semantics), so each watcher reports its own
            // root.
            watcher.Error += (_, _) =>
                Overflow?.Invoke(this, new FileSystemMonitorOverflowEventArgs { Root = dir });

            _watchers.Add(watcher);
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    private void OnChange(string fullPath, FileChangeType changeType)
    {
        FileChanged?.Invoke(this, new FileChangeEventArgs
        {
            FullPath = fullPath,
            ChangeType = changeType,
        });
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
