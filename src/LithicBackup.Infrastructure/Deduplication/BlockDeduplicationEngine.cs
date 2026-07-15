using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.Infrastructure.Deduplication;

/// <summary>
/// Block-level deduplication engine. Breaks files into fixed-size blocks,
/// hashes each block, and deduplicates against the destination's
/// content-addressed block store (<c>_blocks/{hash}.blk</c>).
///
/// <para>
/// The store itself is the index: a block is "existing" when its
/// <c>{hash}.blk</c> file is already present (or was already emitted earlier
/// in the same recipe).  Because every backup set targeting a destination
/// shares that destination's <c>_blocks</c> folder, deduplication is
/// automatically shared across sets without a separate, divergence-prone
/// index, and it follows the destination across drive-letter changes (the
/// caller passes the resolved live path).  This mirrors how whole-file dedup
/// already works against <c>_filestore/{hash}.dat</c>.
/// </para>
/// </summary>
public class BlockDeduplicationEngine : IDeduplicationEngine
{
    /// <summary>
    /// Process a file for deduplication: break into blocks, hash each block,
    /// and return a recipe of block references (existing or new).
    /// </summary>
    public async Task<DeduplicationRecipe> DeduplicateAsync(
        string blockStoreDir,
        string filePath,
        int blockSize,
        CancellationToken ct = default)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        await using var fileStream = new FileStream(
            // ReadWrite share so a file another app holds open for writing
            // (e.g. a note still open in KeyNote NF) can still be read.
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: blockSize, useAsync: true);

        var fileInfo = new FileInfo(filePath);
        var blocks = new List<BlockReference>();
        long bytesSaved = 0;
        var buffer = new byte[blockSize];

        // Blocks emitted as "new" earlier in THIS recipe: a hash that repeats
        // within the file is written to the store only once, so later
        // occurrences are deduplicated even before the store is written.
        var seenThisRecipe = new HashSet<string>();

        // Compute the overall file hash while processing blocks.
        using var fileHasher = SHA256.Create();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int bytesRead = await ReadFullBlockAsync(fileStream, buffer, ct);
            if (bytesRead == 0)
                break;

            // Hash this block.
            fileHasher.TransformBlock(buffer, 0, bytesRead, null, 0);
            var blockHash = SHA256.HashData(buffer.AsSpan(0, bytesRead));
            string blockHashHex = Convert.ToHexString(blockHash).ToLowerInvariant();

            // The block store is the index: a block exists if its file is
            // already on disk, or it was already emitted as new in this recipe.
            string blockPath = Path.Combine(blockStoreDir, blockHashHex + ".blk");
            bool existing = seenThisRecipe.Contains(blockHashHex) || File.Exists(blockPath);

            if (existing)
            {
                blocks.Add(new BlockReference { Hash = blockHashHex, IsExisting = true });
                bytesSaved += bytesRead;
            }
            else
            {
                seenThisRecipe.Add(blockHashHex);
                blocks.Add(new BlockReference { Hash = blockHashHex, IsExisting = false });
            }
        }

        // Finalize the overall file hash.
        fileHasher.TransformFinalBlock([], 0, 0);
        string fileHash = Convert.ToHexString(fileHasher.Hash!).ToLowerInvariant();

        return new DeduplicationRecipe
        {
            OriginalPath = filePath,
            OriginalSize = fileInfo.Length,
            OriginalHash = fileHash,
            Blocks = blocks,
            BytesSaved = bytesSaved,
        };
    }

    /// <summary>
    /// In-memory variant of <see cref="DeduplicateAsync"/> — analyses bytes the
    /// caller already holds, so the file is read only once for both analysis and
    /// the subsequent block/plain write. Mirrors the streaming version exactly.
    /// </summary>
    public DeduplicationRecipe DeduplicateBytes(
        string blockStoreDir,
        string filePath,
        byte[] content,
        int blockSize)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        var blocks = new List<BlockReference>();
        long bytesSaved = 0;
        var seenThisRecipe = new HashSet<string>();
        using var fileHasher = SHA256.Create();

        int offset = 0;
        while (offset < content.Length)
        {
            int len = Math.Min(blockSize, content.Length - offset);

            fileHasher.TransformBlock(content, offset, len, null, 0);
            string blockHashHex = Convert.ToHexString(
                SHA256.HashData(content.AsSpan(offset, len))).ToLowerInvariant();

            string blockPath = Path.Combine(blockStoreDir, blockHashHex + ".blk");
            bool existing = seenThisRecipe.Contains(blockHashHex) || File.Exists(blockPath);

            if (existing)
            {
                blocks.Add(new BlockReference { Hash = blockHashHex, IsExisting = true });
                bytesSaved += len;
            }
            else
            {
                seenThisRecipe.Add(blockHashHex);
                blocks.Add(new BlockReference { Hash = blockHashHex, IsExisting = false });
            }

            offset += len;
        }

        fileHasher.TransformFinalBlock([], 0, 0);
        string fileHash = Convert.ToHexString(fileHasher.Hash!).ToLowerInvariant();

        return new DeduplicationRecipe
        {
            OriginalPath = filePath,
            OriginalSize = content.Length,
            OriginalHash = fileHash,
            Blocks = blocks,
            BytesSaved = bytesSaved,
        };
    }

    /// <summary>
    /// Read exactly <paramref name="buffer"/>.Length bytes (or fewer at EOF).
    /// </summary>
    private static async Task<int> ReadFullBlockAsync(
        Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }
}
