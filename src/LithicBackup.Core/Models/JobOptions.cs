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

    /// <summary>
    /// Whether the backup lives in a folder of its own inside the destination,
    /// named <see cref="SubdirectoryName"/>. See <see cref="BackupRoot"/>.
    /// </summary>
    public bool CreateSubdirectory { get; set; }
    public string? SubdirectoryName { get; set; }
    public List<string> ExcludedExtensions { get; set; } = [];

    /// <summary>
    /// The folder the set's backup lives in: its stored files' destination-relative
    /// paths start here. The destination, plus <see cref="SubdirectoryName"/> when
    /// <see cref="CreateSubdirectory"/> is on and it names one. Every reader and
    /// writer of the backup goes through this, so none can look in, or delete
    /// from, the folder above.
    /// </summary>
    /// <param name="destination">
    /// The destination as found just now (a drive followed to a new letter); null
    /// for <see cref="TargetDirectory"/> as stored.
    /// </param>
    /// <returns>Null when the set has no destination (a disc set).</returns>
    public string? BackupRoot(string? destination = null)
        => BackupRootFor(destination ?? TargetDirectory, CreateSubdirectory, SubdirectoryName);

    /// <summary>
    /// <see cref="BackupRoot"/> from values not yet saved (an editor's fields).
    /// </summary>
    public static string? BackupRootFor(string? destination, bool createSubdirectory, string? subdirectoryName)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return null;
        return createSubdirectory && !string.IsNullOrWhiteSpace(subdirectoryName)
            ? System.IO.Path.Combine(destination, subdirectoryName.Trim())
            : destination;
    }

    /// <summary>
    /// Whether two paths name the same backup folder in the sense the catalog's
    /// disc labels are matched to a set's backup folder: whatever the drive
    /// letter (a drive can come back under another one, which the destination
    /// resolver follows without relabelling anything), the case, and the kind and
    /// number of trailing separators. Everything that decides whether a label is
    /// the set's folder calls this, so that they cannot disagree:
    /// <see cref="Interfaces.ICatalogRepository.FindLiveRecordsOutsideAsync"/>,
    /// the editor's question before moving the folder, and Change Destination's
    /// relabelling.
    /// </summary>
    public static bool SameFolderOnAnyDrive(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(FolderWithoutDrive(a), FolderWithoutDrive(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A folder path without its first two characters - the drive ("X:"), or a
    /// UNC path's leading "\\" - with '/' read as '\' and no trailing separator.
    /// </summary>
    public static string FolderWithoutDrive(string path)
    {
        string p = path.Replace('/', '\\');
        return (p.Length > 2 ? p[2..] : "").TrimEnd('\\');
    }

    /// <summary>
    /// Automated backup schedule. Null or <c>Enabled = false</c> means
    /// manual-only. Stored as JSON — backward-compatible with older sets
    /// that lack this field.
    /// </summary>
    public BackupSchedule? Schedule { get; set; }

    /// <summary>
    /// Automated backups (the Worker's schedule and continuous runs) are paused
    /// until a backup is run from the app. Cleared by the first full backup the
    /// GUI completes for the set, or by the row's Resume link.
    ///
    /// <para>Set whenever the catalog stops being a trustworthy record of the
    /// destination: when the set is imported (on a new machine its destination
    /// and sources may be at other paths, and without history it would copy
    /// everything again), when its history is cleared, and when it is seeded
    /// (a full backup that started mid-seed would treat every file the seed had
    /// not reached yet as new). Without it an imported set with an overdue
    /// schedule would start a full backup within a minute of the import, before
    /// anyone had looked at it.</para>
    ///
    /// <para>Kept out of the editor's unsaved-changes comparison
    /// (<c>MainViewModel.SnapshotEditorSettings</c>): it is written straight to
    /// the catalog, never by the editor's Save.</para>
    /// </summary>
    public bool HoldAutomatedBackups { get; set; }
}
