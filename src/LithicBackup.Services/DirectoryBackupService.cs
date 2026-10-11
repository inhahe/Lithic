using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;

namespace LithicBackup.Services;

/// <summary>
/// Result of a targeted relocation attempt (<see cref="DirectoryBackupService.MoveTargetedAsync"/>).
/// </summary>
public enum TargetedMoveOutcome
{
    /// <summary>The destination copy was renamed/moved in place and the catalog updated.</summary>
    Relocated,

    /// <summary>
    /// The old path was tracked, but the move lands it on a path the set
    /// EXCLUDES (a user glob, a zero-tier tier set, or a hard exclusion such as
    /// the app's own data directory).  Relocating would keep a backup of a file
    /// the set says it does not back up, so the item is released instead: the
    /// caller should reconcile the vacated old path exactly as for a move out of
    /// the selection, and re-offer the new path for backup so that any siblings
    /// that are NOT excluded are still copied.
    /// </summary>
    ExcludedAtDestination,

    /// <summary>
    /// The old path had nothing tracked in the catalog, so there was no destination
    /// copy to relocate — the new path is simply a fresh source (the common
    /// atomic-save pattern: write <c>foo.tmp</c>, rename it over <c>foo</c>). The
    /// caller should back up the new path normally; there is no stale old record to
    /// reconcile. Not a failure.
    /// </summary>
    NothingToRelocate,

    /// <summary>
    /// The old path <em>was</em> tracked but relocation was not safe/possible
    /// (special storage format, version history, missing/locked destination copy).
    /// The caller should back up the new source path as fresh files and reconcile
    /// the vacated old path.
    /// </summary>
    FellBack,
}

/// <summary>
/// Backs up files to a directory target with versioned file history.
///
/// Layout on disk:
///   targetDir/C/Users/foo/file.txt                  -- current version (plain)
///   targetDir/C/Users/foo/bigfile.dat.dedup          -- current version (block-deduplicated)
///   targetDir/C/Users/foo/samefile.txt.fileref       -- current version (file-level dedup ref)
///   targetDir/C_prev/Users/foo/file.txt.v1           -- previous version 1
///   targetDir/C_prev/Users/foo/bigfile.dat.v2.dedup  -- previous (block-deduplicated)
///   targetDir/C_prev/Users/foo/samefile.txt.v1.fileref -- previous (file ref)
///   targetDir/_blocks/{hash}.blk                     -- shared block store (block-level dedup)
///
/// File-level dedup has no separate content store. Each unique file's content
/// is written once as a plain, normally-named file (in the current tree or a
/// "{drive}_prev" version); any byte-identical duplicate is a small .fileref
/// manifest that resolves to that plain copy by content hash via the catalog.
/// </summary>
public class DirectoryBackupService
{
    private readonly ICatalogRepository _catalog;
    private readonly IFileScanner _scanner;
    private readonly VersionRetentionService _retention;
    private readonly IDeduplicationEngine? _dedup;
    private readonly IFileHashLookup? _hashCache;

    /// <summary>
    /// TESTING ONLY — when <c>true</c>, plain (non-deduplicated) file copies are
    /// written as tiny hash+size stubs instead of real content, so a dedup test
    /// run doesn't fill the destination drive. The block store (<c>_blocks</c>)
    /// and all manifests are kept fully real, so block-level dedup restore and
    /// verification still work — but any plain-copied file is non-functional and
    /// cannot be restored. Because file-level dedup references resolve to a plain
    /// copy, .fileref restore is also non-functional in this mode.  This does not
    /// produce a real backup and must never be enabled for one.
    /// Gated behind <c>--test-mode</c> in the UI.
    /// </summary>
    public bool StubPlainContentForTesting { get; set; }

    public DirectoryBackupService(
        ICatalogRepository catalog,
        IFileScanner scanner,
        VersionRetentionService retention,
        IDeduplicationEngine? dedup = null,
        IFileHashLookup? hashCache = null)
    {
        _catalog = catalog;
        _scanner = scanner;
        _retention = retention;
        _dedup = dedup;
        _hashCache = hashCache;
    }

    /// <summary>
    /// Execute a directory backup: copy files with versioned history,
    /// update catalog, and apply retention.
    /// When <paramref name="precomputedDiff"/> is supplied the scan/diff
    /// steps are skipped (they were already done during planning).
    /// </summary>
    /// <param name="scopeRetentionToBackedUpFiles">
    /// Restrict the retention pass to the paths this run gave a second or later
    /// version, instead of re-evaluating every path in the set. Set by
    /// targeted/continuous backups, where the whole-set pass costs orders of
    /// magnitude more than the backup it follows; see
    /// <see cref="VersionRetentionService.ComputeRetentionAsync(int, Func{string, IReadOnlyList{VersionRetentionTier}}, IReadOnlyCollection{string}, CancellationToken)"/>
    /// for why that is safe and what it defers to the next full backup. A full
    /// backup must leave this false.
    /// </param>
    /// <exception cref="BackupCanceledException">
    /// Cancelled: with the files that had failed by then, and whether the copy
    /// itself had finished.
    /// </exception>
    public async Task<BackupResult> ExecuteAsync(
        BackupJob job,
        string targetDirectory,
        IReadOnlyList<VersionRetentionTier>? retentionTiers,
        IProgress<BackupProgress>? progress,
        CancellationToken ct,
        ManualResetEventSlim? pauseEvent = null,
        FailureCallback? onFailure = null,
        BackupDiff? precomputedDiff = null,
        bool scopeRetentionToBackedUpFiles = false,
        CommitBoundaryHandler? onCommitBoundary = null)
    {
        var run = new RunState();
        try
        {
            return await ExecuteCoreAsync(
                job, targetDirectory, retentionTiers, progress, ct, pauseEvent, onFailure,
                precomputedDiff, scopeRetentionToBackedUpFiles, onCommitBoundary, run);
        }
        catch (OperationCanceledException ex) when (ex is not BackupCanceledException)
        {
            throw new BackupCanceledException(run.FailedFiles, run.CopyFinished, ex);
        }
    }

    /// <summary>
    /// A file's version number was taken by another writer while the copy waited
    /// out a pause without the lock; the file is started again.
    /// </summary>
    private sealed class VersionTakenException : Exception;

    /// <summary>What a run has done so far, for reporting it if it is cancelled.</summary>
    private sealed class RunState
    {
        public List<FailedFile> FailedFiles { get; } = [];
        public bool CopyFinished { get; set; }
    }

    /// <inheritdoc cref="ExecuteAsync"/>
    /// <param name="run">Filled in as the run goes.</param>
    private async Task<BackupResult> ExecuteCoreAsync(
        BackupJob job,
        string targetDirectory,
        IReadOnlyList<VersionRetentionTier>? retentionTiers,
        IProgress<BackupProgress>? progress,
        CancellationToken ct,
        ManualResetEventSlim? pauseEvent,
        FailureCallback? onFailure,
        BackupDiff? precomputedDiff,
        bool scopeRetentionToBackedUpFiles,
        CommitBoundaryHandler? onCommitBoundary,
        RunState run)
    {
        BackupDiff diff;
        if (precomputedDiff is not null)
        {
            diff = precomputedDiff;
        }
        else
        {
            // No pre-computed diff — scan and compute from scratch.
            progress?.Report(new BackupProgress { StatusMessage = "Scanning source files..." });
            var isExcluded = BuildExclusionFilter(job);
            var scanned = await _scanner.ScanAsync(
                job.Sources, progress: null, ct, isExcluded, BuildDirectoryPruneFilter(job));

            progress?.Report(new BackupProgress { StatusMessage = "Computing changes..." });
            if (job.BackupSetId.HasValue)
            {
                diff = await _scanner.ComputeDiffAsync(scanned, job.BackupSetId.Value, ct);
            }
            else
            {
                diff = new BackupDiff
                {
                    NewFiles = scanned,
                    ChangedFiles = [],
                    DeletedFiles = [],
                };
            }
        }

        var filesToBackup = diff.NewFiles.Concat(diff.ChangedFiles).ToList();
        if (filesToBackup.Count == 0)
        {
            return new BackupResult
            {
                Success = true,
                DiscsWritten = 0,
                BytesWritten = 0,
                FailedFiles = [],
            };
        }

        // 3. Ensure backup set exists.
        int backupSetId;
        if (job.BackupSetId.HasValue)
        {
            backupSetId = job.BackupSetId.Value;
        }
        else
        {
            var newSet = await _catalog.CreateBackupSetAsync(new BackupSet
            {
                Name = $"Backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                SourceRoots = job.Sources.Select(s => s.Path).ToList(),
                CreatedUtc = DateTime.UtcNow,
                DefaultMediaType = MediaType.Directory,
            }, ct);
            backupSetId = newSet.Id;
        }

        // 4. Each file's version is numbered in the copy loop, from the catalog
        // as it stands when that file is written (see there).

        // Handed to onCommitBoundary so it can stay off the paths this run will
        // write itself.
        IReadOnlySet<string> pathsThisRunWillWrite =
            filesToBackup.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The paths this run gives a second or later version, gathered as the
        // copy loop finds each one's earlier version. Only they can have a
        // previous version for a scoped retention pass to prune; a first version
        // has none. Null when retention covers the whole set.
        List<string>? pathsWithHistory = scopeRetentionToBackedUpFiles ? [] : null;

        // Map of content hash -> destination-relative path of an active PLAIN
        // copy of that content somewhere in the backup tree (a real-bytes file
        // stored under its own name, in the current tree or a "{drive}_prev"
        // version — never a .fileref/.dedup). File-level dedup uses this to tell
        // a genuine duplicate from brand-new unique content: a later byte-
        // identical file becomes a .fileref ONLY when a plain copy of that
        // content is known to exist (so the reference always resolves). Unique
        // content is stored as a plain, normally-named file. The value also
        // stamps each new .fileref's ContentPath hint. Seeded from the catalog
        // and extended/updated as plain files are written and moved during this
        // run.
        //
        // Resolved LAZILY, one hash at a time, and memoised for the run. The
        // whole-set form GROUP BYs every active plain row in the set — another
        // full table scan on every single backup run, for a map whose only
        // consumers are the handful of hashes this run actually writes. Hashes
        // stored during the run are stamped straight into the cache
        // (SetContentPath) exactly as they were stamped into the preloaded map,
        // so intra-run dedup is unchanged.
        //
        // An entry holds only while this run holds the set's write lock. Each
        // time the run lets it go, another writer (the Worker) may prune the
        // plain copy an entry names, and a .fileref pointed at it would never
        // resolve, so ReacquireLockAsync empties the memo.
        var contentPathCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Destination-relative path of an active plain copy of this content, or
        // null when the set has none (so the content still needs real bytes).
        async Task<string?> ContentPathForHashAsync(string? hash)
        {
            if (string.IsNullOrEmpty(hash))
                return null;
            if (contentPathCache.TryGetValue(hash, out string? cached))
                return cached;
            string? found = await _catalog.GetActivePlainContentPathByHashAsync(backupSetId, hash, ct);
            contentPathCache[hash] = found;
            return found;
        }

        void SetContentPath(string hash, string discPath) => contentPathCache[hash] = discPath;

        // Pattern-based tier resolver: matches file paths against tier set
        // FilePatterns to determine each file's retention policy.
        // Tier sets with 0 tiers also serve as exclusion rules (handled at
        // scan time), but we still need the resolver here for versioning.
        var tierResolver = job.TierSets.Count > 0
            ? VersionTierSet.BuildTierResolver(job.TierSets)
            : null;

        // 5. Create a single "virtual" DiscRecord for this backup run.
        Directory.CreateDirectory(targetDirectory);

        // Ensure the block store exists when block-level dedup might be used.
        // File-level dedup no longer uses a dedicated content store: unique
        // content is written as a plain named file, and a duplicate is a
        // .fileref that resolves to an existing plain copy via the catalog.
        string blockStoreDir = Path.Combine(targetDirectory, "_blocks");
        bool blockDedupEnabled = job.EnableDeduplication && _dedup is not null;
        if (blockDedupEnabled)
            Directory.CreateDirectory(blockStoreDir);

        // In-memory content cache to read each file only once. A file that must
        // be examined before it can be written (block-dedup analysis, or a
        // file-level dedup hash check) is read into a buffer; the main loop then
        // writes that file's blocks / plain copy straight from the buffer
        // instead of reading it from disk a second time. Bounded by a total byte
        // budget so a backup larger than memory still works — files that don't
        // fit are read again the old way. Buffers are released as the main loop
        // consumes them.
        //
        // Whether a given file may be buffered is decided PER FILE against LIVE
        // memory (MemoryBudget.CanBuffer), not a single budget snapshotted here:
        // the pre-pass below accumulates buffers until the main loop consumes
        // them, so a start-of-run budget would let the buffer fill to the whole
        // percentage cap (e.g. 50% of 64 GB = 32 GB) and drive the machine into
        // memory pressure — the "leave N GB free" reserve, evaluated only once at
        // the start, would already be stale. CanBuffer re-checks the process
        // working set and currently-available RAM on every call so both the
        // percentage cap and the free-memory reserve hold continuously. See the
        // memory-runaway entry in known-issues.md. A policy that admits nothing
        // (e.g. Fixed 0 GB) simply disables buffering (still correct).
        // The policy is re-read from machine settings while the backup runs (at
        // most ~once a second) so lowering the budget in the GUI mid-backup takes
        // effect on a long-running job instead of being frozen at whatever it was
        // when the job started. job.MemoryBudget is the seed and the fallback when
        // settings can't be read. UserSettings.Load() is a cheap, exception-safe
        // JSON read of the same file the GUI writes, so cross-process changes are
        // visible. See the "memory not released after lowering budget mid-run"
        // entry in known-issues.md.
        var seedPolicy = job.MemoryBudget ?? MemoryBudgetOptions.Default;
        var livePolicy = seedPolicy;
        var policyClock = System.Diagnostics.Stopwatch.StartNew();
        long lastPolicyReadMs = long.MinValue;
        MemoryBudgetOptions CurrentPolicy()
        {
            long now = policyClock.ElapsedMilliseconds;
            if (now - lastPolicyReadMs >= 1000)
            {
                lastPolicyReadMs = now;
                try { livePolicy = UserSettings.Load().MemoryBudget ?? seedPolicy; }
                catch { /* keep the last good policy */ }
            }
            return livePolicy;
        }

        // Reclaim (LOH-compacting GC) is throttled to at most once every few
        // seconds: it's a blocking gen-2 collection, and if a Fixed cap is set
        // below the process's irreducible baseline the budget stays "over" forever,
        // so an unthrottled reclaim-per-file would grind the backup to a halt for no
        // further benefit.
        var reclaimClock = System.Diagnostics.Stopwatch.StartNew();
        long lastReclaimMs = long.MinValue;
        void MaybeReclaim()
        {
            if (!MemoryBudget.IsOverBudget(CurrentPolicy()))
                return;
            long now = reclaimClock.ElapsedMilliseconds;
            if (now - lastReclaimMs < 3000)
                return;
            lastReclaimMs = now;
            MemoryBudget.ReclaimNow();
        }
        var bufferedContent = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        // 5b. Block-dedup pre-pass.
        // Block-level dedup must decide, BEFORE writing anything, which files
        // actually have duplicate blocks — because a file is stored as a .dedup
        // manifest ONLY when it has a duplicate block (shared with another file
        // in this run, already present in the block store from a previous
        // version, or repeated within the file itself). Every other file is
        // written as a plain, normally-named copy, exactly like a non-dedup
        // backup. Cross-file sharing within a single run can't be detected file-
        // by-file (the first file of a sharing pair has nothing to compare
        // against yet), so we scan all candidate files up front:
        //   * preRecipes     : path -> block recipe (block hashes + whole-file
        //                      hash), computed in ONE read per file and reused by
        //                      the main loop so files aren't read twice to hash.
        //   * wholeFileCount : whole-file hash -> number of occurrences in this
        //                      run. With file-level dedup also on, the FIRST copy
        //                      of a whole-file duplicate is stored plain (the
        //                      anchor) and the rest become .fileref pointers — so
        //                      file-level dedup keeps working even with block
        //                      dedup on.
        //   * blockOccur     : block hash -> occurrences across files that are
        //                      NOT handled by file-level dedup (intra-file
        //                      repeats counted). A block is "duplicate" when its
        //                      count reaches 2.
        var preRecipes = new Dictionary<string, DeduplicationRecipe>(
            StringComparer.OrdinalIgnoreCase);
        var wholeFileCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blockOccur = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (blockDedupEnabled)
        {
            var analyzing = PhaseProgress.Begin(
                progress, "Analyzing files for deduplication", filesToBackup.Count);
            for (int i = 0; i < filesToBackup.Count; i++)
            {
                var file = filesToBackup[i];
                ct.ThrowIfCancellationRequested();
                pauseEvent?.Wait(ct);
                // Named before it is read: the whole file is read, so a big one
                // takes a while, and the panel should say which file it is.
                analyzing.Advance(i, file.FullPath);
                try
                {
                    DeduplicationRecipe r;
                    // Read the file into memory once if live memory allows it, so
                    // the main loop can write its blocks / plain copy from the
                    // buffer without reading it again. Otherwise fall back to the
                    // streaming analysis (the main loop re-reads).
                    if (MemoryBudget.CanBuffer(CurrentPolicy(), file.SizeBytes))
                    {
                        byte[] content = await File.ReadAllBytesAsync(file.FullPath, ct);
                        r = _dedup!.DeduplicateBytes(
                            blockStoreDir, file.FullPath, content, job.DeduplicationBlockSize);
                        bufferedContent[file.FullPath] = content;
                    }
                    else
                    {
                        r = await _dedup!.DeduplicateAsync(
                            blockStoreDir, file.FullPath, job.DeduplicationBlockSize, ct);

                        // Refusing to buffer because the process is over budget (e.g.
                        // the user just lowered the limit) means the buffers already
                        // accumulated in this pre-pass are themselves the excess. Drop
                        // them and hand the memory back to the OS now, rather than
                        // holding them until the main loop slowly consumes them. The
                        // main loop safely re-reads any file no longer in
                        // bufferedContent, so this only costs re-reads, never data.
                        if (bufferedContent.Count > 0 && MemoryBudget.IsOverBudget(CurrentPolicy()))
                        {
                            bufferedContent.Clear();
                            MaybeReclaim();
                        }
                    }
                    preRecipes[file.FullPath] = r;
                    wholeFileCount[r.OriginalHash] =
                        wholeFileCount.GetValueOrDefault(r.OriginalHash) + 1;
                }
                catch
                {
                    // Unreadable right now — leave it out of the analysis; the
                    // main loop falls back to a plain copy (and its own
                    // retry/failure handling) for this file.
                }
            }
            // Count block occurrences, skipping files that file-level dedup will
            // handle as a plain anchor + .fileref (their blocks never enter the
            // store, so they must not make other files look "shared").
            foreach (var r in preRecipes.Values)
            {
                if (job.EnableFileDeduplication
                    && wholeFileCount.GetValueOrDefault(r.OriginalHash) >= 2)
                    continue;
                foreach (var b in r.Blocks)
                    blockOccur[b.Hash] = blockOccur.GetValueOrDefault(b.Hash) + 1;
            }
        }

        // A candidate file is stored as .dedup only when it truly has a
        // duplicate block: one shared with another file this run (blockOccur
        // >= 2), one already in the store from a previous version, or one
        // repeated within the file itself. (DeduplicateAsync flags both
        // store-resident and within-file-repeated blocks as IsExisting, and
        // intra-file repeats also push blockOccur to >= 2.) Files with no
        // duplicate block are written plain.
        bool HasDuplicateBlocks(DeduplicationRecipe r) =>
            r.Blocks.Any(b => b.IsExisting || blockOccur.GetValueOrDefault(b.Hash) >= 2);

        // 5c. Whole-file-duplicate size pre-check (single-read fast path).
        // A plain file is normally read twice: once to hash it (needed to decide
        // whether it's a duplicate) and once to copy it. That second read is
        // avoidable for any file we can prove is NOT a whole-file duplicate, since
        // such a file is certainly stored as a plain copy and can be hashed while
        // it is copied, in a single streaming pass. A file can only be a whole-
        // file duplicate of content of the SAME byte size, so a file whose size
        // matches no other file in this run AND no existing plain copy cannot be a
        // duplicate. (Only relevant when file-level dedup is on and block dedup is
        // off; block dedup already reads every file once in the pre-pass above,
        // and with file dedup off there are no .fileref duplicates to worry about
        // — every file is plain anyway, so the single-pass copy always applies.)
        var candidateSizeCounts = new Dictionary<long, int>();
        var existingPlainSizes = new HashSet<long>();
        bool sizePrecheckActive = !blockDedupEnabled && job.EnableFileDeduplication;
        if (sizePrecheckActive)
        {
            foreach (var f in filesToBackup)
                candidateSizeCounts[f.SizeBytes] =
                    candidateSizeCounts.GetValueOrDefault(f.SizeBytes) + 1;
            if (job.BackupSetId.HasValue)
            {
                // Ask only about the sizes this run is actually writing. The
                // whole-set form has to DISTINCT every active plain row (252,289
                // sizes over 2.35M rows on a real set database — full index scan
                // plus a temp B-tree, ~6.6 s) to answer a question about the 1-3
                // sizes below, and it ran on EVERY continuous-backup pass, per
                // set, forever. That was the second half of the pegged CPU core
                // (the first being the whole-set version lookups); see the
                // "Worker pegged a CPU core" entry in known-issues.md.
                try
                {
                    existingPlainSizes = await FindStoredSizesAsync(
                        backupSetId, candidateSizeCounts, filesToBackup.Count, progress, pauseEvent, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Treat no size as stored: those files are then copied plain,
                    // which is always correct; only the dedup saving is lost.
                    existingPlainSizes = new HashSet<long>();
                }
            }
        }

        // True when a file could possibly be a whole-file duplicate (so it must be
        // hashed before its storage format is decided). False guarantees the file
        // is unique content and can take the single-pass hash-while-copy path.
        bool CouldBeWholeFileDuplicate(ScannedFile f) =>
            candidateSizeCounts.GetValueOrDefault(f.SizeBytes) >= 2
            || existingPlainSizes.Contains(f.SizeBytes);

        // Progressive prefix-hash index (roadmap item 5): maps a byte size to the
        // set of prefix hashes of every plain copy of that size stored SO FAR this
        // run. A large size-colliding file that would otherwise be read twice (once
        // to hash, again to copy if it turns out unique) first hashes a cheap prefix;
        // if no same-size plain content this run shares that prefix, the file cannot
        // be an intra-run duplicate and is read just once (hash-while-copy). Only
        // populated/consulted for INTRA-run size collisions — an existing-content
        // size collision keeps the full up-front hash, because we don't have the
        // stored content's prefixes here. Only meaningful when file-level dedup is on
        // and block dedup is off (block dedup already reads every file in its pre-pass).
        var intraRunPlainPrefixes = new Dictionary<long, HashSet<string>>();

        // Whether a size is an intra-run-only collision (≥2 candidates share it AND
        // no already-stored plain content has it) — the exact class the prefix
        // optimization applies to.
        bool IsIntraRunOnlyCollision(long size) =>
            candidateSizeCounts.GetValueOrDefault(size) >= 2
            && !existingPlainSizes.Contains(size);

        // 6. Copy files.
        var failedFiles = run.FailedFiles;
        long totalBytes = filesToBackup.Sum(f => f.SizeBytes);
        // Physical bytes actually stored — drives the disc record's BytesUsed.
        long bytesWritten = 0;
        // Progress numerator: planned bytes *dealt with* so far, whatever the
        // outcome (written, deduped, unchanged, or skipped/failed). This is what
        // the UI's "copied / total" reflects, so the bar keeps advancing to 100%
        // instead of freezing whenever a run hits a long stretch of unchanged
        // files or files that fail to copy (e.g. the disk filling up).
        long bytesProcessed = 0;
        bool permanentSkipAll = false;
        // Set when the destination runs out of space mid-run: rather than
        // recording a "disk full" failure for every remaining file (which spams
        // the failed/skipped list), stop the run cleanly after the first one.
        bool diskFullAbort = false;

        // Track hashes + formats of successfully backed-up files for verification.
        var backedUp = job.VerifyAfterBackup
            ? new Dictionary<string, (string Hash, bool IsDeduped, bool IsFileRef)>(
                StringComparer.OrdinalIgnoreCase)
            : null;

        // Commit catalog records periodically so that cancellation preserves
        // progress for files already copied.  Commits happen:
        //   - every CommitBatchSize small files,
        //   - immediately after any large file (>= 10 MB),
        //   - before starting a large file (to flush pending small files),
        //   - at least every CommitIntervalSeconds regardless of file size.
        const int CommitBatchSize = 50;
        const long CommitLargeFileThreshold = 10 * 1024 * 1024; // 10 MB
        const double CommitIntervalSeconds = 30;
        int batchCount = 0;
        var commitTimer = System.Diagnostics.Stopwatch.StartNew();

        // Taking the set's write lock can mean waiting for the Worker's continuous
        // backup of the same set to reach its next commit. Say so, or the panel
        // keeps showing whatever came before for as long as the Worker holds the
        // lock, which reads as hung.
        void ReportWaitingForLock() => progress?.Report(new BackupProgress
        {
            StatusMessage = "Waiting for the background backup of this set to pause...",
        });
        var tx = await _catalog.BeginTransactionAsync(backupSetId, ct, ReportWaitingForLock);

        try
        {

        var discRecord = await _catalog.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = backupSetId,
            Label = targetDirectory,
            SequenceNumber = 1,
            MediaType = MediaType.Directory,
            FilesystemType = FilesystemType.UDF, // nominal
            Capacity = 0,
            BytesUsed = 0,
            IsMultisession = false,
            Status = BurnSessionStatus.InProgress,
            CreatedUtc = DateTime.UtcNow,
            LastWrittenUtc = DateTime.UtcNow,
        }, ct);

        // Commit what this run has written and let the set's write lock go.
        async Task CommitAndReleaseAsync()
        {
            discRecord.BytesUsed = bytesWritten;
            discRecord.LastWrittenUtc = DateTime.UtcNow;
            await _catalog.UpdateDiscAsync(discRecord, ct);
            tx.Complete();
            tx.Dispose();
        }

        // Take the lock back. What was learned under the last one may be stale.
        async Task ReacquireLockAsync()
        {
            tx = await _catalog.BeginTransactionAsync(backupSetId, ct, ReportWaitingForLock);
            batchCount = 0;
            commitTimer.Restart();
            contentPathCache.Clear();
        }

        // Wait out a Pause without holding the write lock: held, it would stall
        // every other writer of the set - the Worker's whole poll loop - for as
        // long as the backup stayed paused. Called between files and, by the
        // copy helpers, between chunks of a file.
        async Task WaitOutPauseAsync()
        {
            if (pauseEvent is null || pauseEvent.IsSet)
                return;
            await CommitAndReleaseAsync();
            pauseEvent.Wait(ct);
            await ReacquireLockAsync();
        }

        // Per-file progress: only report intermediate progress for files >= 1 MB,
        // and throttle reports to every ~1 MB to avoid flooding the UI thread.
        const long PerFileProgressThreshold = 1024 * 1024;      // 1 MB
        const long PerFileReportInterval    = 1024 * 1024;      // 1 MB

        // Free-space monitoring: check every FreeSpaceCheckInterval files
        // and warn once when space drops below the remaining data to copy.
        const int FreeSpaceCheckInterval = 100;
        bool lowSpaceWarned = false;
        string? targetRoot = Path.GetPathRoot(targetDirectory);

        for (int i = 0; i < filesToBackup.Count; i++)
        {
            await WaitOutPauseAsync();
            ct.ThrowIfCancellationRequested();

            var file = filesToBackup[i];

            // Periodic free-space check.
            if (!lowSpaceWarned && targetRoot is not null
                && i % FreeSpaceCheckInterval == 0 && i > 0)
            {
                try
                {
                    var driveInfo = new DriveInfo(targetRoot);
                    if (driveInfo.IsReady)
                    {
                        long freeSpace = driveInfo.AvailableFreeSpace;
                        long bytesRemaining = totalBytes - bytesProcessed;
                        if (freeSpace < bytesRemaining)
                        {
                            lowSpaceWarned = true;
                            progress?.Report(new BackupProgress
                            {
                                StatusMessage = $"\u26A0 Low disk space: {FormatBytes(freeSpace)} free, " +
                                    $"{FormatBytes(bytesRemaining)} remaining to copy",
                                CurrentFile = file.FullPath,
                                BytesWrittenTotal = bytesProcessed,
                                BytesTotalAll = totalBytes,
                                OverallPercentage = totalBytes > 0
                                    ? (double)bytesProcessed / totalBytes * 100 : 0,
                            });
                        }
                    }
                }
                catch { /* ignore drive-info errors */ }
            }

            progress?.Report(new BackupProgress
            {
                CurrentDisc = 1,
                TotalDiscs = 1,
                CurrentFile = file.FullPath,
                BytesWrittenTotal = bytesProcessed,
                BytesTotalAll = totalBytes,
                OverallPercentage = totalBytes > 0
                    ? (double)bytesProcessed / totalBytes * 100
                    : 0,
                CurrentFileBytesWritten = 0,
                CurrentFileTotalBytes = file.SizeBytes,
            });

            // Before starting a large file, flush pending records so small
            // files already copied are safe if the large copy is cancelled.
            if (file.SizeBytes >= CommitLargeFileThreshold && batchCount > 0)
            {
                await CommitAndReleaseAsync();
                await ReacquireLockAsync();
            }

            // Number this version from the catalog as it stands now, read inside
            // the open transaction, which holds the set's write lock: no other
            // process - the Worker's continuous backup, between two of this run's
            // commits - can add a version of the path before this one is
            // written. Read BEFORE entering the retry loop so retries don't increment the
            // version number.
            int version = 1;
            var last = await _catalog.GetLastVersionAsync(backupSetId, file.FullPath, ct);
            bool hasExistingInfo = last is not null;
            var existingInfo = last?.Version ?? default;

            // Resurrection: if there's no LIVE prior version but this path has a
            // fully-tombstoned history (its file was deleted then reappeared —
            // e.g. an atomic save), continue that chain instead of starting over.
            // Treat it exactly like a normal existing version from here on: the
            // _prev move below picks up the old copy (still at its live on-disk
            // path) and un-deletes it as a retained version.
            bool resurrecting = last is { IsDeleted: true };

            if (hasExistingInfo)
            {
                // After every number taken, tombstoned records included; the live
                // record (existingInfo) is still the copy that moves to _prev.
                version = last!.Value.HighestVersion + 1;
                pathsWithHistory?.Add(file.FullPath);
            }

            // Between chunks of this file. A pause lets the write lock go
            // (WaitOutPauseAsync), and the number this version took was read under
            // that lock: if another writer added a version of the file meanwhile,
            // the number is taken, so abandon this copy and start the file again.
            async Task WaitMidFileAsync()
            {
                if (pauseEvent is null || pauseEvent.IsSet)
                    return;
                await WaitOutPauseAsync();
                var now = await _catalog.GetLastVersionAsync(backupSetId, file.FullPath, ct);
                if ((now?.HighestVersion ?? 0) != version - 1)
                    throw new VersionTakenException();
            }
            Func<Task>? waitMidFile = pauseEvent is null ? null : WaitMidFileAsync;
            bool restartFile = false;

            bool fileRetrying = true;
            while (fileRetrying)
            {
                fileRetrying = false;

            try
            {
                // Compute hash up front — needed for file-level dedup checks
                // and the catalog record regardless.
                // Check the pre-computed hash cache first (populated by dedup
                // analysis) to avoid re-reading files that were already hashed.
                // The persisted size must reflect the bytes we actually hashed
                // and store, not the directory-scan size. When a file is edited
                // between the scan and this read those differ; recording the
                // stale scan size leaves change detection (which compares stored
                // size) perpetually re-flagging the file as changed, re-hashing
                // it every run forever.
                long contentSize = file.SizeBytes;
                string hash = "";

                // In-memory content for this file, when available. The block-dedup
                // pre-pass may have already read it into the buffer cache; if so,
                // reuse those bytes for the write below instead of reading again.
                // (For the file-level-dedup-only path it may instead be read inline
                // just below.) When non-null, the write step writes from it.
                byte[]? contentBuffer = null;
                bufferedContent.TryGetValue(file.FullPath, out contentBuffer);

                // Single-pass fast path: when a file is provably NOT a whole-file
                // duplicate and is not block-deduped, it is certainly stored as a
                // plain copy. Such a file can be hashed WHILE it is copied (one
                // streaming read) instead of being read once to hash and again to
                // copy. We can only defer the hash, though, when nothing before
                // the copy needs it:
                //   - mightBeUnchanged: the content-identity short-circuit needs
                //     the hash up front to compare against the stored version, so
                //     a changed file that has a prior hashed version must be hashed
                //     now (it might be unchanged content with a new timestamp).
                //   - the test stub path writes a placeholder instead of copying,
                //     so it can't produce a hash as a side effect.
                bool mightBeUnchanged = hasExistingInfo
                    && !string.IsNullOrEmpty(existingInfo.Hash);
                bool definitelyPlain = !blockDedupEnabled
                    && (!job.EnableFileDeduplication || !CouldBeWholeFileDuplicate(file));
                bool deferHashToCopy = definitelyPlain
                    && !mightBeUnchanged
                    && !StubPlainContentForTesting;

                // Reading a whole file to hash it before deciding whether to copy
                // it (the duplicate and unchanged-content checks) is as slow as the
                // copy itself for a big file, and it used to report nothing: the
                // progress panel went on showing the previous file for the whole
                // read - one sat on a small .py for minutes while a large file
                // behind it was being hashed. Report the read as its own activity.
                Action<long>? hashProgress = null;
                if (!deferHashToCopy && file.SizeBytes >= PerFileProgressThreshold && progress is not null)
                {
                    long capturedBytesProcessed = bytesProcessed;
                    long lastReportedAt = -PerFileReportInterval;
                    hashProgress = bytesRead =>
                    {
                        if (bytesRead - lastReportedAt < PerFileReportInterval && bytesRead < file.SizeBytes)
                            return;
                        lastReportedAt = bytesRead;
                        progress.Report(new BackupProgress
                        {
                            CurrentDisc = 1,
                            TotalDiscs = 1,
                            CurrentFile = file.FullPath,
                            BytesWrittenTotal = capturedBytesProcessed,
                            BytesTotalAll = totalBytes,
                            OverallPercentage = totalBytes > 0
                                ? (double)capturedBytesProcessed / totalBytes * 100
                                : 0,
                            CurrentFileBytesWritten = bytesRead,
                            CurrentFileTotalBytes = file.SizeBytes,
                            CurrentFileActivity = "Checking for duplicates",
                        });
                    };
                }

                if (!deferHashToCopy)
                {
                    // Compute the hash now — needed for the content-identity
                    // short-circuit, file-level dedup, and/or the block-dedup
                    // format decision below.
                    if (preRecipes.TryGetValue(file.FullPath, out var preRecipe))
                    {
                        // The block-dedup pre-pass already read and hashed this file
                        // (whole-file hash + block hashes) in a single pass; reuse it
                        // instead of reading the file again just to hash it.
                        hash = preRecipe.OriginalHash;
                        contentSize = preRecipe.OriginalSize;
                    }
                    else
                    {
                        string? cachedHash = _hashCache?.TryGetHash(
                            file.FullPath, file.SizeBytes, file.LastWriteUtc);
                        if (cachedHash is not null)
                        {
                            // Cache hit is validated against file.SizeBytes, so the
                            // scan size already matches the hashed content.
                            hash = cachedHash;
                        }
                        else if (contentBuffer is null && MemoryBudget.CanBuffer(CurrentPolicy(), file.SizeBytes))
                        {
                            // File-level-dedup-only path (no block-dedup pre-pass):
                            // read the file once into memory and hash it there, so a
                            // file that turns out to be a plain copy is written from
                            // the buffer with no second read, and a whole-file
                            // duplicate just discards the buffer (a .fileref needs no
                            // copy at all). One file's bytes at a time here; the
                            // buffer is dropped when this iteration ends.
                            try
                            {
                                contentBuffer = await ReadAllBytesSharedAsync(file.FullPath, ct, hashProgress);
                                contentSize = contentBuffer.Length;
                                hash = ComputeHashOfBuffer(contentBuffer);
                            }
                            catch
                            {
                                contentBuffer = null;
                                (hash, contentSize) = await ComputeFileHashAndSizeAsync(file.FullPath, ct, hashProgress);
                            }
                        }
                        else if (!blockDedupEnabled
                            && job.EnableFileDeduplication
                            && !mightBeUnchanged
                            && !StubPlainContentForTesting
                            && IsIntraRunOnlyCollision(file.SizeBytes)
                            && await RuledOutByPrefixAsync(file.FullPath, file.SizeBytes,
                                    intraRunPlainPrefixes, ct))
                        {
                            // Large file whose size collides only with OTHER files in
                            // this run (not already-stored content): a cheap prefix
                            // hash proved it shares no prefix with any same-size plain
                            // copy stored so far, so it cannot be an intra-run
                            // duplicate. Skip the full up-front hash and read the file
                            // exactly once, hashing while copying (deferHashToCopy is
                            // honored by the write section below). Its prefix is
                            // registered after it lands so later same-size files can
                            // be ruled out or escalated against it.
                            deferHashToCopy = true;
                        }
                        else
                        {
                            (hash, contentSize) = await ComputeFileHashAndSizeAsync(file.FullPath, ct, hashProgress);
                        }
                    }
                }

                // Content-identity short-circuit.
                // Change detection (both the targeted USN diff and the full
                // scan) flags a file as "changed" on a size or mtime
                // difference — it never inspects content.  A file re-saved
                // with identical bytes but a newer timestamp would therefore
                // cut a redundant version: pure version-history churn, and
                // under file-level dedup a chain of .fileref pointers all
                // referencing the same _filestore blob.
                // We've already computed the hash above (it's needed for every
                // file regardless), so compare it against the latest stored
                // version.  On a match the file is functionally unchanged:
                // skip versioning entirely and just refresh the recorded
                // source timestamp so the next run doesn't re-detect it.
                if (hasExistingInfo
                    && !string.IsNullOrEmpty(existingInfo.Hash)
                    && string.Equals(hash, existingInfo.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    if (resurrecting)
                    {
                        // The reappeared file is byte-for-byte identical to its
                        // tombstoned latest version. If that version's destination
                        // copy is still on disk, just revive the record in place
                        // (un-delete + refresh timestamp/size) rather than rewriting
                        // identical bytes and cutting a redundant version. If the
                        // copy is gone (e.g. retention purged it) we fall through to
                        // a normal write so a live copy is recreated.
                        string revivePath = GetCurrentPath(
                            targetDirectory, file.FullPath,
                            existingInfo.IsDeduped, existingInfo.IsFileRef);
                        if (File.Exists(revivePath))
                        {
                            var revived = await _catalog.GetFileRecordByPathAndVersionAsync(
                                backupSetId, file.FullPath, existingInfo.MaxVersion, ct);
                            if (revived is not null)
                            {
                                revived.IsDeleted = false;
                                revived.SourceLastWriteUtc = file.LastWriteUtc;
                                revived.SizeBytes = contentSize;
                                await _catalog.UpdateFileRecordAsync(revived, ct);

                                bytesProcessed += file.SizeBytes;
                                break; // revived in place — move on to the next file
                            }
                        }
                        // Fall through: no surviving copy to revive, so write fresh.
                    }
                    else
                    {
                        // Refresh the stored timestamp AND size so the next scan
                        // stops re-detecting this file. A stale stored size (from a
                        // prior scan that raced an edit) would otherwise keep the
                        // file flagged as changed and re-hashed on every run.
                        if ((existingInfo.SourceLastWriteUtc != file.LastWriteUtc
                                || existingInfo.SizeBytes != contentSize))
                        {
                            var latest = await _catalog.GetFileRecordByPathAndVersionAsync(
                                backupSetId, file.FullPath, existingInfo.MaxVersion, ct);
                            if (latest is not null)
                            {
                                latest.SourceLastWriteUtc = file.LastWriteUtc;
                                latest.SizeBytes = contentSize;
                                await _catalog.UpdateFileRecordAsync(latest, ct);
                            }
                        }

                        // Unchanged content writes nothing, but it's a planned file
                        // now dealt with — advance the progress numerator so a run
                        // over a large unchanged tree doesn't look frozen.
                        bytesProcessed += file.SizeBytes;
                        break; // unchanged content — move on to the next file
                    }
                }

                // Decide storage format. Priority:
                //   1. File-level dedup (cheap whole-file hash match)
                //   2. Block-level dedup (more expensive, catches partial similarity)
                //   3. Plain copy
                bool isDeduped = false;
                bool isFileRef = false;
                bool stubbedPlain = false;
                DeduplicationRecipe? recipe = null;

                // Build a throttled per-file progress callback for large files.
                // Captures the current bytesProcessed so intermediate reports
                // show accurate overall progress too.
                Action<long>? fileProgress = null;
                if (file.SizeBytes >= PerFileProgressThreshold && progress is not null)
                {
                    long capturedBytesProcessed = bytesProcessed;
                    long lastReportedAt = 0;
                    fileProgress = bytesCopied =>
                    {
                        if (bytesCopied - lastReportedAt >= PerFileReportInterval
                            || bytesCopied >= file.SizeBytes)
                        {
                            lastReportedAt = bytesCopied;
                            progress.Report(new BackupProgress
                            {
                                CurrentDisc = 1,
                                TotalDiscs = 1,
                                CurrentFile = file.FullPath,
                                BytesWrittenTotal = capturedBytesProcessed + bytesCopied,
                                BytesTotalAll = totalBytes,
                                OverallPercentage = totalBytes > 0
                                    ? (double)(capturedBytesProcessed + bytesCopied) / totalBytes * 100
                                    : 0,
                                CurrentFileBytesWritten = bytesCopied,
                                CurrentFileTotalBytes = file.SizeBytes,
                            });
                        }
                    };
                }

                // Storage-format decision. Priority:
                //   1. File-level dedup HIT — the whole file is byte-identical
                //      to content that already has a plain copy somewhere in the
                //      tree (current or a "{drive}_prev" version). Write a
                //      .fileref pointer ONLY; no bytes are stored, because the
                //      reference resolves to that existing plain copy by hash via
                //      the catalog at restore/verify time.
                //   2. Block-level dedup (when enabled) — but ONLY for files that
                //      actually have a duplicate block (shared with another file
                //      this run, already in the block store from a previous
                //      version, or repeated within the file). Such a file is
                //      stored as a .dedup manifest plus any new blocks. The
                //      pre-pass above pre-computed which files qualify.
                //   3. Plain named copy — everything else: brand-new unique
                //      content AND files with no duplicate blocks. The file is
                //      written under its own name with its real bytes, so it is a
                //      normal, directly-usable file just like a non-dedup backup.
                string? dupContentPath = job.EnableFileDeduplication
                    ? await ContentPathForHashAsync(hash)
                    : null;
                if (dupContentPath is not null)
                {
                    // (1) Genuine whole-file duplicate: byte-identical content
                    // already has a plain copy stored somewhere in the tree.
                    // Write a .fileref pointer and store no bytes — restore
                    // resolves the hash to that plain copy via the catalog.
                    isFileRef = true;
                }
                else if (blockDedupEnabled
                    && preRecipes.TryGetValue(file.FullPath, out var blockRecipe))
                {
                    bool wholeFileDuplicated = job.EnableFileDeduplication
                        && wholeFileCount.GetValueOrDefault(blockRecipe.OriginalHash) >= 2;

                    if (wholeFileDuplicated)
                    {
                        // This content appears as a whole-file duplicate elsewhere
                        // in the run, but no plain copy exists yet — so THIS is
                        // the first occurrence. Store it plain (flags stay false)
                        // so it anchors the later copies' .fileref pointers: this
                        // is what keeps file-level dedup working with block dedup
                        // on. (Its blocks were intentionally excluded from the
                        // block-occurrence count, so it never looks "shared".)
                    }
                    else if (HasDuplicateBlocks(blockRecipe))
                    {
                        // (2) The file has at least one duplicate block — store it
                        // as a .dedup manifest. Reuse the pre-pass recipe; the
                        // store may have grown since, but WriteNewBlocksAsync
                        // re-checks each block before writing and the manifest
                        // stores only block hashes (not IsExisting flags).
                        recipe = blockRecipe;
                        isDeduped = true;
                    }
                    // else: (3) no duplicate blocks — leave flags false so the
                    // write section copies the real bytes to a plain named file.
                }
                // else: (3) file-level dedup miss with block dedup off, or the
                // pre-pass couldn't read this file — write a plain named copy.

                // Check if this file should keep version history.
                // The tier resolver determines versioning: tier sets with
                // tiers > 0 keep versions, tier sets with 0 tiers do not.
                bool keepVersions;
                if (tierResolver is not null)
                {
                    var fileTierSet = tierResolver(file.FullPath);
                    keepVersions = fileTierSet.Tiers.Count > 0;
                }
                else
                {
                    keepVersions = true;
                }

                // Move the existing current file to _prev when there is an earlier
                // version and version history is enabled. The earlier version, not
                // the diff, decides: the diff calls a reappeared path new, and can
                // predate a version committed since, which this write would
                // otherwise overwrite in place.
                if (keepVersions && hasExistingInfo)
                {
                    bool oldDeduped = existingInfo.IsDeduped;
                    bool oldFileRef = existingInfo.IsFileRef;
                    string oldCurrentPath = GetCurrentPath(
                        targetDirectory, file.FullPath,
                        oldDeduped, oldFileRef);

                    if (File.Exists(oldCurrentPath))
                    {
                        int oldVersion = existingInfo.MaxVersion;
                        string prevPath = GetPrevPath(
                            targetDirectory, file.FullPath, oldVersion,
                            oldDeduped, oldFileRef);

                        string? prevDir = Path.GetDirectoryName(prevPath);
                        if (prevDir is not null)
                            Directory.CreateDirectory(prevDir);

                        File.Move(oldCurrentPath, prevPath, overwrite: true);

                        string prevDiscPath = GetPrevDiscPath(
                            file.FullPath, oldVersion, oldDeduped, oldFileRef);

                        // Load the specific old record on-demand (not pre-loaded)
                        // to update its DiscPath to the new _prev location.
                        var oldRecord = await _catalog.GetFileRecordByPathAndVersionAsync(
                            backupSetId, file.FullPath, oldVersion, ct);
                        if (oldRecord is not null)
                        {
                            oldRecord.DiscPath = prevDiscPath;
                            // Resurrection: the old version is a tombstone (its
                            // file was deleted-and-recreated). Now that it holds
                            // the real _prev bytes of a live chain again, un-delete
                            // it so it's a retained version rather than a ghost
                            // hidden from restore/version listings.
                            if (resurrecting)
                                oldRecord.IsDeleted = false;
                            await _catalog.UpdateFileRecordAsync(oldRecord, ct);
                        }

                        // The moved plain copy held the real bytes for its content
                        // hash; any .fileref pointing at it must have its
                        // ContentPath hint repointed to the new _prev location.
                        // (Hash-anchored, so this is best-effort upkeep only.)
                        if (!oldDeduped && !oldFileRef
                            && !string.IsNullOrEmpty(existingInfo.Hash))
                        {
                            await UpdateFileRefContentPathsAsync(
                                backupSetId, existingInfo.Hash, prevDiscPath,
                                targetDirectory, ct);
                            if (await ContentPathForHashAsync(existingInfo.Hash) is not null)
                                SetContentPath(existingInfo.Hash, prevDiscPath);
                        }
                    }
                }

                // Write the new file.
                string currentPath = GetCurrentPath(
                    targetDirectory, file.FullPath, isDeduped, isFileRef);
                string? currentDir = Path.GetDirectoryName(currentPath);
                if (currentDir is not null)
                    Directory.CreateDirectory(currentDir);

                if (isFileRef)
                {
                    // Write a .fileref manifest. It stores no bytes — the Hash
                    // resolves to an existing plain copy of the same content via
                    // the catalog at restore/verify time. SourcePath (this file's
                    // own source path) and ContentPath (where the bytes live) are
                    // self-describing extras for inspection and catalog-free
                    // restore; ContentPath is a hint, anchored by Hash.
                    string? contentDiscPath = await ContentPathForHashAsync(hash);
                    var manifest = new FileRefManifest
                    {
                        OriginalName = Path.GetFileName(file.FullPath),
                        OriginalSize = contentSize,
                        Hash = hash,
                        SourcePath = file.FullPath,
                        ContentPath = contentDiscPath ?? "",
                    };
                    string json = JsonSerializer.Serialize(manifest, _jsonOptions);
                    await File.WriteAllTextAsync(currentPath, json, ct);
                }
                else if (isDeduped && recipe is not null)
                {
                    // Write new blocks to the block store — from the in-memory
                    // buffer if the pre-pass kept it (no second read), otherwise
                    // by streaming the file from disk.
                    if (contentBuffer is not null)
                        await WriteNewBlocksFromBufferAsync(contentBuffer, recipe,
                            job.DeduplicationBlockSize, blockStoreDir, ct);
                    else
                        await WriteNewBlocksAsync(file.FullPath, recipe,
                            job.DeduplicationBlockSize, blockStoreDir, ct);

                    // Write the .dedup manifest (JSON recipe).
                    var manifest = new DedupManifest
                    {
                        OriginalName = Path.GetFileName(file.FullPath),
                        OriginalSize = recipe.OriginalSize,
                        OriginalHash = recipe.OriginalHash,
                        BlockSize = job.DeduplicationBlockSize,
                        BlockHashes = recipe.Blocks.Select(b => b.Hash).ToList(),
                    };
                    string json = JsonSerializer.Serialize(manifest, _jsonOptions);
                    await File.WriteAllTextAsync(currentPath, json, ct);
                }
                else if (StubPlainContentForTesting)
                {
                    // TESTING ONLY — write a tiny stub in place of the real bytes
                    // so a dedup test run doesn't fill the drive. The real
                    // directory tree and filename are preserved; only the content
                    // is a placeholder. Such a file can't be restored, so it's
                    // excluded from verification below.
                    await WritePlainTestStubAsync(currentPath, hash, contentSize, ct);
                    stubbedPlain = true;
                }
                else if (contentBuffer is not null)
                {
                    // Plain copy straight from the in-memory buffer — the file was
                    // already read (by the block-dedup pre-pass or the file-dedup
                    // hash read above), so it is not read from disk a second time.
                    await WritePlainFromBufferAsync(
                        contentBuffer, currentPath, ct, fileProgress, waitMidFile);
                }
                else if (deferHashToCopy)
                {
                    // Plain copy with the hash deferred to this single streaming
                    // pass: the file is read once, its bytes flow to the
                    // destination and through the hash at the same time. The hash
                    // and true byte count weren't known above (we proved the file
                    // is plain without reading it), so capture them here for the
                    // content-path seeding and catalog record below.
                    (hash, contentSize) = await CopyFileWithHashAsync(
                        file.FullPath, currentPath, ct, fileProgress, waitMidFile);
                }
                else
                {
                    // Plain copy (hash already computed above; file not buffered).
                    await CopyFileAsync(file.FullPath, currentPath, ct, fileProgress, waitMidFile);
                }

                // If this file was stored as a plain named copy, its content now
                // has a real-bytes home in the tree: remember the hash -> its
                // disc path so a later byte-identical file in this same run
                // becomes a .fileref (and gets its ContentPath hint) instead of a
                // second plain copy. Only plain copies seed this map — .fileref
                // and .dedup entries don't themselves hold the bytes.
                if (!isFileRef && !isDeduped && !string.IsNullOrEmpty(hash))
                    SetContentPath(hash, GetCurrentDiscPath(file.FullPath, false, false));

                // Register this plain copy's prefix hash so a LATER same-size file can
                // be ruled out (or escalated) by the progressive prefix check. Keyed
                // by the scan size (file.SizeBytes) to match how candidates collide
                // and how the check looks up. Only for intra-run-only colliding sizes
                // — the only sizes the check ever consults — so the index (and the
                // small prefix read for non-buffered files) stays off the common path.
                // Registering EVERY plain copy of such a size is required for
                // correctness: a missing entry could let a later identical file be
                // stored as a second plain copy instead of a .fileref (a missed dedup).
                if (!isFileRef && !isDeduped && !stubbedPlain
                    && IsIntraRunOnlyCollision(file.SizeBytes))
                {
                    string prefix = contentBuffer is not null
                        ? ComputePrefixHashOfBuffer(contentBuffer)
                        : await ComputePrefixHashAsync(currentPath, ct);
                    if (!intraRunPlainPrefixes.TryGetValue(file.SizeBytes, out var prefixSet))
                        intraRunPlainPrefixes[file.SizeBytes] = prefixSet = new HashSet<string>();
                    prefixSet.Add(prefix);
                }

                // Create catalog record.
                await _catalog.CreateFileRecordAsync(new FileRecord
                {
                    DiscId = discRecord.Id,
                    SourcePath = file.FullPath,
                    DiscPath = GetCurrentDiscPath(file.FullPath, isDeduped, isFileRef),
                    SizeBytes = contentSize,
                    Hash = hash,
                    IsZipped = false,
                    IsSplit = false,
                    IsDeduped = isDeduped,
                    IsFileRef = isFileRef,
                    Version = version,
                    SourceLastWriteUtc = file.LastWriteUtc,
                    BackedUpUtc = DateTime.UtcNow,
                }, ct);

                // A stubbed plain file holds placeholder content, so it would
                // fail a hash read-back — skip it during verification.
                if (!stubbedPlain)
                    backedUp?.TryAdd(file.FullPath, (hash, isDeduped, isFileRef));

                bytesWritten += file.SizeBytes;
                bytesProcessed += file.SizeBytes;
                batchCount++;
            }
            catch (OperationCanceledException) { throw; }
            catch (VersionTakenException)
            {
                restartFile = true;
            }
            catch (Exception ex)
            {
                string errorDetail = DescribeFileError(ex, file.FullPath);
                var category = BackupErrorClassifier.Classify(ex);

                // Destination out of space: this isn't a per-file problem — every
                // remaining file will fail the same way. Record this one failure,
                // stop the run cleanly (preserving everything already committed),
                // and let the user free space / deselect and run again, instead of
                // filling the failed/skipped list with hundreds of identical
                // "disk full" entries or prompting once per file.
                if (category == BackupErrorCategory.DiskFull)
                {
                    failedFiles.Add(new FailedFile
                    {
                        Path = file.FullPath,
                        Error = errorDetail,
                        ActionTaken = BurnFailureAction.Abort,
                    });
                    bytesProcessed += file.SizeBytes;
                    diskFullAbort = true;
                    progress?.Report(new BackupProgress
                    {
                        StatusMessage = "Destination disk is full — stopping backup. "
                            + "Free space or deselect files, then run again.",
                        CurrentFile = file.FullPath,
                        BytesWrittenTotal = bytesProcessed,
                        BytesTotalAll = totalBytes,
                        OverallPercentage = totalBytes > 0
                            ? (double)bytesProcessed / totalBytes * 100 : 0,
                    });
                    break; // leave the retry loop; the for-loop breaks below
                }

                // If the user previously chose "Skip All", skip without
                // prompting again.
                if (permanentSkipAll || onFailure is null)
                {
                    failedFiles.Add(new FailedFile
                    {
                        Path = file.FullPath,
                        Error = errorDetail,
                        ActionTaken = BurnFailureAction.Skip,
                    });
                    bytesProcessed += file.SizeBytes;
                    continue;
                }

                // Ask the user what to do.  Wrap in try/catch so a failure
                // in the dialog itself doesn't kill the entire backup.
                FailureDecision decision;
                try
                {
                    decision = await onFailure(file.FullPath, errorDetail, category);
                }
                catch
                {
                    // Callback failed (e.g. dialog error) -- treat as Skip.
                    failedFiles.Add(new FailedFile
                    {
                        Path = file.FullPath,
                        Error = errorDetail,
                        ActionTaken = BurnFailureAction.Skip,
                    });
                    bytesProcessed += file.SizeBytes;
                    continue;
                }

                switch (decision.Action)
                {
                    case BurnFailureAction.Retry:
                        fileRetrying = true;
                        break;

                    case BurnFailureAction.Abort:
                        // Record this file and stop.
                        failedFiles.Add(new FailedFile
                        {
                            Path = file.FullPath,
                            Error = errorDetail,
                            ActionTaken = BurnFailureAction.Abort,
                        });
                        throw new OperationCanceledException(
                            "Backup aborted by user due to file failure.");

                    case BurnFailureAction.SkipAllPermanently:
                        permanentSkipAll = true;
                        goto case BurnFailureAction.Skip;

                    case BurnFailureAction.Skip:
                    default:
                        failedFiles.Add(new FailedFile
                        {
                            Path = file.FullPath,
                            Error = errorDetail,
                            ActionTaken = decision.Action,
                        });
                        bytesProcessed += file.SizeBytes;
                        break;
                }

            }
            } // while (fileRetrying)

            if (restartFile)
            {
                // The same file again, numbered afresh, and read afresh: what was
                // read of it before the pause (its buffer, its block-dedup recipe)
                // may no longer be its content. Without a recipe it is copied plain.
                bufferedContent.Remove(file.FullPath);
                preRecipes.Remove(file.FullPath);
                i--;
                continue;
            }

            // Destination filled up — stop processing further files. Whatever was
            // committed is preserved; the next run picks up the rest.
            if (diskFullAbort)
                break;

            // Release this file's cached buffer (if any) now that it has been
            // written, freeing its memory (and thus system RAM, which the live
            // CanBuffer check reads) back for the files still ahead. Done here so
            // the bytes stay available across any retries above.
            bufferedContent.Remove(file.FullPath);

            // Dropping a buffer frees managed memory but .NET keeps large arrays'
            // pages in the working set until the LOH is compacted, so a mid-run
            // budget cut wouldn't visibly shrink the process just by draining
            // buffers. When we're over the (live) budget, hand the freed memory
            // back to the OS (throttled inside MaybeReclaim so the blocking
            // compaction only runs when needed, not on every file).
            MaybeReclaim();

            // Commit when: batch is full, a large file just finished, or
            // enough wall-clock time has elapsed since the last commit.
            if (batchCount >= CommitBatchSize
                || file.SizeBytes >= CommitLargeFileThreshold
                || commitTimer.Elapsed.TotalSeconds >= CommitIntervalSeconds)
            {
                await CommitAndReleaseAsync();

                // --- the one point in a long run where NO transaction is open ---
                //
                // Everything so far is committed and both the per-set gate and the
                // cross-process write lock are released, so the caller can safely do
                // its own catalog work here. The Worker uses it to drain the USN
                // journal, which otherwise goes unread for the scan's whole
                // duration: a 32 MB journal on a busy volume does not survive a
                // multi-hour backup, and once it wraps those changes are gone and
                // the next poll reports "continuity lost" — the very failure the
                // scan was running to repair.
                //
                // The handler is given the paths THIS run will write, to leave them
                // to it: backing one up from in here would copy the same file twice
                // in one run. (It would not corrupt the numbering, which this run
                // reads per file as it writes.)
                //
                // Deliberately hooked on the batch/interval commit only, not on the
                // pre-large-file flush above: this one is time-bounded
                // (CommitIntervalSeconds), so the handler cannot be called in a tight
                // loop by a run that happens to hit many large files in a row.
                if (onCommitBoundary is not null)
                {
                    try
                    {
                        await onCommitBoundary(pathsThisRunWillWrite, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Opportunistic side work — never fail the backup over it.
                    }
                }

                await ReacquireLockAsync();
            }
        }

        // Report completion of file copying so the UI shows 100%
        // even when many small files all processed within one throttle
        // window. (After a disk-full stop this reflects the partial run.)
        progress?.Report(new BackupProgress
        {
            StatusMessage = diskFullAbort ? "Stopped — disk full." : "Finalizing...",
            CurrentFile = "",
            BytesWrittenTotal = bytesProcessed,
            BytesTotalAll = totalBytes,
            OverallPercentage = diskFullAbort && totalBytes > 0
                ? (double)bytesProcessed / totalBytes * 100
                : 100,
        });

        // Final batch: update disc record and commit remaining records.
        discRecord.BytesUsed = bytesWritten;
        discRecord.Status = BurnSessionStatus.Completed;
        discRecord.LastWrittenUtc = DateTime.UtcNow;
        await _catalog.UpdateDiscAsync(discRecord, ct);

        tx.Complete();

        } // try (batch transaction)
        catch
        {
            // Save partial progress: commit whatever file records are in
            // the current transaction so the next backup doesn't redo them.
            try { tx.Complete(); } catch { /* best-effort */ }
            throw;
        }
        finally
        {
            tx.Dispose();
        }

        // Every planned file copied and committed - unless the disk filled first.
        run.CopyFinished = !diskFullAbort;

        // Update the backup set's last backup timestamp.
        var backupSet = await _catalog.GetBackupSetAsync(backupSetId, ct);
        if (backupSet is not null)
        {
            backupSet.LastBackupUtc = DateTime.UtcNow;
            // Timestamp only. The re-read above keeps this fresh, but between that
            // read and this write the selection can still change underneath.
            await _catalog.UpdateBackupSetMetadataAsync(backupSet, ct);
        }

        // 7. Verify backed-up files by reading them back and comparing hashes.
        if (backedUp is not null && backedUp.Count > 0)
        {
            var verifying = PhaseProgress.Begin(progress, "Verifying backup", backedUp.Count);
            var verifiedBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var verifyPaths = backedUp.Keys.ToList();

            // The newest live version of each path, a batch ahead of the files
            // being verified (see the check below).
            var liveNow = new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);
            var liveBatch = new LookupBatch();
            int liveUpTo = 0;

            for (int v = 0; v < verifyPaths.Count; v++)
            {
                string sourcePath = verifyPaths[v];
                var info = backedUp[sourcePath];
                ct.ThrowIfCancellationRequested();
                pauseEvent?.Wait(ct);

                verifying.Advance(v, sourcePath);

                // The write lock is no longer held, so another writer (the Worker)
                // may have written a newer version since, moving this run's copy
                // into _prev. The current path then holds that version, which is
                // no failure of this one: leave it to that writer.
                if (v >= liveUpTo)
                {
                    var batch = verifyPaths.GetRange(liveUpTo, Math.Min(liveBatch.Size, verifyPaths.Count - liveUpTo));
                    liveBatch.Starting();
                    liveNow = await _catalog.GetLatestVersionInfoForPathsAsync(backupSetId, batch, ct);
                    liveBatch.Finished(batch.Count);
                    liveUpTo += batch.Count;
                }
                if (!liveNow.TryGetValue(sourcePath, out var current)
                    || !string.Equals(current.Hash, info.Hash, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (info.IsFileRef)
                    {
                        // A .fileref stores no bytes — verify that the content it
                        // references resolves to an existing plain copy elsewhere
                        // in the tree with the correct hash.
                        if (!verifiedBlocks.Contains(info.Hash))
                        {
                            string? plainPath = await ResolvePlainContentPathAsync(
                                backupSetId, info.Hash, targetDirectory, ct);
                            if (plainPath is null || !File.Exists(plainPath))
                            {
                                failedFiles.Add(new FailedFile
                                {
                                    Path = sourcePath,
                                    Error = "Verification failed: file reference has no backing plain copy",
                                    ActionTaken = BurnFailureAction.Skip,
                                });
                            }
                            else
                            {
                                string destHash = await ComputeFileHashAsync(plainPath, ct);
                                if (destHash != info.Hash)
                                {
                                    failedFiles.Add(new FailedFile
                                    {
                                        Path = sourcePath,
                                        Error = "Verification failed: referenced content hash mismatch",
                                        ActionTaken = BurnFailureAction.Skip,
                                    });
                                }
                            }
                            verifiedBlocks.Add(info.Hash);
                        }
                    }
                    else if (info.IsDeduped)
                    {
                        // Read the .dedup manifest and verify each block.
                        string destPath = GetCurrentPath(
                            targetDirectory, sourcePath, isDeduped: true, isFileRef: false);
                        string json = await File.ReadAllTextAsync(destPath, ct);
                        var manifest = JsonSerializer.Deserialize<DedupManifest>(json, _jsonOptions);
                        if (manifest is null)
                        {
                            failedFiles.Add(new FailedFile
                            {
                                Path = sourcePath,
                                Error = "Verification failed: corrupt dedup manifest",
                                ActionTaken = BurnFailureAction.Skip,
                            });
                            continue;
                        }

                        foreach (string blockHash in manifest.BlockHashes)
                        {
                            if (verifiedBlocks.Contains(blockHash))
                                continue;

                            string blockPath = Path.Combine(blockStoreDir, blockHash + ".blk");
                            string actualHash = await ComputeFileHashAsync(blockPath, ct);
                            if (actualHash != blockHash)
                            {
                                failedFiles.Add(new FailedFile
                                {
                                    Path = sourcePath,
                                    Error = $"Verification failed: block hash mismatch ({blockHash})",
                                    ActionTaken = BurnFailureAction.Skip,
                                });
                                break;
                            }
                            verifiedBlocks.Add(blockHash);
                        }
                    }
                    else
                    {
                        // Plain copy — hash the destination file.
                        string destPath = GetCurrentPath(
                            targetDirectory, sourcePath, isDeduped: false, isFileRef: false);
                        string destHash = await ComputeFileHashAsync(destPath, ct);
                        if (destHash != info.Hash)
                        {
                            failedFiles.Add(new FailedFile
                            {
                                Path = sourcePath,
                                Error = "Verification failed: hash mismatch",
                                ActionTaken = BurnFailureAction.Skip,
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedFiles.Add(new FailedFile
                    {
                        Path = sourcePath,
                        Error = $"Verification failed: {ex.Message}",
                        ActionTaken = BurnFailureAction.Skip,
                    });
                }
            }
        }

        // 8. Apply retention: physically delete old version files.
        // Use per-file tier resolution when a tier resolver is available,
        // otherwise fall back to the single retentionTiers list.
        bool hasRetention = tierResolver is not null || (retentionTiers is not null && retentionTiers.Count > 0);
        if (hasRetention)
        {
            try
            {
                // A targeted run only added versions to the files it just wrote,
                // so only those paths can have newly exceeded their quota - and
                // of those, only the ones it gave a second or later version
                // (pathsWithHistory), since a first version leaves no previous
                // one to prune. The whole-set alternative materialises every
                // record in the set (2.35M rows / ~10.4 s of SQL on a real set
                // database, plus the objects) after backing up, typically, one
                // file — the largest single term in the Worker pegging a CPU
                // core. Age-driven pruning of untouched paths still happens on
                // the next full backup, which passes a null scope.
                Func<string, IReadOnlyList<VersionRetentionTier>> tierSelector =
                    tierResolver is not null
                        ? path => tierResolver(path).Tiers
                        : _ => retentionTiers!;

                IReadOnlyList<FileRecord> toDelete;
                if (pathsWithHistory is null)
                {
                    // The whole set, in one query: nothing to count through.
                    progress?.Report(new BackupProgress
                    {
                        StatusMessage = "Applying retention rules...",
                        BytesWrittenTotal = bytesProcessed,
                        BytesTotalAll = totalBytes,
                        OverallPercentage = 100,
                    });
                    toDelete = await _retention.ComputeRetentionAsync(backupSetId, tierSelector, null, ct);
                }
                else
                {
                    // A batch of paths at a time, so the count moves and Pause
                    // holds. Retention keeps no state across paths, so the
                    // batches' verdicts are the scope's.
                    var reading = PhaseProgress.Begin(progress, "Applying retention rules", pathsWithHistory.Count);
                    var expired = new List<FileRecord>();
                    await InBatchesAsync(pathsWithHistory,
                        async batch => expired.AddRange(
                            await _retention.ComputeRetentionAsync(backupSetId, tierSelector, batch, ct)),
                        done => reading.Advance(done, ""), pauseEvent, ct);
                    toDelete = expired;
                }

                var toDeleteIds = toDelete.Select(f => f.Id).ToHashSet();
                var removing = PhaseProgress.Begin(progress, "Removing expired versions", toDelete.Count);

                for (int i = 0; i < toDelete.Count; i++)
                {
                    var fileRecord = toDelete[i];
                    pauseEvent?.Wait(ct);
                    ct.ThrowIfCancellationRequested();

                    // Physically delete the .v{N} file (with .dedup/.fileref/no
                    // suffix) from the _prev directory.  Locate it via the
                    // record's stored DiscPath — the authoritative on-disk
                    // location — rather than reconstructing the path from
                    // (SourcePath, Version, flags).  A reconstruction can diverge
                    // from the real DiscPath for legacy/migrated rows, which would
                    // point File.Delete at the wrong path: the bytes survive while
                    // we still flip IsDeleted below, producing the
                    // "catalog-deleted (still on disk)" inconsistency.
                    string prevPath = Path.Combine(targetDirectory, fileRecord.DiscPath);
                    removing.Advance(i, prevPath);

                    // Last-plain-copy guard. A plain _prev file holds the real
                    // bytes for its content hash, and file-level dedup .fileref
                    // entries elsewhere resolve to it by hash. If this is the only
                    // surviving plain copy of that content and a SURVIVING .fileref
                    // still references it, deleting the bytes would orphan that
                    // reference (an unrestorable file). In that case promote one
                    // surviving reference into a real plain file first, using these
                    // bytes, before deleting.
                    if (!fileRecord.IsFileRef && !fileRecord.IsDeduped
                        && !string.IsNullOrEmpty(fileRecord.Hash)
                        && File.Exists(prevPath))
                    {
                        var others = (await _catalog.GetActiveRecordsByHashAsync(
                                backupSetId, fileRecord.Hash, ct))
                            .Where(r => r.Id != fileRecord.Id)
                            .ToList();

                        var survivingPlain = others.FirstOrDefault(r =>
                            !r.IsFileRef && !r.IsDeduped && !toDeleteIds.Contains(r.Id));
                        bool anotherPlainSurvives = survivingPlain is not null;

                        if (!anotherPlainSurvives)
                        {
                            var survivingRef = others.FirstOrDefault(r =>
                                r.IsFileRef && !toDeleteIds.Contains(r.Id));

                            if (survivingRef is not null)
                            {
                                // Promote the surviving reference to a plain copy
                                // (it becomes the content's new home), then fall
                                // through to delete this one. If promotion fails,
                                // keep this file so nothing is ever orphaned.
                                if (!await TryPromoteFileRefToPlainAsync(
                                        targetDirectory, prevPath, survivingRef, ct))
                                {
                                    continue; // leave this file + record intact
                                }

                                // The promoted reference now holds the bytes at its
                                // suffix-less path; repoint every other .fileref's
                                // ContentPath hint to it (best-effort).
                                await UpdateFileRefContentPathsAsync(
                                    backupSetId, fileRecord.Hash,
                                    survivingRef.DiscPath, targetDirectory, ct);
                            }
                        }
                        else
                        {
                            // A different plain copy survives; the bytes don't move,
                            // but this deleted plain may have been a .fileref's
                            // ContentPath target. Repoint hints to the survivor.
                            await UpdateFileRefContentPathsAsync(
                                backupSetId, fileRecord.Hash,
                                survivingPlain!.DiscPath, targetDirectory, ct);
                        }
                    }

                    // Remove the physical file, then flip the catalog bit ONLY
                    // once the bytes are confirmed gone.  If the delete fails
                    // (file locked, ACL, transient IO), leave the record intact so
                    // the catalog never claims a deletion that didn't happen — a
                    // later retention pass retries.  This is the invariant that
                    // prevents "catalog-deleted (still on disk)" records.
                    try
                    {
                        ForceDeleteFile(prevPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue; // leave file + record consistent; retry next run
                    }

                    // Mark as deleted in catalog only if the file is really gone.
                    if (!File.Exists(prevPath))
                    {
                        fileRecord.IsDeleted = true;
                        await _catalog.UpdateFileRecordAsync(fileRecord, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Retention failure is non-fatal; the backup itself succeeded.
                // A cancellation is not a failure to swallow: the user asked to
                // stop, and the run must end as cancelled, not as completed.
            }
        }

        return new BackupResult
        {
            Success = failedFiles.Count == 0,
            DiscsWritten = 1,
            BytesWritten = bytesWritten,
            FailedFiles = failedFiles,
        };
    }

    /// <summary>
    /// Scan sources and compute diff for planning purposes (no writes).
    /// </summary>
    public async Task<(BackupDiff Diff, long TotalBytes, int TotalFiles)> PlanAsync(
        BackupJob job, CancellationToken ct,
        IProgress<ScanProgress>? scanProgress = null)
    {
        var isExcluded = BuildExclusionFilter(job);
        var scanned = await _scanner.ScanAsync(
            job.Sources, progress: scanProgress, ct, isExcluded, BuildDirectoryPruneFilter(job));

        BackupDiff diff;
        if (job.BackupSetId.HasValue)
        {
            diff = await _scanner.ComputeDiffAsync(scanned, job.BackupSetId.Value, ct);
        }
        else
        {
            diff = new BackupDiff
            {
                NewFiles = scanned,
                ChangedFiles = [],
                DeletedFiles = [],
            };
        }

        var files = diff.NewFiles.Concat(diff.ChangedFiles).ToList();
        long totalBytes = files.Sum(f => f.SizeBytes);

        return (diff, totalBytes, files.Count);
    }

    /// <summary>
    /// Back up an explicit list of candidate file paths instead of scanning the
    /// whole source tree. Used by the continuous-backup worker, which is handed
    /// the exact set of changed paths by the NTFS USN change journal.
    /// </summary>
    /// <remarks>
    /// Candidates are filtered by the job's glob/extension exclusions and
    /// compared against the catalog so only genuinely new or changed files are
    /// versioned (a USN record can fire for metadata-only touches). Missing
    /// files are skipped — deletions are reconciled by the periodic full scan.
    /// The resulting <see cref="BackupDiff"/> is handed to
    /// <see cref="ExecuteAsync"/>, reusing all per-file versioning, dedup,
    /// retention, and catalog machinery.
    /// </remarks>
    /// <param name="progress">
    /// Progress through the check, then per-file progress as <see cref="ExecuteAsync"/>
    /// reports it. The GUI's "back up the folders you just added" run shows both.
    /// </param>
    /// <param name="pauseEvent">Pause support, as for <see cref="ExecuteAsync"/>; it holds the check too.</param>
    public async Task<BackupResult> ExecuteTargetedAsync(
        BackupJob job,
        string targetDirectory,
        IReadOnlyList<string> candidatePaths,
        IReadOnlyList<VersionRetentionTier>? retentionTiers,
        CancellationToken ct,
        IProgress<BackupProgress>? progress = null,
        ManualResetEventSlim? pauseEvent = null)
    {
        if (!job.BackupSetId.HasValue)
            throw new ArgumentException("Targeted backup requires an existing backup set.", nameof(job));
        int backupSetId = job.BackupSetId.Value;

        var isExcluded = BuildExclusionFilter(job);

        // Stat each candidate, then ask the catalog about it under the SAME
        // canonical path the comparison below uses (FileInfo.FullName, not the raw
        // USN-supplied string) - a batch at a time (LookupBatch), so the count on
        // screen moves through both halves of the work and Pause holds it.
        //
        // Only the candidate paths matter here, so they are looked up one indexed
        // seek each rather than by materialising the set's entire manifest. This
        // method runs on EVERY continuous-backup wake-up — including the
        // overwhelmingly common case where the USN journal reported a
        // metadata-only touch and nothing ends up being backed up at all — so a
        // whole-set query here cost a full 2.2M-row scan plus a window-function
        // sort (~12.5 s, ~110 MB/s of reads) per wake-up, per set, forever. That is
        // what pegged a CPU core in the Worker service; see the "Worker pegged a
        // CPU core" entry in known-issues.md.
        var newFiles = new List<ScannedFile>();
        var changedFiles = new List<ScannedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new LookupBatch();
        var chunk = new List<ScannedFile>();
        var checking = PhaseProgress.Begin(
            progress, "Checking files against the catalog", candidatePaths.Count);

        for (int i = 0; i < candidatePaths.Count; i++)
        {
            pauseEvent?.Wait(ct);
            ct.ThrowIfCancellationRequested();

            string path = candidatePaths[i];
            if ((isExcluded is null || !isExcluded(path))
                && StatCandidate(path) is { } scanned   // null: deleted — handled by the periodic full scan
                && seen.Add(scanned.FullPath))
            {
                chunk.Add(scanned);
                if (chunk.Count >= batch.Size)
                    await ClassifyChunkAsync();
            }
            checking.Advance(i + 1, path);
        }
        await ClassifyChunkAsync();

        if (newFiles.Count == 0 && changedFiles.Count == 0)
        {
            return new BackupResult
            {
                Success = true,
                DiscsWritten = 0,
                BytesWritten = 0,
                FailedFiles = [],
            };
        }

        var diff = new BackupDiff
        {
            NewFiles = newFiles,
            ChangedFiles = changedFiles,
            DeletedFiles = [],
        };

        return await ExecuteAsync(
            job, targetDirectory, retentionTiers,
            progress, ct, pauseEvent, precomputedDiff: diff,
            scopeRetentionToBackedUpFiles: true);

        // Sort the stat'ed candidates in `chunk` into new and changed by what the
        // catalog holds for them.
        async Task ClassifyChunkAsync()
        {
            if (chunk.Count == 0)
                return;
            batch.Starting();
            var versions = await _catalog.GetLatestVersionInfoForPathsAsync(
                backupSetId, chunk.Select(f => f.FullPath).ToList(), ct);
            batch.Finished(chunk.Count);
            foreach (var file in chunk)
            {
                if (!versions.TryGetValue(file.FullPath, out var last))
                {
                    newFiles.Add(file);
                }
                else if (file.SizeBytes != last.SizeBytes
                         || file.LastWriteUtc > last.SourceLastWriteUtc)
                {
                    changedFiles.Add(file);
                }
                // else: catalog already has this exact version — nothing to do.
            }
            chunk.Clear();
        }
    }

    /// <summary>
    /// How many items (paths, sizes) to put to the catalog in one batch when a run
    /// works through thousands or millions of them: about <see cref="TargetMs"/>
    /// of work. A lookup call holds the set's connection, and Pause and the
    /// progress count are serviced only between batches, so one must stay well
    /// inside a progress interval; yet a call's fixed cost (the gate, preparing
    /// the statement) should vanish into its seeks. On a 5.7M-row set a seek takes 15-60 µs when its
    /// pages are cached and 1.5-3.6 ms when they are not, and 9 ms has been seen
    /// with the Worker writing to the same set, so the size follows the rate
    /// measured on the calls so far.
    /// </summary>
    private sealed class LookupBatch
    {
        private const int TargetMs = 100, Smallest = 16, Largest = 4096;
        private readonly System.Diagnostics.Stopwatch _clock = new();

        /// <summary>Items to put in the next call.</summary>
        public int Size { get; private set; } = 256;

        /// <summary>Call as a lookup starts.</summary>
        public void Starting() => _clock.Restart();

        /// <summary>Call when a lookup of <paramref name="items"/> items is done, to size the next.</summary>
        public void Finished(int items)
        {
            double ms = Math.Max(_clock.Elapsed.TotalMilliseconds, 0.1);
            Size = (int)Math.Clamp(items * TargetMs / ms, Smallest, Largest);
        }
    }

    /// <summary>
    /// The candidate as it is on disk now, under its canonical full path; null
    /// when it no longer exists or cannot be read.
    /// </summary>
    private static ScannedFile? StatCandidate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return new ScannedFile
            {
                FullPath = info.FullName,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sizes, among the keys of <paramref name="filesPerSize"/>, that some
    /// active plain copy in the set already has. Probed a chunk of sizes at a
    /// time, reported as the files those sizes cover.
    /// </summary>
    /// <param name="filesPerSize">This run's files, counted by size.</param>
    /// <param name="fileCount">Files in the run; the phase's total.</param>
    private async Task<HashSet<long>> FindStoredSizesAsync(
        int backupSetId, Dictionary<long, int> filesPerSize, int fileCount,
        IProgress<BackupProgress>? progress, ManualResetEventSlim? pauseEvent, CancellationToken ct)
    {
        var stored = new HashSet<long>();
        var probing = PhaseProgress.Begin(progress, "Checking which files might be duplicates", fileCount);
        long filesCovered = 0;
        await InBatchesAsync(filesPerSize.Keys.ToList(), async sizes =>
        {
            stored.UnionWith(await _catalog.GetActivePlainSizesPresentAsync(backupSetId, sizes, ct));
            foreach (long size in sizes)
                filesCovered += filesPerSize[size];
        }, _ => probing.Advance(filesCovered, ""), pauseEvent, ct);
        return stored;
    }

    /// <summary>
    /// Do <paramref name="work"/> on <paramref name="items"/> a batch at a time,
    /// each batch sized by <see cref="LookupBatch"/>, waiting out a Pause and
    /// stopping for Cancel between batches.
    /// </summary>
    /// <param name="afterBatch">Given how many items are done, after each batch.</param>
    private static async Task InBatchesAsync<T>(
        List<T> items, Func<List<T>, Task> work, Action<int> afterBatch,
        ManualResetEventSlim? pauseEvent, CancellationToken ct)
    {
        var sizing = new LookupBatch();
        for (int start = 0; start < items.Count;)
        {
            pauseEvent?.Wait(ct);
            ct.ThrowIfCancellationRequested();
            var batch = items.GetRange(start, Math.Min(sizing.Size, items.Count - start));
            sizing.Starting();
            await work(batch);
            sizing.Finished(batch.Count);
            start += batch.Count;
            afterBatch(start);
        }
    }

    /// <summary>
    /// Reports a phase that works through files, such as the checks before the
    /// first file is copied, as <see cref="BackupProgress.PhaseFilesDone"/> of
    /// <see cref="BackupProgress.PhaseFilesTotal"/> at the <see cref="ProgressCadence"/>.
    /// Over millions of files such a phase runs for minutes; one unchanging line
    /// for all of them reads as a hang.
    /// </summary>
    private sealed class PhaseProgress
    {
        private readonly IProgress<BackupProgress>? _progress;
        private readonly string _phase;
        private readonly long _total;
        private readonly ProgressCadence _cadence = new();

        private PhaseProgress(IProgress<BackupProgress>? progress, string phase, long total)
        {
            _progress = progress;
            _phase = phase;
            _total = total;
        }

        /// <summary>
        /// Start a phase, and say so at once: the previous phase's last report must
        /// not stay on screen while this one does its first stretch of work.
        /// </summary>
        /// <param name="phase">What it does, as the panel's headline: "Checking files against the catalog".</param>
        /// <param name="total">Files it will work through.</param>
        public static PhaseProgress Begin(IProgress<BackupProgress>? progress, string phase, long total)
        {
            var started = new PhaseProgress(progress, phase, total);
            // Outside the cadence, so the first Advance reports too and names the
            // phase's first file: that file can take minutes on its own.
            started.Report(0, "");
            return started;
        }

        /// <summary>Report <paramref name="done"/> files finished, if one is due.</summary>
        /// <param name="currentPath">The file just finished or now under way, named on the panel; empty for none.</param>
        public void Advance(long done, string currentPath)
        {
            if (_progress is not null && _cadence.IsDue())
                Report(done, currentPath);
        }

        private void Report(long done, string currentPath)
        {
            if (_progress is null || _total <= 0)
                return;
            _progress.Report(new BackupProgress
            {
                StatusMessage = _phase,
                CurrentFile = currentPath,
                PhaseFilesDone = done,
                PhaseFilesTotal = _total,
            });
        }
    }

    /// <summary>
    /// Relocate a file's or directory's existing destination copy to match a
    /// same-volume source rename/move, instead of re-copying its bytes. Handles
    /// every format a directory backup produces — plain copies, <c>.dedup</c> and
    /// <c>.fileref</c> manifests (whose shared block store / content-by-hash isn't
    /// affected by a rename), and the full <c>_prev</c> version history — by
    /// renaming both the current subtree and the parallel <c>_prev</c> subtree and
    /// updating every affected catalog record's paths in one transaction. A single
    /// file relocates each of its versions' on-disk copies individually.
    /// </summary>
    /// <remarks>
    /// Renames and moves are indistinguishable here (both are a same-volume
    /// old-path→new-path pair) and are handled identically. Only split or zipped
    /// records — which a directory backup never creates — force a fall-back, since
    /// their bytes span discs / live inside an archive a plain rename can't move.
    /// The physical rename happens before the catalog commits, so an interrupted
    /// run fails safe toward a harmless re-copy rather than data loss.
    /// </remarks>
    /// <returns>
    /// <see cref="TargetedMoveOutcome.Relocated"/> when the destination copy was
    /// moved and the catalog updated; <see cref="TargetedMoveOutcome.NothingToRelocate"/>
    /// when the old path was never tracked (no destination copy exists — back up the
    /// new path as a fresh source, nothing to reconcile);
    /// <see cref="TargetedMoveOutcome.ExcludedAtDestination"/> when the move lands the
    /// item on a path the set excludes, so its backup is released rather than carried
    /// along; <see cref="TargetedMoveOutcome.FellBack"/> when a tracked item could not be
    /// relocated safely and the caller should back up the new path as fresh files and
    /// reconcile the vacated old path.
    /// </returns>
    public async Task<TargetedMoveOutcome> MoveTargetedAsync(
        BackupJob job,
        string targetDirectory,
        string oldPath,
        string newPath,
        bool isDirectory,
        CancellationToken ct,
        Action<string>? trace = null)
    {
        if (!job.BackupSetId.HasValue)
            throw new ArgumentException("Targeted move requires an existing backup set.", nameof(job));

        int setId = job.BackupSetId.Value;

        var records = isDirectory
            ? await _catalog.GetFileRecordsUnderDirectoryAsync(setId, oldPath, ct)
            : await _catalog.GetFileRecordsByPathAsync(setId, oldPath, ct);

        trace?.Invoke($"MoveTargetedAsync: found {records.Count} catalog record(s) under old path '{oldPath}'.");

        // Nothing tracked under the old path — there is no destination copy to
        // relocate. The new side is just a fresh source (typically an atomic save).
        if (records.Count == 0)
        {
            trace?.Invoke("no records under old path -> NothingToRelocate (old name was never backed up).");
            return TargetedMoveOutcome.NothingToRelocate;
        }

        // A rename can carry a tracked file INTO a path the set excludes — move
        // foo.py into a build\ directory, or rename src\ to debug\ — and the
        // exclusion rules must apply to it exactly as they would to a file
        // created there. Nothing else on the rename fast path consults them: the
        // worker's endpoint test (PathBelongsToSet) only asks whether the path is
        // inside the SELECTION, and the copy path's filter (see
        // ExecuteTargetedAsync) is never reached because a relocation copies
        // nothing. Without this check the destination copy is renamed into place
        // and the catalog row repointed at the excluded path, leaving a file
        // backed up that the set says it does not back up — which then surfaces
        // in Cleanup under "Matches Exclusion Filter" with no explanation of how
        // it got there. See the exclusion-vs-rename entry in known-issues.md.
        //
        // ANY excluded landing path releases the whole move, not just the
        // excluded records: falling back re-offers every file individually to the
        // copy path, where the same filter drops exactly the excluded ones and
        // keeps the rest. Relocating the remainder and releasing only some would
        // have to split one atomic catalog transaction into two.
        var isExcluded = BuildExclusionFilter(job);
        if (isExcluded is not null)
        {
            string? excludedLanding = records
                .Select(r => isDirectory
                    ? RemapPathPrefix(r.SourcePath, oldPath, newPath)
                    : newPath)
                .FirstOrDefault(isExcluded);

            if (excludedLanding is not null)
            {
                trace?.Invoke(
                    $"new path '{excludedLanding}' matches the set's exclusion rules -> " +
                    "ExcludedAtDestination (releasing the backup instead of relocating it).");
                return TargetedMoveOutcome.ExcludedAtDestination;
            }
        }

        // Directory backups only ever produce plain / .dedup / .fileref records
        // (IsSplit and IsZipped are always false — those are optical-media
        // formats). A split file's bytes span discs and a zipped file lives inside
        // an archive, neither of which a plain rename can relocate, so bail safely
        // if one somehow appears.
        if (records.Any(r => r.IsSplit || r.IsZipped))
        {
            trace?.Invoke("record(s) are split/zipped -> FellBack (cannot relocate optical-media formats).");
            return TargetedMoveOutcome.FellBack;
        }

        try
        {
            return isDirectory
                ? await RelocateDirectoryAsync(setId, targetDirectory, oldPath, newPath, records, ct, trace)
                : await RelocateFileAsync(setId, targetDirectory, newPath, records, ct, trace);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Destination busy/locked or a name collision — fall back to a re-copy
            // rather than risk a half-applied relocation.
            trace?.Invoke($"relocation threw {ex.GetType().Name}: {ex.Message} -> FellBack (will re-copy).");
            return TargetedMoveOutcome.FellBack;
        }
    }

    /// <summary>
    /// Relocate a moved/renamed directory: rename both the current subtree
    /// (<c>{drive}\rel</c>) and the parallel history subtree
    /// (<c>{drive}_prev\rel</c>) — same source volume, so the <c>{drive}</c> prefix
    /// is unchanged — then repoint every affected catalog record (current and all
    /// <c>_prev</c> versions, including <c>.dedup</c>/<c>.fileref</c> manifests) in
    /// a single transaction. Physical renames happen first; if the catalog update
    /// throws, they are reversed and the transaction rolls back.
    /// </summary>
    private async Task<TargetedMoveOutcome> RelocateDirectoryAsync(
        int setId, string targetDirectory, string oldPath, string newPath,
        IReadOnlyList<FileRecord> records, CancellationToken ct,
        Action<string>? trace = null)
    {
        string drivePrefix = GetDrivePrefix(oldPath);
        string oldRel = GetRelativePath(oldPath);
        string newRel = GetRelativePath(newPath);

        string oldCur = Path.Combine(targetDirectory, drivePrefix, oldRel);
        string newCur = Path.Combine(targetDirectory, drivePrefix, newRel);
        string oldPrev = Path.Combine(targetDirectory, drivePrefix + "_prev", oldRel);
        string newPrev = Path.Combine(targetDirectory, drivePrefix + "_prev", newRel);

        trace?.Invoke($"RelocateDirectoryAsync: current tree '{oldCur}' -> '{newCur}'");
        trace?.Invoke($"RelocateDirectoryAsync: history tree '{oldPrev}' -> '{newPrev}'");

        bool curMoved = false, prevMoved = false;
        try
        {
            if (Directory.Exists(oldCur))
            {
                EnsureParentDirectory(newCur);
                Directory.Move(oldCur, newCur);
                curMoved = true;
                trace?.Invoke("renamed current-version tree on disk.");
            }
            else
            {
                trace?.Invoke($"current tree not present on disk ('{oldCur}' does not exist).");
            }
            if (Directory.Exists(oldPrev))
            {
                EnsureParentDirectory(newPrev);
                Directory.Move(oldPrev, newPrev);
                prevMoved = true;
                trace?.Invoke("renamed history (_prev) tree on disk.");
            }

            // Nothing physically present under the old path — let the caller
            // re-copy the new path as fresh files.
            if (!curMoved && !prevMoved)
            {
                trace?.Invoke("nothing physically present under old path -> FellBack (will re-copy new path).");
                return TargetedMoveOutcome.FellBack;
            }

            using var tx = await _catalog.BeginTransactionAsync(setId, ct);
            foreach (var rec in records)
            {
                ct.ThrowIfCancellationRequested();
                string newSource = RemapPathPrefix(rec.SourcePath, oldPath, newPath);
                rec.DiscPath = RemapDiscPath(rec, newSource);
                rec.SourcePath = newSource;
                await _catalog.UpdateFileRecordAsync(rec, ct);
            }
            tx.Complete();
            trace?.Invoke($"updated {records.Count} catalog record(s) to the new path -> Relocated.");
            return TargetedMoveOutcome.Relocated;
        }
        catch
        {
            // Reverse whatever we physically moved so the destination tree matches
            // the (rolled-back) catalog, then fall back to a re-copy.
            trace?.Invoke("catalog update failed — reversing physical rename(s) and rolling back.");
            if (prevMoved) TryMoveDirectoryBack(newPrev, oldPrev);
            if (curMoved) TryMoveDirectoryBack(newCur, oldCur);
            throw;
        }
    }

    /// <summary>
    /// Relocate a moved/renamed single file: rename each of its on-disk versions
    /// (the current copy plus every <c>_prev</c> version, in whatever format —
    /// plain, <c>.dedup</c>, <c>.fileref</c>) and repoint their catalog records in
    /// one transaction. Physical renames happen first and are reversed if the
    /// catalog update throws.
    /// </summary>
    private async Task<TargetedMoveOutcome> RelocateFileAsync(
        int setId, string targetDirectory, string newPath,
        IReadOnlyList<FileRecord> records, CancellationToken ct,
        Action<string>? trace = null)
    {
        // (newAbs -> oldAbs) pairs we can undo on failure.
        var undo = new List<(string From, string To)>();
        try
        {
            foreach (var rec in records)
            {
                string oldAbs = Path.Combine(targetDirectory, rec.DiscPath);
                string newAbs = Path.Combine(targetDirectory, RemapDiscPath(rec, newPath));
                if (!File.Exists(oldAbs))
                    continue;

                EnsureParentDirectory(newAbs);
                File.Move(oldAbs, newAbs, overwrite: false);
                undo.Add((newAbs, oldAbs));
                trace?.Invoke($"RelocateFileAsync: renamed '{oldAbs}' -> '{newAbs}'");
            }

            // Nothing physically present — let the caller re-copy from the new source.
            if (undo.Count == 0)
            {
                trace?.Invoke("no destination copies present -> FellBack (will re-copy new path).");
                return TargetedMoveOutcome.FellBack;
            }

            using var tx = await _catalog.BeginTransactionAsync(setId, ct);
            foreach (var rec in records)
            {
                ct.ThrowIfCancellationRequested();
                rec.DiscPath = RemapDiscPath(rec, newPath);
                rec.SourcePath = newPath;
                await _catalog.UpdateFileRecordAsync(rec, ct);
            }
            tx.Complete();
            trace?.Invoke($"updated {records.Count} catalog record(s) -> Relocated.");
            return TargetedMoveOutcome.Relocated;
        }
        catch
        {
            foreach (var (from, to) in undo)
            {
                try { if (File.Exists(from)) File.Move(from, to, overwrite: false); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Recompute a record's disc-relative path for its new source path, preserving
    /// whether it is the current version or a <c>_prev</c> version and its storage
    /// format (<c>.dedup</c>/<c>.fileref</c>). The current on-disk location is read
    /// from <paramref name="rec"/>.DiscPath, so call this before mutating it.
    /// </summary>
    private static string RemapDiscPath(FileRecord rec, string newSourcePath)
    {
        return IsPrevDiscPath(rec.DiscPath)
            ? GetPrevDiscPath(newSourcePath, rec.Version, rec.IsDeduped, rec.IsFileRef)
            : GetCurrentDiscPath(newSourcePath, rec.IsDeduped, rec.IsFileRef);
    }

    /// <summary>
    /// True when a disc-relative path sits under a history tree — its first path
    /// segment (the drive folder) ends with <c>_prev</c>.
    /// </summary>
    private static bool IsPrevDiscPath(string discPath)
    {
        int sep = discPath.IndexOf('\\');
        return sep > 0
            && discPath.AsSpan(0, sep).EndsWith("_prev", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureParentDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null)
            Directory.CreateDirectory(parent);
    }

    private static void TryMoveDirectoryBack(string from, string to)
    {
        try { if (Directory.Exists(from)) Directory.Move(from, to); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Replace an <paramref name="oldPrefix"/> path prefix on <paramref name="path"/>
    /// with <paramref name="newPrefix"/>, respecting directory boundaries. When
    /// <paramref name="path"/> equals <paramref name="oldPrefix"/> exactly (the
    /// moved item itself), it maps to <paramref name="newPrefix"/>.
    /// </summary>
    private static string RemapPathPrefix(string path, string oldPrefix, string newPrefix)
    {
        if (string.Equals(path, oldPrefix, StringComparison.OrdinalIgnoreCase))
            return newPrefix;

        string trimmedOld = oldPrefix.TrimEnd('\\');
        string trimmedNew = newPrefix.TrimEnd('\\');
        if (path.StartsWith(trimmedOld + "\\", StringComparison.OrdinalIgnoreCase))
            return trimmedNew + path[trimmedOld.Length..];

        return path; // not under the prefix — leave untouched
    }

    // -------------------------------------------------------------------
    // Exclusion filter
    // -------------------------------------------------------------------

    /// <summary>
    /// Build the combined file exclusion filter from global extension patterns
    /// and tier-based exclusion (tier sets with 0 tiers = excluded from backup).
    /// </summary>
    public static Func<string, bool>? BuildExclusionFilter(BackupJob job)
        => BuildExclusionFilter(job.ExcludedExtensions, job.TierSets);

    /// <summary>
    /// Overload taking the two settings the filter actually depends on, so
    /// callers holding a <see cref="JobOptions"/> (which carries the same two
    /// fields) rather than a <see cref="BackupJob"/> can use the REAL filter
    /// instead of re-implementing it.
    /// </summary>
    /// <remarks>
    /// This exists because a hand-written copy of this logic in the Orphaned
    /// Directories view had already drifted from the original — it was missing
    /// the unconditional hard exclusions, so anything excluded by the app rather
    /// than by user patterns simply never showed up there. One definition, two
    /// entry points.
    /// </remarks>
    public static Func<string, bool>? BuildExclusionFilter(
        IReadOnlyList<string> excludedExtensions,
        IReadOnlyList<VersionTierSet> tierSets)
    {
        var globalFilter = excludedExtensions.Count > 0
            ? GlobMatcher.CreateFilter(excludedExtensions) : null;

        // Tier sets with 0 tiers act as exclusion rules: matched files are
        // not backed up at all.  Build a resolver and check at scan time.
        Func<string, VersionTierSet>? tierResolver = null;
        if (tierSets.Count > 0)
        {
            var resolver = VersionTierSet.BuildTierResolver(tierSets);
            // Only pay the per-file cost if at least one non-default tier set
            // has 0 tiers (i.e. acts as an exclusion set).
            bool hasExclusionTierSet = tierSets.Any(ts =>
                ts.Tiers.Count == 0
                && ts.FilePatterns.Count > 0
                && !string.Equals(ts.Name, "Default", StringComparison.OrdinalIgnoreCase));
            if (hasExclusionTierSet)
                tierResolver = resolver;
        }

        // Note: we always return a non-null filter (even with no user exclusions)
        // because the app's own data directory is unconditionally excluded below.
        return path =>
        {
            // Hard exclusion: never back up the app's own catalog/log/dump files.
            // This is independent of the selection tree and glob patterns so an
            // auto-include-new parent (e.g. all of C:\) can't sweep in
            // C:\ProgramData\LithicBackup — the worker would otherwise try to back
            // up its own live, open databases and fail with lock/sharing errors.
            if (CatalogLocation.IsInsideAppDataDirectory(path))
                return true;

            // Hard exclusion: NTFS volume metadata ($Extend and friends). The USN
            // journal reports these by name, so continuous backup can resolve a
            // real, openable path under them — but directory enumeration never
            // returns them, so they can't appear in the selection treeview and the
            // user has no way to see or exclude them. The largest of them,
            // \$Extend\$Deleted, is NTFS's holding area for files that are IN THE
            // PROCESS OF BEING DELETED, so backing it up stores exactly the data
            // the user just discarded. See VolumeMetadataPaths and the "$Extend"
            // entry in known-issues.md.
            if (VolumeMetadataPaths.IsVolumeMetadata(path))
                return true;

            if (globalFilter?.Invoke(path) ?? false)
                return true;
            if (tierResolver is not null && tierResolver(path).Tiers.Count == 0)
                return true;
            return false;
        };
    }

    /// <summary>
    /// Companion to <see cref="BuildExclusionFilter(BackupJob)"/> that answers
    /// the stricter question: is every file anywhere beneath this DIRECTORY
    /// excluded, so the scan can skip the subtree entirely?
    ///
    /// <para>Without it a scan walks every directory of an excluded tree and
    /// rejects the files one at a time. Measured symptom: a scan sitting on the
    /// same file count for hours while it enumerated build/, debug/ and target/
    /// trees on a spinning disk at ~3.4 ms per directory, unable to contribute a
    /// single file.</para>
    ///
    /// <para>Conservative by construction, because a wrong answer here means a
    /// file is silently never backed up. It only trusts:</para>
    /// <list type="bullet">
    /// <item>glob patterns that cover a whole subtree (see
    /// <see cref="GlobMatcher.CreateDirectorySubtreeFilter"/>);</item>
    /// <item>zero-tier tier sets, and only those with NO
    /// <see cref="VersionTierSet.FileExemptPatterns"/> — an exempt pattern can
    /// re-include a file deep inside, so the subtree is not uniformly
    /// excluded;</item>
    /// <item>the app's own data directory, which is excluded wholesale;</item>
    /// <item>Windows' per-volume system folders (<see cref="VolumeMetadataPaths"/>),
    /// likewise.</item>
    /// </list>
    /// Anything it cannot prove, it declines to prune: the cost is a slower scan,
    /// never a missing file.
    /// </summary>
    public static Func<string, bool>? BuildDirectoryPruneFilter(BackupJob job)
        => BuildDirectoryPruneFilter(job.ExcludedExtensions, job.TierSets);

    /// <inheritdoc cref="BuildDirectoryPruneFilter(BackupJob)"/>
    public static Func<string, bool>? BuildDirectoryPruneFilter(
        IReadOnlyList<string> excludedExtensions,
        IReadOnlyList<VersionTierSet> tierSets)
    {
        var globPrune = GlobMatcher.CreateDirectorySubtreeFilter(excludedExtensions);

        var zeroTierPatterns = new List<string>();
        foreach (var ts in tierSets)
        {
            if (ts.Tiers.Count != 0)
                continue;
            if (ts.FilePatterns.Count == 0)
                continue;
            if (ts.FileExemptPatterns.Count > 0)
                continue;   // an exemption can re-include a file below; cannot prune
            if (string.Equals(ts.Name, "Default", StringComparison.OrdinalIgnoreCase))
                continue;
            zeroTierPatterns.AddRange(ts.FilePatterns);
        }

        var tierPrune = zeroTierPatterns.Count > 0
            ? GlobMatcher.CreateDirectorySubtreeFilter(zeroTierPatterns)
            : null;

        return directoryPath =>
        {
            if (CatalogLocation.IsInsideAppDataDirectory(directoryPath))
                return true;
            // Never walk the recycle bin or System Volume Information: every file
            // beneath is excluded anyway (BuildExclusionFilter), and one recycle bin
            // alone held 216,208 files.
            if (VolumeMetadataPaths.IsVolumeMetadata(directoryPath))
                return true;
            if (globPrune?.Invoke(directoryPath) ?? false)
                return true;
            if (tierPrune?.Invoke(directoryPath) ?? false)
                return true;
            return false;
        };
    }

    // -------------------------------------------------------------------
    // File I/O helpers
    // -------------------------------------------------------------------

    /// <summary>Async file copy with large buffer and optional per-file progress.</summary>
    /// <param name="onProgress">
    /// Called with bytes copied so far. Caller is responsible for throttling
    /// how often this translates to UI updates.
    /// </param>
    /// <param name="waitIfPaused">
    /// Awaited between chunks; returns once the backup is not paused. The same
    /// for the other copy helpers.
    /// </param>
    private static async Task CopyFileAsync(
        string sourcePath, string destPath, CancellationToken ct,
        Action<long>? onProgress = null,
        Func<Task>? waitIfPaused = null)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
            Directory.CreateDirectory(destDir);

        // Write to a sibling temp file first, then atomically move it into
        // place.  This guarantees the destination is only ever the complete
        // file or absent — never a truncated/0-byte partial.  Without this,
        // an interrupted copy (cancellation, I/O error, drive disconnect,
        // crash) would leave a corrupt "current" file that the next backup
        // run would promote to _prev as a bogus version.
        string tempPath = destPath + ".lbtmp";

        try
        {
            await using (var srcStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read,
                // ReadWrite share so a file another app holds open for writing
                // (e.g. a note still open in KeyNote NF) can still be backed up,
                // instead of failing with a sharing violation and being skipped.
                FileShare.ReadWrite, bufferSize: 81920, useAsync: true))
            await using (var dstStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                if (onProgress is null && waitIfPaused is null)
                {
                    await srcStream.CopyToAsync(dstStream, ct);
                }
                else
                {
                    // Chunked copy with progress callback and pause support.
                    var buffer = new byte[81920];
                    long totalCopied = 0;
                    int read;
                    while ((read = await srcStream.ReadAsync(buffer, ct)) > 0)
                    {
                        if (waitIfPaused is not null)
                            await waitIfPaused();
                        await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        totalCopied += read;
                        onProgress?.Invoke(totalCopied);
                    }
                }
            }

            // Atomic promotion of the fully-written temp file.
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch
        {
            // Clean up the partial temp file so it can't linger or be
            // mistaken for real backup content.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort — nothing more we can do here */ }
            throw;
        }
    }

    /// <summary>
    /// Async file copy that computes the content's SHA-256 in the SAME streaming
    /// pass, returning the lowercase-hex hash and the exact number of bytes
    /// copied. Used for files proven to be plain (non-deduplicated, non-duplicate)
    /// content so the source is read only once for both hashing and copying,
    /// instead of once to hash and again to copy. Same atomic temp-file + move
    /// semantics as <see cref="CopyFileAsync"/>.
    /// </summary>
    private static async Task<(string Hash, long Size)> CopyFileWithHashAsync(
        string sourcePath, string destPath, CancellationToken ct,
        Action<long>? onProgress = null,
        Func<Task>? waitIfPaused = null)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
            Directory.CreateDirectory(destDir);

        string tempPath = destPath + ".lbtmp";

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalCopied = 0;

            await using (var srcStream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read,
                // ReadWrite share so a file another app holds open for writing
                // (e.g. a note still open in KeyNote NF) can still be backed up,
                // instead of failing with a sharing violation and being skipped.
                FileShare.ReadWrite, bufferSize: 81920, useAsync: true))
            await using (var dstStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await srcStream.ReadAsync(buffer, ct)) > 0)
                {
                    if (waitIfPaused is not null)
                        await waitIfPaused();
                    await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    hasher.AppendData(buffer, 0, read);
                    totalCopied += read;
                    onProgress?.Invoke(totalCopied);
                }
            }

            // Atomic promotion of the fully-written temp file.
            File.Move(tempPath, destPath, overwrite: true);

            string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return (hash, totalCopied);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort — nothing more we can do here */ }
            throw;
        }
    }

    /// <summary>
    /// Write a plain copy from bytes already held in memory — used when the file
    /// was read into a buffer earlier (dedup analysis or a file-level dedup hash
    /// check) so it is not read from disk a second time to be copied. Same atomic
    /// temp-file + move semantics as <see cref="CopyFileAsync"/>. The content is
    /// written in chunks so per-file progress and pause still work.
    /// </summary>
    private static async Task WritePlainFromBufferAsync(
        byte[] content, string destPath, CancellationToken ct,
        Action<long>? onProgress = null,
        Func<Task>? waitIfPaused = null)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
            Directory.CreateDirectory(destDir);

        string tempPath = destPath + ".lbtmp";

        try
        {
            await using (var dstStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                const int chunk = 81920;
                int offset = 0;
                while (offset < content.Length)
                {
                    if (waitIfPaused is not null)
                        await waitIfPaused();
                    int len = Math.Min(chunk, content.Length - offset);
                    await dstStream.WriteAsync(content.AsMemory(offset, len), ct);
                    offset += len;
                    onProgress?.Invoke(offset);
                }
            }

            // Atomic promotion of the fully-written temp file.
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort — nothing more we can do here */ }
            throw;
        }
    }

    /// <summary>Lowercase-hex SHA-256 of an in-memory buffer.</summary>
    private static string ComputeHashOfBuffer(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    // -------------------------------------------------------------------
    // Progressive prefix hashing (intra-run collision pre-check)
    //
    // For large (non-buffered) files whose size collides with another file
    // in THIS run — but not with any existing stored plain content — a cheap
    // hash of the first PrefixHashBytes is enough to rule out a genuine
    // duplicate without reading the whole file up front. If the prefix does
    // NOT match any already-stored plain file of the same size, the file
    // cannot be a duplicate, so we defer the full hash into the copy pass
    // (single read). If the prefix DOES match, we fall back to the full
    // up-front hash to confirm (and, if confirmed, store a .fileref).
    // -------------------------------------------------------------------

    /// <summary>Number of leading bytes hashed for the cheap prefix pre-check.</summary>
    private const int PrefixHashBytes = 64 * 1024;

    /// <summary>
    /// Returns true when <paramref name="path"/> can be ruled out as an
    /// intra-run duplicate purely from its prefix hash — i.e. no already-stored
    /// plain file of the same size shares its leading bytes. When true, the
    /// caller can safely defer the full hash into the copy pass.
    /// </summary>
    private static async Task<bool> RuledOutByPrefixAsync(
        string path,
        long size,
        Dictionary<long, HashSet<string>> intraRunPlainPrefixes,
        CancellationToken ct)
    {
        // No same-size plain content stored yet this run — cannot be a dup.
        if (!intraRunPlainPrefixes.TryGetValue(size, out var prefixes) || prefixes.Count == 0)
            return true;

        string prefix = await ComputePrefixHashAsync(path, ct);
        // Ruled out iff no stored plain file of this size shares the prefix.
        return !prefixes.Contains(prefix);
    }

    /// <summary>Lowercase-hex SHA-256 of the first <see cref="PrefixHashBytes"/> bytes of a file.</summary>
    private static async Task<string> ComputePrefixHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            // ReadWrite share so a file another app holds open for writing
            // (e.g. a note still open in KeyNote NF) can still be read.
            FileShare.ReadWrite, bufferSize: 81920, useAsync: true);

        var buf = new byte[PrefixHashBytes];
        int total = 0, n;
        while (total < buf.Length &&
               (n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct)) > 0)
        {
            total += n;
        }

        return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, total))).ToLowerInvariant();
    }

    /// <summary>Lowercase-hex SHA-256 of the first <see cref="PrefixHashBytes"/> bytes of a buffer.</summary>
    private static string ComputePrefixHashOfBuffer(byte[] content)
    {
        int n = Math.Min(PrefixHashBytes, content.Length);
        return Convert.ToHexString(SHA256.HashData(content.AsSpan(0, n))).ToLowerInvariant();
    }

    /// <summary>
    /// TESTING ONLY — write a tiny placeholder file (hash + logical size) in
    /// place of a real plain copy. Used when <see cref="StubPlainContentForTesting"/>
    /// is on so dedup test runs don't fill the destination drive. The resulting
    /// file is NOT restorable.
    /// </summary>
    private static async Task WritePlainTestStubAsync(
        string destPath, string hash, long size, CancellationToken ct)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
            Directory.CreateDirectory(destDir);

        await File.WriteAllTextAsync(
            destPath,
            $"[LithicBackup TEST STUB \u2014 not real content, not restorable]\n" +
            $"sha256: {hash}\nsize: {size}\n",
            ct);
    }

    // -------------------------------------------------------------------
    // Block I/O
    // -------------------------------------------------------------------

    /// <summary>
    /// Write blocks that are new (not already in the store) to _blocks/{hash}.blk.
    /// </summary>
    private static async Task WriteNewBlocksAsync(
        string sourceFilePath,
        DeduplicationRecipe recipe,
        int blockSize,
        string blockStoreDir,
        CancellationToken ct)
    {
        await using var src = new FileStream(
            sourceFilePath, FileMode.Open, FileAccess.Read,
            // ReadWrite share so a file another app holds open for writing
            // (e.g. a note still open in KeyNote NF) can still be read.
            FileShare.ReadWrite, bufferSize: 81920, useAsync: true);

        var buffer = new byte[blockSize];

        foreach (var block in recipe.Blocks)
        {
            int bytesRead = await ReadFullBlockAsync(src, buffer, ct);
            if (bytesRead == 0) break;

            if (!block.IsExisting)
            {
                // New block — write it to the store.
                string blockPath = Path.Combine(blockStoreDir, block.Hash + ".blk");
                if (!File.Exists(blockPath))
                {
                    await using var dst = new FileStream(
                        blockPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 81920, useAsync: true);
                    await dst.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }
            }
        }
    }

    /// <summary>
    /// Write new blocks (those not already in the store) from bytes already held
    /// in memory — used when the file was read into a buffer by the dedup pre-pass
    /// so it is not read from disk again to extract its blocks.
    /// </summary>
    private static async Task WriteNewBlocksFromBufferAsync(
        byte[] content,
        DeduplicationRecipe recipe,
        int blockSize,
        string blockStoreDir,
        CancellationToken ct)
    {
        int offset = 0;
        foreach (var block in recipe.Blocks)
        {
            int len = Math.Min(blockSize, content.Length - offset);
            if (len <= 0) break;

            if (!block.IsExisting)
            {
                string blockPath = Path.Combine(blockStoreDir, block.Hash + ".blk");
                if (!File.Exists(blockPath))
                {
                    await using var dst = new FileStream(
                        blockPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 81920, useAsync: true);
                    await dst.WriteAsync(content.AsMemory(offset, len), ct);
                }
            }

            offset += len;
        }
    }

    private static async Task<int> ReadFullBlockAsync(
        Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    // -------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------

    /// <summary>Get the filename suffix for the storage format.</summary>
    private static string GetFormatSuffix(bool isDeduped, bool isFileRef)
    {
        if (isFileRef) return ".fileref";
        if (isDeduped) return ".dedup";
        return "";
    }

    /// <summary>Get drive letter prefix: "C:\Users\foo" -> "C"</summary>
    private static string GetDrivePrefix(string fullPath)
    {
        return fullPath.Length >= 2 && fullPath[1] == ':'
            ? fullPath[0].ToString()
            : "_";
    }

    /// <summary>Get relative path without drive: "C:\Users\foo\file.txt" -> "Users\foo\file.txt"</summary>
    private static string GetRelativePath(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? "";
        return fullPath[root.Length..];
    }

    /// <summary>
    /// Build disc-relative path for the current version:
    /// {drive}/relative[.dedup|.fileref].
    /// Stored in the catalog so RestoreService can locate the file.
    /// </summary>
    private static string GetCurrentDiscPath(string fullPath, bool isDeduped, bool isFileRef)
    {
        string path = Path.Combine(GetDrivePrefix(fullPath), GetRelativePath(fullPath));
        return path + GetFormatSuffix(isDeduped, isFileRef);
    }

    /// <summary>
    /// Build disc-relative path for a previous version:
    /// {drive}_prev/relative.v{version}[.dedup|.fileref].
    /// </summary>
    private static string GetPrevDiscPath(
        string fullPath, int version, bool isDeduped, bool isFileRef)
    {
        string drivePrefix = GetDrivePrefix(fullPath);
        string relative = GetRelativePath(fullPath);
        string path = Path.Combine(drivePrefix + "_prev", relative + $".v{version}");
        return path + GetFormatSuffix(isDeduped, isFileRef);
    }

    /// <summary>
    /// Build current absolute path: targetDir/{drive}/relative[.dedup|.fileref]
    /// </summary>
    private static string GetCurrentPath(
        string targetDir, string fullPath, bool isDeduped, bool isFileRef)
    {
        string path = Path.Combine(targetDir, GetDrivePrefix(fullPath), GetRelativePath(fullPath));
        return path + GetFormatSuffix(isDeduped, isFileRef);
    }

    /// <summary>
    /// Build prev absolute path:
    /// targetDir/{drive}_prev/relative.v{version}[.dedup|.fileref]
    /// </summary>
    private static string GetPrevPath(
        string targetDir, string fullPath, int version, bool isDeduped, bool isFileRef)
    {
        string drivePrefix = GetDrivePrefix(fullPath);
        string relative = GetRelativePath(fullPath);
        string path = Path.Combine(
            targetDir, drivePrefix + "_prev", relative + $".v{version}");
        return path + GetFormatSuffix(isDeduped, isFileRef);
    }

    /// <summary>
    /// Materialise a <c>.fileref</c> record into a real plain file using bytes
    /// from <paramref name="sourceBytesPath"/> — an existing plain copy of the
    /// same content that is about to be deleted by retention. Replaces the
    /// reference's on-disk <c>.fileref</c> manifest with the actual content and
    /// flips its catalog record to a plain copy at the new (suffix-less) path.
    /// Returns <c>false</c> (leaving everything untouched) on any failure, so the
    /// caller can keep the source bytes rather than orphan the reference.
    /// </summary>
    private async Task<bool> TryPromoteFileRefToPlainAsync(
        string targetDirectory, string sourceBytesPath, FileRecord fileRef, CancellationToken ct)
    {
        try
        {
            string refAbsPath = Path.Combine(targetDirectory, fileRef.DiscPath);
            string plainDiscPath = StripFileRefSuffix(fileRef.DiscPath);
            string plainAbsPath = Path.Combine(targetDirectory, plainDiscPath);

            string? dir = Path.GetDirectoryName(plainAbsPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            // Write the real bytes into the reference's location (suffix-less).
            // File.Copy preserves the source's attributes, so strip read-only
            // afterwards — otherwise read-only source content (git objects, etc.)
            // leaves a read-only file on the destination that later resists
            // retention/cleanup deletion.
            File.Copy(sourceBytesPath, plainAbsPath, overwrite: true);
            ClearReadOnly(plainAbsPath);

            // Remove the now-redundant .fileref manifest (it differs from the
            // plain path only by the ".fileref" suffix).
            if (!string.Equals(refAbsPath, plainAbsPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(refAbsPath))
            {
                ForceDeleteFile(refAbsPath);
            }

            // Flip the catalog record to a plain copy at its new path.
            fileRef.IsFileRef = false;
            fileRef.DiscPath = plainDiscPath;
            await _catalog.UpdateFileRecordAsync(fileRef, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string StripFileRefSuffix(string discPath)
        => discPath.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase)
            ? discPath[..^".fileref".Length]
            : discPath;

    /// <summary>
    /// Delete a file we own on the destination, first clearing its read-only
    /// attribute. Plain <see cref="File.Delete(string)"/> throws
    /// <see cref="UnauthorizedAccessException"/> on a read-only file, and a LOT
    /// of backed-up content carries that flag (git object/pack files are always
    /// read-only, as is anything copied from a read-only source). Without this,
    /// retention and manifest cleanup silently fail on read-only files and leave
    /// stale bytes on disk that the cleanup UI then re-reports forever.
    /// </summary>
    private static void ForceDeleteFile(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Exists)
        {
            if (fi.IsReadOnly)
                fi.IsReadOnly = false;
            fi.Delete();
        }
    }

    /// <summary>
    /// Clear the read-only attribute on a file we just wrote to the destination.
    /// <see cref="File.Copy(string,string,bool)"/> preserves the source's
    /// attributes, so copying read-only source content (e.g. git objects) leaves
    /// read-only files on the destination that later resist deletion. Every file
    /// under our control on the destination must stay writable so retention and
    /// cleanup can manage it.
    /// </summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.IsReadOnly)
                fi.IsReadOnly = false;
        }
        catch { /* best effort — a stuck attribute must not fail the backup */ }
    }

    /// <summary>
    /// Resolve a content hash to the absolute path of an active plain copy of
    /// that content somewhere in the backup tree, or <c>null</c> if none exists.
    /// This is how a <c>.fileref</c> (which stores no bytes) is turned back into
    /// real content for verification and restore.
    /// </summary>
    private async Task<string?> ResolvePlainContentPathAsync(
        int backupSetId, string hash, string targetDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        var candidates = await _catalog.GetActiveRecordsByHashAsync(backupSetId, hash, ct);
        var plain = candidates.FirstOrDefault(r => !r.IsFileRef && !r.IsDeduped);
        return plain is null ? null : Path.Combine(targetDirectory, plain.DiscPath);
    }

    /// <summary>
    /// Repoint the <see cref="FileRefManifest.ContentPath"/> hint of every active
    /// <c>.fileref</c> sharing <paramref name="hash"/> to
    /// <paramref name="newContentDiscPath"/> — the destination-relative location
    /// where the plain copy of that content now lives (after an eviction to
    /// <c>_prev</c>, a retention promotion, or a deletion that leaves a different
    /// plain copy surviving). This rewrites only the on-disk JSON manifests; the
    /// catalog is unchanged because Lithic resolves references by
    /// <see cref="FileRefManifest.Hash"/>, not by ContentPath. The hint exists
    /// solely for catalog-free restore, so all I/O here is best-effort: a failure
    /// to rewrite a manifest leaves a stale hint that catalog-free restore
    /// recovers from via its hash-verify + scan fallback.
    /// </summary>
    private async Task UpdateFileRefContentPathsAsync(
        int backupSetId, string hash, string newContentDiscPath,
        string targetDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hash))
            return;

        IReadOnlyList<FileRecord> refs;
        try
        {
            refs = await _catalog.GetActiveRecordsByHashAsync(backupSetId, hash, ct);
        }
        catch
        {
            return;
        }

        foreach (var r in refs)
        {
            if (!r.IsFileRef)
                continue;

            string refAbs = Path.Combine(targetDirectory, r.DiscPath);
            if (!File.Exists(refAbs))
                continue;

            try
            {
                string json = await File.ReadAllTextAsync(refAbs, ct);
                var m = JsonSerializer.Deserialize<FileRefManifest>(json, _jsonOptions);
                if (m is null)
                    continue;

                // Don't repoint a reference at itself.
                if (string.Equals(m.ContentPath, newContentDiscPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                m.ContentPath = newContentDiscPath;
                await File.WriteAllTextAsync(
                    refAbs, JsonSerializer.Serialize(m, _jsonOptions), ct);
            }
            catch
            {
                // Best-effort hint upkeep — ignore and move on.
            }
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
        => (await ComputeFileHashAndSizeAsync(filePath, ct)).Hash;

    /// <summary>
    /// Read an entire file into memory using a ReadWrite share mode. Unlike
    /// <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/> (which
    /// opens with FileShare.Read and therefore fails with a sharing violation
    /// on a file another app holds open for writing), this can back up a file
    /// that is still open in its editor — e.g. a note open in KeyNote NF.
    /// </summary>
    private static async Task<byte[]> ReadAllBytesSharedAsync(
        string filePath, CancellationToken ct, Action<long>? onProgress = null)
    {
        await using var file = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 81920, useAsync: true);
        Stream stream = onProgress is null ? file : new ProgressReadStream(file, onProgress);

        using var ms = new MemoryStream(
            file.Length > 0 && file.Length <= int.MaxValue ? (int)file.Length : 0);
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>
    /// A read-only pass-through that reports the bytes read so far after each
    /// read, so a whole-file read (to hash it) can drive a progress bar.
    /// </summary>
    private sealed class ProgressReadStream(Stream inner, Action<long> onProgress) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Advance(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Advance(inner.Read(buffer));

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Advance(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                                  .ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Advance(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Advance(int n)
        {
            if (n > 0)
            {
                _read += n;
                onProgress(_read);
            }
            return n;
        }
    }

    /// <summary>
    /// Hash a file and report the number of bytes actually read. The returned
    /// size is authoritative for the hashed content: it is captured from the
    /// same read that produced the hash, so it cannot drift from the stored
    /// bytes the way a separately-scanned size can when the file is edited
    /// between the directory scan and this read.
    /// </summary>
    private static async Task<(string Hash, long Size)> ComputeFileHashAndSizeAsync(
        string filePath, CancellationToken ct, Action<long>? onProgress = null)
    {
        await using var file = new FileStream(
            // ReadWrite share so a file another app holds open for writing
            // (e.g. a note still open in KeyNote NF) can still be hashed.
            // Safe for destination callers too: they never write concurrently.
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 81920, useAsync: true);
        Stream stream = onProgress is null ? file : new ProgressReadStream(file, onProgress);

        var hash = await SHA256.HashDataAsync(stream, ct);
        // Position after HashDataAsync == total bytes consumed == content length.
        return (Convert.ToHexString(hash).ToLowerInvariant(), file.Position);
    }

    /// <summary>
    /// Produce a more descriptive error message for file I/O failures by
    /// decoding the Windows error code and, for sharing violations, trying
    /// to identify the locking process via the Restart Manager API.
    /// </summary>
    private static string DescribeFileError(Exception ex, string filePath)
    {
        // Decode common Win32 error codes embedded in HResult.
        if (ex is IOException)
        {
            int win32 = ex.HResult & 0xFFFF;
            string? reason = win32 switch
            {
                0x0020 => FormatSharingViolation(filePath),  // ERROR_SHARING_VIOLATION
                0x0021 => "File region is locked",           // ERROR_LOCK_VIOLATION
                0x0050 => "File already exists at target",   // ERROR_FILE_EXISTS
                0x0070 => "Disk is full",                    // ERROR_DISK_FULL
                0x00CE => "Path is too long",                // ERROR_FILENAME_EXCED_RANGE
                _ => null,
            };

            if (reason is not null)
                return reason;
        }

        return ex switch
        {
            UnauthorizedAccessException => $"Permission denied: {ex.Message}",
            PathTooLongException => $"Path is too long ({filePath.Length} chars)",
            FileNotFoundException => "File no longer exists (deleted after scan?)",
            DirectoryNotFoundException => "Directory no longer exists",
            _ => ex.Message,
        };
    }

    /// <summary>
    /// For ERROR_SHARING_VIOLATION, use the Restart Manager API to find
    /// which process holds the file open.
    /// </summary>
    private static string FormatSharingViolation(string filePath)
    {
        try
        {
            var lockers = GetLockingProcesses(filePath);
            if (lockers.Count > 0)
            {
                var names = string.Join(", ", lockers.Select(
                    p => $"{p.ProcessName} (PID {p.Id})"));
                return $"File is locked by: {names}";
            }
        }
        catch { /* Restart Manager not available or failed */ }

        return "File is locked by another process";
    }

    // --- Restart Manager P/Invoke for locking-process detection ---

    private static List<System.Diagnostics.Process> GetLockingProcesses(string path)
    {
        int res = RmStartSession(out uint sessionHandle, 0, Guid.NewGuid().ToString());
        if (res != 0) return [];

        try
        {
            string[] resources = [path];
            res = RmRegisterResources(sessionHandle, (uint)resources.Length, resources,
                0, null!, 0, null!);
            if (res != 0) return [];

            uint needed = 0, count = 0;
            var reason = 0u;
            res = RmGetList(sessionHandle, out needed, ref count, null!, ref reason);
            if (res != 234 /* ERROR_MORE_DATA */ || needed == 0) return [];

            var processInfo = new RM_PROCESS_INFO[needed];
            count = needed;
            res = RmGetList(sessionHandle, out _, ref count, processInfo, ref reason);
            if (res != 0) return [];

            var result = new List<System.Diagnostics.Process>();
            foreach (var pi in processInfo.Take((int)count))
            {
                try { result.Add(System.Diagnostics.Process.GetProcessById(pi.Process.dwProcessId)); }
                catch { /* process exited */ }
            }
            return result;
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle,
        uint nFiles, string[] rgsFileNames,
        uint nApplications, RM_UNIQUE_PROCESS[] rgApplications,
        uint nServices, string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint pSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    // ------------------------------------------------------------------
    // Seed from existing mirror directory
    // ------------------------------------------------------------------

    /// <summary>
    /// Import files from an existing mirror-format backup directory (e.g. a
    /// backup4all mirror or a plain robocopy mirror) into the LithicBackup
    /// catalog. Each file under the mirror's drive-letter subdirectories
    /// (e.g. <c>C/Users/foo/file.txt</c>) is recorded as "already backed up"
    /// so that future incremental backups only copy new or changed files.
    /// </summary>
    /// <returns>Number of files imported.</returns>
    public async Task<int> SeedFromExistingDirectoryAsync(
        int backupSetId,
        string targetDir,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        bool skipHashing = false)
    {
        // Create a disc record for this import.
        var discRecord = await _catalog.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = backupSetId,
            Label = targetDir,
            SequenceNumber = 1,
            MediaType = MediaType.Directory,
            FilesystemType = FilesystemType.UDF,
            Capacity = 0,
            BytesUsed = 0,
            Status = BurnSessionStatus.Completed,
            CreatedUtc = DateTime.UtcNow,
        }, ct);

        int importedCount = 0;
        int skippedExisting = 0;
        long totalBytes = 0;
        var progressSw = System.Diagnostics.Stopwatch.StartNew();
        long lastProgressMs = 0;

        // Idempotency guard: load the set of SourcePaths already present in
        // the catalog so re-running the seed on the same destination doesn't
        // create duplicate FileRecords.  GetLatestVersionInfoAsync returns
        // one entry per unique source path (non-deleted), which is exactly
        // the set we need to skip.
        var existing = await _catalog.GetLatestVersionInfoAsync(backupSetId, ct);
        var existingPaths = new HashSet<string>(
            existing.Keys, StringComparer.OrdinalIgnoreCase);

        // Enumerate all single-letter (or short) subdirectories that look like
        // drive prefixes: C, D, E, etc.
        var rootDir = new DirectoryInfo(targetDir);
        if (!rootDir.Exists)
            throw new DirectoryNotFoundException($"Target directory not found: {targetDir}");

        foreach (var driveDir in rootDir.EnumerateDirectories())
        {
            // Accept single-letter or two-letter names as potential drive prefixes.
            // Skip _prev, _blocks, _filestore directories.
            string dirName = driveDir.Name;
            if (dirName.StartsWith('_') || dirName.Length > 2)
                continue;

            // Reconstruct the drive root: "C" → "C:\"
            string driveRoot = dirName + @":\";

            foreach (var file in driveDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                // Classify the stored file by its LithicBackup suffix:
                //   .fileref — a file-level duplicate (no bytes of its own; its
                //              content lives as a plain copy elsewhere). Import
                //              it as an IsFileRef record so dedup'd source files
                //              are represented in the catalog.
                //   .dedup   — a block-deduplicated file (manifest + _blocks).
                //              Import as an IsDeduped record.
                //   .lbtmp   — a partial copy left behind by an interrupted
                //              backup (CopyFileAsync writes there before the
                //              atomic rename); ignore it entirely.
                // Anything else is a plain named copy holding its own bytes.
                bool isFileRefFile = file.Name.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase);
                bool isDedupFile = file.Name.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase);
                if (file.Name.EndsWith(".lbtmp", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip files under any "_prev" segment — these are
                // versioned-history copies kept by other backup tools (e.g.
                // backup4all stores superseded files in per-folder _prev
                // subdirectories) and importing them would create catalog
                // rows whose SourcePath isn't a real source location.
                string relativeToDrive = System.IO.Path.GetRelativePath(driveDir.FullName, file.FullName);
                if (relativeToDrive.Split(['\\', '/'])
                        .Any(seg => seg.Equals("_prev", StringComparison.OrdinalIgnoreCase)))
                    continue;

                // For stored-content manifests (.fileref / .dedup) the real
                // file metadata — original size, content hash, and (for new-
                // format filerefs) the original source path — comes from the
                // manifest, NOT from the tiny JSON file on disk. Using the
                // manifest's OriginalSize is essential: seeding the manifest
                // file's own length would make every duplicate look "changed"
                // to the next incremental backup (size mismatch) and force a
                // needless re-copy.
                long recordSize = file.Length;
                string? manifestHash = null;
                string? manifestSourcePath = null;
                if (isFileRefFile || isDedupFile)
                {
                    try
                    {
                        string manifestJson = await File.ReadAllTextAsync(file.FullName, ct);
                        if (isFileRefFile)
                        {
                            var m = JsonSerializer.Deserialize<FileRefManifest>(manifestJson, _jsonOptions);
                            if (m is null) continue;
                            recordSize = m.OriginalSize;
                            manifestHash = m.Hash ?? "";
                            manifestSourcePath = string.IsNullOrEmpty(m.SourcePath) ? null : m.SourcePath;
                        }
                        else
                        {
                            var m = JsonSerializer.Deserialize<DedupManifest>(manifestJson, _jsonOptions);
                            if (m is null) continue;
                            recordSize = m.OriginalSize;
                            manifestHash = m.OriginalHash ?? "";
                        }
                    }
                    catch
                    {
                        // Unreadable / malformed manifest — skip it.
                        continue;
                    }
                }

                // Reconstruct the original source path. New-format filerefs
                // carry it explicitly; otherwise rebuild it from the drive
                // prefix plus the relative path with any storage suffix removed.
                string logicalRelative = relativeToDrive;
                if (isFileRefFile)
                    logicalRelative = relativeToDrive[..^".fileref".Length];
                else if (isDedupFile)
                    logicalRelative = relativeToDrive[..^".dedup".Length];

                string fullSourcePath = manifestSourcePath
                    ?? System.IO.Path.Combine(driveRoot, logicalRelative);
                // DiscPath is the path of the stored file itself, INCLUDING the
                // .fileref / .dedup suffix, so restore/verify can locate it.
                string discPath = System.IO.Path.Combine(dirName, relativeToDrive);

                // Idempotent skip: this source path is already in the catalog
                // (from a previous seed of the same destination).  Re-importing
                // would create a duplicate FileRecord that retention logic
                // would later flag as an excess version.
                if (existingPaths.Contains(fullSourcePath))
                {
                    skippedExisting++;
                    continue;
                }

                string hash;
                if (manifestHash is not null)
                {
                    // Stored-content manifest: the content hash is recorded in
                    // the manifest — no need to (and we must not) hash the JSON.
                    hash = manifestHash;
                }
                else if (skipHashing)
                {
                    // Use empty hash — incremental detection relies on
                    // size + last-write-time, not content hash.
                    hash = "";
                }
                else
                {
                    // Report before hashing so the UI shows which file is
                    // being processed — large files take a long time to hash.
                    long nowPre = progressSw.ElapsedMilliseconds;
                    if (nowPre - lastProgressMs >= ProgressCadence.IntervalMs)
                    {
                        lastProgressMs = nowPre;
                        progress?.Report(new ScanProgress
                        {
                            CurrentDirectory = fullSourcePath,
                            FilesFound = importedCount,
                            TotalBytes = totalBytes,
                            FilesSkipped = skippedExisting,
                        });
                    }

                    try
                    {
                        hash = await ComputeFileHashAsync(file.FullName, ct);
                    }
                    catch
                    {
                        // If we can't read the file (locked, etc.), skip it.
                        continue;
                    }
                }

                await _catalog.CreateFileRecordAsync(new FileRecord
                {
                    DiscId = discRecord.Id,
                    SourcePath = fullSourcePath,
                    DiscPath = discPath,
                    SizeBytes = recordSize,
                    Hash = hash,
                    IsZipped = false,
                    IsSplit = false,
                    IsDeduped = isDedupFile,
                    IsFileRef = isFileRefFile,
                    Version = 1,
                    SourceLastWriteUtc = file.LastWriteTimeUtc,
                    BackedUpUtc = DateTime.UtcNow,
                }, ct);

                importedCount++;
                totalBytes += recordSize;

                // Throttle progress reports to avoid flooding the UI thread.
                long nowPost = progressSw.ElapsedMilliseconds;
                if (nowPost - lastProgressMs >= ProgressCadence.IntervalMs)
                {
                    lastProgressMs = nowPost;
                    progress?.Report(new ScanProgress
                    {
                        CurrentDirectory = fullSourcePath,
                        FilesFound = importedCount,
                        TotalBytes = totalBytes,
                        FilesSkipped = skippedExisting,
                    });
                }
            }
        }

        // Always report final state so the UI shows the true total.
        progress?.Report(new ScanProgress
        {
            CurrentDirectory = "",
            FilesFound = importedCount,
            TotalBytes = totalBytes,
            FilesSkipped = skippedExisting,
        });

        // Update disc used bytes.
        discRecord.BytesUsed = totalBytes;
        await _catalog.UpdateDiscAsync(discRecord, ct);

        return importedCount;
    }
}
