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
        IProgress<string>? progress)
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
                progress.Report($"Deleting files {idx:N0}/{total:N0}: {Path.GetFileName(discRel)}");
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
        // hollow trees after large purges.
        progress?.Report("Cleaning up empty directories...");
        try
        {
            foreach (var subDir in new DirectoryInfo(targetDir).EnumerateDirectories())
                CleanEmptyDirectories(subDir);
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
    {
        try
        {
            if (dir.Name.Equals("_blocks", StringComparison.OrdinalIgnoreCase) ||
                dir.Name.Equals("_filestore", StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var subDir in dir.EnumerateDirectories())
                CleanEmptyDirectories(subDir);

            if (!dir.EnumerateFileSystemInfos().Any())
                dir.Delete();
        }
        catch
        {
            // Permission errors etc. — skip.
        }
    }
}
