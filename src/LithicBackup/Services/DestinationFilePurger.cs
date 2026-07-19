using System.Diagnostics;
using System.IO;

namespace LithicBackup.Services;

/// <summary>
/// Shared destination-side deletion logic used by both the Cleanup
/// (orphaned-directories) purge and the post-edit "remove deleted sources"
/// flow.  Deletes destination-relative files under a target directory and
/// sweeps the empty subdirectories left behind.
/// </summary>
/// <remarks>
/// Extracted from <c>OrphanedDirectoriesViewModel.PurgeSelected</c> so the two
/// call sites stay behaviourally identical — in particular the read-only-flag
/// clearing (a large fraction of backed-up content is read-only; without this
/// the delete fails silently and the file keeps getting re-reported) and the
/// <c>_blocks</c>/<c>_filestore</c> exclusions during the empty-directory sweep.
/// </remarks>
internal static class DestinationFilePurger
{
    /// <summary>Throttle cadence for progress reports (matches the rest of the app).</summary>
    private const int ProgressIntervalMs = 500;

    /// <summary>
    /// Physically delete the given destination-relative files under
    /// <paramref name="targetDir"/>, clearing the read-only attribute first,
    /// then sweep away any subdirectories left empty.  Best-effort: permission
    /// or lock errors on individual files are counted as failures and skipped
    /// rather than aborting the whole operation.
    /// </summary>
    /// <param name="targetDir">Destination root the disc-relative paths are under.</param>
    /// <param name="discRelPaths">
    /// Destination-relative file paths (backslash-separated) to delete.  The
    /// caller is responsible for de-duplication if a file might appear twice.
    /// </param>
    /// <param name="progress">Optional throttled status reporter.</param>
    /// <returns>Count of files deleted, count of deletion failures, and bytes freed.</returns>
    public static (int FilesDeleted, int Failures, long BytesFreed) DeleteFilesAndSweep(
        string targetDir, IReadOnlyCollection<string> discRelPaths,
        IProgress<ProgressReport>? progress)
    {
        int filesDeleted = 0;
        int failures = 0;
        long bytesFreed = 0;

        var sw = Stopwatch.StartNew();
        // Negative initial value guarantees the first Report fires immediately.
        long lastProgressMs = -ProgressIntervalMs;

        int idx = 0;
        int total = discRelPaths.Count;
        foreach (var discRel in discRelPaths)
        {
            idx++;
            long nowMs = sw.ElapsedMilliseconds;
            if (progress is not null
                && (nowMs - lastProgressMs >= ProgressIntervalMs || idx == total))
            {
                lastProgressMs = nowMs;
                int pct = total == 0 ? 100 : (int)(idx * 100L / total);
                progress.Report(new ProgressReport(
                    $"Deleting backed-up files {idx:N0}/{total:N0} ({pct}%): {Path.GetFileName(discRel)}",
                    pct));
            }

            string fullPath = Path.Combine(targetDir, discRel);
            try
            {
                if (File.Exists(fullPath))
                {
                    var fi = new FileInfo(fullPath);
                    long size = fi.Length;
                    // Clear the read-only attribute before deleting:
                    // FileInfo.Delete() throws UnauthorizedAccessException on a
                    // read-only file, and a LOT of backed-up content carries that
                    // flag — git object/pack files are always read-only, as is
                    // anything copied from a read-only source. Without this the
                    // delete fails silently, the file survives, and the next scan
                    // re-reports it.
                    if (fi.IsReadOnly)
                        fi.IsReadOnly = false;
                    fi.Delete();
                    bytesFreed += size;
                    filesDeleted++;
                }
            }
            catch
            {
                // Permission / locked-file errors — count and skip.
                failures++;
            }
        }

        // Sweep empty subdirectories so the destination doesn't accumulate
        // hollow trees after large purges.  This walk can traverse the whole
        // destination tree (tens of thousands of directories), so it drives a
        // throttled live counter rather than a single static message — without
        // it the UI sits on "Cleaning up empty directories..." looking frozen
        // for the entire sweep.
        progress?.Report("Cleaning up empty directories...");
        try
        {
            var sweep = new SweepProgress(progress, sw);
            foreach (var subDir in new DirectoryInfo(targetDir).EnumerateDirectories())
                CleanEmptyDirectories(subDir, sweep);
        }
        catch
        {
            // Best-effort — don't fail the whole purge over this.
        }

        return (filesDeleted, failures, bytesFreed);
    }

    /// <summary>
    /// Recursively delete empty subdirectories, preserving the content-addressed
    /// store roots (<c>_blocks</c>, <c>_filestore</c>) which must never be
    /// removed even when they momentarily appear empty.
    /// </summary>
    public static void CleanEmptyDirectories(DirectoryInfo dir)
        => CleanEmptyDirectories(dir, null);

    /// <summary>
    /// Recursive worker for <see cref="CleanEmptyDirectories(DirectoryInfo)"/>,
    /// threading an optional <see cref="SweepProgress"/> so large sweeps can
    /// report how many directories they've scanned so far.
    /// </summary>
    private static void CleanEmptyDirectories(DirectoryInfo dir, SweepProgress? sweep)
    {
        try
        {
            if (dir.Name.Equals("_blocks", StringComparison.OrdinalIgnoreCase) ||
                dir.Name.Equals("_filestore", StringComparison.OrdinalIgnoreCase))
                return;

            sweep?.Tick();

            foreach (var subDir in dir.EnumerateDirectories())
                CleanEmptyDirectories(subDir, sweep);

            if (!dir.EnumerateFileSystemInfos().Any())
                dir.Delete();
        }
        catch
        {
            // Permission errors etc. — skip.
        }
    }

    /// <summary>
    /// Throttled directory-scan counter for the empty-directory sweep.  Shares
    /// the caller's stopwatch so the whole purge uses one monotonic clock, and
    /// reports at the same <see cref="ProgressIntervalMs"/> cadence as the rest
    /// of the operation.
    /// </summary>
    private sealed class SweepProgress(IProgress<ProgressReport>? progress, Stopwatch sw)
    {
        private int _scanned;
        private long _lastMs = long.MinValue;

        public void Tick()
        {
            _scanned++;
            if (progress is null) return;
            long nowMs = sw.ElapsedMilliseconds;
            if (nowMs - _lastMs < ProgressIntervalMs) return;
            _lastMs = nowMs;
            progress.Report($"Cleaning empty directories: {_scanned:N0} scanned");
        }
    }
}
