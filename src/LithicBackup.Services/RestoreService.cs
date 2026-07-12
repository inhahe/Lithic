using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Implementation of <see cref="IRestoreService"/> that reads files from
/// backup discs (plain, zipped, or split) and writes them to a destination.
/// </summary>
public class RestoreService : IRestoreService
{
    private readonly ICatalogRepository _catalog;

    /// <summary>
    /// Callback invoked when a disc needs to be inserted. Receives the disc
    /// label and returns the root path of the mounted disc (e.g., "D:\"),
    /// or null to cancel the restore.
    /// </summary>
    public Func<string, Task<string?>>? DiscInsertCallback { get; set; }

    public RestoreService(ICatalogRepository catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// List all non-deleted files in a backup set with their disc locations.
    /// </summary>
    public async Task<IReadOnlyList<RestorableFile>> GetRestorableFilesAsync(
        int backupSetId, CancellationToken ct = default)
    {
        var discs = await _catalog.GetDiscsForBackupSetAsync(backupSetId, ct);
        var discLookup = discs.ToDictionary(d => d.Id);
        var results = new List<RestorableFile>();

        foreach (var disc in discs)
        {
            ct.ThrowIfCancellationRequested();

            var files = await _catalog.GetFilesOnDiscAsync(disc.Id, ct);
            foreach (var file in files)
            {
                if (file.IsDeleted)
                    continue;

                var chunks = file.IsSplit
                    ? await _catalog.GetChunksForFileAsync(disc.Id, file.Id, ct)
                    : [];

                results.Add(new RestorableFile
                {
                    Record = file,
                    Disc = disc,
                    Chunks = chunks,
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Restore specific files, routing each to a per-drive destination.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        IReadOnlyList<RestorableFile> files,
        IReadOnlyDictionary<string, string> driveDestinations,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Split files may have chunks on several discs, so they can't be restored
        // within a single disc's pass; handle them separately below. Everything
        // else is grouped by disc for efficient disc-at-a-time processing.
        var splitFiles = files.Where(f => f.Record.IsSplit).ToList();
        var byDisc = files
            .Where(f => !f.Record.IsSplit)
            .GroupBy(f => f.Disc.Id)
            .OrderBy(g => g.Key)
            .ToList();

        int totalFiles = files.Count;
        long totalBytes = files.Sum(f => f.Record.SizeBytes);
        int filesCompleted = 0;
        long bytesCompleted = 0;
        var errors = new List<string>();

        foreach (var discGroup in byDisc)
        {
            ct.ThrowIfCancellationRequested();

            var disc = discGroup.First().Disc;

            // Notify that this disc is needed.
            progress?.Report(new RestoreProgress
            {
                CurrentFile = $"Please insert disc: {disc.Label}",
                FilesCompleted = filesCompleted,
                TotalFiles = totalFiles,
                BytesCompleted = bytesCompleted,
                TotalBytes = totalBytes,
                Percentage = totalBytes > 0 ? (double)bytesCompleted / totalBytes * 100 : 0,
                RequiredDiscId = disc.Id,
                RequiredDiscLabel = disc.Label,
            });

            // Wait for disc insertion via callback.
            string? discRoot = null;
            if (DiscInsertCallback is not null)
            {
                discRoot = await DiscInsertCallback(disc.Label);
                if (discRoot is null)
                {
                    errors.Add($"Disc '{disc.Label}' was not inserted. Skipping {discGroup.Count()} files.");
                    continue;
                }
            }
            else
            {
                // No callback — assume disc is already mounted; try common drive letters.
                discRoot = FindDiscRoot(disc.Label);
                if (discRoot is null)
                {
                    // Directory-mode backups store their data in a folder whose
                    // path is the disc label; optical backups use a volume
                    // label. Tailor the message so a missing backup folder
                    // isn't reported as a missing disc.
                    errors.Add(LooksLikeDirectoryPath(disc.Label)
                        ? $"Backup folder not found: '{disc.Label}'. The backup data may have been moved or deleted. Skipping {discGroup.Count()} files."
                        : $"Could not find mounted disc '{disc.Label}'. Skipping {discGroup.Count()} files.");
                    continue;
                }
            }

            // Process each file on this disc.
            foreach (var restorableFile in discGroup)
            {
                ct.ThrowIfCancellationRequested();

                var record = restorableFile.Record;

                progress?.Report(new RestoreProgress
                {
                    CurrentFile = record.SourcePath,
                    FilesCompleted = filesCompleted,
                    TotalFiles = totalFiles,
                    BytesCompleted = bytesCompleted,
                    TotalBytes = totalBytes,
                    Percentage = totalBytes > 0 ? (double)bytesCompleted / totalBytes * 100 : 0,
                });

                try
                {
                    // Route to the destination chosen for this file's source
                    // drive, recreating its path below the drive root.
                    var (driveKey, relative) = SplitSourcePath(record.SourcePath);
                    if (!driveDestinations.TryGetValue(driveKey, out var destRoot)
                        || string.IsNullOrWhiteSpace(destRoot))
                    {
                        errors.Add(
                            $"No destination set for drive '{driveKey}:' — skipping '{record.SourcePath}'.");
                        continue;
                    }

                    string destPath = Path.Combine(destRoot, relative);
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (destDir is not null)
                        Directory.CreateDirectory(destDir);

                    if (record.IsZipped)
                    {
                        // Unzip the file.
                        await UnzipFileAsync(
                            Path.Combine(discRoot, record.DiscPath), destPath, ct);
                    }
                    else if (record.IsFileRef)
                    {
                        // Restore from file-level dedup: a .fileref stores no
                        // bytes. Its content lives as a plain copy elsewhere in
                        // the tree; resolve the hash to that copy via the catalog.
                        await RestoreFileRefAsync(
                            disc.BackupSetId, record.Hash, discRoot, destPath, ct);
                    }
                    else if (record.IsDeduped)
                    {
                        // Reassemble from block-level dedup manifest + block store.
                        await ReassembleDedupFileAsync(
                            Path.Combine(discRoot, record.DiscPath),
                            Path.Combine(discRoot, "_blocks"),
                            destPath, ct);
                    }
                    else
                    {
                        // Plain file copy.
                        string sourcePath = Path.Combine(discRoot, record.DiscPath);
                        File.Copy(sourcePath, destPath, overwrite: true);
                    }

                    filesCompleted++;
                    bytesCompleted += record.SizeBytes;
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to restore '{record.SourcePath}': {ex.Message}");
                }
            }
        }

        // --- Split files (chunks possibly spanning multiple discs) ---
        // Each chunk carries its absolute byte offset, so chunks can be written in
        // any order into the destination file. We process disc-by-disc (mounting
        // each disc once) and seek to each chunk's offset, which lets a single
        // large file be reassembled from pieces on different physical discs.
        if (splitFiles.Count > 0)
        {
            var (splitDone, splitBytes) = await RestoreSplitFilesAsync(
                splitFiles, driveDestinations, errors,
                filesCompleted, totalFiles, bytesCompleted, totalBytes, progress, ct);
            filesCompleted += splitDone;
            bytesCompleted += splitBytes;
        }

        return new RestoreResult
        {
            Success = errors.Count == 0,
            FilesRestored = filesCompleted,
            BytesRestored = bytesCompleted,
            Errors = errors,
        };
    }

    /// <summary>
    /// Integrity-test one already-burned disc against the catalog. See
    /// <see cref="IRestoreService.VerifyDiscAsync"/>.
    /// </summary>
    public async Task<DiscVerifyResult> VerifyDiscAsync(
        int discId,
        string discRoot,
        bool verifyContents = false,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        var disc = await _catalog.GetDiscAsync(discId, ct)
            ?? throw new InvalidOperationException($"Disc {discId} not found in the catalog.");

        if (string.IsNullOrWhiteSpace(discRoot) || !Directory.Exists(discRoot))
            throw new DirectoryNotFoundException(
                $"The disc is not readable at '{discRoot}'. Insert the disc and try again.");

        string blockStore = Path.Combine(discRoot, "_blocks");

        var files = (await _catalog.GetFilesOnDiscAsync(discId, ct))
            .Where(f => !f.IsDeleted)
            .ToList();

        int total = files.Count;
        int checkedCount = 0;
        long bytesChecked = 0;
        long totalBytes = files.Sum(f => f.SizeBytes);
        var issues = new List<DiscFileIssue>();

        foreach (var record in files)
        {
            ct.ThrowIfCancellationRequested();
            checkedCount++;
            bytesChecked += record.SizeBytes;

            progress?.Report(new RestoreProgress
            {
                CurrentFile = record.SourcePath,
                FilesCompleted = checkedCount,
                TotalFiles = total,
                BytesCompleted = bytesChecked,
                TotalBytes = totalBytes,
                Percentage = totalBytes > 0 ? (double)bytesChecked / totalBytes * 100 : 0,
            });

            IReadOnlyList<FileChunk> chunks = record.IsSplit
                ? await _catalog.GetChunksForFileAsync(discId, record.Id, ct)
                : [];

            var issue = await CheckDiscFileAsync(
                disc.BackupSetId, record, chunks, discRoot, blockStore, verifyContents, ct);
            if (issue is not null)
                issues.Add(issue);
        }

        var failedIds = issues
            .Where(i => i.Kind != DiscFileIssueKind.UnresolvedReference)
            .Select(i => i.FileRecordId)
            .Distinct()
            .ToList();

        return new DiscVerifyResult
        {
            FilesChecked = checkedCount,
            BytesChecked = bytesChecked,
            ContentsVerified = verifyContents,
            Cancelled = ct.IsCancellationRequested,
            Issues = issues,
            FailedFileRecordIds = failedIds,
        };
    }

    /// <summary>
    /// Check one catalog file record against its stored representation on the
    /// mounted disc. Returns a <see cref="DiscFileIssue"/> describing the first
    /// problem found, or <c>null</c> when the file verifies cleanly.
    /// </summary>
    private async Task<DiscFileIssue?> CheckDiscFileAsync(
        int backupSetId,
        FileRecord record,
        IReadOnlyList<FileChunk> chunks,
        string discRoot,
        string blockStore,
        bool verifyContents,
        CancellationToken ct)
    {
        DiscFileIssue Issue(DiscFileIssueKind kind, string detail) => new()
        {
            FileRecordId = record.Id,
            SourcePath = record.SourcePath,
            DiscPath = record.DiscPath,
            SizeBytes = record.SizeBytes,
            Kind = kind,
            Detail = detail,
        };

        try
        {
            // --- Split across chunk files -------------------------------------
            if (record.IsSplit)
            {
                if (chunks.Count == 0)
                    return Issue(DiscFileIssueKind.Missing, "No chunk records for split file.");

                long chunkTotal = 0;
                foreach (var chunk in chunks.OrderBy(c => c.Sequence))
                {
                    string p = Path.Combine(discRoot, chunk.DiscFilename);
                    if (!File.Exists(p))
                        return Issue(DiscFileIssueKind.Missing, $"Missing chunk: {chunk.DiscFilename}");
                    chunkTotal += new FileInfo(p).Length;
                }
                if (chunkTotal != record.SizeBytes)
                    return Issue(DiscFileIssueKind.SizeMismatch,
                        $"Chunks total {chunkTotal:N0} B, expected {record.SizeBytes:N0} B.");

                if (verifyContents && !HashEquals(await HashSplitAsync(chunks, discRoot, ct), record.Hash))
                    return Issue(DiscFileIssueKind.ContentMismatch,
                        "Reassembled content does not match the catalog hash.");
                return null;
            }

            // --- File-level dedup reference (.fileref) ------------------------
            if (record.IsFileRef)
            {
                bool ptrExists = File.Exists(Path.Combine(discRoot, record.DiscPath));

                var candidates = await _catalog.GetActiveRecordsByHashAsync(backupSetId, record.Hash, ct);
                var plain = candidates.FirstOrDefault(r =>
                    !r.IsFileRef && !r.IsDeduped
                    && File.Exists(Path.Combine(discRoot, r.DiscPath)));

                if (plain is null)
                    return Issue(DiscFileIssueKind.UnresolvedReference,
                        ptrExists
                            ? "Backing copy is on another disc; not verified from this disc."
                            : "Reference pointer missing and backing copy is on another disc.");

                string plainPath = Path.Combine(discRoot, plain.DiscPath);
                long len = new FileInfo(plainPath).Length;
                if (len != record.SizeBytes)
                    return Issue(DiscFileIssueKind.SizeMismatch,
                        $"Backing copy {len:N0} B, expected {record.SizeBytes:N0} B.");

                if (verifyContents && !HashEquals(await HashFileAsync(plainPath, ct), record.Hash))
                    return Issue(DiscFileIssueKind.ContentMismatch,
                        "Backing copy content does not match the catalog hash.");
                return null;
            }

            // --- Block-level dedup (.dedup manifest + _blocks) ----------------
            if (record.IsDeduped)
            {
                string manifestPath = Path.Combine(discRoot, record.DiscPath);
                if (!File.Exists(manifestPath))
                    return Issue(DiscFileIssueKind.Missing, "Missing dedup manifest.");

                DedupManifest? manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<DedupManifest>(
                        await File.ReadAllTextAsync(manifestPath, ct));
                }
                catch
                {
                    return Issue(DiscFileIssueKind.Unreadable, "Dedup manifest is unreadable.");
                }
                if (manifest is null)
                    return Issue(DiscFileIssueKind.Unreadable, "Dedup manifest is empty/invalid.");

                foreach (string bh in manifest.BlockHashes)
                {
                    if (!File.Exists(Path.Combine(blockStore, bh + ".blk")))
                        return Issue(DiscFileIssueKind.Missing, $"Missing block: {bh}.blk");
                }

                if (verifyContents && !HashEquals(await HashDedupAsync(manifest, blockStore, ct), record.Hash))
                    return Issue(DiscFileIssueKind.ContentMismatch,
                        "Reassembled content does not match the catalog hash.");
                return null;
            }

            // --- Zipped -------------------------------------------------------
            if (record.IsZipped)
            {
                string zipPath = Path.Combine(discRoot, record.DiscPath);
                if (!File.Exists(zipPath))
                    return Issue(DiscFileIssueKind.Missing, "Missing zip archive.");

                if (verifyContents)
                {
                    string h;
                    try { h = await HashZipEntryAsync(zipPath, ct); }
                    catch { return Issue(DiscFileIssueKind.Unreadable, "Zip archive could not be read."); }
                    if (!HashEquals(h, record.Hash))
                        return Issue(DiscFileIssueKind.ContentMismatch,
                            "Unzipped content does not match the catalog hash.");
                }
                return null;
            }

            // --- Plain copy ---------------------------------------------------
            string plainFile = Path.Combine(discRoot, record.DiscPath);
            if (!File.Exists(plainFile))
                return Issue(DiscFileIssueKind.Missing, "Missing on disc.");

            long plainLen = new FileInfo(plainFile).Length;
            if (plainLen != record.SizeBytes)
                return Issue(DiscFileIssueKind.SizeMismatch,
                    $"Disc copy {plainLen:N0} B, expected {record.SizeBytes:N0} B.");

            if (verifyContents && !HashEquals(await HashFileAsync(plainFile, ct), record.Hash))
                return Issue(DiscFileIssueKind.ContentMismatch,
                    "Disc copy content does not match the catalog hash.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            return Issue(DiscFileIssueKind.Unreadable, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Issue(DiscFileIssueKind.Unreadable, ex.Message);
        }
    }

    private static bool HashEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var s = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }

    private static async Task<string> HashStreamsAsync(
        IEnumerable<string> filePaths, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        foreach (var path in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            await using var s = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
            int read;
            while ((read = await s.ReadAsync(buffer, ct)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Task<string> HashSplitAsync(
        IReadOnlyList<FileChunk> chunks, string discRoot, CancellationToken ct) =>
        HashStreamsAsync(
            chunks.OrderBy(c => c.Sequence).Select(c => Path.Combine(discRoot, c.DiscFilename)), ct);

    private static Task<string> HashDedupAsync(
        DedupManifest manifest, string blockStore, CancellationToken ct) =>
        HashStreamsAsync(
            manifest.BlockHashes.Select(bh => Path.Combine(blockStore, bh + ".blk")), ct);

    private static async Task<string> HashZipEntryAsync(string zipPath, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidOperationException("Zip archive is empty.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        await using var s = archive.Entries[0].Open();
        int read;
        while ((read = await s.ReadAsync(buffer, ct)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Reassemble every split file from its chunks, which may live on different
    /// discs. Chunks are grouped by disc so each disc is mounted at most once; each
    /// chunk is written at its recorded byte offset, so cross-disc files reassemble
    /// correctly regardless of the order discs are inserted. Returns the number of
    /// split files fully restored and their total bytes.
    /// </summary>
    private async Task<(int Files, long Bytes)> RestoreSplitFilesAsync(
        IReadOnlyList<RestorableFile> splitFiles,
        IReadOnlyDictionary<string, string> driveDestinations,
        List<string> errors,
        int filesCompletedSoFar,
        int totalFiles,
        long bytesCompletedSoFar,
        long totalBytes,
        IProgress<RestoreProgress>? progress,
        CancellationToken ct)
    {
        // Resolve every chunk's disc label so we can request it by insertion.
        var setId = splitFiles[0].Disc.BackupSetId;
        var discById = (await _catalog.GetDiscsForBackupSetAsync(setId, ct))
            .ToDictionary(d => d.Id);

        // Map each split file to its destination path, truncating/pre-sizing the
        // destination so offset writes land correctly with no stale trailing data.
        var destByRecordId = new Dictionary<long, string>();
        var failedRecordIds = new HashSet<long>();
        // Chunks grouped by the disc they live on: discId -> (destPath, chunk).
        var chunksByDisc = new Dictionary<int, List<(string DestPath, FileChunk Chunk)>>();

        foreach (var sf in splitFiles)
        {
            var record = sf.Record;
            var (driveKey, relative) = SplitSourcePath(record.SourcePath);
            if (!driveDestinations.TryGetValue(driveKey, out var destRoot)
                || string.IsNullOrWhiteSpace(destRoot))
            {
                errors.Add($"No destination set for drive '{driveKey}:' — skipping '{record.SourcePath}'.");
                failedRecordIds.Add(record.Id);
                continue;
            }

            string destPath = Path.Combine(destRoot, relative);
            try
            {
                string? destDir = Path.GetDirectoryName(destPath);
                if (destDir is not null)
                    Directory.CreateDirectory(destDir);
                await using var fs = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                fs.SetLength(record.SizeBytes);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to prepare '{record.SourcePath}': {ex.Message}");
                failedRecordIds.Add(record.Id);
                continue;
            }

            destByRecordId[record.Id] = destPath;
            foreach (var chunk in sf.Chunks)
            {
                if (!chunksByDisc.TryGetValue(chunk.DiscId, out var list))
                    chunksByDisc[chunk.DiscId] = list = new List<(string, FileChunk)>();
                list.Add((destPath, chunk));
            }
        }

        // Track which record each chunk belongs to so we can null out counts for
        // files that lose any chunk.
        var recordIdByDest = destByRecordId.ToDictionary(kv => kv.Value, kv => kv.Key);

        foreach (var (discId, placements) in chunksByDisc.OrderBy(kv => kv.Key))
        {
            ct.ThrowIfCancellationRequested();

            if (!discById.TryGetValue(discId, out var chunkDisc))
            {
                errors.Add($"Chunk references unknown disc id {discId}.");
                foreach (var (destPath, _) in placements)
                    if (recordIdByDest.TryGetValue(destPath, out var rid)) failedRecordIds.Add(rid);
                continue;
            }

            progress?.Report(new RestoreProgress
            {
                CurrentFile = $"Please insert disc: {chunkDisc.Label}",
                FilesCompleted = filesCompletedSoFar,
                TotalFiles = totalFiles,
                BytesCompleted = bytesCompletedSoFar,
                TotalBytes = totalBytes,
                Percentage = totalBytes > 0 ? (double)bytesCompletedSoFar / totalBytes * 100 : 0,
                RequiredDiscId = chunkDisc.Id,
                RequiredDiscLabel = chunkDisc.Label,
            });

            string? discRoot = DiscInsertCallback is not null
                ? await DiscInsertCallback(chunkDisc.Label)
                : FindDiscRoot(chunkDisc.Label);
            if (discRoot is null)
            {
                errors.Add($"Could not mount disc '{chunkDisc.Label}' holding split-file chunks.");
                foreach (var (destPath, _) in placements)
                    if (recordIdByDest.TryGetValue(destPath, out var rid)) failedRecordIds.Add(rid);
                continue;
            }

            foreach (var (destPath, chunk) in placements)
            {
                ct.ThrowIfCancellationRequested();
                string chunkPath = Path.Combine(discRoot, chunk.DiscFilename);
                try
                {
                    if (!File.Exists(chunkPath))
                        throw new FileNotFoundException($"Chunk file not found: {chunkPath}", chunkPath);

                    await using var chunkStream = new FileStream(
                        chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 81920, useAsync: true);
                    await using var destStream = new FileStream(
                        destPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                        bufferSize: 81920, useAsync: true);
                    destStream.Seek(chunk.Offset, SeekOrigin.Begin);
                    await chunkStream.CopyToAsync(destStream, ct);
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to restore chunk {chunk.Sequence} from '{chunkDisc.Label}': {ex.Message}");
                    if (recordIdByDest.TryGetValue(destPath, out var rid)) failedRecordIds.Add(rid);
                }
            }
        }

        int filesRestored = 0;
        long bytesRestored = 0;
        foreach (var sf in splitFiles)
        {
            if (destByRecordId.ContainsKey(sf.Record.Id) && !failedRecordIds.Contains(sf.Record.Id))
            {
                filesRestored++;
                bytesRestored += sf.Record.SizeBytes;
            }
        }
        return (filesRestored, bytesRestored);
    }

    /// <summary>
    /// Extract a single file from a zip archive.
    /// </summary>
    private static async Task UnzipFileAsync(
        string zipPath,
        string destPath,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        if (archive.Entries.Count == 0)
            throw new InvalidOperationException($"Zip archive is empty: {zipPath}");

        // The zip should contain one file — the original.
        var entry = archive.Entries[0];

        await using var entryStream = entry.Open();
        await using var destStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        await entryStream.CopyToAsync(destStream, ct);
    }

    /// <summary>
    /// Restore a file-level dedup reference. A <c>.fileref</c> stores no bytes;
    /// its content is held by a plain copy elsewhere in the same backup set's
    /// tree. Resolve <paramref name="hash"/> to that plain copy via the catalog
    /// and copy it to the destination.
    /// </summary>
    private async Task RestoreFileRefAsync(
        int backupSetId,
        string hash,
        string discRoot,
        string destPath,
        CancellationToken ct)
    {
        var candidates = await _catalog.GetActiveRecordsByHashAsync(backupSetId, hash, ct);
        var plain = candidates.FirstOrDefault(r => !r.IsFileRef && !r.IsDeduped);
        if (plain is null)
            throw new FileNotFoundException(
                $"No plain copy found for referenced content {hash}.");

        string sourcePath = Path.Combine(discRoot, plain.DiscPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Referenced content file missing: {plain.DiscPath}", sourcePath);

        File.Copy(sourcePath, destPath, overwrite: true);
    }

    /// <summary>
    /// Read a .dedup manifest and reassemble the original file from the block store.
    /// </summary>
    private static async Task ReassembleDedupFileAsync(
        string manifestPath,
        string blockStoreDir,
        string destPath,
        CancellationToken ct)
    {
        string json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<DedupManifest>(json)
            ?? throw new InvalidOperationException(
                $"Failed to parse dedup manifest: {manifestPath}");

        await using var destStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        foreach (string blockHash in manifest.BlockHashes)
        {
            ct.ThrowIfCancellationRequested();

            string blockPath = Path.Combine(blockStoreDir, blockHash + ".blk");
            if (!File.Exists(blockPath))
                throw new FileNotFoundException(
                    $"Missing block in store: {blockHash}.blk", blockPath);

            await using var blockStream = new FileStream(
                blockPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            await blockStream.CopyToAsync(destStream, ct);
        }
    }

    /// <summary>
    /// Whether a disc label looks like a directory path (directory-mode backup)
    /// rather than an optical volume label. Rooted paths such as
    /// <c>D:\backups\out</c> or <c>\\server\share</c> qualify.
    /// </summary>
    private static bool LooksLikeDirectoryPath(string label) =>
        !string.IsNullOrWhiteSpace(label)
        && (Path.IsPathRooted(label)
            || label.Contains('\\')
            || label.Contains('/'));

    /// <summary>
    /// Try to find a mounted disc or directory backup target.
    /// </summary>
    private static string? FindDiscRoot(string discLabel)
    {
        // For directory-mode backups, the label IS the target directory path.
        if (Directory.Exists(discLabel))
            return discLabel;

        // For optical discs, check mounted drives.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.CDRom && drive.IsReady)
            {
                if (string.Equals(drive.VolumeLabel, discLabel, StringComparison.OrdinalIgnoreCase))
                    return drive.RootDirectory.FullName;
            }
        }
        return null;
    }

    /// <summary>
    /// Split a source path into its drive key (uppercase drive letter, or
    /// <c>"_"</c> for non-drive paths such as UNC) and the path relative to the
    /// drive root. For example <c>D:\docs\a.txt</c> → (<c>"D"</c>,
    /// <c>"docs\a.txt"</c>).
    /// </summary>
    private static (string driveKey, string relative) SplitSourcePath(string sourcePath)
    {
        string root = Path.GetPathRoot(sourcePath) ?? "";
        string relative = sourcePath[root.Length..];

        string driveKey = sourcePath.Length >= 2 && sourcePath[1] == ':'
            ? char.ToUpperInvariant(sourcePath[0]).ToString()
            : "_";
        return (driveKey, relative);
    }
}
