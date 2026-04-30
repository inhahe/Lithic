namespace LithicBackup.Core.Models;

/// <summary>
/// A logical grouping of discs that together back up one source tree.
/// </summary>
public class BackupSet
{
    public int Id { get; set; }

    /// <summary>User-chosen name for this backup set (e.g. "Photos 2024").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Root source path(s) this set covers.</summary>
    public List<string> SourceRoots { get; set; } = [];

    /// <summary>Maximum number of incremental discs before auto-consolidation.</summary>
    public int MaxIncrementalDiscs { get; set; } = 5;

    /// <summary>Default media type for new discs in this set.</summary>
    public MediaType DefaultMediaType { get; set; }

    /// <summary>Default filesystem type for new discs in this set.</summary>
    public FilesystemType DefaultFilesystemType { get; set; } = FilesystemType.UDF;

    /// <summary>
    /// Capacity override in bytes. When set, this value is used instead of
    /// the disc's reported capacity (e.g. 22.5 GB instead of 25 GB for M-Discs).
    /// Null means use the disc's reported capacity.
    /// </summary>
    public long? CapacityOverrideBytes { get; set; }

    /// <summary>
    /// Full source selection tree persisted as JSON. Stores only nodes that
    /// were explicitly configured (selected/deselected, non-default flags).
    /// Null for legacy sets that only have SourceRoots.
    /// </summary>
    public List<SourceSelection>? SourceSelections { get; set; }

    /// <summary>
    /// Job configuration options persisted as JSON so incremental runs
    /// reuse the same settings. Null for legacy sets.
    /// </summary>
    public JobOptions? JobOptions { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastBackupUtc { get; set; }
}
