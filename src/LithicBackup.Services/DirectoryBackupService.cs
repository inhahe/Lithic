using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

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
///   targetDir/_filestore/{hash}.dat                  -- shared file store (file-level dedup)
/// </summary>
public class DirectoryBackupService
{
    private readonly ICatalogRepository _catalog;
    private readonly IFileScanner _scanner;
    private readonly VersionRetentionService _retention;
    private readonly IDeduplicationEngine? _dedup;

    public DirectoryBackupService(
        ICatalogRepository catalog,
        IFileScanner scanner,
        VersionRetentionService retention,
        IDeduplicationEngine? dedup = null)
    {
        _catalog = catalog;
        _scanner = scanner;
        _retention = retention;
        _dedup = dedup;
    }

    /// <summary>
    /// Execute a directory backup: scan sources, compute diff, copy files
    /// with versioned history, update catalog, and apply retention.
    /// </summary>
    public async Task<BackupResult> ExecuteAsync(
        BackupJob job,
        string targetDirectory,
        IReadOnlyList<VersionRetentionTier>? retentionTiers,
        IProgress<BackupProgress>? progress,
        CancellationToken ct,
        ManualResetEventSlim? pauseEvent = null,
        FailureCallback? onFailure = null)
    {
        // 1. Scan sources (global + per-directory exclusions).
        var isExcluded = GlobMatcher.CreateCombinedFilter(job.ExcludedExtensions, job.Sources);
        var scanned = await _scanner.ScanAsync(job.Sources, progress: null, ct, isExcluded);

        // 2. Compute diff.
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

        // 4. Build version lookup and storage format from existing catalog.
        // Uses a lightweight SQL aggregate query that returns one row per
        // unique source path instead of loading every FileRecord into memory.
        var versionInfo = job.BackupSetId.HasValue
            ? await _catalog.GetLatestVersionInfoAsync(backupSetId, ct)
            : new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);

        // Build a set of changed paths for quick lookup.
        var changedPaths = new HashSet<string>(
            diff.ChangedFiles.Select(f => f.FullPath),
            StringComparer.OrdinalIgnoreCase);

        // Build a lookup of paths where version history is disabled.
        var (noVersionFiles, noVersionDirPrefixes, noVersionGlobs) =
            SourceSelection.CollectNoVersionPaths(job.Sources);
        var noVersionGlobFilter = noVersionGlobs.Count > 0
            ? GlobMatcher.CreateFilter(noVersionGlobs)
            : null;

        // 5. Create a single "virtual" DiscRecord for this backup run.
        Directory.CreateDirectory(targetDirectory);

        // Ensure store directories exist when dedup might be used.
        string blockStoreDir = Path.Combine(targetDirectory, "_blocks");
        if (job.EnableDeduplication && _dedup is not null)
            Directory.CreateDirectory(blockStoreDir);

        string fileStoreDir = Path.Combine(targetDirectory, "_filestore");
        if (job.EnableFileDeduplication)
            Directory.CreateDirectory(fileStoreDir);

        // 6. Copy files.
        var failedFiles = new List<FailedFile>();
        long totalBytes = filesToBackup.Sum(f => f.SizeBytes);
        long bytesWritten = 0;
        bool permanentSkipAll = false;

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
        var tx = await _catalog.BeginTransactionAsync(ct);

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

        // Per-file progress: only report intermediate progress for files >= 1 MB,
        // and throttle reports to every ~1 MB to avoid flooding the UI thread.
        const long PerFileProgressThreshold = 1024 * 1024;      // 1 MB
        const long PerFileReportInterval    = 1024 * 1024;      // 1 MB

        for (int i = 0; i < filesToBackup.Count; i++)
        {
            pauseEvent?.Wait(ct);
            ct.ThrowIfCancellationRequested();

            var file = filesToBackup[i];
            bool isChanged = changedPaths.Contains(file.FullPath);

            progress?.Report(new BackupProgress
            {
                CurrentDisc = 1,
                TotalDiscs = 1,
                CurrentFile = file.FullPath,
                BytesWrittenTotal = bytesWritten,
                BytesTotalAll = totalBytes,
                OverallPercentage = totalBytes > 0
                    ? (double)bytesWritten / totalBytes * 100
                    : 0,
                CurrentFileBytesWritten = 0,
                CurrentFileTotalBytes = file.SizeBytes,
            });

            // Before starting a large file, flush pending records so small
            // files already copied are safe if the large copy is cancelled.
            if (file.SizeBytes >= CommitLargeFileThreshold && batchCount > 0)
            {
                discRecord.BytesUsed = bytesWritten;
                discRecord.LastWrittenUtc = DateTime.UtcNow;
                await _catalog.UpdateDiscAsync(discRecord, ct);
                tx.Complete();
                tx.Dispose();
                tx = await _catalog.BeginTransactionAsync(ct);
                batchCount = 0;
                commitTimer.Restart();
            }

            // Determine version number from the lightweight lookup
            // BEFORE entering the retry loop so retries don't increment
            // the version number.
            int version = 1;
            bool hasExistingInfo = versionInfo.TryGetValue(file.FullPath, out var existingInfo);
            if (hasExistingInfo)
                version = existingInfo.MaxVersion + 1;

            bool fileRetrying = true;
            while (fileRetrying)
            {
                fileRetrying = false;

            try
            {
                // Compute hash up front — needed for file-level dedup checks
                // and the catalog record regardless.
                string hash = await ComputeFileHashAsync(file.FullPath, ct);

                // Decide storage format. Priority:
                //   1. File-level dedup (cheap whole-file hash match)
                //   2. Block-level dedup (more expensive, catches partial similarity)
                //   3. Plain copy
                bool isDeduped = false;
                bool isFileRef = false;
                DeduplicationRecipe? recipe = null;

                // Build a throttled per-file progress callback for large files.
                // Captures the current bytesWritten so intermediate reports show
                // accurate overall progress too.
                Action<long>? fileProgress = null;
                if (file.SizeBytes >= PerFileProgressThreshold && progress is not null)
                {
                    long capturedBytesWritten = bytesWritten;
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
                                BytesWrittenTotal = capturedBytesWritten + bytesCopied,
                                BytesTotalAll = totalBytes,
                                OverallPercentage = totalBytes > 0
                                    ? (double)(capturedBytesWritten + bytesCopied) / totalBytes * 100
                                    : 0,
                                CurrentFileBytesWritten = bytesCopied,
                                CurrentFileTotalBytes = file.SizeBytes,
                            });
                        }
                    };
                }

                if (job.EnableFileDeduplication)
                {
                    // File-level dedup: store the canonical copy in
                    // _filestore/{hash}.dat and write a .fileref pointer.
                    string fileStorePath = Path.Combine(fileStoreDir, hash + ".dat");
                    if (!File.Exists(fileStorePath))
                    {
                        // First occurrence of this content — copy to the store.
                        await CopyFileAsync(file.FullPath, fileStorePath, ct, fileProgress, pauseEvent);
                    }
                    isFileRef = true;
                }
                else if (job.EnableDeduplication && _dedup is not null)
                {
                    try
                    {
                        recipe = await _dedup.DeduplicateAsync(
                            file.FullPath, job.DeduplicationBlockSize, ct);

                        // Only use dedup format if we actually saved space
                        // (some blocks were shared with other files).
                        isDeduped = recipe.BytesSaved > 0;
                    }
                    catch
                    {
                        // Dedup failure is non-fatal; fall back to plain copy.
                        recipe = null;
                        isDeduped = false;
                    }
                }

                // Check if this file should keep version history.
                bool keepVersions = !noVersionFiles.Contains(file.FullPath)
                    && !noVersionDirPrefixes.Any(p =>
                        file.FullPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    && !(noVersionGlobFilter?.Invoke(file.FullPath) ?? false);

                // Move existing current file to _prev if this is a changed file
                // and version history is enabled.
                if (isChanged && keepVersions && hasExistingInfo)
                {
                    bool oldDeduped = existingInfo.IsDeduped;
                    bool oldFileRef = existingInfo.IsFileRef;
                    string oldCurrentPath = GetCurrentPath(
                        targetDirectory, file.FullPath,
                        oldDeduped, oldFileRef);

                    if (File.Exists(oldCurrentPath))
                    {
                        int oldVersion = version - 1;
                        string prevPath = GetPrevPath(
                            targetDirectory, file.FullPath, oldVersion,
                            oldDeduped, oldFileRef);

                        string? prevDir = Path.GetDirectoryName(prevPath);
                        if (prevDir is not null)
                            Directory.CreateDirectory(prevDir);

                        File.Move(oldCurrentPath, prevPath, overwrite: true);

                        // Load the specific old record on-demand (not pre-loaded)
                        // to update its DiscPath to the new _prev location.
                        if (job.BackupSetId.HasValue)
                        {
                            var oldRecord = await _catalog.GetFileRecordByPathAndVersionAsync(
                                job.BackupSetId.Value, file.FullPath, oldVersion, ct);
                            if (oldRecord is not null)
                            {
                                oldRecord.DiscPath = GetPrevDiscPath(
                                    file.FullPath, oldVersion,
                                    oldDeduped, oldFileRef);
                                await _catalog.UpdateFileRecordAsync(oldRecord, ct);
                            }
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
                    // Write a .fileref manifest pointing to _filestore/{hash}.dat.
                    var manifest = new FileRefManifest
                    {
                        OriginalName = Path.GetFileName(file.FullPath),
                        OriginalSize = file.SizeBytes,
                        Hash = hash,
                    };
                    string json = JsonSerializer.Serialize(manifest, _jsonOptions);
                    await File.WriteAllTextAsync(currentPath, json, ct);
                }
                else if (isDeduped && recipe is not null)
                {
                    // Write new blocks to the block store.
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
                else
                {
                    // Plain copy.
                    await CopyFileAsync(file.FullPath, currentPath, ct, fileProgress, pauseEvent);
                }

                // Update version info so a later file in this same run
                // picks up the correct format for prev-moves.
                versionInfo[file.FullPath] = new FileVersionInfo(
                    version, file.SizeBytes, file.LastWriteUtc, isDeduped, isFileRef);

                // Create catalog record.
                await _catalog.CreateFileRecordAsync(new FileRecord
                {
                    DiscId = discRecord.Id,
                    SourcePath = file.FullPath,
                    DiscPath = GetCurrentDiscPath(file.FullPath, isDeduped, isFileRef),
                    SizeBytes = file.SizeBytes,
                    Hash = hash,
                    IsZipped = false,
                    IsSplit = false,
                    IsDeduped = isDeduped,
                    IsFileRef = isFileRef,
                    Version = version,
                    SourceLastWriteUtc = file.LastWriteUtc,
                    BackedUpUtc = DateTime.UtcNow,
                }, ct);

                bytesWritten += file.SizeBytes;
                batchCount++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // If the user previously chose "Skip All", skip without
                // prompting again.
                if (permanentSkipAll || onFailure is null)
                {
                    failedFiles.Add(new FailedFile
                    {
                        Path = file.FullPath,
                        Error = ex.Message,
                        ActionTaken = BurnFailureAction.Skip,
                    });
                    continue;
                }

                // Ask the user what to do.
                var decision = await onFailure(file.FullPath, ex.Message);

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
                            Error = ex.Message,
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
                            Error = ex.Message,
                            ActionTaken = decision.Action,
                        });
                        break;
                }

                // If ApplyToAll was checked, lock in the decision for
                // all subsequent failures.
                if (decision.ApplyToAllOnDisc
                    && decision.Action == BurnFailureAction.Skip)
                {
                    permanentSkipAll = true;
                }
            }
            } // while (fileRetrying)

            // Commit when: batch is full, a large file just finished, or
            // enough wall-clock time has elapsed since the last commit.
            if (batchCount >= CommitBatchSize
                || file.SizeBytes >= CommitLargeFileThreshold
                || commitTimer.Elapsed.TotalSeconds >= CommitIntervalSeconds)
            {
                discRecord.BytesUsed = bytesWritten;
                discRecord.LastWrittenUtc = DateTime.UtcNow;
                await _catalog.UpdateDiscAsync(discRecord, ct);
                tx.Complete();
                tx.Dispose();
                tx = await _catalog.BeginTransactionAsync(ct);
                batchCount = 0;
                commitTimer.Restart();
            }
        }

        // Final batch: update disc record and commit remaining records.
        discRecord.BytesUsed = bytesWritten;
        discRecord.Status = BurnSessionStatus.Completed;
        discRecord.LastWrittenUtc = DateTime.UtcNow;
        await _catalog.UpdateDiscAsync(discRecord, ct);

        tx.Complete();

        } // try (batch transaction)
        finally
        {
            tx.Dispose();
        }

        // Update the backup set's last backup timestamp.
        var backupSet = await _catalog.GetBackupSetAsync(backupSetId, ct);
        if (backupSet is not null)
        {
            backupSet.LastBackupUtc = DateTime.UtcNow;
            await _catalog.UpdateBackupSetAsync(backupSet, ct);
        }

        // 7. Apply retention: physically delete old version files.
        if (retentionTiers is not null && retentionTiers.Count > 0)
        {
            try
            {
                var toDelete = await _retention.ComputeRetentionAsync(backupSetId, retentionTiers, ct);

                foreach (var fileRecord in toDelete)
                {
                    ct.ThrowIfCancellationRequested();

                    // Physically delete the .v{N} file (with .dedup/.fileref/no suffix)
                    // from the _prev directory.
                    string prevPath = GetPrevPath(
                        targetDirectory, fileRecord.SourcePath,
                        fileRecord.Version, fileRecord.IsDeduped, fileRecord.IsFileRef);
                    if (File.Exists(prevPath))
                    {
                        File.Delete(prevPath);
                    }

                    // Note: _filestore/{hash}.dat entries are NOT deleted here
                    // because other .fileref files may still reference them.
                    // A separate maintenance/GC task could clean up unreferenced
                    // entries by cross-checking against all non-deleted IsFileRef
                    // records in the catalog.

                    // Mark as deleted in catalog.
                    fileRecord.IsDeleted = true;
                    await _catalog.UpdateFileRecordAsync(fileRecord, ct);
                }
            }
            catch
            {
                // Retention failure is non-fatal; the backup itself succeeded.
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
        BackupJob job, CancellationToken ct)
    {
        var isExcluded = GlobMatcher.CreateCombinedFilter(job.ExcludedExtensions, job.Sources);
        var scanned = await _scanner.ScanAsync(job.Sources, progress: null, ct, isExcluded);

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

    // -------------------------------------------------------------------
    // File I/O helpers
    // -------------------------------------------------------------------

    /// <summary>Async file copy with large buffer and optional per-file progress.</summary>
    /// <param name="onProgress">
    /// Called with bytes copied so far. Caller is responsible for throttling
    /// how often this translates to UI updates.
    /// </param>
    private static async Task CopyFileAsync(
        string sourcePath, string destPath, CancellationToken ct,
        Action<long>? onProgress = null,
        ManualResetEventSlim? pauseEvent = null)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
            Directory.CreateDirectory(destDir);

        await using var srcStream = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        await using var dstStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);

        if (onProgress is null && pauseEvent is null)
        {
            await srcStream.CopyToAsync(dstStream, ct);
            return;
        }

        // Chunked copy with progress callback and pause support.
        var buffer = new byte[81920];
        long totalCopied = 0;
        int read;
        while ((read = await srcStream.ReadAsync(buffer, ct)) > 0)
        {
            pauseEvent?.Wait(ct);
            await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalCopied += read;
            onProgress?.Invoke(totalCopied);
        }
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
            FileShare.Read, bufferSize: 81920, useAsync: true);

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

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };
}
