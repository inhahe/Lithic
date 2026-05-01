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
    private readonly IFileSplitter _fileSplitter;
    private readonly IDiscSessionStrategy _sessionStrategy;
    private readonly IDeduplicationEngine? _deduplicationEngine;
    private readonly IFileSystemMonitor? _fileSystemMonitor;

    public BackupOrchestrator(
        ICatalogRepository catalog,
        IDiscBurner burner,
        IFileScanner scanner,
        IBinPacker packer,
        IZipHandler zipHandler,
        IFileSplitter fileSplitter,
        IDiscSessionStrategy sessionStrategy,
        IDeduplicationEngine? deduplicationEngine = null,
        IFileSystemMonitor? fileSystemMonitor = null)
    {
        _catalog = catalog;
        _burner = burner;
        _scanner = scanner;
        _packer = packer;
        _zipHandler = zipHandler;
        _fileSplitter = fileSplitter;
        _sessionStrategy = sessionStrategy;
        _deduplicationEngine = deduplicationEngine;
        _fileSystemMonitor = fileSystemMonitor;
    }

    // -------------------------------------------------------------------
    // PlanAsync
    // -------------------------------------------------------------------

    public async Task<BackupPlan> PlanAsync(BackupJob job, CancellationToken ct = default,
        IProgress<ScanProgress>? scanProgress = null)
    {
        // 1. Scan source directories (global + per-directory exclusions).
        var isExcluded = GlobMatcher.CreateCombinedFilter(job.ExcludedExtensions, job.Sources);
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

        // Build a lookup of max existing version per source path so we can
        // increment version numbers for changed files.  Uses a lightweight
        // SQL aggregate query instead of loading all FileRecord objects.
        var versionInfo = plan.Job.BackupSetId.HasValue
            ? await _catalog.GetLatestVersionInfoAsync(backupSetId, ct)
            : new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);

        // Tracks a "skip all" decision: SkipAllForDisc resets per-disc,
        // SkipAllPermanently persists for the entire run.
        BurnFailureAction? permanentSkip = null;

        var filesystemType = plan.Job.FilesystemType;

        // Files that were found to have changed during staging get re-queued
        // for the next disc.
        var reQueuedFiles = new List<ScannedFile>();

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

        for (int discIndex = 0; discIndex < plan.DiscAllocations.Count; discIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var allocation = plan.DiscAllocations[discIndex];
            int discSequence = discIndex + 1;

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
                TotalDiscs = plan.TotalDiscsRequired,
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

            try
            {
                // Copy files to staging, recording which ones succeed.
                var stagedFiles = new List<StagedFileInfo>();
                long discCapacity = allocation.TotalBytes + allocation.FreeBytes;

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

                var filesToProcess = new List<ScannedFile>(allocation.Files);

                foreach (var file in filesToProcess)
                {
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

                    // --- Deduplication ---
                    if (plan.Job.EnableDeduplication && _deduplicationEngine is not null)
                    {
                        try
                        {
                            var recipe = await _deduplicationEngine.DeduplicateAsync(
                                file.FullPath, plan.Job.DeduplicationBlockSize, ct);

                            if (recipe.BytesSaved > 0)
                            {
                                progress?.Report(new BackupProgress
                                {
                                    CurrentDisc = discSequence,
                                    TotalDiscs = plan.TotalDiscsRequired,
                                    CurrentFile = $"Deduplicated {file.FullPath} (saved {recipe.BytesSaved} bytes)",
                                    BytesWrittenTotal = totalBytesWritten,
                                    BytesTotalAll = plan.TotalBytes,
                                    OverallPercentage = plan.TotalBytes > 0
                                        ? (double)totalBytesWritten / plan.TotalBytes * 100
                                        : 0,
                                });
                            }
                        }
                        catch
                        {
                            // Deduplication failure is non-fatal; proceed with normal staging.
                        }
                    }

                    // --- File splitting ---
                    // If a file is larger than the total disc capacity, always split.
                    // If it doesn't fit in remaining space and splitting is allowed, split.
                    long currentUsed = stagedFiles.Sum(sf => sf.StagedSizeBytes);
                    long remainingSpace = discCapacity - currentUsed;
                    bool fileExceedsDisc = file.SizeBytes > discCapacity;
                    bool fileExceedsRemaining = file.SizeBytes > remainingSpace && remainingSpace > 0;

                    if ((fileExceedsDisc || fileExceedsRemaining)
                        && (plan.Job.AllowFileSplitting || fileExceedsDisc))
                    {
                        try
                        {
                            long chunkSize = fileExceedsDisc ? discCapacity : remainingSpace;
                            if (chunkSize <= 0) chunkSize = discCapacity;

                            var chunks = await _fileSplitter.SplitAsync(
                                file.FullPath, stagingDir, chunkSize, ct);

                            stagedFiles.Add(new StagedFileInfo
                            {
                                Source = file,
                                StagedPath = chunks[0].DiscFilename,
                                IsZipped = false,
                                IsSplit = true,
                                Chunks = chunks,
                                StagedSizeBytes = chunks.Sum(c => c.Length),
                            });
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

                                // Open with FileShare.Read to acquire a read lock
                                // that prevents writes while we copy.
                                await using (var srcStream = new FileStream(
                                    file.FullPath, FileMode.Open, FileAccess.Read,
                                    FileShare.Read, bufferSize: 81920, useAsync: true))
                                await using (var dstStream = new FileStream(
                                    destPath, FileMode.Create, FileAccess.Write,
                                    FileShare.None, bufferSize: 81920, useAsync: true))
                                {
                                    await srcStream.CopyToAsync(dstStream, ct);
                                }

                                stagedFiles.Add(new StagedFileInfo
                                {
                                    Source = file,
                                    StagedPath = relativePath,
                                    IsZipped = false,
                                    IsSplit = false,
                                    StagedSizeBytes = file.SizeBytes,
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            // Determine what action to take.
                            BurnFailureAction action;

                            if (permanentSkip.HasValue)
                            {
                                action = permanentSkip.Value;
                            }
                            else if (discSkip.HasValue)
                            {
                                action = discSkip.Value;
                            }
                            else if (onFailure is not null)
                            {
                                var decision = await onFailure(file.FullPath, ex.Message);
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
                                        });
                                    }
                                    catch (Exception zipEx)
                                    {
                                        // Zip also failed — ask user what to do.
                                        // Only offer Skip / Skip All / Abort (zip
                                        // already failed so Zip/Retry won't help).
                                        if (onFailure is not null
                                            && !permanentSkip.HasValue
                                            && discSkip is not BurnFailureAction.SkipAllForDisc)
                                        {
                                            var zipDecision = await onFailure(
                                                file.FullPath,
                                                $"Zip also failed: {zipEx.Message}");

                                            switch (zipDecision.Action)
                                            {
                                                case BurnFailureAction.SkipAllForDisc:
                                                    discSkip = BurnFailureAction.SkipAllForDisc;
                                                    goto default;
                                                case BurnFailureAction.SkipAllPermanently:
                                                    permanentSkip = BurnFailureAction.SkipAllPermanently;
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

                                // Clean up the stale staged file from disk.
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
                if (plan.Job.IncludeCatalogOnDisc && discIndex == plan.DiscAllocations.Count - 1)
                {
                    string catalogDest = Path.Combine(stagingDir, "LithicBackup-Catalog.db");
                    await _catalog.ExportDatabaseAsync(catalogDest, ct);
                }

                // Burn.
                var burnProgress = progress is not null
                    ? new Progress<BurnProgress>(bp =>
                    {
                        progress.Report(new BackupProgress
                        {
                            CurrentDisc = discSequence,
                            TotalDiscs = plan.TotalDiscsRequired,
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

                await _burner.BurnAsync(recorderId, stagingDir, burnOptions, burnProgress, ct);

                // Record this disc and its files in the catalog.
                using var tx = await _catalog.BeginTransactionAsync(ct);

                // If erasing and rewriting, update the existing disc record.
                DiscRecord discRecord;
                if (sessionDecision.Action == SessionAction.EraseAndRewrite
                    && sessionDecision.ExistingDiscId.HasValue)
                {
                    discRecord = (await _catalog.GetDiscAsync(sessionDecision.ExistingDiscId.Value, ct))!;
                    discRecord.RewriteCount++;
                    discRecord.BytesUsed = stagedFiles.Sum(sf => sf.StagedSizeBytes);
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
                        Capacity = allocation.TotalBytes + allocation.FreeBytes,
                        BytesUsed = allocation.TotalBytes,
                        IsMultisession = burnOptions.Multisession,
                        Status = BurnSessionStatus.Completed,
                        CreatedUtc = DateTime.UtcNow,
                        LastWrittenUtc = DateTime.UtcNow,
                    }, ct);
                }

                foreach (var staged in stagedFiles)
                {
                    string hash = await ComputeFileHashAsync(staged.Source.FullPath, ct);

                    // Determine version: increment from the max existing version
                    // for this source path, or 1 if this is the first backup.
                    int version = 1;
                    if (versionInfo.TryGetValue(staged.Source.FullPath, out var vi))
                        version = vi.MaxVersion + 1;
                    // Update the lookup so subsequent files in this same run get
                    // the correct version (shouldn't happen, but be safe).
                    versionInfo[staged.Source.FullPath] = new FileVersionInfo(
                        version, staged.Source.SizeBytes, staged.Source.LastWriteUtc, false, false);

                    var fileRecord = await _catalog.CreateFileRecordAsync(new FileRecord
                    {
                        DiscId = discRecord.Id,
                        SourcePath = staged.Source.FullPath,
                        DiscPath = staged.StagedPath,
                        SizeBytes = staged.Source.SizeBytes,
                        Hash = hash,
                        IsZipped = staged.IsZipped,
                        IsSplit = staged.IsSplit,
                        Version = version,
                        SourceLastWriteUtc = staged.Source.LastWriteUtc,
                        BackedUpUtc = DateTime.UtcNow,
                    }, ct);

                    // Record chunks for split files.
                    if (staged.IsSplit && staged.Chunks is not null)
                    {
                        foreach (var chunk in staged.Chunks)
                        {
                            chunk.FileRecordId = fileRecord.Id;
                            chunk.DiscId = discRecord.Id;
                            await _catalog.CreateFileChunkAsync(chunk, ct);
                        }
                    }
                }

                // Commit the catalog updates.
                tx.Complete();
                tx.Dispose();

                discsWritten++;
                totalBytesWritten += allocation.TotalBytes;
            }
            finally
            {
                // Clean up staging directory.
                try { Directory.Delete(stagingDir, true); } catch { }
            }
        }

        } // end try (coordinator)
        finally
        {
            coordinator?.Dispose();
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

                await _burner.BurnAsync(recorderId, stagingDir, burnOptions, burnProgress, ct);

                // Record the new disc and files in the catalog.
                using var tx = await _catalog.BeginTransactionAsync(ct);

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

    public async Task ReplaceDiscAsync(int badDiscId, string recorderId, CancellationToken ct = default)
    {
        // 1. Mark the disc as bad.
        await _catalog.MarkDiscAsBadAsync(badDiscId, ct);

        // 2. Get the bad disc and its files.
        var badDisc = await _catalog.GetDiscAsync(badDiscId, ct)
            ?? throw new InvalidOperationException($"Disc {badDiscId} not found.");

        var filesToReplace = await _catalog.GetFilesForReplacementAsync(badDiscId, ct);
        if (filesToReplace.Count == 0)
            return;

        // 3. Stage files from their original source paths.
        string stagingDir = Path.Combine(
            Path.GetTempPath(), "LithicBackup", $"replace-disc-{badDiscId}");
        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        try
        {
            var stagedFiles = new List<(FileRecord Original, string StagedPath)>();
            foreach (var fileRecord in filesToReplace)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(fileRecord.SourcePath))
                    continue; // Source file no longer available.

                string relativePath = GetRelativeStagingPath(fileRecord.SourcePath);
                string destPath = Path.Combine(stagingDir, relativePath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (destDir is not null)
                    Directory.CreateDirectory(destDir);

                File.Copy(fileRecord.SourcePath, destPath, overwrite: true);
                stagedFiles.Add((fileRecord, relativePath));
            }

            if (stagedFiles.Count == 0)
                return;

            // 4. Burn to a new disc.
            var burnOptions = new BurnOptions
            {
                FilesystemType = badDisc.FilesystemType,
                Multisession = false,
                VerifyAfterBurn = true,
            };

            await _burner.BurnAsync(recorderId, stagingDir, burnOptions, progress: null, ct);

            // 5. Update the catalog: create new disc, new file records.
            using var tx = await _catalog.BeginTransactionAsync(ct);

            var newDisc = await _catalog.CreateDiscAsync(new DiscRecord
            {
                BackupSetId = badDisc.BackupSetId,
                Label = $"{badDisc.Label}-Replacement",
                SequenceNumber = badDisc.SequenceNumber,
                MediaType = (await _burner.GetMediaInfoAsync(recorderId, ct)).MediaType,
                FilesystemType = burnOptions.FilesystemType,
                Capacity = badDisc.Capacity,
                BytesUsed = stagedFiles.Sum(sf =>
                    new FileInfo(Path.Combine(stagingDir, sf.StagedPath)).Length),
                IsMultisession = false,
                Status = BurnSessionStatus.Completed,
                CreatedUtc = DateTime.UtcNow,
                LastWrittenUtc = DateTime.UtcNow,
            }, ct);

            foreach (var (original, stagedPath) in stagedFiles)
            {
                string hash = await ComputeFileHashAsync(original.SourcePath, ct);

                await _catalog.CreateFileRecordAsync(new FileRecord
                {
                    DiscId = newDisc.Id,
                    SourcePath = original.SourcePath,
                    DiscPath = stagedPath,
                    SizeBytes = original.SizeBytes,
                    Hash = hash,
                    Version = original.Version,
                    SourceLastWriteUtc = original.SourceLastWriteUtc,
                    BackedUpUtc = DateTime.UtcNow,
                }, ct);
            }

            tx.Complete();
            tx.Dispose();
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
    /// Intermediate record of a staged file, tracking zip/split status.
    /// </summary>
    private class StagedFileInfo
    {
        public required ScannedFile Source { get; init; }
        public required string StagedPath { get; init; }
        public required bool IsZipped { get; init; }
        public required bool IsSplit { get; init; }
        public IReadOnlyList<FileChunk>? Chunks { get; init; }
        public long StagedSizeBytes { get; init; }
    }
}
