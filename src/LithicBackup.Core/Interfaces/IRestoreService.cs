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

    /// <summary>
    /// Integrity-test one already-burned disc against the catalog: confirm every
    /// non-deleted file recorded on the disc is physically present on the mounted
    /// media with the expected size, and — when <paramref name="verifyContents"/>
    /// is <c>true</c> — that its stored representation still reconstructs to the
    /// SHA-256 recorded in the catalog (catching bit-rot / a decaying disc).
    /// <para>
    /// <paramref name="discRoot"/> is the mounted volume root of the inserted disc
    /// (e.g. <c>"E:\"</c>). The check understands every on-disc storage form —
    /// plain, zipped, split, <c>.dedup</c> (block store), and <c>.fileref</c>
    /// (file-level dedup) — mirroring the restore reader. A <c>.fileref</c> whose
    /// backing plain copy lives on a different disc cannot be content-verified from
    /// this disc alone and is reported as an unresolved (cross-disc) reference
    /// rather than a hard failure.
    /// </para>
    /// </summary>
    Task<DiscVerifyResult> VerifyDiscAsync(
        int discId,
        string discRoot,
        bool verifyContents = false,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>The kind of problem found for one file during a disc integrity test.</summary>
public enum DiscFileIssueKind
{
    /// <summary>The file's stored data (or a chunk/block/manifest it needs) is missing on the disc.</summary>
    Missing,

    /// <summary>The stored file is present but the wrong size.</summary>
    SizeMismatch,

    /// <summary>The stored bytes no longer match the catalog's SHA-256 (corruption / bit-rot).</summary>
    ContentMismatch,

    /// <summary>The stored file exists but could not be read back (I/O error).</summary>
    Unreadable,

    /// <summary>A <c>.fileref</c> whose backing plain copy is not on this disc — cannot verify content here.</summary>
    UnresolvedReference,
}

/// <summary>One file that failed a disc integrity test.</summary>
public class DiscFileIssue
{
    /// <summary>Catalog id of the failing file record (used to re-burn just this file).</summary>
    public long FileRecordId { get; init; }

    public string SourcePath { get; init; } = string.Empty;
    public string DiscPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DiscFileIssueKind Kind { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>Outcome of a single disc integrity test.</summary>
public class DiscVerifyResult
{
    /// <summary>True when no failures were found.</summary>
    public bool Success => Issues.Count == 0;

    /// <summary>Number of catalog file records checked on the disc.</summary>
    public int FilesChecked { get; init; }

    /// <summary>Bytes of source data represented by the checked files.</summary>
    public long BytesChecked { get; init; }

    /// <summary>True when this run read stored data back and re-hashed it.</summary>
    public bool ContentsVerified { get; init; }

    /// <summary>True when the run was cancelled before finishing.</summary>
    public bool Cancelled { get; init; }

    public IReadOnlyList<DiscFileIssue> Issues { get; init; } = [];

    /// <summary>
    /// Distinct catalog ids of the failing files that can be re-burned (excludes
    /// unresolved cross-disc references, which aren't a defect of this disc).
    /// </summary>
    public IReadOnlyList<long> FailedFileRecordIds { get; init; } = [];
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
