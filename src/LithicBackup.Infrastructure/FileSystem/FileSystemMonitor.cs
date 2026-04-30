using LithicBackup.Core.Interfaces;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// <see cref="IFileSystemMonitor"/> implementation using <see cref="FileSystemWatcher"/>.
/// </summary>
public class FileSystemMonitorImpl : IFileSystemMonitor
{
    private readonly List<FileSystemWatcher> _watchers = [];

    public event EventHandler<FileChangeEventArgs>? FileChanged;

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
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, e) => OnChange(e.FullPath, FileChangeType.Created);
            watcher.Changed += (_, e) => OnChange(e.FullPath, FileChangeType.Modified);
            watcher.Deleted += (_, e) => OnChange(e.FullPath, FileChangeType.Deleted);
            watcher.Renamed += (_, e) => OnChange(e.FullPath, FileChangeType.Renamed);

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
