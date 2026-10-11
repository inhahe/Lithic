using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>Callback for per-file failure decisions. Must be invoked on the UI thread.</summary>
public delegate Task<FailureDecision> FailureCallback(
    string filePath, string error, BackupErrorCategory category);

/// <summary>
/// Invoked at a backup's periodic commit boundary — the only moment during a
/// long run when the catalog is fully committed and no transaction, per-set gate
/// or cross-process write lock is held. Lets a caller interleave its own catalog
/// work into a scan that would otherwise monopolise the set for hours.
/// </summary>
/// <param name="pathsThisRunWillWrite">
/// Every source path the running backup intends to write. The handler should
/// leave these to it: backing one up here copies the same file twice in one run.
/// (The run numbers each file's version as it writes it, from the catalog under
/// its write lock, so a version written here is followed, not duplicated.)
/// </param>
/// <remarks>
/// Runs inline, so the backup is paused for its duration — keep the work short
/// and self-throttled. Exceptions other than cancellation are swallowed by the
/// caller: this is opportunistic side work and must never fail a backup.
/// </remarks>
public delegate Task CommitBoundaryHandler(
    IReadOnlySet<string> pathsThisRunWillWrite, CancellationToken ct);

/// <summary>The user's decision for a file failure.</summary>
public class FailureDecision
{
    public BurnFailureAction Action { get; init; }
}

/// <summary>
/// Top-level orchestrator: plans, executes, and consolidates backups.
/// </summary>
public interface IBackupOrchestrator
{
    /// <summary>
    /// Scan sources, compute diff against catalog, allocate files to discs.
    /// </summary>
    Task<BackupPlan> PlanAsync(BackupJob job, CancellationToken ct = default,
        IProgress<ScanProgress>? scanProgress = null);

    /// <summary>
    /// Inspect a computed plan and report how many of its files would be auto-zipped
    /// for compatibility with the given disc <paramref name="filesystemType"/> (the
    /// same per-file check the burn applies under <see cref="ZipMode.IncompatibleOnly"/>).
    /// Lets the caller warn the user — and suggest a more permissive format such as
    /// UDF — before the burn silently zips a large fraction of the set.
    /// </summary>
    DiscCompatibilitySummary SummarizeCompatibility(BackupPlan plan, FilesystemType filesystemType);

    /// <summary>
    /// Execute the backup plan: stage files, burn discs, update catalog.
    /// </summary>
    Task<BackupResult> ExecuteAsync(
        BackupPlan plan,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute the backup plan with a per-file failure callback.
    /// </summary>
    Task<BackupResult> ExecuteAsync(
        BackupPlan plan,
        IProgress<BackupProgress>? progress = null,
        FailureCallback? onFailure = null,
        CancellationToken ct = default);

    /// <summary>
    /// Consolidate incremental discs when the limit is exceeded:
    /// re-burn data onto fewer discs.
    /// </summary>
    Task ConsolidateAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Consolidate incremental discs with progress reporting.
    /// </summary>
    Task ConsolidateAsync(int backupSetId, IProgress<BackupProgress>? progress, CancellationToken ct = default);

    /// <summary>
    /// Replace a bad disc: stage its files and burn them to a new disc.
    /// </summary>
    Task ReplaceDiscAsync(int badDiscId, string recorderId, CancellationToken ct = default);

    /// <summary>
    /// Replace a bad disc with progress reporting: mark it bad, re-stage every
    /// file it held from the live source, burn a fresh disc, and record the new
    /// disc + files in the catalog.
    /// </summary>
    Task ReplaceDiscAsync(int badDiscId, string recorderId, IProgress<BackupProgress>? progress, CancellationToken ct = default);

    /// <summary>
    /// Re-burn only the given file records (identified by catalog id) from one
    /// disc onto a new supplementary disc, then mark the old records superseded
    /// so restore resolves to the fresh copies. The original disc and its
    /// still-good files are left untouched. Used to repair just the files that
    /// failed a disc integrity test without discarding the whole disc.
    /// Returns the number of files actually re-burned (source files that no
    /// longer exist on disk are skipped).
    /// </summary>
    Task<int> ReplaceDiscFilesAsync(
        int discId,
        IReadOnlyCollection<long> fileRecordIds,
        string recorderId,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>The computed plan for a backup operation.</summary>
public class BackupPlan
{
    public BackupJob Job { get; init; } = new();
    public BackupDiff Diff { get; init; } = new();
    public IReadOnlyList<DiscAllocation> DiscAllocations { get; init; } = [];
    public int TotalDiscsRequired { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>Result of executing a backup plan.</summary>
public class BackupResult
{
    public bool Success { get; init; }
    public int DiscsWritten { get; init; }
    public long BytesWritten { get; init; }
    public IReadOnlyList<FailedFile> FailedFiles { get; init; } = [];
}

/// <summary>
/// A directory backup was cancelled. Still an <see cref="OperationCanceledException"/>,
/// but carrying what a caller needs to report the cancelled run honestly.
/// </summary>
/// <param name="failedFiles">Files that had failed before the cancellation, to list as for a finished run.</param>
/// <param name="copyFinished">
/// Every file had been copied and committed: only verification or retention was
/// cut short, so the backup itself is complete.
/// </param>
/// <param name="cancellation">Where the run stopped: kept as the inner exception.</param>
public sealed class BackupCanceledException(
    IReadOnlyList<FailedFile> failedFiles, bool copyFinished, OperationCanceledException cancellation)
    : OperationCanceledException("The backup was cancelled.", cancellation, cancellation.CancellationToken)
{
    public IReadOnlyList<FailedFile> FailedFiles { get; } = failedFiles;
    public bool CopyFinished { get; } = copyFinished;
}

/// <summary>A file that failed to back up.</summary>
public class FailedFile
{
    public string Path { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public BurnFailureAction ActionTaken { get; init; }
}

/// <summary>Overall progress of a backup execution.</summary>
public class BackupProgress
{
    public int CurrentDisc { get; init; }
    public int TotalDiscs { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
    public long BytesWrittenTotal { get; init; }
    public long BytesTotalAll { get; init; }
    public double OverallPercentage { get; init; }
    public BurnProgress? DiscBurnProgress { get; init; }

    /// <summary>Bytes written so far for the current file (0 if not tracked).</summary>
    public long CurrentFileBytesWritten { get; init; }

    /// <summary>Total size of the current file in bytes (0 if not tracked).</summary>
    public long CurrentFileTotalBytes { get; init; }

    /// <summary>
    /// Optional status message describing the current phase
    /// (e.g. "Scanning source files...", "Computing changes...").
    /// When null or empty, the UI shows default copy-progress text.
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// What is being done to <see cref="CurrentFile"/> when it is not the copy
    /// itself - e.g. "Checking for duplicates" while a file is read to hash it
    /// before deciding whether to copy it at all. Null means copying. Lets a long
    /// read show as progress on the right file instead of as a frozen display.
    /// </summary>
    public string? CurrentFileActivity { get; init; }

    /// <summary>
    /// For a report from a phase that works through files rather than bytes, such
    /// as the checks before the first file is copied: how many of the phase's
    /// <see cref="PhaseFilesTotal"/> files it has finished.
    /// <see cref="StatusMessage"/> names the phase, and <see cref="CurrentFile"/>
    /// the file it is on (empty when it is not working file by file).
    /// </summary>
    public long PhaseFilesDone { get; init; }

    /// <summary>
    /// Files the phase works through in all, or zero when the report is not from
    /// such a phase. Above zero, the display shows the phase's own progress
    /// instead of the byte totals.
    /// </summary>
    public long PhaseFilesTotal { get; init; }
}
