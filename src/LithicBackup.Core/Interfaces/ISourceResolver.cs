using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Follows a backup set's source drives across Windows drive-letter
/// reassignments (the source analogue of <see cref="IDestinationResolver"/>),
/// and reports which configured source locations are currently unavailable so
/// the caller can warn the user instead of silently backing up nothing.
/// </summary>
public interface ISourceResolver
{
    /// <summary>
    /// Resolve <paramref name="set"/>'s sources against the current volume
    /// layout.
    ///
    /// <para>
    /// Rewrites source drive letters in <see cref="BackupSet.SourceRoots"/> and
    /// <see cref="BackupSet.SourceSelections"/> in place when a tracked source
    /// volume has moved to a different letter, and backfills
    /// <see cref="JobOptions.SourceVolumeMappings"/> for pre-feature sets. When
    /// <see cref="SourceResolution.MetadataChanged"/> is true the caller should
    /// persist the set.
    /// </para>
    /// </summary>
    SourceResolution Resolve(BackupSet set);
}

/// <summary>Outcome of resolving a set's sources to the current volume layout.</summary>
/// <param name="MetadataChanged">
/// True when source paths or the stored source-volume identities were updated,
/// meaning the set should be persisted.
/// </param>
/// <param name="LetterChanges">
/// Human-readable "E: → F:" notes for each source drive that moved.
/// </param>
/// <param name="MissingSources">
/// Configured top-level source locations that are not currently present on disk
/// (e.g. <c>"E:\Photos"</c>) — their files would be silently skipped by the scan.
/// </param>
/// <param name="AnyAvailable">
/// True when at least one configured source location currently exists on disk.
/// </param>
public sealed record SourceResolution(
    bool MetadataChanged,
    IReadOnlyList<string> LetterChanges,
    IReadOnlyList<string> MissingSources,
    bool AnyAvailable);
