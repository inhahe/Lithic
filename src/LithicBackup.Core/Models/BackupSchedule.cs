namespace LithicBackup.Core.Models;

/// <summary>
/// Schedule configuration for automated backups. Stored as part of
/// <see cref="JobOptions"/> (JSON-serialized on BackupSet).
/// </summary>
public class BackupSchedule
{
    /// <summary>Whether scheduled/automated backup is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>The scheduling mode.</summary>
    public ScheduleMode Mode { get; set; } = ScheduleMode.Interval;

    /// <summary>Hours between backups (for <see cref="ScheduleMode.Interval"/>).</summary>
    public double IntervalHours { get; set; } = 24;

    /// <summary>Hour of day to run (0–23) for <see cref="ScheduleMode.Daily"/>.</summary>
    public int DailyHour { get; set; } = 2;

    /// <summary>Minute of the hour to run (0–59) for <see cref="ScheduleMode.Daily"/>.</summary>
    public int DailyMinute { get; set; }

    /// <summary>
    /// Seconds to wait after the last detected file change before triggering
    /// a backup (for <see cref="ScheduleMode.Continuous"/>). Prevents thrashing
    /// while the user is actively editing files.
    /// </summary>
    public int DebounceSeconds { get; set; } = 60;

    /// <summary>
    /// Upper bound (seconds) on how long a continuously-changing file may stay
    /// pending before it is backed up regardless of ongoing edits (for
    /// <see cref="ScheduleMode.Continuous"/>). The per-file debounce
    /// (<see cref="DebounceSeconds"/>) never fires while a file keeps changing,
    /// so this cap guarantees an actively-edited file (e.g. a document you save
    /// repeatedly, or a busy log) still gets versioned periodically. When the
    /// cap is lower than the debounce it dominates, yielding one version every
    /// <see cref="MaxWaitSeconds"/> during a long editing burst. Defaults to 300
    /// (5 minutes). Values &lt;= 0 fall back to that default in the Worker.
    /// </summary>
    public int MaxWaitSeconds { get; set; } = 300;

    /// <summary>
    /// How often (seconds) the Worker polls the change journal / watcher for
    /// this set's sources in <see cref="ScheduleMode.Continuous"/> mode. Lower
    /// values detect changes sooner at the cost of more frequent journal reads.
    /// The Worker runs a single shared poll loop, so the effective interval is
    /// the smallest configured value across all active continuous sets.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;
}

/// <summary>
/// How automated backups are triggered.
/// </summary>
public enum ScheduleMode
{
    /// <summary>Run every N hours.</summary>
    Interval,

    /// <summary>Run once per day at a specific time.</summary>
    Daily,

    /// <summary>
    /// Watch source directories for changes and run a backup automatically
    /// after a quiet period (debounce). Requires the Worker Service.
    /// </summary>
    Continuous,
}
