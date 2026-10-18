namespace LithicBackup.Core.Models;

/// <summary>
/// Portable representation of a backup set configuration for export/import.
/// Contains everything needed to recreate a backup set on another machine
/// (or restore a lost configuration), but no runtime state like its Id.
///
/// <para>The backup history (the catalog records of what is already on the
/// destination) is not part of this object: it is a whole SQLite database,
/// often gigabytes, and travels beside it in the export file. See
/// <c>BackupSetTransfer</c> for the file format.</para>
/// </summary>
public class ExportedBackupSet
{
    public string Name { get; set; } = string.Empty;
    public List<string> SourceRoots { get; set; } = [];
    public int MaxIncrementalDiscs { get; set; } = 5;
    public MediaType DefaultMediaType { get; set; }
    public FilesystemType DefaultFilesystemType { get; set; } = FilesystemType.UDF;
    public long? CapacityOverrideBytes { get; set; }
    public List<SourceSelection>? SourceSelections { get; set; }
    public JobOptions? JobOptions { get; set; }

    /// <summary>
    /// When the set last completed a backup. Exported only together with the
    /// backup history: without the history, the imported set has backed up
    /// nothing as far as its catalog knows, and must say "Never backed up".
    /// </summary>
    public DateTime? LastBackupUtc { get; set; }

    /// <summary>Create an export from an existing backup set.</summary>
    public static ExportedBackupSet FromBackupSet(BackupSet set, bool includesHistory = false) => new()
    {
        Name = set.Name,
        SourceRoots = [.. set.SourceRoots],
        MaxIncrementalDiscs = set.MaxIncrementalDiscs,
        DefaultMediaType = set.DefaultMediaType,
        DefaultFilesystemType = set.DefaultFilesystemType,
        CapacityOverrideBytes = set.CapacityOverrideBytes,
        SourceSelections = set.SourceSelections,
        JobOptions = set.JobOptions,
        LastBackupUtc = includesHistory ? set.LastBackupUtc : null,
    };

    /// <summary>
    /// Create a new backup set from an imported configuration. Automated backups
    /// arrive paused (<see cref="JobOptions.HoldAutomatedBackups"/>) whatever the
    /// export said, so nothing writes to the destination until the user has run
    /// a backup from the app.
    /// </summary>
    /// <param name="includesHistory">
    /// Whether the backup history is being imported with it. Only then is
    /// <see cref="LastBackupUtc"/> carried over.
    /// </param>
    public BackupSet ToBackupSet(bool includesHistory = false)
    {
        var set = new BackupSet
        {
            Name = Name,
            SourceRoots = [.. SourceRoots],
            MaxIncrementalDiscs = MaxIncrementalDiscs,
            DefaultMediaType = DefaultMediaType,
            DefaultFilesystemType = DefaultFilesystemType,
            CapacityOverrideBytes = CapacityOverrideBytes,
            SourceSelections = SourceSelections,
            JobOptions = JobOptions,
            CreatedUtc = DateTime.UtcNow,
            LastBackupUtc = includesHistory ? LastBackupUtc : null,
        };
        if (set.JobOptions is not null)
            set.JobOptions.HoldAutomatedBackups = true;
        return set;
    }
}
