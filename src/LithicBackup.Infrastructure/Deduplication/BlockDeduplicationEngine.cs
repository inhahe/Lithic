using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Deduplication;

/// <summary>
/// Block-level deduplication engine. Breaks files into fixed-size blocks,
/// hashes each block, and deduplicates against the catalog.
/// </summary>
public class BlockDeduplicationEngine : IDeduplicationEngine
{
    private readonly ICatalogRepository _catalog;

    public BlockDeduplicationEngine(ICatalogRepository catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// Process a file for deduplication: break into blocks, hash each block,
    /// and return a recipe of block references (existing or new).
    /// </summary>
    public async Task<DeduplicationRecipe> DeduplicateAsync(
        string filePath,
        int blockSize,
        CancellationToken ct = default)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: blockSize, useAsync: true);

        var fileInfo = new FileInfo(filePath);
        var blocks = new List<BlockReference>();
        long bytesSaved = 0;
        var buffer = new byte[blockSize];

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

            // Check catalog for existing block.
            var existingBlock = await _catalog.FindBlockByHashAsync(blockHashHex, ct);

            if (existingBlock is not null)
            {
                // Block already exists — reference it and increment ref count.
                await _catalog.IncrementBlockReferenceAsync(existingBlock.Id, ct);
                blocks.Add(new BlockReference
                {
                    BlockId = existingBlock.Id,
                    Hash = blockHashHex,
                    IsExisting = true,
                });
                bytesSaved += bytesRead;
            }
            else
            {
                // New block — create it in the catalog.
                // DiscId will be set to 0 for now; it will be updated when
                // the block is actually written to a disc during the burn phase.
                var newBlock = new DeduplicationBlock
                {
                    Hash = blockHashHex,
                    SizeBytes = bytesRead,
                    ReferenceCount = 1,
                    DiscId = 0,
                };
                await _catalog.CreateBlockAsync(newBlock, ct);

                blocks.Add(new BlockReference
                {
                    BlockId = newBlock.Id,
                    Hash = blockHashHex,
                    IsExisting = false,
                });
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
