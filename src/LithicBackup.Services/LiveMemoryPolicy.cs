using System.Diagnostics;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// The memory budget a running directory backup buffers against: the job's own
/// until the machine settings file is written after that budget was read, then the
/// file's. So lowering the budget in the app reaches a run already under way, the
/// Worker's included, while a job given a budget of its own (a harness forcing
/// streaming, say) keeps it unless someone actually saves new settings.
/// </summary>
/// <remarks>
/// Looks at the file's write time about once a second, and reads the file only
/// when that time has moved. A file that cannot be read leaves the budget as it
/// was and is tried again at the next look.
/// </remarks>
public sealed class LiveMemoryPolicy
{
    private const long LookIntervalMs = 1000;

    private readonly string? _settingsPath;
    private readonly Func<long> _clockMs;
    private MemoryBudgetOptions _current;
    private DateTime? _seenWriteTimeUtc;
    private long? _lastLookMs;

    /// <param name="seed">The job's budget, in force until the settings change.</param>
    /// <param name="seedWrittenUtc">The settings file's write time when the seed
    /// was read from it (<see cref="Core.Models.BackupJob.MemoryBudgetWrittenUtc"/>),
    /// so a save made between then and now is followed at the first look; null
    /// when the seed is the job's own, which stands until the file is next written.</param>
    /// <param name="settingsPath">The machine settings file; null for the real one.</param>
    /// <param name="clockMs">Milliseconds elapsed, for a test to drive; null for a
    /// stopwatch started now.</param>
    public LiveMemoryPolicy(
        MemoryBudgetOptions seed, DateTime? seedWrittenUtc = null,
        string? settingsPath = null, Func<long>? clockMs = null)
    {
        _current = seed;
        _settingsPath = settingsPath;
        if (clockMs is null)
        {
            var stopwatch = Stopwatch.StartNew();
            clockMs = () => stopwatch.ElapsedMilliseconds;
        }
        _clockMs = clockMs;
        _seenWriteTimeUtc = seedWrittenUtc ?? MachineSettings.LastWriteTimeUtc(settingsPath);
    }

    /// <summary>The budget in force now.</summary>
    public MemoryBudgetOptions Current()
    {
        long now = _clockMs();
        if (_lastLookMs is { } last && now - last < LookIntervalMs)
            return _current;
        _lastLookMs = now;

        var written = MachineSettings.LastWriteTimeUtc(_settingsPath);
        if (written is null || written == _seenWriteTimeUtc)
            return _current;

        switch (MachineSettings.TryLoad(out var settings, _settingsPath))
        {
            case MachineSettings.LoadResult.Loaded:
                _current = settings.MemoryBudget ?? _current;
                _seenWriteTimeUtc = written;
                break;
            case MachineSettings.LoadResult.Absent:
                // Deleted during the run: nothing new to follow.
                _seenWriteTimeUtc = written;
                break;
        }
        return _current;
    }
}
