namespace LithicBackup.Core.Models;

/// <summary>
/// One piece of a file that has been split across multiple discs.
/// </summary>
public class FileChunk
{
    public long Id { get; set; }

    /// <summary>The parent <see cref="FileRecord"/> this chunk belongs to.</summary>
    public long FileRecordId { get; set; }

    /// <summary>ID of the disc this chunk is stored on.</summary>
    public int DiscId { get; set; }

    /// <summary>
    /// Zero-based sequence number. Chunks are reassembled in sequence order.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>Byte offset within the original file where this chunk starts.</summary>
    public long Offset { get; set; }

    /// <summary>Size of this chunk in bytes.</summary>
    public long Length { get; set; }

    /// <summary>
    /// Filename as stored on the disc (e.g. "a1b2c3d4.0001.discburn-split").
    /// </summary>
    public string DiscFilename { get; set; } = string.Empty;
}
