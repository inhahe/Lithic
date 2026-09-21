using LithicBackup.Infrastructure.Data;

namespace LithicBackup.Worker;

/// <summary>
/// Remembers when each set last completed a WHOLE-TREE scan, across restarts.
///
/// <para>Continuous sets get their per-file work from the USN journal and were
/// given no periodic full scan at all. <see cref="BackupWorker"/> now runs one as
/// a safety net, and that needs a clock the service's own lifetime cannot reset:
/// timing it from in-memory state alone would mean a machine that restarts the
/// service daily never reaches the threshold, which is precisely the machine most
/// likely to have missed something.</para>
///
/// <para>A plain file per set rather than a catalog column, deliberately. The
/// catalog's on-disc format is a compatibility surface — changing it invalidates
/// existing backup sets — and this is a scheduling hint, not backup data. Losing
/// it costs one extra scan and nothing else, which is why every failure path here
/// degrades to "scan sooner" instead of throwing.</para>
///
/// <para>Stored under <see cref="CatalogLocation.RootDirectory"/>, which the
/// backup engine excludes unconditionally, so these never appear in a backup of
/// C: and can never make a set look changed.</para>
/// </summary>
public static class FullScanStamp
{
    /// <summary>Where the stamp for <paramref name="setId"/> lives.</summary>
    public static string PathFor(int setId)
        => Path.Combine(CatalogLocation.RootDirectory, "state", $"fullscan-{setId}.txt");

    /// <summary>
    /// The recorded time, or <c>null</c> when there is none or it cannot be read.
    /// Null means "no idea", which callers must treat as "due for a scan".
    /// </summary>
    public static DateTime? Read(int setId)
    {
        try
        {
            var path = PathFor(setId);
            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path).Trim();
            return DateTime.TryParse(
                text, null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var when)
                ? when.ToUniversalTime()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Record a completed full scan. Returns false if it could not be persisted —
    /// the caller carries on either way, it just loses the cross-restart memory.
    /// </summary>
    public static bool Write(int setId, DateTime whenUtc)
    {
        try
        {
            var path = PathFor(setId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, whenUtc.ToString("O"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
