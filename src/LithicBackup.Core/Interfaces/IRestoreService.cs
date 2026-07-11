using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Service for restoring files from backup discs.
/// </summary>
public interface IRestoreService
{
    /// <summary>List all files in a backup set with their disc locations.</summary>
    Task<IReadOnlyList<RestorableFile>> GetRestorableFilesAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Restore specific files, routing each to a destination chosen per source
    /// drive. <paramref name="driveDestinations"/> maps an uppercase drive
    /// letter (e.g. <c>"D"</c>) to the directory under which that drive's files
    /// are recreated, preserving their path below the drive root. For example,
    /// mapping <c>"D"</c> to <c>E:\restored</c> restores <c>D:\docs\a.txt</c> to
    /// <c>E:\restored\docs\a.txt</c>; mapping <c>"D"</c> to <c>D:\</c> restores
    /// it to its original location. User must insert the required discs.
    /// </summary>
    Task<RestoreResult> RestoreAsync(
        IReadOnlyList<RestorableFile> files,
        IReadOnlyDictionary<string, string> driveDestinations,
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
