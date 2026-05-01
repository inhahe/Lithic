using System.IO;
using System.Threading;

namespace LithicBackup.ViewModels;

/// <summary>
/// Centralised, priority-aware scheduler for directory-size computation.
/// Directories the user has just expanded are computed before background
/// (full-tree) scan items, so the visible directory always fills in first.
/// </summary>
/// <remarks>
/// A single worker processes one directory at a time to avoid I/O contention.
/// Between each directory the worker re-checks the priority queue, so
/// user-triggered expansions cut ahead of the background scan with at most
/// one directory of latency.
/// </remarks>
public sealed class SizeComputeScheduler
{
    private readonly object _lock = new();
    private readonly LinkedList<WorkItem> _priorityQueue = new();
    private readonly LinkedList<WorkItem> _backgroundQueue = new();
    private bool _workerRunning;
    private readonly DirectorySizeCache _cache = new();

    /// <summary>
    /// Enqueue directory nodes for size computation.
    /// </summary>
    /// <param name="nodes">Directory nodes whose <see cref="SourceSelectionNodeViewModel.Size"/> should be set.</param>
    /// <param name="isPriority">
    /// <c>true</c> for directories the user has just expanded (computed first);
    /// <c>false</c> for background-scan items.
    /// </param>
    /// <returns>A task that completes when every node in this batch has been processed.</returns>
    internal Task EnqueueAsync(IReadOnlyList<SourceSelectionNodeViewModel> nodes, bool isPriority)
    {
        var pending = new List<SourceSelectionNodeViewModel>();
        foreach (var n in nodes)
        {
            if (n.Size < 0 && n.IsDirectory)
                pending.Add(n);
        }

        if (pending.Count == 0)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remaining = pending.Count;

        lock (_lock)
        {
            var queue = isPriority ? _priorityQueue : _backgroundQueue;
            foreach (var node in pending)
            {
                queue.AddLast(new WorkItem(node, () =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                        tcs.TrySetResult();
                }));
            }
        }

        EnsureWorkerRunning();
        return tcs.Task;
    }

    private void EnsureWorkerRunning()
    {
        bool shouldStart;
        lock (_lock)
        {
            shouldStart = !_workerRunning;
            if (shouldStart)
                _workerRunning = true;
        }

        if (shouldStart)
            _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                WorkItem item;
                lock (_lock)
                {
                    if (_priorityQueue.Count > 0)
                    {
                        item = _priorityQueue.First!.Value;
                        _priorityQueue.RemoveFirst();
                    }
                    else if (_backgroundQueue.Count > 0)
                    {
                        item = _backgroundQueue.First!.Value;
                        _backgroundQueue.RemoveFirst();
                    }
                    else
                    {
                        break;
                    }
                }

                // Already computed by another code path (e.g. node was
                // duplicated across a priority and background batch).
                if (item.Node.Size >= 0)
                {
                    item.OnComplete();
                    continue;
                }

                // Mark this single node as actively computing so the UI
                // shows "working..." only for the directory in progress.
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    item.Node.IsComputing = true);

                try
                {
                    var dirInfo = new DirectoryInfo(item.Node.Path);
                    var (size, fileCount) = SourceSelectionNodeViewModel.ComputeDirectorySizeCached(dirInfo, _cache);

                    // Marshal to the UI thread so the bound property fires
                    // PropertyChanged on the correct dispatcher.
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.Node.Size = size;
                        item.Node.FileCount = fileCount;
                        item.Node.IsComputing = false;
                    });
                }
                catch
                {
                    // Size computation failed (access denied, I/O error, etc.).
                    // Leave at -1; clear the computing flag so UI shows blank
                    // rather than a perpetual "working..." indicator.
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        item.Node.IsComputing = false);
                }

                item.OnComplete();
            }
        }
        finally
        {
            // Flush cached sizes to disk when the queue drains.
            try { _cache.Flush(); }
            catch { }

            // Clear the flag and restart if items arrived between the
            // empty-queue check and here (race with EnqueueAsync).
            bool restartNeeded;
            lock (_lock)
            {
                _workerRunning = false;
                restartNeeded = _priorityQueue.Count > 0 || _backgroundQueue.Count > 0;
            }

            if (restartNeeded)
                EnsureWorkerRunning();
        }
    }

    private sealed record WorkItem(SourceSelectionNodeViewModel Node, Action OnComplete);
}
