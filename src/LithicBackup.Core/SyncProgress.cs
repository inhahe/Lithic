namespace LithicBackup.Core;

/// <summary>
/// An <see cref="IProgress{T}"/> that calls its handler on the reporting thread,
/// at once. <see cref="Progress{T}"/> instead posts each report to the context it
/// captured - with none, through the thread pool, which can reorder a count - so
/// it suits the UI end of a report; this suits a loop handing counts to a
/// throttle or a counter on its own thread.
/// </summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
