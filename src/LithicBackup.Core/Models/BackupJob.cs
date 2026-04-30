namespace LithicBackup.Core.Models;

/// <summary>
/// A single backup operation to execute: what to back up, where, and how.
/// </summary>
public class BackupJob
{
    /// <summary>The backup set this job belongs to (null for a new set).</summary>
    public int? BackupSetId { get; set; }

    /// <summary>Source file/directory selections.</summary>
    public List<SourceSelection> Sources { get; set; } = [];

    /// <summary>Whether to include the backup catalog database on a disc.</summary>
    public bool IncludeCatalogOnDisc { get; set; } = true;

    /// <summary>Whether to include the backup software on a disc.</summary>
    public bool IncludeSoftwareOnDisc { get; set; }

    /// <summary>Whether to zip all files, or only those with incompatible paths.</summary>
    public ZipMode ZipMode { get; set; } = ZipMode.IncompatibleOnly;

    /// <summary>
    /// Whether to enable file-level deduplication. Whole-file matching by SHA-256 hash.
    /// Cheaper than block-level dedup and effective when many files are exact copies.
    /// Identical files are stored once in _filestore/{hash}.dat and referenced via
    /// .fileref manifests. Checked before block-level dedup when both are enabled.
    /// </summary>
    public bool EnableFileDeduplication { get; set; }

    /// <summary>Whether to enable block-level deduplication.</summary>
    public bool EnableDeduplication { get; set; }

    /// <summary>
    /// Block size in bytes for deduplication (when enabled).
    /// NOTE: Changing the block size between backup runs is safe — each .dedup
    /// manifest records the block size it was written with, so old manifests always
    /// restore correctly. However, dedup efficiency drops across block size changes
    /// because different chunk boundaries produce different hashes; blocks from
    /// earlier runs will not match blocks from new runs.
    /// </summary>
    public int DeduplicationBlockSize { get; set; } = 64 * 1024; // 64 KB default

    /// <summary>Whether to allow splitting large files across discs.</summary>
    public bool AllowFileSplitting { get; set; } = true;

    /// <summary>
    /// Whether to split smaller files across discs to maximize space usage
    /// (not just files too large for a single disc).
    /// </summary>
    public bool AggressiveSplitting { get; set; }

    /// <summary>Whether to verify data after burning.</summary>
    public bool VerifyAfterBurn { get; set; } = true;

    /// <summary>
    /// Version retention tiers for directory-mode backups. When empty, the default
    /// tiers from <see cref="Services.VersionRetentionService.DefaultTiers"/> are used.
    /// </summary>
    public List<VersionRetentionTier> RetentionTiers { get; set; } = [];

    /// <summary>When non-null, back up to this directory instead of optical media.</summary>
    public string? TargetDirectory { get; set; }

    /// <summary>
    /// Glob patterns to exclude from the backup (e.g. "*.log", "temp_*", "debug*.txt").
    /// Also accepts legacy extension forms (".log", "log") which are treated as "*.log".
    /// Matched case-insensitively against the file name via <see cref="GlobMatcher"/>.
    /// </summary>
    public List<string> ExcludedExtensions { get; set; } = [];

    /// <summary>Filesystem type to use when burning discs.</summary>
    public FilesystemType FilesystemType { get; set; } = FilesystemType.UDF;

    /// <summary>
    /// Optional capacity override in bytes. When set, this value is used instead
    /// of the disc's reported capacity (e.g. for M-Disc media).
    /// Null means use the disc's reported capacity.
    /// </summary>
    public long? CapacityOverrideBytes { get; set; }
}

public enum ZipMode
{
    /// <summary>Store files with their original names. No zipping.</summary>
    None,

    /// <summary>Only zip files whose names/paths are incompatible with the disc filesystem.</summary>
    IncompatibleOnly,

    /// <summary>Zip all files.</summary>
    All,
}
