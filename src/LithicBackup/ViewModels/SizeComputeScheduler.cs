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
    /// When set, the scheduler computes both unfiltered and filtered sizes
    /// for every directory.  The filtered size is stored on the node's
    /// <see cref="SourceSelectionNodeViewModel.FilteredSize"/> and accounts
    /// for excluded subdirectories so that "selected only" mode shows the
    /// correct backup size without requiring children to be loaded.
    /// </summary>
    internal Func<string, bool>? GlobalExcludeFilter { get; set; }

    /// <summary>
    /// Compute a directory's size synchronously using the shared cache.
    /// Intended for callers that already run on a background thread and
    /// want to avoid the per-node scheduler overhead (e.g. pre-populating
    /// child sizes during directory expansion).
    /// </summary>
    internal (long Size, int FileCount) ComputeInline(
        DirectoryInfo dir, Func<string, bool>? excludeFilter)
    {
        return SourceSelectionNodeViewModel.ComputeDirectorySize(dir, _cache, excludeFilter);
    }

    /// <summary>
    /// Check whether the persistent cache has an entry for the given path.
    /// Used to decide whether inline size computation is likely fast
    /// (cache warm from a prior session) without committing to a full scan.
    /// </summary>
    internal bool HasCacheEntry(string path) => _cache.TryGet(path).HasValue;

    /// <summary>
    /// Try to get the cached recursive total for a directory. This is an
    /// O(1) lookup (single dictionary read + one filesystem stat) — no
    /// subdirectory traversal. Returns <c>null</c> if the directory is not
    /// cached or its timestamp has changed since the cache was written.
    /// </summary>
    internal (long Size, int FileCount)? TryGetCachedSize(string path)
    {
        return SourceSelectionNodeViewModel.TryGetCachedRecursiveSize(path, _cache);
    }

    /// <summary>
    /// Enqueue directory nodes for size computation.
    /// </summary>
    /// <param name="nodes">Directory nodes whose <see cref="SourceSelectionNodeViewModel.Size"/> should be set.</param>
    /// <param name="isPriority">
    /// <c>true</c> for directories the user has just expanded (computed first);
    /// <c>false</c> for background-scan items.
    /// </param>
    /// <param name="progress">
    /// Optional progress reporter. When provided, the scheduler reports
    /// the path of each directory as it starts computing its size (rate-
    /// limited to avoid flooding the UI).
    /// </param>
    /// <returns>A task that completes when every node in this batch has been processed.</returns>
    internal Task EnqueueAsync(IReadOnlyList<SourceSelectionNodeViewModel> nodes,
        bool isPriority, IProgress<string>? progress = null)
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
                }, ExcludeFilter: null, progress));
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

    /// <summary>Maximum results to accumulate before flushing to the UI.</summary>
    private const int FlushBatchSize = 50;

    /// <summary>Minimum interval between progress reports (ms).</summary>
    private const long ProgressIntervalMs = 150;

    /// <summary>Minimum interval between SQLite cache flushes (ms).</summary>
    private const long CacheFlushIntervalMs = 1000;

    private async Task ProcessQueueAsync()
    {
        try
        {
            var batch = new List<(SourceSelectionNodeViewModel Node, long Size,
                                  int FileCount, long FilteredSize,
                                  int FilteredFileCount, Action OnComplete)>();
            long lastProgressTick = 0;
            long lastCacheFlushTick = Environment.TickCount64;

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

                // Check if this node still needs work. A node might
                // already have its unfiltered size (from cached recursive
                // totals) but still need its filtered size computed.
                bool needsUnfiltered = item.Node.Size < 0;
                var gf = GlobalExcludeFilter;
                bool needsFiltered = gf is not null && item.Node.FilteredSize < 0;

                if (!needsUnfiltered && !needsFiltered)
                {
                    item.OnComplete();
                    continue;
                }

                // Rate-limited progress report so the UI shows what's
                // being scanned without flooding the dispatcher.
                if (item.Progress is not null)
                {
                    long now = Environment.TickCount64;
                    if (now - lastProgressTick >= ProgressIntervalMs)
                    {
                        item.Progress.Report(item.Node.Path);
                        lastProgressTick = now;
                    }
                }

                long size = -1;
                int fileCount = -1;
                long filteredSize = -1;
                int filteredFileCount = -1;
                try
                {
                    var dirInfo = new DirectoryInfo(item.Node.Path);

                    if (needsUnfiltered)
                    {
                        // Full recursive computation (uses persistent cache).
                        (size, fileCount) = SourceSelectionNodeViewModel.ComputeDirectorySize(
                            dirInfo, _cache, item.ExcludeFilter);
                    }

                    // When an exclusion filter is active, compute the
                    // filtered size so "selected only" mode is correct.
                    if (needsFiltered)
                    {
                        (filteredSize, filteredFileCount) =
                            SourceSelectionNodeViewModel.ComputeDirectorySizeFiltered(dirInfo, gf!);
                    }
                }
                catch { }

                batch.Add((item.Node, size, fileCount, filteredSize,
                           filteredFileCount, item.OnComplete));

                // Flush when the batch is full or when priority items
                // arrived (so they aren't delayed by the current batch).
                bool shouldFlush;
                lock (_lock)
                {
                    shouldFlush = batch.Count >= FlushBatchSize
                        || _priorityQueue.Count > 0;
                }

                if (shouldFlush)
                    await FlushBatchAsync(batch);

                // Persist computed sizes to SQLite periodically so they
                // survive if the user closes the app mid-computation.
                long nowMs = Environment.TickCount64;
                if (nowMs - lastCacheFlushTick >= CacheFlushIntervalMs)
                {
                    try { _cache.Flush(); }
                    catch { }
                    lastCacheFlushTick = nowMs;
                }
            }

            // Flush any remaining results.
            if (batch.Count > 0)
                await FlushBatchAsync(batch);
        }
        finally
        {
            // Final flush when the queue drains.
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

    /// <summary>
    /// Dispatch a batch of computed sizes to the UI thread in a single call,
    /// then fire all completion callbacks. This avoids per-node UI dispatches
    /// that cause layout storms with SharedSizeGroup columns.
    /// </summary>
    private static async Task FlushBatchAsync(
        List<(SourceSelectionNodeViewModel Node, long Size,
              int FileCount, long FilteredSize,
              int FilteredFileCount, Action OnComplete)> batch)
    {
        if (batch.Count == 0) return;

        // Snapshot and clear before the await so the caller can start
        // filling the next batch immediately.
        var results = batch.ToList();
        batch.Clear();

        // Single UI dispatch for the entire batch — one layout pass
        // instead of 2N passes (IsComputing + Size per node).
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var (node, size, fileCount, filteredSize, filteredFileCount, _) in results)
            {
                if (size >= 0)
                {
                    node.Size = size;
                    node.FileCount = fileCount;
                }
                if (filteredSize >= 0)
                {
                    node.FilteredSize = filteredSize;
                    node.FilteredFileCount = filteredFileCount;
                }
            }
        });

        foreach (var (_, _, _, _, _, onComplete) in results)
            onComplete();
    }

    private sealed record WorkItem(
        SourceSelectionNodeViewModel Node,
        Action OnComplete,
        Func<string, bool>? ExcludeFilter,
        IProgress<string>? Progress);
}
