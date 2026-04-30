namespace LithicBackup.Core.Models;

/// <summary>
/// One file (or file version) tracked in the backup catalog.
/// </summary>
public class FileRecord
{
    public long Id { get; set; }

    /// <summary>ID of the disc this file (or its chunks) resides on.</summary>
    public int DiscId { get; set; }

    /// <summary>Original full path on the source filesystem.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Path as stored on the disc. May differ from <see cref="SourcePath"/>
    /// if the file was zipped or renamed for compatibility.
    /// </summary>
    public string DiscPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 hash of the file contents.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Whether this file is stored inside a zip archive on the disc.</summary>
    public bool IsZipped { get; set; }

    /// <summary>Whether this file is split across multiple discs.</summary>
    public bool IsSplit { get; set; }

    /// <summary>Version number for files with multiple backups of the same source path.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Whether this file is stored in deduplicated block format (.dedup manifest).
    /// Tracked in the database because a source file could legitimately end in .dedup.
    /// </summary>
    public bool IsDeduped { get; set; }

    /// <summary>
    /// Whether this file is stored as a file-level dedup reference (.fileref manifest).
    /// The actual contents live in _filestore/{hash}.dat; the .fileref file is a small
    /// JSON pointer. Tracked in the database because a source file could legitimately
    /// end in .fileref.
    /// </summary>
    public bool IsFileRef { get; set; }

    /// <summary>
    /// Whether this file version has been marked as deleted by retention policy.
    /// Optical media cannot physically delete, but excluded from consolidation.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime SourceLastWriteUtc { get; set; }
    public DateTime BackedUpUtc { get; set; }
}
