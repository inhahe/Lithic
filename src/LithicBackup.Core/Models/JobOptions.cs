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

    /// <summary>
    /// The last-resolved full destination path (e.g. <c>"E:\Backups\Photos"</c>).
    /// This is a <i>cache</i> for display and backward compatibility, kept in
    /// sync with the current drive letter; the durable identity is
    /// <see cref="DestinationVolumeId"/> + <see cref="DestinationSubpath"/>.
    /// When the volume cannot be resolved (drive not connected) this is the
    /// best-known path to show the user.
    /// </summary>
    public string? TargetDirectory { get; set; }

    /// <summary>
    /// Stable volume GUID path (<c>\\?\Volume{GUID}\</c>) of the destination
    /// drive.  Survives Windows drive-letter reassignments.  Null for sets
    /// created before the volume-identity feature or whose volume has never
    /// been resolvable; such sets fall back to <see cref="TargetDirectory"/>
    /// and are backfilled the first time the volume resolves.
    /// </summary>
    public string? DestinationVolumeId { get; set; }

    /// <summary>
    /// Destination path relative to the volume root (e.g. <c>"Backups\Photos"</c>).
    /// Combined with the volume's current mount point to form the live target
    /// path.  Null when <see cref="DestinationVolumeId"/> is null.
    /// </summary>
    public string? DestinationSubpath { get; set; }

    /// <summary>
    /// Stable volume-GUID identities for the set's source drives, so sources
    /// follow their drives across drive-letter reassignments (the source
    /// analogue of <see cref="DestinationVolumeId"/>). One entry per distinct
    /// source drive, keyed by its last-known drive letter. Empty for sets
    /// created before the feature; backfilled on the first run where each
    /// source drive is present.
    /// </summary>
    public List<SourceVolumeMapping> SourceVolumeMappings { get; set; } = [];

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
