using System.IO.Compression;
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
        // Group files by disc ID for efficient disc-at-a-time processing.
        var byDisc = files
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

                    if (record.IsSplit)
                    {
                        // Reassemble split file from chunks.
                        await ReassembleSplitFileAsync(
                            restorableFile.Chunks, discRoot, destPath, ct);
                    }
                    else if (record.IsZipped)
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

        return new RestoreResult
        {
            Success = errors.Count == 0,
            FilesRestored = filesCompleted,
            BytesRestored = bytesCompleted,
            Errors = errors,
        };
    }

    /// <summary>
    /// Reassemble a split file from its chunks on disc.
    /// </summary>
    private static async Task ReassembleSplitFileAsync(
        IReadOnlyList<FileChunk> chunks,
        string discRoot,
        string destPath,
        CancellationToken ct)
    {
        var orderedChunks = chunks.OrderBy(c => c.Sequence).ToList();

        await using var destStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        foreach (var chunk in orderedChunks)
        {
            ct.ThrowIfCancellationRequested();

            string chunkPath = Path.Combine(discRoot, chunk.DiscFilename);

            if (!File.Exists(chunkPath))
                throw new FileNotFoundException(
                    $"Chunk file not found: {chunkPath}", chunkPath);

            await using var chunkStream = new FileStream(
                chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            await chunkStream.CopyToAsync(destStream, ct);
        }
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
