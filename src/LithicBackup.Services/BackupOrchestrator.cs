using System.IO.Compression;
using System.Security.Cryptography;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Top-level backup orchestrator: plans, executes, and consolidates.
/// </summary>
public class BackupOrchestrator : IBackupOrchestrator
{
    private readonly ICatalogRepository _catalog;
    private readonly IDiscBurner _burner;
    private readonly IFileScanner _scanner;
    private readonly IBinPacker _packer;
    private readonly IZipHandler _zipHandler;
    private readonly IDiscSessionStrategy _sessionStrategy;
    private readonly IFileSystemMonitor? _fileSystemMonitor;

    public BackupOrchestrator(
        ICatalogRepository catalog,
        IDiscBurner burner,
        IFileScanner scanner,
        IBinPacker packer,
        IZipHandler zipHandler,
        IDiscSessionStrategy sessionStrategy,
        IFileSystemMonitor? fileSystemMonitor = null)
    {
        _catalog = catalog;
        _burner = burner;
        _scanner = scanner;
        _packer = packer;
        _zipHandler = zipHandler;
        _sessionStrategy = sessionStrategy;
        _fileSystemMonitor = fileSystemMonitor;
    }

    // -------------------------------------------------------------------
    // PlanAsync
    // -------------------------------------------------------------------

    public async Task<BackupPlan> PlanAsync(BackupJob job, CancellationToken ct = default,
        IProgress<ScanProgress>? scanProgress = null)
    {
        // 1. Scan source directories (global + tier-set exclusions).
        var isExcluded = DirectoryBackupService.BuildExclusionFilter(job);
        var scanned = await _scanner.ScanAsync(job.Sources, progress: scanProgress, ct, isExcluded);

        // 2. Compute diff against existing catalog (if this is an existing set).
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

        // 3. Determine disc capacity.
        var recorderIds = _burner.GetRecorderIds();
        long discCapacity;
        if (job.CapacityOverrideBytes.HasValue)
        {
            discCapacity = job.CapacityOverrideBytes.Value;
        }
        else if (job.BackupSetId.HasValue)
        {
            var set = await _catalog.GetBackupSetAsync(job.BackupSetId.Value, ct);
            discCapacity = set?.CapacityOverrideBytes
                ?? (recorderIds.Count > 0
                    ? (await _burner.GetMediaInfoAsync(recorderIds[0], ct)).FreeSpaceBytes
                    : 25L * 1024 * 1024 * 1024);
        }
        else
        {
            discCapacity = recorderIds.Count > 0
                ? (await _burner.GetMediaInfoAsync(recorderIds[0], ct)).FreeSpaceBytes
                : 25L * 1024 * 1024 * 1024;
        }

        // 4. Bin-pack files to discs.
        var filesToBackup = diff.NewFiles.Concat(diff.ChangedFiles).ToList();
        var allocations = _packer.Pack(filesToBackup, discCapacity);

        return new BackupPlan
        {
            Job = job,
            Diff = diff,
            DiscAllocations = allocations,
            TotalDiscsRequired = allocations.Count,
            TotalBytes = filesToBackup.Sum(f => f.SizeBytes),
        };
    }

    // -------------------------------------------------------------------
    // ExecuteAsync
    // -------------------------------------------------------------------

    public Task<BackupResult> ExecuteAsync(
        BackupPlan plan,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        return ExecuteAsync(plan, progress, onFailure: null, ct);
    }

    public async Task<BackupResult> ExecuteAsync(
        BackupPlan plan,
        IProgress<BackupProgress>? progress = null,
        FailureCallback? onFailure = null,
        CancellationToken ct = default)
    {
        var recorderIds = _burner.GetRecorderIds();
        if (recorderIds.Count == 0)
            return Fail("No disc recorder detected.");

        string recorderId = recorderIds[0];

        // Ensure the backup set exists in the catalog.
        int backupSetId;
        if (plan.Job.BackupSetId.HasValue)
        {
            backupSetId = plan.Job.BackupSetId.Value;
        }
        else
        {
            var newSet = await _catalog.CreateBackupSetAsync(new BackupSet
            {
                Name = $"Backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                SourceRoots = plan.Job.Sources.Select(s => s.Path).ToList(),
                CreatedUtc = DateTime.UtcNow,
            }, ct);
            backupSetId = newSet.Id;
        }

        var failedFiles = new List<FailedFile>();
        int discsWritten = 0;
        long totalBytesWritten = 0;

        // Continue disc numbering from any discs already recorded for this set so
        // an incremental (multisession) run doesn't reuse "Disc-001" and collide
        // with an earlier run's disc. Each run's discs get fresh, unique sequence
        // numbers/labels within the set.
        var existingDiscsForSet = await _catalog.GetDiscsForBackupSetAsync(backupSetId, ct);
        int sequenceBase = existingDiscsForSet.Count > 0
            ? existingDiscsForSet.Max(d => d.SequenceNumber)
            : 0;

        // Build a lookup of max existing version per source path so we can
        // increment version numbers for changed files.  Uses a lightweight
        // SQL aggregate query instead of loading all FileRecord objects.
        var versionInfo = plan.Job.BackupSetId.HasValue
            ? await _catalog.GetLatestVersionInfoAsync(backupSetId, ct)
            : new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);

        // Tracks a "skip all" decision: SkipAllForDisc resets per-disc,
        // SkipAllPermanently persists for the entire run.
        BurnFailureAction? permanentSkip = null;

        // Error categories the user chose to "skip all of this type" for.
        // Persists for the entire run (across discs), like permanentSkip.
        var skippedCategories = new HashSet<BackupErrorCategory>();

        var filesystemType = plan.Job.FilesystemType;

        // Files that were found to have changed during staging get re-queued
        // for the next disc.
        var reQueuedFiles = new List<ScannedFile>();

        // Whole files bumped off a disc because a split (or a previous file) had
        // already filled it. They are placed on the next disc the loop opens.
        var overflowFiles = new List<ScannedFile>();

        // Remainder of a file being split across discs: non-null between the disc
        // that started the split and the disc that finishes it.
        SplitCarry? carry = null;

        // Spill snapshots created for split files; deleted after the whole run.
        var spillDirs = new List<string>();

        // Capacity of the most recently opened disc, reused for any discs the loop
        // must open beyond the planned allocations (to finish a spanning file or
        // place overflow). The bin-packer guarantees TotalBytes+FreeBytes == the
        // disc capacity for every allocation.
        long lastCapacity = plan.DiscAllocations.Count > 0
            ? plan.DiscAllocations[0].TotalBytes + plan.DiscAllocations[0].FreeBytes
            : 25L * 1024 * 1024 * 1024;

        // Live burn coordinator: real-time FSW-based change detection that
        // catches modifications DURING file copy (complements the inline
        // metadata check which only catches changes BEFORE copy).
        LiveBurnCoordinator? coordinator = null;
        if (_fileSystemMonitor is not null)
        {
            var sourceDirs = plan.Job.Sources
                .Select(s => s.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .ToList();

            if (sourceDirs.Count > 0)
            {
                coordinator = new LiveBurnCoordinator(_fileSystemMonitor);
                coordinator.Start(sourceDirs);
            }
        }

        try
        {

        // The loop is driven by the planned allocations, but continues opening
        // extra discs while a split file still has bytes to write (carry) or whole
        // files were bumped off earlier discs (overflowFiles), so a large file can
        // genuinely span multiple physical discs.
        int discIndex = 0;
        while (discIndex < plan.DiscAllocations.Count || carry is not null || overflowFiles.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            DiscAllocation? allocation = discIndex < plan.DiscAllocations.Count
                ? plan.DiscAllocations[discIndex]
                : null;
            long discCapacityForDisc = allocation is not null
                ? allocation.TotalBytes + allocation.FreeBytes
                : lastCapacity;
            lastCapacity = discCapacityForDisc;
            int discSequence = sequenceBase + discIndex + 1;

            // Per-disc "skip all" decision resets at each new disc.
            BurnFailureAction? discSkip = null;

            // --- Disc session strategy: decide write/append/erase ---
            var sessionDecision = await _sessionStrategy.DecideAsync(recorderId, backupSetId, ct);
            if (sessionDecision.Action == SessionAction.EraseAndRewrite)
            {
                await _burner.EraseAsync(recorderId, fullErase: false, ct);
            }

            progress?.Report(new BackupProgress
            {
                CurrentDisc = discSequence,
                TotalDiscs = Math.Max(plan.TotalDiscsRequired, discSequence),
                CurrentFile = "Staging files...",
                BytesWrittenTotal = totalBytesWritten,
                BytesTotalAll = plan.TotalBytes,
                OverallPercentage = plan.TotalBytes > 0
                    ? (double)totalBytesWritten / plan.TotalBytes * 100
                    : 0,
            });

            // Stage files to a temp directory.
            string stagingDir = Path.Combine(Path.GetTempPath(), "LithicBackup", $"disc-{discSequence}");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            // Read locks held on in-place (uncopied) source files. Kept open from
            // size-validation through the burn so the files can't change, then
            // released in the finally below. Empty in TemporaryCopy mode.
            var heldLocks = new List<FileStream>();

            try
            {
                // Copy files to staging, recording which ones succeed.
                var stagedFiles = new List<StagedFileInfo>();
                long discCapacity = discCapacityForDisc;

                // --- Continue any split carried from the previous disc ---
                // A carried split is placed first, filling this disc before any
                // other file, until either the file is fully written or the disc
                // is full (in which case the carry rolls over to the next disc).
                carry = await PlaceSplitChunksAsync(
                    carry, stagedFiles, stagingDir, discCapacity, ct);

                // Build a pending queue from remaining allocations for
                // LiveBurnCoordinator to draw replacements from.
                var pendingQueue = new Queue<ScannedFile>();

                // Include any files re-queued from a previous disc (changed
                // during staging).
                foreach (var rq in reQueuedFiles)
                    pendingQueue.Enqueue(rq);
                reQueuedFiles.Clear();

                for (int futureDisc = discIndex + 1; futureDisc < plan.DiscAllocations.Count; futureDisc++)
                {
                    foreach (var f in plan.DiscAllocations[futureDisc].Files)
                        pendingQueue.Enqueue(f);
                }

                // Whole files bumped off earlier discs go first, then this disc's
                // planned files.
                var filesToProcess = new List<ScannedFile>(overflowFiles);
                overflowFiles.Clear();
                if (allocation is not null)
                    filesToProcess.AddRange(allocation.Files);

                // Index-based loop (not foreach): TryFillGapFromPending appends a
                // replacement file to filesToProcess when the current one is skipped
                // or re-queued, and those appended files must themselves be processed.
                // A foreach would throw because the list is modified mid-iteration.
                for (int fileIndex = 0; fileIndex < filesToProcess.Count; fileIndex++)
                {
                    var file = filesToProcess[fileIndex];
                    ct.ThrowIfCancellationRequested();

                    // --- Live change detection ---
                    // Validate the file hasn't changed since scanning.
                    try
                    {
                        var currentInfo = new FileInfo(file.FullPath);
                        if (!currentInfo.Exists)
                        {
                            failedFiles.Add(new FailedFile
                            {
                                Path = file.FullPath,
                                Error = "File no longer exists.",
                                ActionTaken = BurnFailureAction.Skip,
                            });
                            // Try to fill gap with a pending file.
                            TryFillGapFromPending(filesToProcess, pendingQueue,
                                discCapacity - stagedFiles.Sum(sf => sf.StagedSizeBytes));
                            continue;
                        }

                        if (currentInfo.Length != file.SizeBytes ||
                            currentInfo.LastWriteTimeUtc != file.LastWriteUtc)
                        {
                            // File changed — re-enqueue with updated metadata.
                            pendingQueue.Enqueue(new ScannedFile
                            {
                                FullPath = file.FullPath,
                                SizeBytes = currentInfo.Length,
                                LastWriteUtc = currentInfo.LastWriteTimeUtc,
                            });
                            // Try to fill the gap with a pending file that fits.
                            TryFillGapFromPending(filesToProcess, pendingQueue,
                                discCapacity - stagedFiles.Sum(sf => sf.StagedSizeBytes));
                            continue;
                        }
                    }
                    catch (IOException)
                    {
                        // Can't check file — skip and try to fill gap.
                        failedFiles.Add(new FailedFile
                        {
                            Path = file.FullPath,
                            Error = "File is inaccessible during staging.",
                            ActionTaken = BurnFailureAction.Skip,
                        });
                        continue;
                    }

                    // Register with live coordinator so FSW catches any writes
                    // that happen DURING the copy below.
                    coordinator?.RegisterStagedFile(file.FullPath);

                    // NOTE: Block-level deduplication is intentionally not applied
                    // to optical/disc backups. Block dedup relies on a persistent
                    // content-addressed _blocks store on the destination that later
                    // runs deduplicate against; optical media is write-once with no
                    // such shared store, so there is nothing to dedup against. (The
                    // previous code here computed a recipe and discarded it apart
                    // from a progress message, storing no blocks — dead work.)
                    // Block dedup is a directory-backup feature; see
                    // DirectoryBackupService.

                    // --- File splitting / spanning ---
                    // Decide whether this file fits in the space left on the disc.
                    // If not, either split it (writing what fits here and carrying
                    // the rest to the next disc) or, when splitting isn't allowed,
                    // bump the whole file to the next disc.
                    long currentUsed = stagedFiles.Sum(sf => sf.StagedSizeBytes);
                    long remainingSpace = discCapacity - currentUsed;

                    if (remainingSpace <= 0)
                    {
                        // Disc already full (e.g. a carried split filled it) —
                        // place this whole file on the next disc.
                        overflowFiles.Add(file);
                        continue;
                    }

                    bool fileExceedsDisc = file.SizeBytes > discCapacity;
                    bool fileExceedsRemaining = file.SizeBytes > remainingSpace;

                    if (fileExceedsRemaining)
                    {
                        bool canSplit = plan.Job.AllowFileSplitting || fileExceedsDisc;
                        if (!canSplit)
                        {
                            // Won't fit and can't split: try later files that do fit
                            // this disc, and place this one on the next disc.
                            overflowFiles.Add(file);
                            continue;
                        }

                        try
                        {
                            // Snapshot the file so every chunk (even those written to
                            // later discs) is byte-consistent, then write as many
                            // chunks as fit here and carry the remainder forward.
                            var newCarry = await BeginSplitAsync(file, spillDirs, ct);
                            carry = await PlaceSplitChunksAsync(
                                newCarry, stagedFiles, stagingDir, discCapacity, ct);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            failedFiles.Add(new FailedFile
                            {
                                Path = file.FullPath,
                                Error = $"Split failed: {ex.Message}",
                                ActionTaken = BurnFailureAction.Skip,
                            });
                            continue;
                        }
                    }

                    // --- Zip handling ---
                    // ZipMode.All -> zip everything.
                    // ZipMode.IncompatibleOnly -> zip only if path is incompatible.
                    // ZipMode.None -> never zip proactively.
                    bool shouldZip = false;
                    if (plan.Job.ZipMode == ZipMode.All)
                    {
                        shouldZip = true;
                    }
                    else if (plan.Job.ZipMode == ZipMode.IncompatibleOnly)
                    {
                        shouldZip = !_zipHandler.IsPathCompatible(file.FullPath, filesystemType);
                    }

                    // Resolve this file's version now (from the pre-run max) so its
                    // disc path can be made version-unique BEFORE the burn writes it.
                    // A file re-backed-up after changing gets version > 1; on a
                    // multisession append that lands on the SAME physical disc as an
                    // earlier version, a shared disc path would (a) collide in IMAPI's
                    // AddFile after ImportFileSystem imports the earlier session, and
                    // (b) let the newer session shadow the older file so the earlier
                    // version could no longer be read back. Encoding the version into
                    // the disc path (see VersionedDiscPath) keeps every version at a
                    // distinct path, so both problems disappear. versionInfo still
                    // holds the pre-run max here (it's mutated only at record time),
                    // so this matches the version assigned when the record is written.
                    int fileVersion = versionInfo.TryGetValue(file.FullPath, out var fvi)
                        ? fvi.MaxVersion + 1
                        : 1;

                    bool retrying = true;

                    while (retrying)
                    {
                        retrying = false;
                        try
                        {
                            if (shouldZip)
                            {
                                // Zip the file into staging directory.
                                string zipPath = await _zipHandler.ZipFileAsync(
                                    file.FullPath, stagingDir, ct);
                                string zipRelative = Path.GetFileName(zipPath);

                                stagedFiles.Add(new StagedFileInfo
                                {
                                    Source = file,
                                    StagedPath = zipRelative,
                                    IsZipped = true,
                                    IsSplit = false,
                                    StagedSizeBytes = new FileInfo(zipPath).Length,
                                    Version = fileVersion,
                                });
                            }
                            else if (plan.Job.StagingMode == DiscStagingMode.InPlace)
                            {
                                // Burn-in-place: don't copy the file to temp. Instead
                                // take a read lock on the ORIGINAL and hold it through
                                // the burn, then point the burn item at the source.
                                // FileShare.Read blocks writers (so the file can't grow,
                                // change, or be deleted before it's burned) while still
                                // letting the burner read it concurrently. Re-check the
                                // size under the lock for the same growth-safety reason
                                // as the copy path: capacity accounting must match the
                                // bytes that actually land on the disc.
                                string relativePath = GetRelativeStagingPath(file.FullPath);
                                var lockStream = new FileStream(
                                    file.FullPath, FileMode.Open, FileAccess.Read,
                                    FileShare.Read, bufferSize: 4096, useAsync: false);
                                long lockedSize = lockStream.Length;
                                if (lockedSize != file.SizeBytes || lockedSize > remainingSpace)
                                {
                                    lockStream.Dispose();
                                    pendingQueue.Enqueue(new ScannedFile
                                    {
                                        FullPath = file.FullPath,
                                        SizeBytes = lockedSize,
                                        LastWriteUtc = File.GetLastWriteTimeUtc(file.FullPath),
                                    });
                                    TryFillGapFromPending(
                                        filesToProcess, pendingQueue, remainingSpace);
                                    break;
                                }

                                heldLocks.Add(lockStream);
                                stagedFiles.Add(new StagedFileInfo
                                {
                                    Source = file,
                                    StagedPath = relativePath,
                                    IsZipped = false,
                                    IsSplit = false,
                                    StagedSizeBytes = lockedSize,
                                    HeldLock = lockStream,
                                    Version = fileVersion,
                                });
                            }
                            else
                            {
                                // Preserve relative directory structure within staging.
                                string relativePath = GetRelativeStagingPath(file.FullPath);
                                string destPath = Path.Combine(stagingDir, relativePath);
                                string? destDir = Path.GetDirectoryName(destPath);
                                if (destDir is not null)
                                    Directory.CreateDirectory(destDir);

                                // Open with FileShare.Read FIRST to take a read lock
                                // that blocks writers, THEN re-check the size while
                                // holding it. This closes the window between the
                                // pre-copy metadata check above and the copy itself:
                                // once the lock is held the file can no longer grow, so
                                // the bytes we stage — and the capacity accounting below
                                // — are guaranteed to match what actually lands on the
                                // disc. Without this, a file that grew after the pre-copy
                                // check would be copied at its larger size but counted at
                                // the planned size, and could overflow the disc.
                                await using (var srcStream = new FileStream(
                                    file.FullPath, FileMode.Open, FileAccess.Read,
                                    FileShare.Read, bufferSize: 81920, useAsync: true))
                                {
                                    long lockedSize = srcStream.Length;
                                    if (lockedSize != file.SizeBytes || lockedSize > remainingSpace)
                                    {
                                        // Changed since planning, or grew past the space
                                        // left on this disc. Don't stage a stale/oversized
                                        // copy — re-queue at the true (locked) size for a
                                        // later disc and try to fill the gap it leaves.
                                        pendingQueue.Enqueue(new ScannedFile
                                        {
                                            FullPath = file.FullPath,
                                            SizeBytes = lockedSize,
                                            LastWriteUtc = File.GetLastWriteTimeUtc(file.FullPath),
                                        });
                                        TryFillGapFromPending(
                                            filesToProcess, pendingQueue, remainingSpace);
                                        break;
                                    }

                                    await using var dstStream = new FileStream(
                                        destPath, FileMode.Create, FileAccess.Write,
                                        FileShare.None, bufferSize: 81920, useAsync: true);
                                    await srcStream.CopyToAsync(dstStream, ct);

                                    stagedFiles.Add(new StagedFileInfo
                                    {
                                        Source = file,
                                        StagedPath = relativePath,
                                        IsZipped = false,
                                        IsSplit = false,
                                        StagedSizeBytes = lockedSize,
                                        Version = fileVersion,
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Determine what action to take.
                            BurnFailureAction action;
                            var category = BackupErrorClassifier.Classify(ex);

                            if (permanentSkip.HasValue)
                            {
                                action = permanentSkip.Value;
                            }
                            else if (discSkip.HasValue)
                            {
                                action = discSkip.Value;
                            }
                            else if (skippedCategories.Contains(category))
                            {
                                // User chose "skip all of this type" for this
                                // error category earlier in the run.
                                action = BurnFailureAction.Skip;
                            }
                            else if (onFailure is not null)
                            {
                                var decision = await onFailure(file.FullPath, ex.Message, category);
                                action = decision.Action;
                            }
                            else
                            {
                                action = BurnFailureAction.Skip;
                            }

                            // Certain actions implicitly set the disc/permanent flags.
                            switch (action)
                            {
                                case BurnFailureAction.SkipAllForDisc:
                                case BurnFailureAction.ZipAllForDisc:
                                    discSkip = action;
                                    break;
                                case BurnFailureAction.SkipAllPermanently:
                                    permanentSkip = action;
                                    break;
                                case BurnFailureAction.SkipAllOfThisType:
                                    // Remember this category and skip this file;
                                    // future failures of the same kind auto-skip.
                                    skippedCategories.Add(category);
                                    action = BurnFailureAction.Skip;
                                    break;
                            }

                            switch (action)
                            {
                                case BurnFailureAction.Retry:
                                    retrying = true;
                                    break;

                                case BurnFailureAction.Abort:
                                    failedFiles.Add(new FailedFile
                                    {
                                        Path = file.FullPath,
                                        Error = ex.Message,
                                        ActionTaken = BurnFailureAction.Abort,
                                    });
                                    throw new OperationCanceledException(
                                        "Backup aborted by user due to file failure.");

                                case BurnFailureAction.Zip:
                                case BurnFailureAction.ZipAllForDisc:
                                    try
                                    {
                                        string zipPath = await _zipHandler.ZipFileAsync(
                                            file.FullPath, stagingDir, ct);
                                        string zipRelative = Path.GetFileName(zipPath);

                                        stagedFiles.Add(new StagedFileInfo
                                        {
                                            Source = file,
                                            StagedPath = zipRelative,
                                            IsZipped = true,
                                            IsSplit = false,
                                            StagedSizeBytes = new FileInfo(zipPath).Length,
                                            Version = fileVersion,
                                        });
                                    }
                                    catch (Exception zipEx)
                                    {
                                        // Zip also failed — ask user what to do.
                                        // Only offer Skip / Skip All / Abort (zip
                                        // already failed so Zip/Retry won't help).
                                        var zipCategory = BackupErrorClassifier.Classify(zipEx);
                                        if (onFailure is not null
                                            && !permanentSkip.HasValue
                                            && discSkip is not BurnFailureAction.SkipAllForDisc
                                            && !skippedCategories.Contains(zipCategory))
                                        {
                                            var zipDecision = await onFailure(
                                                file.FullPath,
                                                $"Zip also failed: {zipEx.Message}",
                                                zipCategory);

                                            switch (zipDecision.Action)
                                            {
                                                case BurnFailureAction.SkipAllForDisc:
                                                    discSkip = BurnFailureAction.SkipAllForDisc;
                                                    goto default;
                                                case BurnFailureAction.SkipAllPermanently:
                                                    permanentSkip = BurnFailureAction.SkipAllPermanently;
                                                    goto default;
                                                case BurnFailureAction.SkipAllOfThisType:
                                                    skippedCategories.Add(zipCategory);
                                                    goto default;
                                                case BurnFailureAction.Abort:
                                                    failedFiles.Add(new FailedFile
                                                    {
                                                        Path = file.FullPath,
                                                        Error = $"Zip also failed: {zipEx.Message}",
                                                        ActionTaken = BurnFailureAction.Abort,
                                                    });
                                                    throw new OperationCanceledException(
                                                        "Backup aborted by user due to file failure.");
                                                default:
                                                    failedFiles.Add(new FailedFile
                                                    {
                                                        Path = file.FullPath,
                                                        Error = $"Zip also failed: {zipEx.Message}",
                                                        ActionTaken = zipDecision.Action,
                                                    });
                                                    break;
                                            }
                                        }
                                        else
                                        {
                                            failedFiles.Add(new FailedFile
                                            {
                                                Path = file.FullPath,
                                                Error = $"Zip also failed: {zipEx.Message}",
                                                ActionTaken = BurnFailureAction.Zip,
                                            });
                                        }
                                    }
                                    break;

                                case BurnFailureAction.Skip:
                                case BurnFailureAction.SkipAllForDisc:
                                case BurnFailureAction.SkipAllPermanently:
                                default:
                                    failedFiles.Add(new FailedFile
                                    {
                                        Path = file.FullPath,
                                        Error = ex.Message,
                                        ActionTaken = action,
                                    });
                                    break;
                            }
                        }
                    }
                }

                // --- Live coordinator post-staging check ---
                // Detect files that changed DURING the copy (the inline metadata
                // check above only catches changes BEFORE the copy starts).
                if (coordinator is not null)
                {
                    // Unregister all files now that staging is complete.
                    foreach (var sf in stagedFiles)
                        coordinator.UnregisterStagedFile(sf.Source.FullPath);

                    var changedDuringStaging = coordinator.GetChangedFiles();
                    if (changedDuringStaging.Count > 0)
                    {
                        var changedSet = new HashSet<string>(
                            changedDuringStaging, StringComparer.OrdinalIgnoreCase);

                        for (int j = stagedFiles.Count - 1; j >= 0; j--)
                        {
                            if (changedSet.Contains(stagedFiles[j].Source.FullPath))
                            {
                                var stale = stagedFiles[j];
                                stagedFiles.RemoveAt(j);

                                // Release the in-place read lock (if any) so the file
                                // isn't held open after we drop it from this disc.
                                if (stale.HeldLock is not null)
                                {
                                    heldLocks.Remove(stale.HeldLock);
                                    try { stale.HeldLock.Dispose(); } catch { /* best effort */ }
                                }

                                // Clean up the stale staged file from disk (no-op for
                                // in-place files, which were never copied to staging).
                                string stalePath = Path.Combine(stagingDir, stale.StagedPath);
                                try { if (File.Exists(stalePath)) File.Delete(stalePath); }
                                catch { /* best effort */ }

                                // Re-queue with fresh metadata for a later disc.
                                try
                                {
                                    var freshInfo = new FileInfo(stale.Source.FullPath);
                                    if (freshInfo.Exists)
                                    {
                                        reQueuedFiles.Add(new ScannedFile
                                        {
                                            FullPath = stale.Source.FullPath,
                                            SizeBytes = freshInfo.Length,
                                            LastWriteUtc = freshInfo.LastWriteTimeUtc,
                                        });
                                    }
                                }
                                catch { /* file may have been deleted */ }
                            }
                        }
                        coordinator.ClearChangedFiles();
                    }
                }

                if (stagedFiles.Count == 0)
                    continue; // All files on this disc failed to stage.

                // --- Include backup software on disc ---
                if (plan.Job.IncludeSoftwareOnDisc)
                {
                    CopySoftwareToStaging(stagingDir);
                }

                // Export catalog database to staging directory if requested.
                string? catalogStagedPath = null;
                if (plan.Job.IncludeCatalogOnDisc && discIndex == plan.DiscAllocations.Count - 1)
                {
                    catalogStagedPath = Path.Combine(stagingDir, "LithicBackup-Catalog.db");
                    await _catalog.ExportDatabaseAsync(backupSetId, catalogStagedPath, ct);
                }

                // Burn.
                var burnProgress = progress is not null
                    ? new Progress<BurnProgress>(bp =>
                    {
                        progress.Report(new BackupProgress
                        {
                            CurrentDisc = discSequence,
                            TotalDiscs = Math.Max(plan.TotalDiscsRequired, discSequence),
                            CurrentFile = bp.CurrentFile,
                            BytesWrittenTotal = totalBytesWritten + bp.BytesWritten,
                            BytesTotalAll = plan.TotalBytes,
                            OverallPercentage = plan.TotalBytes > 0
                                ? (double)(totalBytesWritten + bp.BytesWritten) / plan.TotalBytes * 100
                                : 0,
                            DiscBurnProgress = bp,
                        });
                    })
                    : null;

                // Always leave the disc open (multisession) so future incremental
                // backups can append to the last disc until it's full.  Consolidation
                // and disc-replacement burns set Multisession = false explicitly.
                var burnOptions = new BurnOptions
                {
                    FilesystemType = filesystemType,
                    Multisession = true,
                    VerifyAfterBurn = plan.Job.VerifyAfterBurn,
                };

                // Build the burn item list. Plain files staged in-place point at
                // their original (locked) source; everything else (zipped, split,
                // software, catalog) points at its temp staging copy.
                var burnItems = new List<BurnItem>(stagedFiles.Count);
                foreach (var sf in stagedFiles)
                {
                    string src = sf.HeldLock is not null
                        ? sf.Source.FullPath
                        : Path.Combine(stagingDir, sf.StagedPath);
                    burnItems.Add(new BurnItem(VersionedDiscPath(sf.StagedPath, sf.Version), src));
                }
                // Software payload and exported catalog always live under staging.
                if (plan.Job.IncludeSoftwareOnDisc)
                {
                    string swDir = Path.Combine(stagingDir, "LithicBackup-Software");
                    if (Directory.Exists(swDir))
                    {
                        foreach (var f in Directory.GetFiles(swDir, "*", SearchOption.AllDirectories))
                            burnItems.Add(new BurnItem(Path.GetRelativePath(stagingDir, f), f));
                    }
                }
                if (catalogStagedPath is not null)
                    burnItems.Add(new BurnItem("LithicBackup-Catalog.db", catalogStagedPath));

                await _burner.BurnAsync(recorderId, burnItems, burnOptions, burnProgress, ct);

                long discBytesUsed = stagedFiles.Sum(sf => sf.StagedSizeBytes);

                // Record this disc and its files in the catalog.
                using var tx = await _catalog.BeginTransactionAsync(backupSetId, ct);

                // If erasing and rewriting, update the existing disc record.
                DiscRecord discRecord;
                if (sessionDecision.Action == SessionAction.EraseAndRewrite
                    && sessionDecision.ExistingDiscId.HasValue)
                {
                    discRecord = (await _catalog.GetDiscAsync(sessionDecision.ExistingDiscId.Value, ct))!;
                    discRecord.RewriteCount++;
                    discRecord.BytesUsed = discBytesUsed;
                    discRecord.LastWrittenUtc = DateTime.UtcNow;
                    discRecord.Status = BurnSessionStatus.Completed;
                    await _catalog.UpdateDiscAsync(discRecord, ct);
                }
                else
                {
                    discRecord = await _catalog.CreateDiscAsync(new DiscRecord
                    {
                        BackupSetId = backupSetId,
                        Label = $"Disc-{discSequence:D3}",
                        SequenceNumber = discSequence,
                        MediaType = (await _burner.GetMediaInfoAsync(recorderId, ct)).MediaType,
                        FilesystemType = burnOptions.FilesystemType,
                        Capacity = discCapacity,
                        BytesUsed = discBytesUsed,
                        IsMultisession = burnOptions.Multisession,
                        Status = BurnSessionStatus.Completed,
                        CreatedUtc = DateTime.UtcNow,
                        LastWrittenUtc = DateTime.UtcNow,
                    }, ct);
                }

                foreach (var staged in stagedFiles)
                {
                    // --- Split-file chunk ---
                    // Each chunk of a split file is staged individually and may
                    // land on a different disc. The owning FileRecord is created
                    // once (with the first chunk); every chunk — including those on
                    // later discs — attaches to it.
                    if (staged.Split is not null && staged.Chunk is not null)
                    {
                        var sc = staged.Split;
                        if (sc.FileRecordId < 0)
                        {
                            int splitVersion = 1;
                            if (versionInfo.TryGetValue(sc.Source.FullPath, out var svi))
                                splitVersion = svi.MaxVersion + 1;
                            // Use the snapshot's actual size (== sum of chunk
                            // lengths), not the planned scan size, so the record's
                            // SizeBytes matches the bytes actually written even if the
                            // source grew between planning and the split.
                            versionInfo[sc.Source.FullPath] = new FileVersionInfo(
                                splitVersion, sc.Size, sc.Source.LastWriteUtc, false, false, sc.Hash);

                            var splitRecord = await _catalog.CreateFileRecordAsync(new FileRecord
                            {
                                DiscId = discRecord.Id,
                                SourcePath = sc.Source.FullPath,
                                DiscPath = staged.Chunk.DiscFilename,
                                SizeBytes = sc.Size,
                                Hash = sc.Hash,
                                IsZipped = false,
                                IsSplit = true,
                                Version = splitVersion,
                                SourceLastWriteUtc = sc.Source.LastWriteUtc,
                                BackedUpUtc = DateTime.UtcNow,
                            }, ct);
                            sc.FileRecordId = splitRecord.Id;
                        }

                        staged.Chunk.FileRecordId = sc.FileRecordId;
                        staged.Chunk.DiscId = discRecord.Id;
                        await _catalog.CreateFileChunkAsync(staged.Chunk, ct);
                        continue;
                    }

                    string hash = await ComputeFileHashAsync(staged.Source.FullPath, ct);

                    // The version was fixed at staging time (see fileVersion) so the
                    // recorded DiscPath matches the versioned path actually burned.
                    int version = staged.Version;
                    // Update the lookup so subsequent files in this same run get
                    // the correct version (shouldn't happen, but be safe).
                    versionInfo[staged.Source.FullPath] = new FileVersionInfo(
                        version, staged.Source.SizeBytes, staged.Source.LastWriteUtc, false, false, hash);

                    await _catalog.CreateFileRecordAsync(new FileRecord
                    {
                        DiscId = discRecord.Id,
                        SourcePath = staged.Source.FullPath,
                        DiscPath = VersionedDiscPath(staged.StagedPath, staged.Version),
                        SizeBytes = staged.Source.SizeBytes,
                        Hash = hash,
                        IsZipped = staged.IsZipped,
                        IsSplit = staged.IsSplit,
                        Version = version,
                        SourceLastWriteUtc = staged.Source.LastWriteUtc,
                        BackedUpUtc = DateTime.UtcNow,
                    }, ct);
                }

                // Commit the catalog updates.
                tx.Complete();
                tx.Dispose();

                discsWritten++;
                totalBytesWritten += discBytesUsed;
            }
            finally
            {
                // Release any in-place read locks now that the burn (and catalog
                // recording) are done, then clean up the staging directory.
                foreach (var h in heldLocks)
                {
                    try { h.Dispose(); } catch { /* best effort */ }
                }
                try { Directory.Delete(stagingDir, true); } catch { }
            }

            discIndex++;
        }

        } // end try (coordinator)
        finally
        {
            coordinator?.Dispose();

            // Remove any spill snapshots taken for split files.
            foreach (var spill in spillDirs)
            {
                try { if (Directory.Exists(spill)) Directory.Delete(spill, true); } catch { }
            }
        }

        // Update the backup set's last backup timestamp.
        var backupSet = await _catalog.GetBackupSetAsync(backupSetId, ct);
        if (backupSet is not null)
        {
            backupSet.LastBackupUtc = DateTime.UtcNow;
            await _catalog.UpdateBackupSetAsync(backupSet, ct);
        }

        return new BackupResult
        {
            Success = failedFiles.Count == 0,
            DiscsWritten = discsWritten,
            BytesWritten = totalBytesWritten,
            FailedFiles = failedFiles,
        };
    }

    // -------------------------------------------------------------------
    // ConsolidateAsync
    // -------------------------------------------------------------------

    public Task ConsolidateAsync(int backupSetId, CancellationToken ct = default)
    {
        return ConsolidateAsync(backupSetId, progress: null, ct);
    }

    public async Task ConsolidateAsync(int backupSetId, IProgress<BackupProgress>? progress, CancellationToken ct = default)
    {
        var set = await _catalog.GetBackupSetAsync(backupSetId, ct)
            ?? throw new InvalidOperationException($"Backup set {backupSetId} not found.");

        int discCount = await _catalog.GetIncrementalDiscCountAsync(backupSetId, ct);

        if (discCount <= set.MaxIncrementalDiscs)
            return; // No consolidation needed.

        // 1. Get all discs in the set.
        var oldDiscs = await _catalog.GetDiscsForBackupSetAsync(backupSetId, ct);

        // 2. Get all files across all discs -- latest version of each source path only.
        var allFiles = await _catalog.GetAllFilesForBackupSetAsync(backupSetId, ct);
        var latestByPath = allFiles
            .Where(f => !f.IsDeleted)
            .GroupBy(f => f.SourcePath)
            .Select(g => g.OrderByDescending(f => f.Version).First())
            .ToList();

        if (latestByPath.Count == 0)
            return;

        // 3. Re-plan: bin-pack those files onto fresh discs.
        var scannedFiles = latestByPath
            .Select(fr => new ScannedFile
            {
                FullPath = fr.SourcePath,
                SizeBytes = fr.SizeBytes,
                LastWriteUtc = fr.SourceLastWriteUtc,
            })
            .ToList();

        var recorderIds = _burner.GetRecorderIds();
        if (recorderIds.Count == 0)
            throw new InvalidOperationException("No disc recorder detected for consolidation.");

        string recorderId = recorderIds[0];
        long discCapacity = set.CapacityOverrideBytes
            ?? (await _burner.GetMediaInfoAsync(recorderId, ct)).TotalCapacityBytes;

        var allocations = _packer.Pack(scannedFiles, discCapacity);

        long totalBytes = scannedFiles.Sum(f => f.SizeBytes);
        long totalBytesWritten = 0;

        // 4. Burn each fresh disc.
        for (int i = 0; i < allocations.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var allocation = allocations[i];
            int discSequence = i + 1;

            progress?.Report(new BackupProgress
            {
                CurrentDisc = discSequence,
                TotalDiscs = allocations.Count,
                CurrentFile = "Staging files for consolidation...",
                BytesWrittenTotal = totalBytesWritten,
                BytesTotalAll = totalBytes,
                OverallPercentage = totalBytes > 0
                    ? (double)totalBytesWritten / totalBytes * 100
                    : 0,
            });

            string stagingDir = Path.Combine(
                Path.GetTempPath(), "LithicBackup", $"consolidate-{backupSetId}-disc-{discSequence}");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            try
            {
                var stagedFiles = new List<(ScannedFile Source, string StagedPath)>();
                foreach (var file in allocation.Files)
                {
                    ct.ThrowIfCancellationRequested();

                    string relativePath = GetRelativeStagingPath(file.FullPath);
                    string destPath = Path.Combine(stagingDir, relativePath);
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (destDir is not null)
                        Directory.CreateDirectory(destDir);

                    File.Copy(file.FullPath, destPath, overwrite: true);
                    stagedFiles.Add((file, relativePath));
                }

                if (stagedFiles.Count == 0)
                    continue;

                var burnOptions = new BurnOptions
                {
                    FilesystemType = set.DefaultFilesystemType,
                    Multisession = false,
                    VerifyAfterBurn = true,
                };

                // Wire up burn progress to the consolidation progress callback.
                var burnProgress = progress is not null
                    ? new Progress<BurnProgress>(bp =>
                    {
                        progress.Report(new BackupProgress
                        {
                            CurrentDisc = discSequence,
                            TotalDiscs = allocations.Count,
                            CurrentFile = bp.CurrentFile,
                            BytesWrittenTotal = totalBytesWritten + bp.BytesWritten,
                            BytesTotalAll = totalBytes,
                            OverallPercentage = totalBytes > 0
                                ? (double)(totalBytesWritten + bp.BytesWritten) / totalBytes * 100
                                : 0,
                            DiscBurnProgress = bp,
                        });
                    })
                    : null;

                // Consolidation always copies files to temp staging, so every
                // burn item reads from the staging directory.
                var consolidateItems = Directory
                    .GetFiles(stagingDir, "*", SearchOption.AllDirectories)
                    .Select(f => new BurnItem(Path.GetRelativePath(stagingDir, f), f))
                    .ToList();
                await _burner.BurnAsync(recorderId, consolidateItems, burnOptions, burnProgress, ct);

                // Record the new disc and files in the catalog.
                using var tx = await _catalog.BeginTransactionAsync(backupSetId, ct);

                var newDisc = await _catalog.CreateDiscAsync(new DiscRecord
                {
                    BackupSetId = backupSetId,
                    Label = $"Consolidated-{discSequence:D3}",
                    SequenceNumber = discSequence,
                    MediaType = (await _burner.GetMediaInfoAsync(recorderId, ct)).MediaType,
                    FilesystemType = burnOptions.FilesystemType,
                    Capacity = allocation.TotalBytes + allocation.FreeBytes,
                    BytesUsed = allocation.TotalBytes,
                    IsMultisession = false,
                    Status = BurnSessionStatus.Completed,
                    CreatedUtc = DateTime.UtcNow,
                    LastWrittenUtc = DateTime.UtcNow,
                }, ct);

                foreach (var (source, stagedPath) in stagedFiles)
                {
                    string hash = await ComputeFileHashAsync(source.FullPath, ct);

                    await _catalog.CreateFileRecordAsync(new FileRecord
                    {
                        DiscId = newDisc.Id,
                        SourcePath = source.FullPath,
                        DiscPath = stagedPath,
                        SizeBytes = source.SizeBytes,
                        Hash = hash,
                        SourceLastWriteUtc = source.LastWriteUtc,
                        BackedUpUtc = DateTime.UtcNow,
                    }, ct);
                }

                tx.Complete();
                tx.Dispose();

                totalBytesWritten += allocation.TotalBytes;
            }
            finally
            {
                try { Directory.Delete(stagingDir, true); } catch { }
            }
        }

        // 5. Mark old discs as superseded (skip discs already marked bad or failed).
        foreach (var oldDisc in oldDiscs)
        {
            if (oldDisc.IsBad || oldDisc.Status == BurnSessionStatus.Failed)
                continue;

            oldDisc.Status = BurnSessionStatus.Superseded;
            await _catalog.UpdateDiscAsync(oldDisc, ct);
        }

        // 6. Update backup set timestamp.
        set.LastBackupUtc = DateTime.UtcNow;
        await _catalog.UpdateBackupSetAsync(set, ct);
    }

    // -------------------------------------------------------------------
    // ReplaceDiscAsync
    // -------------------------------------------------------------------

    public Task ReplaceDiscAsync(int badDiscId, string recorderId, CancellationToken ct = default) =>
        ReplaceDiscAsync(badDiscId, recorderId, progress: null, ct);

    public async Task ReplaceDiscAsync(
        int badDiscId, string recorderId, IProgress<BackupProgress>? progress, CancellationToken ct = default)
    {
        // 1. Mark the disc as bad.
        await _catalog.MarkDiscAsBadAsync(badDiscId, ct);

        // 2. Get the bad disc and its files.
        var badDisc = await _catalog.GetDiscAsync(badDiscId, ct)
            ?? throw new InvalidOperationException($"Disc {badDiscId} not found.");

        var filesToReplace = await _catalog.GetFilesForReplacementAsync(badDiscId, ct);
        if (filesToReplace.Count == 0)
            return;

        // 3. Stage from live sources + burn a fresh disc (shared with the
        //    per-file repair path below).
        var staged = await StageFilesForReburnAsync(
            filesToReplace, badDisc.BackupSetId, recorderId, badDisc.FilesystemType, progress, ct);
        if (staged is null)
            return;

        // 4. Record the new disc + file records. The whole disc was replaced, so
        //    the new records carry the same version (this is a fresh copy of the
        //    same content, not a new backup generation).
        using var tx = await _catalog.BeginTransactionAsync(badDisc.BackupSetId, ct);

        var newDisc = await _catalog.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = badDisc.BackupSetId,
            Label = $"{badDisc.Label}-Replacement",
            SequenceNumber = badDisc.SequenceNumber,
            MediaType = (await _burner.GetMediaInfoAsync(recorderId, ct)).MediaType,
            FilesystemType = badDisc.FilesystemType,
            Capacity = badDisc.Capacity,
            BytesUsed = staged.Sum(s => s.StagedSize),
            IsMultisession = false,
            Status = BurnSessionStatus.Completed,
            CreatedUtc = DateTime.UtcNow,
            LastWrittenUtc = DateTime.UtcNow,
        }, ct);

        foreach (var (original, stagedPath, stagedSize) in staged)
        {
            string hash = await ComputeFileHashAsync(original.SourcePath, ct);

            // Re-read the live source's size/mtime: the file may have grown or
            // shrunk since it was first backed up, so recording the original's
            // stale size would leave the new record inconsistent with the bytes
            // actually on the replacement disc (and fail later size checks).
            var (freshSize, freshMtime) = FreshSourceMetadata(original, stagedSize);

            await _catalog.CreateFileRecordAsync(new FileRecord
            {
                DiscId = newDisc.Id,
                SourcePath = original.SourcePath,
                DiscPath = stagedPath,
                SizeBytes = freshSize,
                Hash = hash,
                Version = original.Version,
                SourceLastWriteUtc = freshMtime,
                BackedUpUtc = DateTime.UtcNow,
            }, ct);
        }

        tx.Complete();
        tx.Dispose();
    }

    /// <summary>
    /// Current size and last-write time of a re-staged source file, falling back
    /// to the record's stored values (and the staged byte count) if the live file
    /// can't be inspected. Used by the disc-replacement paths so a file that grew
    /// or shrank since its first backup is recorded with its true current size.
    /// </summary>
    private static (long Size, DateTime Mtime) FreshSourceMetadata(FileRecord original, long stagedSize)
    {
        try
        {
            var fi = new FileInfo(original.SourcePath);
            if (fi.Exists)
                return (fi.Length, fi.LastWriteTimeUtc);
        }
        catch { /* fall through to staged/stored values */ }
        return (stagedSize, original.SourceLastWriteUtc);
    }

    // -------------------------------------------------------------------
    // ReplaceDiscFilesAsync (repair only the files that failed a disc test)
    // -------------------------------------------------------------------

    public async Task<int> ReplaceDiscFilesAsync(
        int discId,
        IReadOnlyCollection<long> fileRecordIds,
        string recorderId,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (fileRecordIds.Count == 0)
            return 0;

        var disc = await _catalog.GetDiscAsync(discId, ct)
            ?? throw new InvalidOperationException($"Disc {discId} not found.");

        var idSet = fileRecordIds.ToHashSet();
        var filesToReplace = (await _catalog.GetFilesOnDiscAsync(discId, ct))
            .Where(f => idSet.Contains(f.Id) && !f.IsDeleted)
            .ToList();
        if (filesToReplace.Count == 0)
            return 0;

        // Stage from live sources + burn a fresh supplementary disc.
        var staged = await StageFilesForReburnAsync(
            filesToReplace, disc.BackupSetId, recorderId, disc.FilesystemType, progress, ct);
        if (staged is null)
            return 0;

        // The repaired files go onto a new disc appended after the last one in
        // the set; the old disc keeps its still-good files.
        var discs = await _catalog.GetDiscsForBackupSetAsync(disc.BackupSetId, ct);
        int nextSeq = (discs.Count == 0 ? 0 : discs.Max(d => d.SequenceNumber)) + 1;

        using var tx = await _catalog.BeginTransactionAsync(disc.BackupSetId, ct);

        var newDisc = await _catalog.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = disc.BackupSetId,
            Label = $"{disc.Label}-Repair",
            SequenceNumber = nextSeq,
            MediaType = (await _burner.GetMediaInfoAsync(recorderId, ct)).MediaType,
            FilesystemType = disc.FilesystemType,
            Capacity = disc.Capacity,
            BytesUsed = staged.Sum(s => s.StagedSize),
            IsMultisession = false,
            Status = BurnSessionStatus.Completed,
            CreatedUtc = DateTime.UtcNow,
            LastWrittenUtc = DateTime.UtcNow,
        }, ct);

        foreach (var (original, stagedPath, stagedSize) in staged)
        {
            string hash = await ComputeFileHashAsync(original.SourcePath, ct);

            // Record the live source's current size/mtime (it may have grown or
            // shrunk since the first backup) so the repair disc's record matches
            // the bytes actually written.
            var (freshSize, freshMtime) = FreshSourceMetadata(original, stagedSize);

            await _catalog.CreateFileRecordAsync(new FileRecord
            {
                DiscId = newDisc.Id,
                SourcePath = original.SourcePath,
                DiscPath = stagedPath,
                SizeBytes = freshSize,
                Hash = hash,
                Version = original.Version + 1,
                SourceLastWriteUtc = freshMtime,
                BackedUpUtc = DateTime.UtcNow,
            }, ct);

            // Supersede the failed original so restore resolves to the fresh copy
            // on the repair disc rather than the corrupt/missing one.
            original.IsDeleted = true;
            await _catalog.UpdateFileRecordAsync(original, ct);
        }

        tx.Complete();
        tx.Dispose();
        return staged.Count;
    }

    /// <summary>
    /// Stage each of <paramref name="files"/> from its live <see cref="FileRecord.SourcePath"/>
    /// into a temp folder and burn them to a fresh single-session disc via
    /// <paramref name="recorderId"/> (with post-burn verification). Files whose
    /// source no longer exists on disk are skipped. Returns the staged
    /// (original record, disc-relative path, staged byte size) tuples, or
    /// <c>null</c> if nothing could be staged. The staging folder is always
    /// cleaned up; callers compute catalog hashes from the live source, not the
    /// staged copy, so the folder is safe to delete before records are written.
    /// </summary>
    private async Task<List<(FileRecord Original, string StagedPath, long StagedSize)>?>
        StageFilesForReburnAsync(
            IReadOnlyList<FileRecord> files,
            int backupSetId,
            string recorderId,
            FilesystemType filesystemType,
            IProgress<BackupProgress>? progress,
            CancellationToken ct)
    {
        string stagingDir = Path.Combine(
            Path.GetTempPath(), "LithicBackup", $"reburn-{backupSetId}-{Guid.NewGuid():N}");
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        try
        {
            var staged = new List<(FileRecord Original, string StagedPath, long StagedSize)>();
            int idx = 0;
            foreach (var fileRecord in files)
            {
                ct.ThrowIfCancellationRequested();
                idx++;

                if (!File.Exists(fileRecord.SourcePath))
                    continue; // Source file no longer available.

                string relativePath = GetRelativeStagingPath(fileRecord.SourcePath);
                string destPath = Path.Combine(stagingDir, relativePath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (destDir is not null)
                    Directory.CreateDirectory(destDir);

                File.Copy(fileRecord.SourcePath, destPath, overwrite: true);
                staged.Add((fileRecord, relativePath, new FileInfo(destPath).Length));

                progress?.Report(new BackupProgress
                {
                    CurrentDisc = 1,
                    TotalDiscs = 1,
                    CurrentFile = $"Staging: {Path.GetFileName(fileRecord.SourcePath)}",
                    StatusMessage = "Staging files for re-burn...",
                    OverallPercentage = files.Count > 0 ? (double)idx / files.Count * 50 : 0,
                });
            }

            if (staged.Count == 0)
                return null;

            var burnOptions = new BurnOptions
            {
                FilesystemType = filesystemType,
                Multisession = false,
                VerifyAfterBurn = true,
            };

            var burnProgress = progress is not null
                ? new Progress<BurnProgress>(bp => progress.Report(new BackupProgress
                {
                    CurrentDisc = 1,
                    TotalDiscs = 1,
                    CurrentFile = bp.CurrentFile,
                    BytesWrittenTotal = bp.BytesWritten,
                    BytesTotalAll = bp.TotalBytes,
                    OverallPercentage = 50 + bp.Percentage / 2,
                    DiscBurnProgress = bp,
                }))
                : null;

            // Re-burn always copies files to temp staging, so every burn item
            // reads from the staging directory.
            var reburnItems = Directory
                .GetFiles(stagingDir, "*", SearchOption.AllDirectories)
                .Select(f => new BurnItem(Path.GetRelativePath(stagingDir, f), f))
                .ToList();
            await _burner.BurnAsync(recorderId, reburnItems, burnOptions, burnProgress, ct);

            return staged;
        }
        finally
        {
            try { Directory.Delete(stagingDir, true); } catch { }
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// Try to pull a file from the pending queue that fits in the remaining space.
    /// </summary>
    private static void TryFillGapFromPending(
        List<ScannedFile> filesToProcess,
        Queue<ScannedFile> pendingQueue,
        long remainingBytes)
    {
        int count = pendingQueue.Count;
        for (int i = 0; i < count; i++)
        {
            var candidate = pendingQueue.Dequeue();
            if (candidate.SizeBytes <= remainingBytes)
            {
                filesToProcess.Add(candidate);
                return;
            }
            pendingQueue.Enqueue(candidate);
        }
    }

    /// <summary>
    /// Create a relative path for staging by stripping the drive letter/root
    /// and preserving directory structure.
    /// </summary>
    private static string GetRelativeStagingPath(string fullPath)
    {
        // "C:\Users\foo\file.txt" -> "Users\foo\file.txt"
        string root = Path.GetPathRoot(fullPath) ?? "";
        string relative = fullPath[root.Length..];

        // Prefix with the drive letter to avoid collisions between drives.
        char driveLetter = fullPath.Length >= 2 && fullPath[1] == ':'
            ? fullPath[0]
            : '_';
        return Path.Combine(driveLetter.ToString(), relative);
    }

    /// <summary>
    /// Produce the on-disc path for a given file version. Version 1 keeps the
    /// natural relative path; later versions insert a <c>.v{N}</c> tag before the
    /// extension (e.g. <c>Users\foo\file.txt</c> → <c>Users\foo\file.v2.txt</c>).
    /// This guarantees every version of a file occupies a distinct disc path, so
    /// re-burning a changed file onto the same physical (multisession) disc neither
    /// collides with the prior version (IMAPI's AddFile rejects duplicate paths)
    /// nor shadows it at restore time.
    /// </summary>
    private static string VersionedDiscPath(string relativePath, int version)
    {
        if (version <= 1) return relativePath;
        string dir = Path.GetDirectoryName(relativePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(relativePath);
        string ext = Path.GetExtension(relativePath);
        string versioned = $"{name}.v{version}{ext}";
        return string.IsNullOrEmpty(dir) ? versioned : Path.Combine(dir, versioned);
    }

    /// <summary>
    /// Copy the running application's EXE directory to a subfolder in staging.
    /// </summary>
    private static void CopySoftwareToStaging(string stagingDir)
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string destDir = Path.Combine(stagingDir, "LithicBackup-Software");
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(appDir, file);
            string destPath = Path.Combine(destDir, relativePath);
            string? parentDir = Path.GetDirectoryName(destPath);
            if (parentDir is not null)
                Directory.CreateDirectory(parentDir);

            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static BackupResult Fail(string message) => new()
    {
        Success = false,
        FailedFiles = [new FailedFile { Path = "", Error = message }],
    };

    /// <summary>
    /// Intermediate record of a staged item, tracking zip/split status. A split
    /// file is staged one chunk at a time: each <see cref="StagedFileInfo"/> with
    /// a non-null <see cref="Split"/> represents a single chunk on the current
    /// disc, and <see cref="Chunk"/> carries that chunk's metadata. All chunks of
    /// the same file share one <see cref="SplitContext"/> so a single
    /// <see cref="FileRecord"/> is created (on the first disc that holds a chunk)
    /// and every chunk — even those burned to later discs — points back to it.
    /// </summary>
    private class StagedFileInfo
    {
        public required ScannedFile Source { get; init; }
        public required string StagedPath { get; init; }
        public required bool IsZipped { get; init; }
        public required bool IsSplit { get; init; }
        public SplitContext? Split { get; init; }
        public FileChunk? Chunk { get; init; }
        public long StagedSizeBytes { get; init; }

        /// <summary>
        /// The version number of this file within the backup set (1 for the first
        /// copy, incrementing each time a changed file is re-staged). Versions &gt; 1
        /// are given a distinct on-disc path by <see cref="VersionedDiscPath"/> so
        /// that re-burning a changed file to the same physical disc does not collide
        /// with the prior version's path (IMAPI AddFile rejects duplicate paths) and
        /// so restore does not shadow one version with another.
        /// </summary>
        public int Version { get; init; } = 1;

        /// <summary>
        /// For a plain file staged in-place (burn-in-place mode): the read lock
        /// held on the original source for the duration of the burn. Null when the
        /// file was copied to temp staging instead. When non-null, the burn item
        /// reads from <see cref="Source"/>'s original path rather than a temp copy.
        /// </summary>
        public FileStream? HeldLock { get; init; }
    }

    /// <summary>
    /// Shared state for a file being split across one or more discs. Created when
    /// a file first needs splitting; carries the full-file hash (computed once from
    /// a spill snapshot) and, once the first chunk is recorded, the id of the
    /// owning <see cref="FileRecord"/> so later discs attach their chunks to it.
    /// </summary>
    private sealed class SplitContext
    {
        public required ScannedFile Source { get; init; }
        public required string Hash { get; init; }
        /// <summary>
        /// Actual byte length of the spill snapshot the chunks are carved from
        /// (captured under a read lock when the split began), so the owning
        /// <see cref="FileRecord"/>'s size matches the sum of its chunk lengths
        /// even if the live source grew between planning and the split.
        /// </summary>
        public required long Size { get; init; }
        public long FileRecordId { get; set; } = -1;
    }

    /// <summary>
    /// Progress of a split still being written out disc by disc. Bytes are read
    /// from <see cref="SpillPath"/> (an immutable snapshot of the source taken when
    /// the split began) so every chunk is consistent even if the live source
    /// changes between discs.
    /// </summary>
    private sealed class SplitCarry
    {
        public required SplitContext Ctx { get; init; }
        public required string SpillPath { get; init; }
        public long Offset { get; set; }
        public long Remaining { get; set; }
        public int Sequence { get; set; }
    }

    /// <summary>
    /// Snapshot a file that needs splitting into a private spill directory and
    /// return a fresh <see cref="SplitCarry"/> positioned at its start. The
    /// snapshot (rather than the live file) is the source for every chunk, so a
    /// file that spans discs stays internally consistent even if the original
    /// changes between discs.
    /// </summary>
    private async Task<SplitCarry> BeginSplitAsync(
        ScannedFile file, List<string> spillDirs, CancellationToken ct)
    {
        string spillDir = Path.Combine(
            Path.GetTempPath(), "LithicBackup", $"spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDir);
        spillDirs.Add(spillDir);

        string spillPath = Path.Combine(spillDir, "data");
        await using (var src = new FileStream(
            file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true))
        await using (var dst = new FileStream(
            spillPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true))
        {
            await src.CopyToAsync(dst, ct);
        }

        long size = new FileInfo(spillPath).Length;
        string hash = await ComputeFileHashAsync(spillPath, ct);

        return new SplitCarry
        {
            Ctx = new SplitContext { Source = file, Hash = hash, Size = size },
            SpillPath = spillPath,
            Offset = 0,
            Remaining = size,
            Sequence = 0,
        };
    }

    /// <summary>
    /// Write as many of a split file's remaining chunks into the current disc's
    /// staging directory as will fit, appending a <see cref="StagedFileInfo"/> per
    /// chunk. Returns the still-unfinished carry (to continue on the next disc) or
    /// <c>null</c> once the whole file has been written. Each chunk fills the disc's
    /// remaining space, so the first chunk on a fresh disc takes a full disc's worth.
    /// </summary>
    private async Task<SplitCarry?> PlaceSplitChunksAsync(
        SplitCarry? carry,
        List<StagedFileInfo> stagedFiles,
        string stagingDir,
        long discCapacity,
        CancellationToken ct)
    {
        if (carry is null)
            return null;

        long used = stagedFiles.Sum(sf => sf.StagedSizeBytes);

        while (carry.Remaining > 0)
        {
            long space = discCapacity - used;
            if (space <= 0)
                return carry; // Disc full — finish this split on the next disc.

            long len = Math.Min(space, carry.Remaining);
            string chunkName = $"{carry.Ctx.Hash[..8]}.{carry.Sequence:D4}.discburn-split";
            string chunkPath = Path.Combine(stagingDir, chunkName);

            await WriteChunkAsync(carry.SpillPath, carry.Offset, len, chunkPath, ct);

            var chunk = new FileChunk
            {
                Sequence = carry.Sequence,
                Offset = carry.Offset,
                Length = len,
                DiscFilename = chunkName,
            };

            stagedFiles.Add(new StagedFileInfo
            {
                Source = carry.Ctx.Source,
                StagedPath = chunkName,
                IsZipped = false,
                IsSplit = true,
                Split = carry.Ctx,
                Chunk = chunk,
                StagedSizeBytes = len,
            });

            carry.Offset += len;
            carry.Remaining -= len;
            carry.Sequence++;
            used += len;
        }

        return null; // Fully written.
    }

    /// <summary>
    /// Copy <paramref name="length"/> bytes starting at <paramref name="offset"/>
    /// from <paramref name="sourcePath"/> into a new file at <paramref name="destPath"/>.
    /// Used to carve one disc-sized chunk out of a spill snapshot.
    /// </summary>
    private static async Task WriteChunkAsync(
        string sourcePath, long offset, long length, string destPath, CancellationToken ct)
    {
        await using var src = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        src.Seek(offset, SeekOrigin.Begin);

        await using var dst = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await src.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0)
                break;
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }
}
