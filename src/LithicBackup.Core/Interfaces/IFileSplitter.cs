using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Splits large files into chunks for spanning across discs, and reassembles them.
/// </summary>
public interface IFileSplitter
{
    /// <summary>
    /// Split a file into chunks of at most <paramref name="maxChunkSize"/> bytes.
    /// Chunks are written to <paramref name="outputDirectory"/> with collision-safe names.
    /// </summary>
    /// <returns>Chunk metadata for catalog storage.</returns>
    Task<IReadOnlyList<FileChunk>> SplitAsync(
        string sourceFilePath,
        string outputDirectory,
        long maxChunkSize,
        CancellationToken ct = default);

    /// <summary>
    /// Reassemble chunks back into the original file.
    /// </summary>
    Task ReassembleAsync(
        IReadOnlyList<FileChunk> chunks,
        Func<FileChunk, Task<Stream>> openChunkStream,
        string destinationPath,
        CancellationToken ct = default);
}
