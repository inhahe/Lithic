using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Enumerates files from source selections and detects changes since last backup.
/// </summary>
public interface IFileScanner
{
    /// <summary>
    /// Scan source directories according to the selection tree and return all
    /// files that should be included in the backup.
    /// </summary>
    /// <param name="isExcluded">
    /// Optional predicate that tests a full file path. Returns true if the file
    /// should be excluded. Build with <see cref="Core.GlobMatcher.CreateFilter"/>
    /// for glob/wildcard pattern matching.
    /// </param>
    /// <param name="isDirectoryExcluded">
    /// Optional predicate testing whether EVERY file beneath a directory is
    /// excluded, so the scan can skip the subtree instead of walking it and
    /// rejecting each file. Build with
    /// <c>DirectoryBackupService.BuildDirectoryPruneFilter</c>, which only
    /// answers true when it can prove it — a wrong answer here means files are
    /// silently never backed up.
    /// </param>
    Task<IReadOnlyList<ScannedFile>> ScanAsync(
        IReadOnlyList<SourceSelection> sources,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        Func<string, bool>? isExcluded = null,
        Func<string, bool>? isDirectoryExcluded = null);

    /// <summary>
    /// Compare scanned files against the catalog to find new, changed, and deleted files.
    /// </summary>
    Task<BackupDiff> ComputeDiffAsync(
        IReadOnlyList<ScannedFile> scannedFiles,
        int backupSetId,
        CancellationToken ct = default);
}

/// <summary>A file found during scanning.</summary>
public class ScannedFile
{
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastWriteUtc { get; init; }
}

/// <summary>Progress during a source scan.</summary>
public class ScanProgress
{
    public string CurrentDirectory { get; init; } = string.Empty;
    public int FilesFound { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>
    /// Directories visited so far.
    ///
    /// <para>Reported because <see cref="FilesFound"/> counts only files that
    /// will actually be backed up, so a scan crossing a large excluded tree sits
    /// on an unchanging number for a long time and looks hung. It is not hung;
    /// this is the counter that proves it.</para>
    /// </summary>
    public int DirectoriesScanned { get; init; }

    /// <summary>
    /// Number of files skipped because they were already present in the
    /// catalog.  Currently only reported by the seed-from-existing flow,
    /// which checks for existing SourcePath records to stay idempotent.
    /// </summary>
    public int FilesSkipped { get; init; }
}

/// <summary>Diff between current source state and last backup.</summary>
public class BackupDiff
{
    public IReadOnlyList<ScannedFile> NewFiles { get; init; } = [];
    public IReadOnlyList<ScannedFile> ChangedFiles { get; init; } = [];
    public IReadOnlyList<FileRecord> DeletedFiles { get; init; } = [];
}
