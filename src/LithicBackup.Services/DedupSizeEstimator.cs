using System.Security.Cryptography;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>Result of a dedup-aware backup-size estimate.</summary>
/// <param name="RawBytes">Sum of raw source file sizes (what a naive scan reports).</param>
/// <param name="StoredBytes">Bytes the backup would actually write after
/// deduplication against already-stored content and files seen earlier in this
/// scan — i.e. the real footprint the next run consumes.</param>
/// <param name="TotalFiles">Number of files considered.</param>
/// <param name="FilesFullyRead">How many files had to be read in full (I/O cost).</param>
/// <param name="BytesRead">Total bytes actually read to compute the estimate
/// (full reads + cheap prefix reads); 0 for the trivial no-dedup case.</param>
/// <param name="BlockLevel">True when block-level accounting was used (every file
/// is read in full — the expensive path).</param>
public sealed record DedupSizeEstimate(
    long RawBytes,
    long StoredBytes,
    int TotalFiles,
    int FilesFullyRead,
    long BytesRead,
    bool BlockLevel)
{
    /// <summary>Bytes saved by dedup versus the naive raw total.</summary>
    public long SavedBytes => Math.Max(0, RawBytes - StoredBytes);

    /// <summary>Fraction of raw bytes saved by dedup (0..1).</summary>
    public double SavedFraction => RawBytes > 0 ? (double)SavedBytes / RawBytes : 0.0;
}

/// <summary>Progress tick during a dedup-aware size estimate.</summary>
public sealed record EstimateProgress(int FilesProcessed, int TotalFiles, long BytesRead);

/// <summary>
/// Computes a dedup-aware "actual backup size" for a directory backup by
/// mirroring what <see cref="DirectoryBackupService"/> would store, so the
/// number can't drift from the real result. Reuses the same primitives the
/// backup uses: the catalog's active-plain-content pool
/// (<see cref="ICatalogRepository.GetActivePlainContentPathsAsync"/> /
/// <see cref="ICatalogRepository.GetActivePlainContentSizesAsync"/>), the
/// block-dedup engine, the file hash cache, and the roadmap-item-5 progressive
/// prefix hash for cheap intra-run collision rule-outs.
///
/// <para><b>File-level dedup, block off:</b> a file only dedupes if its whole
/// content matches existing plain content or an earlier file this run. Sizes
/// that collide with nothing already stored are new (counted in full, no read).
/// Size-colliders are settled by content hash — but intra-run-only collisions use
/// the cheap prefix pre-check first, so unique-prefix files are ruled out after a
/// few KiB instead of a full read.</para>
///
/// <para><b>Block-level dedup on:</b> every block of every file must be hashed to
/// know which blocks are already stored, so the whole content is read regardless
/// — partial hashing buys nothing (<see cref="RequiresFullRead"/> is true). Gate
/// this behind an explicit user action and a "this reads all your data" warning.</para>
/// </summary>
public sealed class DedupSizeEstimator
{
    private readonly ICatalogRepository _catalog;
    private readonly IDeduplicationEngine? _dedup;
    private readonly IFileHashLookup? _hashCache;

    /// <summary>Leading bytes hashed for the cheap intra-run prefix pre-check.</summary>
    private const int PrefixHashBytes = 64 * 1024;

    public DedupSizeEstimator(
        ICatalogRepository catalog,
        IDeduplicationEngine? dedup = null,
        IFileHashLookup? hashCache = null)
    {
        _catalog = catalog;
        _dedup = dedup;
        _hashCache = hashCache;
    }

    /// <summary>
    /// True when estimating this job would read every file in full (block-level
    /// dedup is enabled and an engine is available). The UI should warn before
    /// running such an estimate.
    /// </summary>
    public bool RequiresFullRead(BackupJob job) =>
        job.EnableDeduplication && _dedup is not null;

    public async Task<DedupSizeEstimate> EstimateAsync(
        BackupJob job,
        IReadOnlyList<ScannedFile> files,
        string? targetDirectory,
        IProgress<EstimateProgress>? progress,
        CancellationToken ct)
    {
        long raw = 0;
        foreach (var f in files) raw += f.SizeBytes;

        // Block-level dominates cost and subsumes whole-file dedup (a whole-file
        // duplicate's blocks are all already stored), so use it whenever enabled.
        if (job.EnableDeduplication && _dedup is not null)
            return await EstimateBlockLevelAsync(job, files, targetDirectory, raw, progress, ct);

        if (job.EnableFileDeduplication)
            return await EstimateFileLevelAsync(job, files, raw, progress, ct);

        // No dedup: stored == raw, nothing to read.
        return new DedupSizeEstimate(raw, raw, files.Count, 0, 0, BlockLevel: false);
    }

    // -------------------------------------------------------------------
    // File-level dedup (block off)
    // -------------------------------------------------------------------

    private sealed class PrefixGroup
    {
        /// <summary>The single plain-counted file of this (size, prefix) whose full
        /// hash hasn't been needed yet. Materialized lazily on the first collision.</summary>
        public ScannedFile? PendingAnchor;

        /// <summary>Distinct full content hashes already counted plain in this group.</summary>
        public readonly HashSet<string> Hashes = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DedupSizeEstimate> EstimateFileLevelAsync(
        BackupJob job,
        IReadOnlyList<ScannedFile> files,
        long raw,
        IProgress<EstimateProgress>? progress,
        CancellationToken ct)
    {
        // Seed from the catalog's active plain-content pool: hashes that already
        // have a plain copy (a later identical file dedups to a .fileref) and the
        // set of sizes that pool spans (the only sizes that can collide with it).
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingSizes = new HashSet<long>();
        if (job.BackupSetId is int setId)
        {
            try
            {
                foreach (var h in (await _catalog.GetActivePlainContentPathsAsync(setId, ct)).Keys)
                    known.Add(h);
                existingSizes = await _catalog.GetActivePlainContentSizesAsync(setId, ct);
            }
            catch { /* fall back to "no existing content" — over-counts, never wrong-way */ }
        }

        // How many scanned files share each size (intra-run collisions).
        var sizeCounts = new Dictionary<long, int>();
        foreach (var f in files)
            sizeCounts[f.SizeBytes] = sizeCounts.GetValueOrDefault(f.SizeBytes) + 1;

        // Intra-run-only prefix index: size -> prefix -> group. Only consulted for
        // sizes that collide within the run but NOT with existing content (those
        // keep the full up-front hash — we don't have the store's prefixes here).
        var prefixIndex = new Dictionary<long, Dictionary<string, PrefixGroup>>();

        long stored = 0, bytesRead = 0;
        int filesRead = 0, processed = 0;

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            long size = f.SizeBytes;
            bool couldExisting = existingSizes.Contains(size);
            bool couldIntra = sizeCounts.GetValueOrDefault(size) >= 2;

            if (!couldExisting && !couldIntra)
            {
                // Size collides with nothing already stored and nothing else this
                // run — definitely unique content, stored plain, no read needed.
                stored += size;
            }
            else if (couldExisting)
            {
                // Collides with stored content: we can't prefix-match the store, so
                // take the full hash and check the known pool (existing + this run).
                var (hash, didRead) = await FullHashAsync(f, ct);
                if (didRead) { filesRead++; bytesRead += size; }
                if (!known.Contains(hash))
                {
                    stored += size;     // new content -> plain copy
                    known.Add(hash);    // anchors later identical files -> .fileref
                }
                // else: whole-file duplicate -> .fileref, writes nothing.
            }
            else
            {
                // Intra-run-only collision: rule out by cheap prefix first.
                string prefix = await ComputePrefixHashAsync(f.FullPath, ct);
                bytesRead += Math.Min(PrefixHashBytes, size);

                if (!prefixIndex.TryGetValue(size, out var byPrefix))
                    prefixIndex[size] = byPrefix = new Dictionary<string, PrefixGroup>();

                if (!byPrefix.TryGetValue(prefix, out var grp))
                {
                    // Unique (size, prefix) so far -> cannot be a dup yet -> plain.
                    // Defer its full hash unless a same-prefix file forces the issue.
                    byPrefix[prefix] = new PrefixGroup { PendingAnchor = f };
                    stored += size;
                }
                else
                {
                    // Prefix collision -> escalate to full hashes to disambiguate.
                    if (grp.PendingAnchor is { } anchor)
                    {
                        var (ah, aRead) = await FullHashAsync(anchor, ct);
                        if (aRead) { filesRead++; bytesRead += anchor.SizeBytes; }
                        grp.Hashes.Add(ah);
                        grp.PendingAnchor = null;
                    }
                    var (hash, didRead) = await FullHashAsync(f, ct);
                    if (didRead) { filesRead++; bytesRead += size; }
                    if (!grp.Hashes.Contains(hash))
                    {
                        stored += size;      // same size + prefix but different bytes
                        grp.Hashes.Add(hash);
                    }
                    // else: genuine duplicate -> .fileref, writes nothing.
                }
            }

            progress?.Report(new EstimateProgress(++processed, files.Count, bytesRead));
        }

        return new DedupSizeEstimate(raw, stored, files.Count, filesRead, bytesRead, BlockLevel: false);
    }

    // -------------------------------------------------------------------
    // Block-level dedup
    // -------------------------------------------------------------------

    private async Task<DedupSizeEstimate> EstimateBlockLevelAsync(
        BackupJob job,
        IReadOnlyList<ScannedFile> files,
        string? targetDirectory,
        long raw,
        IProgress<EstimateProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(targetDirectory))
            throw new ArgumentException(
                "A target directory is required to estimate block-level dedup (it locates the _blocks store).",
                nameof(targetDirectory));

        string blockStore = Path.Combine(targetDirectory, "_blocks");
        int blockSize = job.DeduplicationBlockSize > 0 ? job.DeduplicationBlockSize : 64 * 1024;

        // New block hashes counted so far this run. The store isn't written during
        // an estimate, so a block shared by two files (both flagged "new" by the
        // engine) must only be counted once — this set provides the intra-run view.
        var seenNewBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long stored = 0, bytesRead = 0;
        int filesRead = 0, processed = 0;

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            DeduplicationRecipe recipe;
            try
            {
                recipe = await _dedup!.DeduplicateAsync(blockStore, f.FullPath, blockSize, ct);
            }
            catch
            {
                // Unreadable file: assume it would be stored in full (conservative).
                stored += f.SizeBytes;
                progress?.Report(new EstimateProgress(++processed, files.Count, bytesRead));
                continue;
            }
            filesRead++;
            bytesRead += recipe.OriginalSize;

            int n = recipe.Blocks.Count;
            for (int i = 0; i < n; i++)
            {
                var block = recipe.Blocks[i];
                if (block.IsExisting)
                    continue; // already in the store or repeated within this file
                if (seenNewBlocks.Add(block.Hash))
                {
                    // Fixed-size blocks: every block is blockSize except the last.
                    long len = i < n - 1
                        ? blockSize
                        : recipe.OriginalSize - (long)(n - 1) * blockSize;
                    if (len > 0) stored += len;
                }
                // else: this new block was already counted for an earlier file.
            }

            progress?.Report(new EstimateProgress(++processed, files.Count, bytesRead));
        }

        return new DedupSizeEstimate(raw, stored, files.Count, filesRead, bytesRead, BlockLevel: true);
    }

    // -------------------------------------------------------------------
    // Hashing helpers (mirror DirectoryBackupService)
    // -------------------------------------------------------------------

    /// <summary>Full SHA-256 of a file, preferring the (path+size+mtime) hash cache
    /// so unchanged files aren't re-read on repeat estimates. Returns whether the
    /// file had to be read from disk.</summary>
    private async Task<(string Hash, bool DidRead)> FullHashAsync(ScannedFile f, CancellationToken ct)
    {
        string? cached = _hashCache?.TryGetHash(f.FullPath, f.SizeBytes, f.LastWriteUtc);
        if (cached is not null)
            return (cached, false);

        await using var stream = new FileStream(
            f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return (Convert.ToHexString(hash).ToLowerInvariant(), true);
    }

    /// <summary>Lowercase-hex SHA-256 of the first <see cref="PrefixHashBytes"/> bytes.</summary>
    private static async Task<string> ComputePrefixHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        var buf = new byte[PrefixHashBytes];
        int total = 0, read;
        while (total < buf.Length &&
               (read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct)) > 0)
        {
            total += read;
        }
        return Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, total))).ToLowerInvariant();
    }
}
