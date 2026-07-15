using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// The three ways a user can answer the plan-time "many files are incompatible with
/// this disc format" warning.
/// </summary>
public enum UdfWarningChoice
{
    /// <summary>Switch this run to UDF so the incompatible files land unzipped.</summary>
    SwitchToUdf,

    /// <summary>Keep the selected format; the incompatible files get zipped at burn time.</summary>
    KeepFormat,

    /// <summary>Abort the backup.</summary>
    Cancel,
}

/// <summary>
/// Pure decision logic behind the plan-time UDF-compatibility warning. Kept UI-free
/// (no <c>MessageBox</c>) so the WPF view-model and the headless test harness drive the
/// exact same rules — the view-model only supplies the user's <see cref="UdfWarningChoice"/>
/// (from a real dialog) while a test can inject one programmatically.
/// </summary>
public static class DiscCompatibilityAdvisor
{
    /// <summary>Warn if at least this fraction of planned files are incompatible.</summary>
    public const double FileFractionThreshold = 0.05;

    /// <summary>Warn if at least this fraction of planned bytes are incompatible.</summary>
    public const double ByteFractionThreshold = 0.05;

    /// <summary>Warn if at least this many files are incompatible (absolute floor).</summary>
    public const int FileCountThreshold = 20;

    /// <summary>
    /// Decide whether the plan-time warning is worth showing. Only
    /// <see cref="ZipMode.IncompatibleOnly"/> silently zips for compatibility, and only
    /// a non-UDF format has a more permissive option to suggest; beyond that the
    /// incompatibility has to be "significant" (<see cref="FileFractionThreshold"/> of
    /// files, <see cref="ByteFractionThreshold"/> of bytes, or
    /// <see cref="FileCountThreshold"/> files) before we interrupt the user.
    /// </summary>
    public static bool ShouldWarn(ZipMode zipMode, FilesystemType format, DiscCompatibilitySummary summary)
    {
        if (zipMode != ZipMode.IncompatibleOnly || format == FilesystemType.UDF)
            return false;
        if (!summary.HasIncompatible)
            return false;

        return summary.IncompatibleFileFraction >= FileFractionThreshold
            || summary.IncompatibleByteFraction >= ByteFractionThreshold
            || summary.IncompatibleFiles >= FileCountThreshold;
    }

    /// <summary>
    /// Apply the user's choice to the job. On <see cref="UdfWarningChoice.SwitchToUdf"/>
    /// the job's <see cref="BackupJob.FilesystemType"/> is flipped to UDF in place (the
    /// bin-packing is capacity-based and format-independent, so no re-plan/re-scan is
    /// needed — the burn reads the format at write time). Returns whether the backup
    /// should proceed (<c>false</c> only on <see cref="UdfWarningChoice.Cancel"/>).
    /// </summary>
    public static bool ApplyChoice(BackupJob job, UdfWarningChoice choice)
    {
        switch (choice)
        {
            case UdfWarningChoice.SwitchToUdf:
                job.FilesystemType = FilesystemType.UDF;
                return true;
            case UdfWarningChoice.KeepFormat:
                return true;
            default:
                return false;
        }
    }
}
