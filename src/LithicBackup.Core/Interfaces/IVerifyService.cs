using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Service for verifying the integrity of a directory-mode backup against its
/// source: every source file is still represented in the backup, every catalog
/// record's backing file actually exists on disk, and every <c>.fileref</c>
/// resolves to an existing plain copy of its content.
/// </summary>
public interface IVerifyService
{
    /// <summary>
    /// Verify a backup set's integrity. <paramref name="targetDirectory"/> is the
    /// directory the set was backed up into (where the file tree, <c>_prev</c>
    /// versions, and <c>_blocks</c> store live).
    /// <para>
    /// When <paramref name="verifyContents"/> is <c>false</c> (the default) the
    /// check is fast and metadata-only: it confirms every backing file,
    /// <c>.fileref</c> target, <c>.dedup</c> manifest, <em>and every block the
    /// manifest references</em> exists on disk. When <c>true</c> it additionally
    /// reads every stored file/block back and compares its SHA-256 against the
    /// hash recorded in the catalog (or, for blocks, the block's content-addressed
    /// name) — detecting silent corruption / bit-rot at the cost of reading the
    /// whole backup.
    /// </para>
    /// </summary>
    Task<VerifyResult> VerifyAsync(
        BackupJob job,
        string targetDirectory,
        bool verifyContents = false,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>The kind of problem a <see cref="VerifyIssue"/> describes.</summary>
public enum VerifyIssueKind
{
    /// <summary>A current source file has no active record in the catalog.</summary>
    MissingFromBackup,

    /// <summary>A catalog record's backing file is missing on disk.</summary>
    BackingFileMissing,

    /// <summary>A <c>.fileref</c> has no backing plain copy of its content.</summary>
    FileRefUnresolved,

    /// <summary>A block referenced by a <c>.dedup</c> manifest is missing.</summary>
    BlockMissing,

    /// <summary>
    /// A stored file/block's content no longer matches its recorded SHA-256
    /// (silent corruption / bit-rot). Only detected when content verification
    /// is enabled.
    /// </summary>
    ContentMismatch,
}

/// <summary>A single integrity problem found during verification.</summary>
public class VerifyIssue
{
    /// <summary>Source path (or disc path) the problem relates to.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Human-readable description of the problem.</summary>
    public string Detail { get; init; } = string.Empty;

    public VerifyIssueKind Kind { get; init; }
}

/// <summary>Outcome of a verification run.</summary>
public class VerifyResult
{
    /// <summary>True when no issues were found.</summary>
    public bool Success => Issues.Count == 0;

    /// <summary>Number of current source files checked for backup coverage.</summary>
    public int SourceFilesChecked { get; init; }

    /// <summary>Number of catalog records whose backing storage was checked.</summary>
    public int RecordsChecked { get; init; }

    /// <summary>Number of <c>.fileref</c> records whose resolution was checked.</summary>
    public int FileRefsChecked { get; init; }

    /// <summary>Whether the run was cancelled before completing.</summary>
    public bool Cancelled { get; init; }

    /// <summary>True when this run read and re-hashed stored content (not just existence).</summary>
    public bool ContentsVerified { get; init; }

    /// <summary>Number of files/blocks whose content was read back and re-hashed.</summary>
    public int ItemsHashed { get; init; }

    public IReadOnlyList<VerifyIssue> Issues { get; init; } = [];
}

/// <summary>Progress update during verification.</summary>
public class VerifyProgress
{
    public string? StatusMessage { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
    public int ItemsChecked { get; init; }
    public int TotalItems { get; init; }
    public double Percentage { get; init; }
}
