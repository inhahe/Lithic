using System.Collections.Concurrent;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.Services;

/// <summary>
/// Background monitoring service that watches configured directories for
/// file changes and fires <see cref="BackupSuggested"/> when enough changes
/// accumulate. Designed to be hosted by a system-tray icon or background
/// service without depending on any UI framework.
/// </summary>
public class TrayService : IDisposable
{
    private readonly IFileSystemMonitor _monitor;
    private readonly ICatalogRepository _catalog;

    /// <summary>
    /// Pending file paths that have changed since the last backup.
    /// Using a <see cref="ConcurrentDictionary{TKey,TValue}"/> (as a set) to
    /// deduplicate paths -- the same file changing multiple times counts once.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _checkTimer;
    private bool _disposed;

    /// <summary>
    /// Number of accumulated changes that triggers a <see cref="BackupSuggested"/> event.
    /// </summary>
    public int ChangeThreshold { get; set; } = 100;

    /// <summary>
    /// Raised when enough file changes have accumulated to suggest running a backup.
    /// The string argument describes what changed.
    /// </summary>
    public event Action<string>? BackupSuggested;

    public TrayService(IFileSystemMonitor monitor, ICatalogRepository catalog)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Number of unique files that have changed since the last clear.
    /// </summary>
    public int PendingChangeCount => _pendingPaths.Count;

    /// <summary>
    /// Start watching the specified directories and checking for accumulated
    /// changes at the given interval.
    /// </summary>
    public void Start(IReadOnlyList<string> directories, TimeSpan checkInterval)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (directories.Count == 0)
            return;

        _monitor.FileChanged += OnFileChanged;
        _monitor.Start(directories);

        _checkTimer = new Timer(
            OnCheckTimerElapsed,
            state: null,
            dueTime: checkInterval,
            period: checkInterval);
    }

    /// <summary>
    /// Clear all pending changes, e.g. after a backup has been completed.
    /// </summary>
    public void ClearPendingChanges()
    {
        _pendingPaths.Clear();
    }

    /// <summary>
    /// Stop watching directories and dispose the check timer.
    /// </summary>
    public void Stop()
    {
        _monitor.FileChanged -= OnFileChanged;
        _monitor.Stop();

        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    private void OnFileChanged(object? sender, FileChangeEventArgs e)
    {
        // Deduplicate: same file changing multiple times counts once.
        _pendingPaths[e.FullPath] = 0;
    }

    private void OnCheckTimerElapsed(object? state)
    {
        int count = _pendingPaths.Count;
        if (count >= ChangeThreshold)
        {
            BackupSuggested?.Invoke(
                $"{count} file(s) have changed since the last backup. Consider running a backup.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
