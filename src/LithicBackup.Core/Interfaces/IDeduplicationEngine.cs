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
    /// </summary>
    Task<DeduplicationRecipe> DeduplicateAsync(
        string filePath,
        int blockSize,
        CancellationToken ct = default);

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
    /// <summary>ID of the <see cref="DeduplicationBlock"/> in the catalog.</summary>
    public long BlockId { get; init; }

    public string Hash { get; init; } = string.Empty;

    /// <summary>Whether this block already existed (true) or is new (false).</summary>
    public bool IsExisting { get; init; }
}
