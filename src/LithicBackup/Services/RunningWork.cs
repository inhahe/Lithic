namespace LithicBackup.Services;

/// <summary>
/// Work the app must not simply drop when it exits: what a progress dialog runs,
/// and what <c>MainViewModel.RunOnRowAsync</c> runs for a held row (the removals
/// after an edit, a source-drive remap). On a real exit <c>MainWindow</c> calls
/// <see cref="CancelAndWait"/>. A process that simply ends runs no clean-up: an
/// export would leave gigabytes of temporary files beside the export, an import
/// a set without the history it claims, a removal its catalog changed without
/// its files deleted, and a remap its records on one drive and its selection on
/// the other.
/// </summary>
public static class RunningWork
{
    private static readonly List<(CancellationTokenSource? Cancel, Task Work)> Running = [];

    /// <summary>
    /// Count <paramref name="work"/> as running until the returned object is
    /// disposed, which the caller does once it has awaited the work.
    /// </summary>
    /// <param name="cancel">What stops the work on exit; null when it cannot be stopped and is only waited for.</param>
    public static IDisposable Track(Task work, CancellationTokenSource? cancel)
    {
        var entry = (cancel, work);
        lock (Running)
            Running.Add(entry);
        return new Untrack(entry);
    }

    /// <summary>
    /// The app is exiting: cancel all running work that can be cancelled, and
    /// wait, up to <paramref name="timeout"/>, for all of it to finish.
    /// </summary>
    public static void CancelAndWait(TimeSpan timeout)
    {
        Task[] work;
        lock (Running)
        {
            foreach (var (cancel, _) in Running)
                cancel?.Cancel();
            work = Running.Select(r => r.Work).ToArray();
        }
        // Blocking is safe: the work runs on the thread pool, and a progress
        // report only posts to this thread, never waits for it.
        try
        {
            Task.WaitAll(work, timeout);
        }
        catch (AggregateException)
        {
            // Work that failed or was cancelled has ended, which is all an exit
            // waits for; each caller reports its own failure.
        }
    }

    private sealed class Untrack((CancellationTokenSource? Cancel, Task Work) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (Running)
                Running.Remove(entry);
        }
    }
}
