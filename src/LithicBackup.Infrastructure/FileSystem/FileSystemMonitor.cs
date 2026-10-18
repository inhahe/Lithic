using LithicBackup.Core.Interfaces;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// <see cref="IFileSystemMonitor"/> implementation using <see cref="FileSystemWatcher"/>.
/// </summary>
public class FileSystemMonitorImpl : IFileSystemMonitor
{
    /// <summary>One watcher per watched root. Touched only by the caller's thread.</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

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
        Watch(directories);
    }

    /// <summary>
    /// Watch exactly the listed directories that exist, changing only what differs
    /// from what is watched now: a root that is no longer listed, or no longer
    /// there, stops being watched, and a listed root that exists and is not
    /// watched starts. Watchers of roots that stay keep running and lose nothing.
    /// </summary>
    /// <returns>The roots watching started for just now. Changes made under them
    /// before this call were seen by no watcher.</returns>
    public IReadOnlyList<string> Watch(IReadOnlyList<string> directories)
    {
        var wanted = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);

        foreach (var (root, watcher) in _watchers.ToList())
        {
            if (wanted.Contains(root) && Directory.Exists(root))
                continue;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(root);
        }

        var started = new List<string>();
        foreach (var dir in wanted)
        {
            if (_watchers.ContainsKey(dir) || !Directory.Exists(dir))
                continue;
            _watchers[dir] = CreateWatcher(dir);
            started.Add(dir);
        }
        return started;
    }

    private FileSystemWatcher CreateWatcher(string dir)
    {
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
        // so signal the owner to recheck this entire root.
        watcher.Error += (_, _) =>
            Overflow?.Invoke(this, new FileSystemMonitorOverflowEventArgs { Root = dir });

        return watcher;
    }

    public void Stop()
    {
        foreach (var w in _watchers.Values)
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
