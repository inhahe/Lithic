using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Implementation of <see cref="ICatalogFreeRestoreService"/>. Walks a backup
/// destination tree and reconstructs every original file without any catalog,
/// relying solely on the self-describing on-disk layout:
/// <code>
///   {backup}/{drive}/relative                      -- current plain file
///   {backup}/{drive}/relative.dedup                -- current block-deduped file
///   {backup}/{drive}/relative.fileref              -- current file-level dup
///   {backup}/{drive}_prev/relative.v{N}[.dedup|.fileref] -- previous versions
///   {backup}/_blocks/{hash}.blk                    -- shared block store
/// </code>
/// </summary>
public class CatalogFreeRestoreService : ICatalogFreeRestoreService
{
    private const string BlockStoreDirName = "_blocks";

    /// <summary>How a backup-tree entry stores its bytes.</summary>
    private enum ItemKind { Plain, Dedup, FileRef }

    /// <summary>One file to reconstruct.</summary>
    private sealed record RestoreItem(
        string SourceAbsPath, ItemKind Kind, string OutputRelPath, long Size);

    public async Task<RestoreResult> RestoreAsync(
        string backupDirectory,
        string outputDirectory,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(backupDirectory))
            throw new DirectoryNotFoundException(
                $"Backup directory not found: {backupDirectory}");

        Directory.CreateDirectory(outputDirectory);

        string blockStoreDir = Path.Combine(backupDirectory, BlockStoreDirName);

        // --- Pass 1: enumerate the work and tally totals for progress. ---
        var items = await CollectItemsAsync(backupDirectory, ct);

        int totalFiles = items.Count;
        long totalBytes = items.Sum(i => i.Size);
        int filesCompleted = 0;
        long bytesCompleted = 0;
        var errors = new List<string>();

        // Lazily-built fallback index: content hash -> absolute path of a plain
        // file in the tree holding that content. Only constructed if a .fileref's
        // ContentPath hint is missing or stale, so the common (healthy) case
        // never pays the cost of hashing the whole tree.
        Dictionary<string, string>? hashIndex = null;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new RestoreProgress
            {
                CurrentFile = item.OutputRelPath,
                FilesCompleted = filesCompleted,
                TotalFiles = totalFiles,
                BytesCompleted = bytesCompleted,
                TotalBytes = totalBytes,
                Percentage = totalBytes > 0 ? (double)bytesCompleted / totalBytes * 100 : 0,
            });

            string destPath = Path.Combine(outputDirectory, item.OutputRelPath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null)
                Directory.CreateDirectory(destDir);

            try
            {
                switch (item.Kind)
                {
                    case ItemKind.Plain:
                        File.Copy(item.SourceAbsPath, destPath, overwrite: true);
                        break;

                    case ItemKind.Dedup:
                        await ReassembleDedupFileAsync(
                            item.SourceAbsPath, blockStoreDir, destPath, ct);
                        break;

                    case ItemKind.FileRef:
                        await RestoreFileRefAsync(
                            item.SourceAbsPath, backupDirectory, destPath,
                            () => hashIndex ??= BuildHashIndex(backupDirectory, ct),
                            ct);
                        break;
                }

                filesCompleted++;
                bytesCompleted += item.Size;
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to restore '{item.OutputRelPath}': {ex.Message}");
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
    /// Walk the backup tree and classify every restorable entry. Skips the
    /// <c>_blocks</c> store (used for reassembly, not a user file) and
    /// <c>.lbtmp</c> partial copies left by interrupted backups.
    /// </summary>
    private static async Task<List<RestoreItem>> CollectItemsAsync(
        string backupDirectory, CancellationToken ct)
    {
        var items = new List<RestoreItem>();
        var root = new DirectoryInfo(backupDirectory);

        foreach (var topDir in root.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();

            // The block store is reassembly input, not a restorable file.
            if (topDir.Name.Equals(BlockStoreDirName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var file in topDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string name = file.Name;
                if (name.EndsWith(".lbtmp", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Relative path within the backup tree, with the Lithic format
                // suffix stripped so the output path is the reconstructed name.
                string relInTree = Path.GetRelativePath(backupDirectory, file.FullName);

                if (name.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase))
                {
                    long size = await TryReadManifestSizeAsync(file.FullName, ct);
                    items.Add(new RestoreItem(
                        file.FullName, ItemKind.FileRef,
                        StripSuffix(relInTree, ".fileref"), size));
                }
                else if (name.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase))
                {
                    long size = await TryReadManifestSizeAsync(file.FullName, ct);
                    items.Add(new RestoreItem(
                        file.FullName, ItemKind.Dedup,
                        StripSuffix(relInTree, ".dedup"), size));
                }
                else
                {
                    items.Add(new RestoreItem(
                        file.FullName, ItemKind.Plain, relInTree, file.Length));
                }
            }
        }

        return items;
    }

    /// <summary>Best-effort read of a manifest's OriginalSize for progress tallies.</summary>
    private static async Task<long> TryReadManifestSizeAsync(
        string manifestPath, CancellationToken ct)
    {
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("OriginalSize", out var sizeEl)
                && sizeEl.TryGetInt64(out long size))
                return size;
        }
        catch
        {
            // Fall through to 0 — progress tally only, not correctness-critical.
        }
        return 0;
    }

    /// <summary>
    /// Reconstruct a <c>.fileref</c> using only the destination tree. Follows the
    /// manifest's <see cref="FileRefManifest.ContentPath"/> hint first, verifying
    /// the bytes against <see cref="FileRefManifest.Hash"/>; if the hint is
    /// missing, stale, or fails verification, falls back to a content-hash index
    /// built by scanning the tree.
    /// </summary>
    private static async Task RestoreFileRefAsync(
        string manifestPath,
        string backupDirectory,
        string destPath,
        Func<Dictionary<string, string>> getHashIndex,
        CancellationToken ct)
    {
        string json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<FileRefManifest>(json)
            ?? throw new InvalidOperationException(
                $"Failed to parse fileref manifest: {manifestPath}");

        string? contentPath = null;

        // 1) Try the ContentPath hint, verifying it actually holds the content.
        if (!string.IsNullOrEmpty(manifest.ContentPath))
        {
            string hintAbs = Path.Combine(backupDirectory, manifest.ContentPath);
            if (File.Exists(hintAbs)
                && await HashMatchesAsync(hintAbs, manifest.Hash, ct))
            {
                contentPath = hintAbs;
            }
        }

        // 2) Fall back to a content-hash scan of the tree.
        if (contentPath is null && !string.IsNullOrEmpty(manifest.Hash))
        {
            var index = getHashIndex();
            if (index.TryGetValue(manifest.Hash, out string? found) && File.Exists(found))
                contentPath = found;
        }

        if (contentPath is null)
            throw new FileNotFoundException(
                $"No plain copy found for referenced content {manifest.Hash} " +
                $"(hint '{manifest.ContentPath}' missing or stale).");

        File.Copy(contentPath, destPath, overwrite: true);
    }

    /// <summary>
    /// Read a <c>.dedup</c> manifest and reassemble the original file from the
    /// shared block store, identically to the catalog-based restore.
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
    /// Build a content-hash -> plain-file-path index by scanning every plain file
    /// in the tree (excluding manifests and the block store). Used only as a
    /// fallback when a <c>.fileref</c>'s ContentPath hint can't be trusted.
    /// </summary>
    private static Dictionary<string, string> BuildHashIndex(
        string backupDirectory, CancellationToken ct)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = new DirectoryInfo(backupDirectory);

        foreach (var topDir in root.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();

            if (topDir.Name.Equals(BlockStoreDirName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var file in topDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string name = file.Name;
                if (name.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".lbtmp", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    string hash = ComputeFileHash(file.FullName, ct);
                    // First writer wins; any plain copy of the content suffices.
                    index.TryAdd(hash, file.FullName);
                }
                catch
                {
                    // Unreadable file — skip; another copy may still resolve it.
                }
            }
        }

        return index;
    }

    /// <summary>True if the file at <paramref name="path"/> hashes to <paramref name="expectedHash"/>.</summary>
    private static async Task<bool> HashMatchesAsync(
        string path, string expectedHash, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedHash))
            return false;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return string.Equals(
                Convert.ToHexString(hash).ToLowerInvariant(),
                expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeFileHash(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string StripSuffix(string path, string suffix)
        => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? path[..^suffix.Length]
            : path;
}
