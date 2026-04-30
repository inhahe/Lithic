using System.Collections.Concurrent;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.Services;

/// <summary>
/// Detects when files being backed up change during the burn process by
/// monitoring source directories in real time via <see cref="IFileSystemMonitor"/>.
/// Files that change while they are being staged are tracked so the orchestrator
/// can re-add them to a later disc.
/// </summary>
public class LiveBurnCoordinator : IDisposable
{
    private readonly IFileSystemMonitor _monitor;

    /// <summary>
    /// Files currently being staged (source path -> true).
    /// Thread-safe because FileSystemWatcher events arrive on thread-pool threads.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _stagedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files that changed while they were in the staged set.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _changedDuringStaging = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public LiveBurnCoordinator(IFileSystemMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    /// <summary>
    /// Begin monitoring the specified source directories for changes.
    /// </summary>
    public void Start(IReadOnlyList<string> sourceDirs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _monitor.FileChanged += OnFileChanged;
        _monitor.Start(sourceDirs);
    }

    /// <summary>
    /// Mark a file as currently being staged (copied to the staging directory).
    /// While a file is registered, any change detected by the monitor will
    /// add it to the "changed during staging" set.
    /// </summary>
    public void RegisterStagedFile(string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        _stagedFiles[sourcePath] = true;
    }

    /// <summary>
    /// Mark a file as no longer being staged (copy completed).
    /// </summary>
    public void UnregisterStagedFile(string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        _stagedFiles.TryRemove(sourcePath, out _);
    }

    /// <summary>
    /// Returns the list of files that changed while they were being staged.
    /// The caller can use this after staging each disc to re-add those files
    /// to a later disc.
    /// </summary>
    public IReadOnlyList<string> GetChangedFiles()
    {
        return _changedDuringStaging.Keys.ToList();
    }

    /// <summary>
    /// Clear the list of files that changed during staging, e.g. after
    /// they have been re-queued for a later disc.
    /// </summary>
    public void ClearChangedFiles()
    {
        _changedDuringStaging.Clear();
    }

    /// <summary>
    /// Stop monitoring directories and unsubscribe from events.
    /// </summary>
    public void Stop()
    {
        _monitor.FileChanged -= OnFileChanged;
        _monitor.Stop();
    }

    private void OnFileChanged(object? sender, FileChangeEventArgs e)
    {
        // If this file is currently being staged, record it as changed.
        if (_stagedFiles.ContainsKey(e.FullPath))
        {
            _changedDuringStaging[e.FullPath] = true;
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
