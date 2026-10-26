using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Verifies the integrity of a directory-mode backup against its source.
/// Three independent checks are performed:
///   1. Coverage — every current source file has an active catalog record.
///   2. Backing storage — every active record's on-disk file actually exists
///      (plain copies and <c>.dedup</c>/<c>.fileref</c> manifests alike), and
///      every block a <c>.dedup</c> manifest references exists in the block store.
///   3. Reference resolution — every <c>.fileref</c> (which stores no bytes)
///      resolves, by content hash via the catalog, to an existing plain copy.
/// </summary>
public class VerifyService : IVerifyService
{
    private readonly ICatalogRepository _catalog;
    private readonly IFileScanner _scanner;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public VerifyService(ICatalogRepository catalog, IFileScanner scanner)
    {
        _catalog = catalog;
        _scanner = scanner;
    }

    public async Task<VerifyResult> VerifyAsync(
        BackupJob job,
        string targetDirectory,
        bool verifyContents = false,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!job.BackupSetId.HasValue)
        {
            return new VerifyResult
            {
                Issues =
                [
                    new VerifyIssue
                    {
                        Path = targetDirectory,
                        Detail = "Backup set has not been backed up yet — nothing to verify.",
                        Kind = VerifyIssueKind.MissingFromBackup,
                    },
                ],
            };
        }

        int backupSetId = job.BackupSetId.Value;
        var issues = new List<VerifyIssue>();
        string blockStoreDir = Path.Combine(targetDirectory, "_blocks");

        // What each step has done so far, read by the reports while it works.
        var live = new LiveCounts();

        // --- Step 1: the full catalog for this set (all versions, current + _prev),
        // in no particular order: sorting it first is most of the read. Counted
        // first for the percentage, inside the step, as the count waits for the
        // set's catalog like the read. ---
        var rowsRead = new SyncProgress<int>(read => live.Done = read);
        var allRecords = await ReportingAsync(
            Task.Run(async () =>
            {
                live.Total = (int)Math.Min(int.MaxValue,
                    await _catalog.GetFileRecordCountAsync(backupSetId, ct).ConfigureAwait(false));
                return await _catalog.GetAllFilesForBackupSetAsync(
                    backupSetId, ct, rowsRead, inPathOrder: false).ConfigureAwait(false);
            }, ct),
            elapsed => Step("Step 1 of 3: loading the catalog", live.Done, live.Total, "records", elapsed),
            progress).ConfigureAwait(false);
        var activeRecords = allRecords.Where(r => !r.IsDeleted).ToList();

        // Source paths that have at least one active (non-deleted) record.
        var backedUpSourcePaths = new HashSet<string>(
            activeRecords.Select(r => r.SourcePath),
            StringComparer.OrdinalIgnoreCase);

        // --- Step 2, check 1: every current source file is represented in the
        // backup. How many there are is known only once the scan ends; the
        // catalog's count of live paths stands in until then. ---
        var isExcluded = DirectoryBackupService.BuildExclusionFilter(job);
        var scanProgress = new SyncProgress<ScanProgress>(p => live.Scan = p);
        var scanned = await ReportingAsync(
            Task.Run(() => _scanner.ScanAsync(
                job.Sources, scanProgress, ct, isExcluded,
                DirectoryBackupService.BuildDirectoryPruneFilter(job)), ct),
            elapsed =>
            {
                var p = live.Scan;
                return Step("Step 2 of 3: scanning the sources", p?.FilesFound ?? 0, backedUpSourcePaths.Count,
                    "files", elapsed, p?.CurrentDirectory, estimated: true);
            },
            progress).ConfigureAwait(false);

        int sourceChecked = 0;
        foreach (var file in scanned)
        {
            ct.ThrowIfCancellationRequested();
            sourceChecked++;
            if (!backedUpSourcePaths.Contains(file.FullPath))
            {
                issues.Add(new VerifyIssue
                {
                    Path = file.FullPath,
                    Detail = "Source file is not present in the backup.",
                    Kind = VerifyIssueKind.MissingFromBackup,
                });
            }
        }

        // --- Checks 2 & 3: backing storage + .fileref resolution per record. ---
        // Cache hash -> resolved plain path so repeated references to the same
        // content don't re-query the catalog or re-stat the disk.
        var resolvedPlain = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // When verifying contents, remember which absolute plain paths and which
        // blocks we've already hashed so shared anchors / blocks are read once -
        // with each plain path's actual hash (null when unreadable), so that a
        // wrong anchor is reported for its own file and for every reference to it,
        // whichever the catalog reaches first.
        var hashedPaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var hashedBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recordsChecked = 0;
        int fileRefsChecked = 0;
        int itemsHashed = 0;
        // The actual hash of a plain copy, read once however many records share it.
        async Task<string?> ActualHashAsync(string path)
        {
            if (!hashedPaths.TryGetValue(path, out string? actual))
            {
                itemsHashed++;
                hashedPaths[path] = actual = await ComputeFileHashAsync(path, ct);
            }
            return actual;
        }
        int total = activeRecords.Count;
        int processed = 0;
        string checking = verifyContents
            ? "Step 3 of 3: reading and hashing the backed-up files"
            : "Step 3 of 3: checking the backed-up files";

        // On a task of its own, so the reports keep coming while one record takes
        // long: a large file being hashed, a slow destination.
        async Task<int> CheckRecordsAsync()
        {
            foreach (var record in activeRecords)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                recordsChecked++;
                live.Done = processed;
                live.Current = record.SourcePath;

                if (record.IsFileRef)
                {
                    fileRefsChecked++;

                    // The .fileref manifest itself should exist on disk.
                    string refPath = Path.Combine(targetDirectory, record.DiscPath);
                    if (!File.Exists(refPath))
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = $"File reference manifest is missing: {record.DiscPath}",
                            Kind = VerifyIssueKind.BackingFileMissing,
                        });
                        continue;
                    }

                    // Empty content needs no copy (EmptyContent).
                    if (EmptyContent.Is(record.Hash))
                        continue;

                    // The referenced content must resolve to an existing plain copy.
                    if (!resolvedPlain.TryGetValue(record.Hash, out string? plainPath))
                    {
                        plainPath = await PlainContent.FirstExistingFullPathAsync(
                            _catalog, backupSetId, record.Hash, targetDirectory, ct);
                        resolvedPlain[record.Hash] = plainPath;
                    }

                    if (plainPath is null)
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = "File reference does not resolve to an existing plain copy "
                                + $"of its content (hash {Shorten(record.Hash)}).",
                            Kind = VerifyIssueKind.FileRefUnresolved,
                        });
                    }
                    else if (verifyContents
                        && await ActualHashAsync(plainPath) is { } actual && !HashEquals(actual, record.Hash))
                    {
                        // The shared plain copy, re-hashed once, does not hold the content.
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = "Resolved content hash does not match the catalog "
                                + $"(expected {Shorten(record.Hash)}, got {Shorten(actual)}): "
                                + plainPath,
                            Kind = VerifyIssueKind.ContentMismatch,
                        });
                    }
                }
                else if (record.IsDeduped)
                {
                    string manifestPath = Path.Combine(targetDirectory, record.DiscPath);
                    if (!File.Exists(manifestPath))
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = $"Dedup manifest is missing: {record.DiscPath}",
                            Kind = VerifyIssueKind.BackingFileMissing,
                        });
                        continue;
                    }

                    // Each block the manifest references must exist in the block store.
                    try
                    {
                        string json = await File.ReadAllTextAsync(manifestPath, ct);
                        var manifest = JsonSerializer.Deserialize<DedupManifest>(json, _jsonOptions);
                        if (manifest is null)
                        {
                            issues.Add(new VerifyIssue
                            {
                                Path = record.SourcePath,
                                Detail = $"Dedup manifest is corrupt: {record.DiscPath}",
                                Kind = VerifyIssueKind.BackingFileMissing,
                            });
                            continue;
                        }

                        foreach (string blockHash in manifest.BlockHashes)
                        {
                            ct.ThrowIfCancellationRequested();
                            string blockPath = Path.Combine(blockStoreDir, blockHash + ".blk");
                            if (!File.Exists(blockPath))
                            {
                                issues.Add(new VerifyIssue
                                {
                                    Path = record.SourcePath,
                                    Detail = $"Missing block in store: {blockHash}.blk",
                                    Kind = VerifyIssueKind.BlockMissing,
                                });
                            }
                            else if (verifyContents && hashedBlocks.Add(blockHash))
                            {
                                // Each block is content-addressed: its file name IS its
                                // SHA-256, so re-hash it and compare against the name.
                                itemsHashed++;
                                string? actual = await ComputeFileHashAsync(blockPath, ct);
                                if (actual is not null && !HashEquals(actual, blockHash))
                                {
                                    issues.Add(new VerifyIssue
                                    {
                                        Path = record.SourcePath,
                                        Detail = "Block content does not match its hash "
                                            + $"(expected {Shorten(blockHash)}, got {Shorten(actual)}): "
                                            + $"{blockHash}.blk",
                                        Kind = VerifyIssueKind.ContentMismatch,
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = $"Could not read dedup manifest: {ex.Message}",
                            Kind = VerifyIssueKind.BackingFileMissing,
                        });
                    }
                }
                else
                {
                    // Plain copy (or zipped/split — DiscPath points at the stored file).
                    string filePath = Path.Combine(targetDirectory, record.DiscPath);
                    if (!File.Exists(filePath))
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = $"Backing file is missing: {record.DiscPath}",
                            Kind = VerifyIssueKind.BackingFileMissing,
                        });
                    }
                    else if (verifyContents
                        && !record.IsZipped && !record.IsSplit
                        && !string.IsNullOrEmpty(record.Hash))
                    {
                        // Re-hash the stored plain copy and compare against the catalog.
                        // Zipped/split stores don't hold the raw content directly,
                        // so a name-vs-hash comparison wouldn't be meaningful for them.
                        string? actual = await ActualHashAsync(filePath);
                        if (actual is not null && !HashEquals(actual, record.Hash))
                        {
                            issues.Add(new VerifyIssue
                            {
                                Path = record.SourcePath,
                                Detail = "Stored content hash does not match the catalog "
                                    + $"(expected {Shorten(record.Hash)}, got {Shorten(actual)}): "
                                    + record.DiscPath,
                                Kind = VerifyIssueKind.ContentMismatch,
                            });
                        }
                    }
                }
            }
            return processed;
        }
        live.Done = 0;
        int firstRecordIssue = issues.Count;
        await ReportingAsync(Task.Run(CheckRecordsAsync, ct),
            elapsed => Step(checking, live.Done, total, "records", elapsed, live.Current),
            progress).ConfigureAwait(false);
        // The records were read in no order, and the window lists the first few
        // thousand issues: by path, so a folder's issues stay together.
        issues.Sort(firstRecordIssue, issues.Count - firstRecordIssue,
            Comparer<VerifyIssue>.Create((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path)));

        progress?.Report(new VerifyProgress
        {
            StatusMessage = "Verification complete.",
            ItemsChecked = total,
            TotalItems = total,
            Percentage = 100,
        });

        return new VerifyResult
        {
            SourceFilesChecked = sourceChecked,
            RecordsChecked = recordsChecked,
            FileRefsChecked = fileRefsChecked,
            ContentsVerified = verifyContents,
            ItemsHashed = itemsHashed,
            Issues = issues,
        };
    }

    /// <summary>
    /// Compute the lowercase hex SHA-256 of a file's contents, streaming so large
    /// files don't have to fit in memory. Returns <c>null</c> if the file can't be
    /// read (the caller has already confirmed existence; an I/O failure here is
    /// reported separately by the existence check on a later run).
    /// </summary>
    private static async Task<string?> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool HashEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the step under way has done, written by the step and read by its
    /// reports from another thread.
    /// </summary>
    private sealed class LiveCounts
    {
        public volatile int Done;
        public volatile int Total;
        public volatile string? Current;
        public volatile ScanProgress? Scan;
    }

    /// <summary>
    /// Await <paramref name="work"/>, reporting <paramref name="snapshot"/> of it as
    /// it starts and then at the <see cref="ProgressCadence"/> until it ends - also
    /// through stretches with nothing new to count (a wait for the catalog, one
    /// large file being hashed), so the display shows the work going on.
    /// </summary>
    /// <param name="snapshot">The report for the step so far, given how long it has run.</param>
    private static async Task<T> ReportingAsync<T>(
        Task<T> work, Func<TimeSpan, VerifyProgress> snapshot, IProgress<VerifyProgress>? progress)
    {
        if (progress is null)
            return await work.ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        progress.Report(snapshot(clock.Elapsed));
        while (await Task.WhenAny(work, Task.Delay(ProgressCadence.IntervalMs)).ConfigureAwait(false) != work)
            progress.Report(snapshot(clock.Elapsed));
        var result = await work.ConfigureAwait(false);
        // Where it ended, also when that came before the first tick.
        progress.Report(snapshot(clock.Elapsed));
        return result;
    }

    /// <summary>
    /// A step's report: what it does, how much of it is done, and for how long it
    /// has run.
    /// </summary>
    /// <param name="done">Items done so far.</param>
    /// <param name="total">Items in all, or 0 when not known.</param>
    /// <param name="unit">What an item is, for the text ("records").</param>
    /// <param name="current">The item or folder being worked on, if any.</param>
    /// <param name="estimated">
    /// Whether <paramref name="total"/> is a guess: shown as "about", and the
    /// percentage held below 100 until the step ends.
    /// </param>
    private static VerifyProgress Step(
        string step, long done, long total, string unit, TimeSpan elapsed,
        string? current = null, bool estimated = false)
    {
        double percent = total > 0 ? Math.Min(estimated ? 99 : 100, done * 100.0 / total) : 0;
        string counted = total > 0
            ? $"{done:N0} of {(estimated ? "about " : "")}{total:N0} {unit}, {percent:F0}%"
            : $"{done:N0} {unit}";
        return new VerifyProgress
        {
            StatusMessage = $"{step}: {counted} - {Clock(elapsed)}",
            CurrentFile = current ?? "",
            ItemsChecked = (int)Math.Min(done, int.MaxValue),
            TotalItems = (int)Math.Min(total, int.MaxValue),
            Percentage = percent,
        };
    }

    /// <summary>Elapsed time as m:ss, or h:mm:ss from an hour on.</summary>
    private static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{t.Minutes}:{t.Seconds:D2}";

    /// <summary>The first twelve characters of a hash, for a message.</summary>
    private static string Shorten(string hash)
        => hash.Length <= 12 ? hash : hash[..12];
}
