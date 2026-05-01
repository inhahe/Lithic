using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>Callback for per-file failure decisions. Must be invoked on the UI thread.</summary>
public delegate Task<FailureDecision> FailureCallback(string filePath, string error);

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
}
