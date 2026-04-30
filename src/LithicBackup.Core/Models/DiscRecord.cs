namespace LithicBackup.Core.Models;

/// <summary>
/// Represents one physical disc in the backup catalog.
/// </summary>
public class DiscRecord
{
    public int Id { get; set; }

    /// <summary>ID of the <see cref="BackupSet"/> this disc belongs to.</summary>
    public int BackupSetId { get; set; }

    /// <summary>Human-readable label (e.g. "Backup-003") printed on the disc or its case.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Sequence number within the backup set (1-based).</summary>
    public int SequenceNumber { get; set; }

    public MediaType MediaType { get; set; }
    public FilesystemType FilesystemType { get; set; }

    /// <summary>Usable capacity in bytes (may be overridden by user).</summary>
    public long Capacity { get; set; }

    /// <summary>Bytes used so far.</summary>
    public long BytesUsed { get; set; }

    /// <summary>Number of times this disc has been erased and rewritten.</summary>
    public int RewriteCount { get; set; }

    /// <summary>Whether this is a multisession disc with room for more sessions.</summary>
    public bool IsMultisession { get; set; }

    /// <summary>Whether this disc has been marked as bad (unreadable or damaged).</summary>
    public bool IsBad { get; set; }

    public BurnSessionStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastWrittenUtc { get; set; }
}
