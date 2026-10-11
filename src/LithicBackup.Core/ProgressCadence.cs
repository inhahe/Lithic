using System.Diagnostics;

namespace LithicBackup.Core;

/// <summary>
/// How often progress reaches the screen: one value for every layer, so a service
/// loop and the display it feeds tick together, plus a gate that holds a loop to it.
/// </summary>
/// <remarks>
/// A loop that could report after every item (a file, a catalog row) calls
/// <see cref="IsDue"/> after each one and reports only when it returns true.
/// Reporting every item floods the UI thread, and reporting every N items is too
/// slow when items are slow and too busy when they are fast. One instance per
/// loop: it is not thread-safe.
/// </remarks>
public sealed class ProgressCadence
{
    /// <summary>
    /// Minimum time between two progress updates, in milliseconds: two a second.
    /// Every progress display and every throttled reporter uses this value.
    /// </summary>
    public const int IntervalMs = 500;

    private static readonly long IntervalTicks = IntervalMs * Stopwatch.Frequency / 1000;

    /// <summary>
    /// <see cref="Stopwatch"/> timestamp from which the next report is due. Zero,
    /// so the first call is due at once.
    /// </summary>
    private long _nextDue;

    /// <summary>
    /// True on the first call, and then whenever <see cref="IntervalMs"/> has
    /// passed since the last call that returned true.
    /// </summary>
    public bool IsDue()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextDue)
            return false;
        _nextDue = now + IntervalTicks;
        return true;
    }
}
