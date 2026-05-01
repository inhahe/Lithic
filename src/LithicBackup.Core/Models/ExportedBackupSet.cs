namespace LithicBackup.Core.Models;

/// <summary>
/// Portable representation of a backup set configuration for export/import.
/// Contains everything needed to recreate a backup set on another machine
/// (or restore a lost configuration), but no runtime state like Id or timestamps.
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

    /// <summary>Create an export from an existing backup set.</summary>
    public static ExportedBackupSet FromBackupSet(BackupSet set) => new()
    {
        Name = set.Name,
        SourceRoots = [.. set.SourceRoots],
        MaxIncrementalDiscs = set.MaxIncrementalDiscs,
        DefaultMediaType = set.DefaultMediaType,
        DefaultFilesystemType = set.DefaultFilesystemType,
        CapacityOverrideBytes = set.CapacityOverrideBytes,
        SourceSelections = set.SourceSelections,
        JobOptions = set.JobOptions,
    };

    /// <summary>Create a new backup set from an imported configuration.</summary>
    public BackupSet ToBackupSet() => new()
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
    };
}
