using System.Collections.Concurrent;
using System.IO;

namespace LithicBackup.Services;

/// <summary>What the catalog knows about one destination-relative path.</summary>
/// <param name="HasActive">Any non-deleted record exists for the path.</param>
/// <param name="SourcePathWhenAllDeleted">
/// A source path to display, kept only while every record for the path is
/// deleted; an active record clears it.
/// </param>
public readonly record struct DestPathState(bool HasActive, string? SourcePathWhenAllDeleted);

/// <summary>Outcome of a destination walk.</summary>
public sealed record DestinationWalkResult(
    List<(string DiscRel, long Size, string? SourcePath)> Untracked,
    List<(string DiscRel, long Size, string? SourcePath)> CatalogDeleted,
    int DirectoriesSkipped,
    int FilesScanned);

/// <summary>
/// Walks a backup destination and classifies every file against the catalog:
/// tracked (skip), catalog-deleted, or untracked.
///
/// <para><b>This decides what Cleanup offers to delete</b>, so the optimisations
/// below are held to producing byte-identical verdicts, not merely similar ones.
/// <c>tools\dest_walk_test</c> runs the pre-optimisation algorithm and this one
/// over the same trees and compares the result sets exactly.</para>
///
/// <para>Three things changed from the original sequential walk:</para>
///
/// <list type="number">
/// <item><b>One directory enumeration instead of two.</b> It called
/// <c>GetFiles()</c> and then <c>GetDirectories()</c> — two full
/// FindFirstFile/FindNextFile sweeps of the same directory. <c>GetFileSystemInfos()</c>
/// returns both in one sweep and the entries are partitioned on
/// <see cref="FileAttributes.Directory"/>. Across hundreds of thousands of
/// directories on a spinning USB disk that is one seek pass instead of two.</item>
///
/// <item><b>Parallel across directories.</b> The walk was single-threaded.
/// design.md records the measurement for the same kind of metadata-bound work in
/// the deleted-file check: 16 threads gave 235 probes/s against 21
/// single-threaded, because ~96% of these calls never reach the platter (the cost
/// is per-call overhead) and NCQ reorders a deep queue into a shorter sweep.</item>
///
/// <item><b>No per-file string.</b> It built <c>dir + "\" + name</c> for every
/// file just to hash it, plus two more for the <c>.fileref</c>/<c>.dedup</c>
/// probes — then discarded them, because a tracked file needs no string at all.
/// The key is now composed from spans and the path is materialised only for the
/// few files that actually land in a result list.</item>
/// </list>
///
/// <para><b>One intentional behaviour change.</b> A directory that cannot be read
/// now counts <b>once</b> toward <c>DirectoriesSkipped</c>. The original
/// enumerated twice and so counted an entirely unreadable directory twice. The
/// number is a diagnostic shown in the summary, and counting a skipped directory
/// once is what it claims to mean.</para>
/// </summary>
public static class DestinationWalker
{
    /// <summary>
    /// Directories walked at once. Matches the deleted-file check's measured
    /// value; 32 bought only ~20% more there while making the drive unresponsive.
    /// </summary>
    public const int DefaultParallelism = 16;

    private const int ProgressIntervalMs = 500;

    /// <summary>Per-worker accumulator, merged once at the end.</summary>
    private sealed class Bucket
    {
        public readonly List<(string, long, string?)> Untracked = [];
        public readonly List<(string, long, string?)> CatalogDeleted = [];
        public int FilesScanned;
        public int DirectoriesSkipped;
    }

    public static DestinationWalkResult Walk(
        string targetDir,
        Dictionary<UInt128, DestPathState> discPathLookup,
        Func<string, string?> reconstructSourcePath,
        IProgress<string>? progress = null,
        int maxParallelism = DefaultParallelism,
        CancellationToken ct = default)
    {
        var targetInfo = new DirectoryInfo(targetDir);
        if (!targetInfo.Exists)
            throw new DirectoryNotFoundException($"Destination directory not found: {targetDir}");

        var buckets = new ConcurrentBag<Bucket>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastProgressMs = 0;
        int totalFilesScanned = 0;

        // Level-order traversal: every directory at one depth is processed in
        // parallel, and their children become the next level. Simpler than a
        // shared work-stack with idle detection, and a backup destination is wide
        // enough at every level below the root for this to saturate the pool.
        var current = new List<(DirectoryInfo Dir, string RelativeDir)> { (targetInfo, "") };

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxParallelism),
            CancellationToken = ct,
        };

        while (current.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var next = new ConcurrentBag<(DirectoryInfo, string)>();

            Parallel.ForEach(
                current,
                options,
                () => new Bucket(),
                (item, _, bucket) =>
                {
                    ProcessDirectory(
                        item.Dir, item.RelativeDir, discPathLookup, reconstructSourcePath,
                        bucket, next);

                    // Progress is reported from whichever worker notices the
                    // interval has elapsed. A racing double-report is harmless;
                    // the alternative is a lock on the hot path.
                    if (progress is not null)
                    {
                        int scanned = Interlocked.Add(ref totalFilesScanned, bucket.FilesScanned);
                        bucket.FilesScanned = 0;

                        long now = sw.ElapsedMilliseconds;
                        long last = Interlocked.Read(ref lastProgressMs);
                        if (now - last >= ProgressIntervalMs
                            && Interlocked.CompareExchange(ref lastProgressMs, now, last) == last)
                        {
                            progress.Report(
                                $"Scanning: {scanned:N0} files examined — {item.Dir.Name}");
                        }
                    }

                    return bucket;
                },
                buckets.Add);

            current = [.. next];
        }

        // Merge. Order within a level is not deterministic, which the original
        // already documented as cosmetic — it affected only progress messages.
        var untracked = new List<(string, long, string?)>();
        var catalogDeleted = new List<(string, long, string?)>();
        int skipped = 0;
        int scannedTotal = totalFilesScanned;

        foreach (var b in buckets)
        {
            untracked.AddRange(b.Untracked);
            catalogDeleted.AddRange(b.CatalogDeleted);
            skipped += b.DirectoriesSkipped;
            scannedTotal += b.FilesScanned;   // whatever was not yet folded in above
        }

        return new DestinationWalkResult(untracked, catalogDeleted, skipped, scannedTotal);
    }

    private static void ProcessDirectory(
        DirectoryInfo dir,
        string relativeDir,
        Dictionary<UInt128, DestPathState> discPathLookup,
        Func<string, string?> reconstructSourcePath,
        Bucket bucket,
        ConcurrentBag<(DirectoryInfo, string)> next)
    {
        // Skip the shared content-addressed stores. These are not user-visible
        // backup files and are managed internally.
        if (relativeDir.Length > 0 && (
                relativeDir.Equals("_blocks", StringComparison.OrdinalIgnoreCase) ||
                relativeDir.Equals("_filestore", StringComparison.OrdinalIgnoreCase) ||
                relativeDir.StartsWith("_blocks\\", StringComparison.OrdinalIgnoreCase) ||
                relativeDir.StartsWith("_filestore\\", StringComparison.OrdinalIgnoreCase)))
            return;

        // One sweep for files AND subdirectories.
        //
        // The broad catch is deliberate and inherited: the docs list a few
        // exception types, but security providers, antivirus drivers and reparse
        // points throw arbitrary derived ones, and a single unreadable folder
        // must never terminate the walk.
        FileSystemInfo[] entries;
        try
        {
            entries = dir.GetFileSystemInfos();
        }
        catch (Exception ex)
        {
            bucket.DirectoriesSkipped++;
            System.Diagnostics.Debug.WriteLine(
                $"DestinationWalker: skip '{dir.FullName}': {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            bool isDir;
            try
            {
                isDir = (entry.Attributes & FileAttributes.Directory) != 0;
            }
            catch (Exception)
            {
                // Vanished between enumeration and stat — skip just this entry.
                continue;
            }

            if (isDir)
            {
                var sub = (DirectoryInfo)entry;
                next.Add((sub, relativeDir.Length == 0
                    ? sub.Name
                    : relativeDir + "\\" + sub.Name));
                continue;
            }

            var file = (FileInfo)entry;
            bucket.FilesScanned++;

            long size;
            string fileName;
            try
            {
                size = file.Length;
                fileName = file.Name;
            }
            catch (Exception)
            {
                continue;
            }

            // Partial-copy temp files from an interrupted backup
            // (DirectoryBackupService writes "*.lbtmp" before the atomic rename)
            // are not real backup content.
            if (fileName.EndsWith(".lbtmp", StringComparison.OrdinalIgnoreCase))
                continue;

            // A deduplicated file's catalog DiscPath carries a ".fileref"/".dedup"
            // manifest suffix, while the manifest can later be MATERIALISED back
            // into a plain, suffix-less file whose bytes ARE the referenced
            // content. So a plain on-disk file must match an exact-path record OR
            // a "<path>.fileref"/"<path>.dedup" record — otherwise real,
            // catalog-referenced backup content is reported as untracked and
            // "cleaning" it would delete actual backup data. Exact match wins.
            //
            // Composed from spans: no string is built for a file that matches,
            // which is nearly all of them.
            var dirSpan = relativeDir.AsSpan();
            var nameSpan = fileName.AsSpan();

            if (discPathLookup.TryGetValue(DiscPathKey.From(dirSpan, nameSpan), out var state)
                || discPathLookup.TryGetValue(DiscPathKey.From(dirSpan, nameSpan, ".fileref"), out state)
                || discPathLookup.TryGetValue(DiscPathKey.From(dirSpan, nameSpan, ".dedup"), out state))
            {
                if (state.HasActive)
                    continue;   // tracked and live

                bucket.CatalogDeleted.Add(
                    (Combine(relativeDir, fileName), size, state.SourcePathWhenAllDeleted));
            }
            else
            {
                string relativePath = Combine(relativeDir, fileName);
                bucket.Untracked.Add((relativePath, size, reconstructSourcePath(relativePath)));
            }
        }
    }

    private static string Combine(string relativeDir, string fileName)
        => relativeDir.Length == 0 ? fileName : relativeDir + "\\" + fileName;
}
