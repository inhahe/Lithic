using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Splits large files into chunks for spanning across discs.
/// Chunk filenames use the pattern <c>{hash8}.{seq:D4}.discburn-split</c>
/// to avoid collisions with source filenames.
/// </summary>
public class FileSplitter : IFileSplitter
{
    public async Task<IReadOnlyList<FileChunk>> SplitAsync(
        string sourceFilePath,
        string outputDirectory,
        long maxChunkSize,
        CancellationToken ct = default)
    {
        if (maxChunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChunkSize));

        // Compute a short hash prefix for collision-safe naming.
        var hashPrefix = await ComputeHashPrefixAsync(sourceFilePath, ct);
        var chunks = new List<FileChunk>();

        await using var sourceStream = new FileStream(
            sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        long offset = 0;
        int sequence = 0;
        var buffer = new byte[81920];

        while (offset < sourceStream.Length)
        {
            ct.ThrowIfCancellationRequested();

            long chunkLength = Math.Min(maxChunkSize, sourceStream.Length - offset);
            string chunkFilename = GenerateChunkFilename(hashPrefix, sequence, outputDirectory);
            string chunkPath = Path.Combine(outputDirectory, chunkFilename);

            await using (var chunkStream = new FileStream(
                chunkPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                long remaining = chunkLength;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int bytesRead = await sourceStream.ReadAsync(
                        buffer.AsMemory(0, toRead), ct);
                    if (bytesRead == 0)
                        break;

                    await chunkStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    remaining -= bytesRead;
                }
            }

            chunks.Add(new FileChunk
            {
                Sequence = sequence,
                Offset = offset,
                Length = chunkLength,
                DiscFilename = chunkFilename,
            });

            offset += chunkLength;
            sequence++;
        }

        return chunks;
    }

    public async Task ReassembleAsync(
        IReadOnlyList<FileChunk> chunks,
        Func<FileChunk, Task<Stream>> openChunkStream,
        string destinationPath,
        CancellationToken ct = default)
    {
        var ordered = chunks.OrderBy(c => c.Sequence).ToList();

        await using var destStream = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];

        foreach (var chunk in ordered)
        {
            ct.ThrowIfCancellationRequested();

            await using var chunkStream = await openChunkStream(chunk);

            while (true)
            {
                int bytesRead = await chunkStream.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                    break;

                await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }
    }

    /// <summary>
    /// First 8 hex digits of the file's SHA-256 hash.
    /// </summary>
    private static async Task<string> ComputeHashPrefixAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Generate a chunk filename, ensuring no collision with existing files.
    /// </summary>
    private static string GenerateChunkFilename(string hashPrefix, int sequence, string directory)
    {
        string filename = $"{hashPrefix}.{sequence:D4}.discburn-split";

        // Check for collision (extremely unlikely with hash prefix, but spec requires it).
        if (File.Exists(Path.Combine(directory, filename)))
        {
            int suffix = 1;
            string candidate;
            do
            {
                candidate = $"{hashPrefix}.{sequence:D4}.{suffix}.discburn-split";
                suffix++;
            } while (File.Exists(Path.Combine(directory, candidate)));

            return candidate;
        }

        return filename;
    }
}
