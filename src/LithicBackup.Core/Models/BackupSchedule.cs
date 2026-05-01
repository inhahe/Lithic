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
