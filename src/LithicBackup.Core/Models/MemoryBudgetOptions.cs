namespace LithicBackup.Core.Models;

/// <summary>How the in-memory file-buffer budget is determined.</summary>
public enum MemoryBudgetMode
{
    /// <summary>
    /// Automatic: use the smaller of a percentage of total physical RAM and
    /// (currently-available RAM minus a reserve left free for other programs).
    /// This adapts to machines of different sizes and to current memory
    /// pressure, so a backup never starves the rest of the system.
    /// </summary>
    Auto = 0,

    /// <summary>Use a fixed number of gigabytes regardless of system memory.</summary>
    Fixed = 1,
}

/// <summary>
/// Policy for how much RAM a directory backup may use to buffer file contents
/// in memory (so each file is read from disk only once for both analysis and
/// writing). This is a machine/runtime concern shared by every backup, not a
/// per-set setting. A larger budget means fewer disk reads; a smaller budget
/// leaves more memory for other programs (files that don't fit the budget fall
/// back to being read twice, which is always correct, just slower).
/// </summary>
public class MemoryBudgetOptions
{
    public MemoryBudgetMode Mode { get; set; } = MemoryBudgetMode.Auto;

    /// <summary>
    /// Auto mode: the percentage of total physical RAM the buffer may use.
    /// </summary>
    public int PercentOfTotal { get; set; } = 50;

    /// <summary>
    /// Auto mode: gigabytes of currently-available RAM to leave free for other
    /// programs. The budget never pushes available memory below this.
    /// </summary>
    public double ReserveGb { get; set; } = 2.0;

    /// <summary>Fixed mode: the budget in gigabytes.</summary>
    public double FixedGb { get; set; } = 1.0;

    /// <summary>The built-in default policy (Auto, 50% of total, 2 GB reserved).</summary>
    public static MemoryBudgetOptions Default => new();
}
