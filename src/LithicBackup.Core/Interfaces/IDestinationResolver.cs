using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Turns a backup set's stored destination (a stable volume GUID + subpath,
/// with a drive-letter path cached for display) into the concrete path to use
/// for a run, following the volume across drive-letter reassignments.
/// </summary>
public interface IDestinationResolver
{
    /// <summary>
    /// Resolve <paramref name="options"/>' destination to a live path.
    ///
    /// <para>
    /// May mutate the destination cache fields on <paramref name="options"/>
    /// (<see cref="JobOptions.TargetDirectory"/>, and a one-time backfill of
    /// <see cref="JobOptions.DestinationVolumeId"/> /
    /// <see cref="JobOptions.DestinationSubpath"/>).  When
    /// <see cref="DestinationResolution.MetadataChanged"/> is true the caller
    /// should persist the updated <see cref="JobOptions"/>.
    /// </para>
    /// </summary>
    DestinationResolution Resolve(JobOptions options);
}

/// <summary>Outcome of resolving a destination to a live path.</summary>
/// <param name="LivePath">
/// The full path to use right now (current mount point + subpath), or
/// <c>null</c> when the destination volume is not currently connected.
/// </param>
/// <param name="IsConnected">Whether the destination volume is currently mounted.</param>
/// <param name="LetterChanged">
/// True when the resolved path differs from the previously cached path because
/// the volume's drive letter (or mount point) moved — the signal to notify the
/// user.
/// </param>
/// <param name="PreviousPath">The cached path before this resolution (for the notice).</param>
/// <param name="MetadataChanged">
/// True when this call updated the destination cache/identity fields on the
/// <see cref="JobOptions"/>, meaning the set should be persisted.
/// </param>
public sealed record DestinationResolution(
    string? LivePath,
    bool IsConnected,
    bool LetterChanged,
    string? PreviousPath,
    bool MetadataChanged);
