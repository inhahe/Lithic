namespace LithicBackup.Core.Models;

/// <summary>
/// Serializable snapshot of job configuration options, stored as JSON
/// on BackupSet to persist settings between sessions.
/// </summary>
public class JobOptions
{
    public ZipMode ZipMode { get; set; } = ZipMode.IncompatibleOnly;
    public FilesystemType FilesystemType { get; set; } = FilesystemType.UDF;
    public long? CapacityOverrideBytes { get; set; }
    public bool VerifyAfterBurn { get; set; } = true;
    public bool VerifyAfterBackup { get; set; }
    public bool IncludeCatalogOnDisc { get; set; } = true;
    public bool AllowFileSplitting { get; set; } = true;
    public bool EnableFileDeduplication { get; set; }
    public bool EnableDeduplication { get; set; }
    public int DeduplicationBlockSize { get; set; } = 64 * 1024;
    public List<VersionRetentionTier> RetentionTiers { get; set; } = [];

    /// <summary>
    /// Named version tier sets. Each set contains a name and a list of
    /// retention tiers. Built-in sets ("Default" and "None") are always
    /// available; this list stores user-defined sets and any modifications
    /// to the "Default" set's tiers.
    /// </summary>
    public List<VersionTierSet> TierSets { get; set; } = [];

    public string? TargetDirectory { get; set; }
    public bool CreateSubdirectory { get; set; }
    public string? SubdirectoryName { get; set; }
    public List<string> ExcludedExtensions { get; set; } = [];

    /// <summary>
    /// Automated backup schedule. Null or <c>Enabled = false</c> means
    /// manual-only. Stored as JSON — backward-compatible with older sets
    /// that lack this field.
    /// </summary>
    public BackupSchedule? Schedule { get; set; }
}
