using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Service for restoring files from backup discs.
/// </summary>
public interface IRestoreService
{
    /// <summary>List all files in a backup set with their disc locations.</summary>
    Task<IReadOnlyList<RestorableFile>> GetRestorableFilesAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>Restore specific files. User must insert the required discs.</summary>
    Task<RestoreResult> RestoreAsync(
        IReadOnlyList<RestorableFile> files,
        string destinationDirectory,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// A file that can be restored from a backup set, with its disc location.
/// </summary>
public class RestorableFile
{
    /// <summary>Catalog record for this file.</summary>
    public FileRecord Record { get; init; } = new();

    /// <summary>Disc this file resides on.</summary>
    public DiscRecord Disc { get; init; } = new();

    /// <summary>Chunks if the file is split across discs.</summary>
    public IReadOnlyList<FileChunk> Chunks { get; init; } = [];
}

/// <summary>
/// Result of a restore operation.
/// </summary>
public class RestoreResult
{
    public bool Success { get; init; }
    public int FilesRestored { get; init; }
    public long BytesRestored { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Progress update during a restore operation.
/// </summary>
public class RestoreProgress
{
    public string CurrentFile { get; init; } = string.Empty;
    public int FilesCompleted { get; init; }
    public int TotalFiles { get; init; }
    public long BytesCompleted { get; init; }
    public long TotalBytes { get; init; }
    public double Percentage { get; init; }

    /// <summary>
    /// Disc ID that needs to be inserted. Null if no disc change is needed.
    /// </summary>
    public int? RequiredDiscId { get; init; }

    /// <summary>
    /// Label of the disc that needs to be inserted.
    /// </summary>
    public string? RequiredDiscLabel { get; init; }
}
