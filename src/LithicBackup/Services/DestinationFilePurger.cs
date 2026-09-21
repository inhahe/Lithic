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
    /// Whether a configured destination directory is actually reachable right
    /// now. A backup set's target is a saved PATH STRING, so having one
    /// configured says nothing about whether the removable drive is plugged in,
    /// the share is up, or the letter still points at the same volume.
    ///
    /// <para>Every caller that is about to mark catalog rows deleted must ask
    /// this FIRST. The deletion loop below skips a path that isn't there without
    /// raising anything, which is correct for a file an earlier pass already
    /// removed, but means an absent destination looks exactly like a completed
    /// no-op — and by then the catalog has already been rewritten.</para>
    /// </summary>
    public static bool IsAvailable(string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            return false;
        try
        {
            return Directory.Exists(targetDir);
        }
        catch
        {
            // Malformed path, unmapped letter, or a share that throws rather
            // than returning false — all "not usable".
            return false;
        }
    }

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
    /// <returns>
    /// Count of files deleted, count of deletion failures, bytes freed, and the
    /// count that were ALREADY ABSENT from the destination.
    ///
    /// <para>That last one is reported rather than ignored because it is the
    /// only externally visible symptom of a destination that is not really
    /// there: <see cref="File.Exists"/> answers false for every path under an
    /// unplugged drive, so without it a purge that deleted nothing at all is
    /// indistinguishable from one that had nothing to do. A caller that has
    /// verified the destination is present can still treat a nonzero count as
    /// benign (the file was cleaned by an earlier pass).</para>
    /// </returns>
    /// <param name="ct">
    /// Cancellation for the deletion pass.  Honoured COOPERATIVELY — the loop
    /// breaks and the method returns the counts collected so far rather than
    /// throwing.  That is deliberate: by the time this runs the catalog
    /// transaction has already committed, so the caller must still be able to
    /// report what was actually deleted.  Files not yet reached keep their
    /// (deleted) catalog rows and resurface as "catalog-deleted files" on the
    /// next destination scan, which is an already-supported state.
    /// </param>
    public static (int FilesDeleted, int Failures, long BytesFreed, int AlreadyAbsent) DeleteFilesAndSweep(
        string targetDir, IReadOnlyCollection<string> discRelPaths,
        IProgress<ProgressReport>? progress, CancellationToken ct = default)
    {
        int filesDeleted = 0;
        int failures = 0;
        int alreadyAbsent = 0;
        long bytesFreed = 0;

        var sw = Stopwatch.StartNew();
        // Negative initial value guarantees the first Report fires immediately.
        long lastProgressMs = -ProgressIntervalMs;

        int idx = 0;
        int total = discRelPaths.Count;
        foreach (var discRel in discRelPaths)
        {
            if (ct.IsCancellationRequested) break;

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
                else
                {
                    alreadyAbsent++;
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
        // Skipped entirely when cancelled: the sweep is a tidy-up, and walking
        // the whole destination tree is exactly the kind of long unresponsive
        // tail a user who just pressed Cancel is trying to get away from.
        if (!ct.IsCancellationRequested)
        {
            progress?.Report("Cleaning up empty directories...");
            try
            {
                var sweep = new SweepProgress(progress, sw);
                foreach (var subDir in new DirectoryInfo(targetDir).EnumerateDirectories())
                {
                    if (ct.IsCancellationRequested) break;
                    CleanEmptyDirectories(subDir, sweep, ct);
                }
            }
            catch
            {
                // Best-effort — don't fail the whole purge over this.
            }
        }

        return (filesDeleted, failures, bytesFreed, alreadyAbsent);
    }

    /// <summary>
    /// Recursively delete empty subdirectories, preserving the content-addressed
    /// store roots (<c>_blocks</c>, <c>_filestore</c>) which must never be
    /// removed even when they momentarily appear empty.
    /// </summary>
    public static void CleanEmptyDirectories(DirectoryInfo dir)
        => CleanEmptyDirectories(dir, null, default);

    /// <summary>
    /// Recursive worker for <see cref="CleanEmptyDirectories(DirectoryInfo)"/>,
    /// threading an optional <see cref="SweepProgress"/> so large sweeps can
    /// report how many directories they've scanned so far.  Cancellation stops
    /// the walk where it stands; a half-finished sweep is harmless, since the
    /// only thing it does is remove directories that are already empty.
    /// </summary>
    private static void CleanEmptyDirectories(
        DirectoryInfo dir, SweepProgress? sweep, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return;

            if (dir.Name.Equals("_blocks", StringComparison.OrdinalIgnoreCase) ||
                dir.Name.Equals("_filestore", StringComparison.OrdinalIgnoreCase))
                return;

            sweep?.Tick();

            foreach (var subDir in dir.EnumerateDirectories())
            {
                if (ct.IsCancellationRequested) return;
                CleanEmptyDirectories(subDir, sweep, ct);
            }

            if (ct.IsCancellationRequested) return;

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
