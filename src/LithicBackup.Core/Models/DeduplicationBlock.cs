namespace LithicBackup.Core.Models;

/// <summary>
/// One deduplicated block of data stored in the backup.
/// </summary>
public class DeduplicationBlock
{
    public long Id { get; set; }

    /// <summary>SHA-256 hash of this block's contents.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Size of this block in bytes.</summary>
    public int SizeBytes { get; set; }

    /// <summary>Number of files that reference this block.</summary>
    public int ReferenceCount { get; set; }

    /// <summary>ID of the disc this block is physically stored on.</summary>
    public int DiscId { get; set; }
}
