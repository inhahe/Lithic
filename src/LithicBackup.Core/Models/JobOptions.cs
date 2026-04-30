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
    public bool IncludeCatalogOnDisc { get; set; } = true;
    public bool AllowFileSplitting { get; set; } = true;
    public bool EnableFileDeduplication { get; set; }
    public bool EnableDeduplication { get; set; }
    public int DeduplicationBlockSize { get; set; } = 64 * 1024;
    public List<VersionRetentionTier> RetentionTiers { get; set; } = [];
    public string? TargetDirectory { get; set; }
    public bool CreateSubdirectory { get; set; }
    public string? SubdirectoryName { get; set; }
    public List<string> ExcludedExtensions { get; set; } = [];
}
