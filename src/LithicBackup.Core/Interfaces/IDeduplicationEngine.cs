using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Block-level deduplication engine.
/// </summary>
public interface IDeduplicationEngine
{
    /// <summary>
    /// Process a file for deduplication: break into blocks, hash each block,
    /// and return a recipe of block references (existing or new).
    ///
    /// <para>
    /// Block presence is decided directly against the destination's
    /// content-addressed block store (<paramref name="blockStoreDir"/>, the
    /// <c>_blocks</c> folder), so deduplication is shared by every backup set
    /// that targets the same destination and needs no separate index to keep
    /// in sync.  The store is identified by its resolved live path, so it
    /// follows the destination across drive-letter changes.
    /// </para>
    /// </summary>
    /// <param name="blockStoreDir">
    /// The destination's <c>_blocks</c> directory (content-addressed store of
    /// <c>{hash}.blk</c> files).  Need not exist yet.
    /// </param>
    Task<DeduplicationRecipe> DeduplicateAsync(
        string blockStoreDir,
        string filePath,
        int blockSize,
        CancellationToken ct = default);

    /// <summary>
    /// In-memory variant of <see cref="DeduplicateAsync"/>: build the recipe
    /// from bytes already held in memory instead of reading the file from disk.
    /// Used when the caller has read the file into a buffer (because it intends
    /// to write the file's blocks or a plain copy straight from that buffer),
    /// so the file is read exactly once for both analysis and writing rather
    /// than once to analyse and again to write. The result is identical to
    /// <see cref="DeduplicateAsync"/> over the same bytes; block presence is
    /// still decided against <paramref name="blockStoreDir"/>.
    /// </summary>
    DeduplicationRecipe DeduplicateBytes(
        string blockStoreDir,
        string filePath,
        byte[] content,
        int blockSize);
}

/// <summary>
/// Describes how a file is composed of deduplicated blocks.
/// </summary>
public class DeduplicationRecipe
{
    public string OriginalPath { get; init; } = string.Empty;
    public long OriginalSize { get; init; }
    public string OriginalHash { get; init; } = string.Empty;

    /// <summary>Ordered list of block references that compose this file.</summary>
    public IReadOnlyList<BlockReference> Blocks { get; init; } = [];

    /// <summary>Bytes saved by deduplication (blocks that already existed).</summary>
    public long BytesSaved { get; init; }
}

/// <summary>Reference to a single block in a deduplicated file.</summary>
public class BlockReference
{
    public string Hash { get; init; } = string.Empty;

    /// <summary>
    /// Whether this block already existed in the store — or was already
    /// emitted earlier in this same recipe — (true) versus being new (false).
    /// New blocks are the ones written to the store by the caller.
    /// </summary>
    public bool IsExisting { get; init; }
}
