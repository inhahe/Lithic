using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Default <see cref="IDestinationResolver"/>: resolves a stored destination to
/// a live path via <see cref="IVolumeResolver"/>, follows drive-letter changes,
/// and backfills the stable volume identity for sets created before the feature.
/// </summary>
public sealed class DestinationResolver : IDestinationResolver
{
    private readonly IVolumeResolver _volumes;

    public DestinationResolver(IVolumeResolver volumes)
    {
        _volumes = volumes;
    }

    public DestinationResolution Resolve(JobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? cached = options.TargetDirectory;

        // No destination configured at all.
        if (string.IsNullOrWhiteSpace(options.DestinationVolumeId)
            && string.IsNullOrWhiteSpace(cached))
        {
            return new DestinationResolution(
                LivePath: null, IsConnected: false, LetterChanged: false,
                PreviousPath: null, MetadataChanged: false);
        }

        // --- Unmigrated set: try to backfill the stable identity. ---
        if (string.IsNullOrWhiteSpace(options.DestinationVolumeId))
        {
            string? volumeId = _volumes.GetVolumeId(cached!);
            if (volumeId is null)
            {
                // Volume not connectable right now — keep using the cached path,
                // backfill on a later run when the drive is present.
                return new DestinationResolution(
                    LivePath: cached,
                    IsConnected: Directory.Exists(cached!),
                    LetterChanged: false,
                    PreviousPath: cached,
                    MetadataChanged: false);
            }

            string? mount = _volumes.GetCurrentMountPoint(volumeId);
            string subpath = mount is not null ? RelativeSubpath(mount, cached!) : "";

            options.DestinationVolumeId = volumeId;
            options.DestinationSubpath = subpath;
            // cached is already the live path (we just resolved it), so keep it.

            return new DestinationResolution(
                LivePath: cached,
                IsConnected: true,
                LetterChanged: false,
                PreviousPath: cached,
                MetadataChanged: true);
        }

        // --- Identity known: resolve the current mount point. ---
        string? currentMount = _volumes.GetCurrentMountPoint(options.DestinationVolumeId);
        if (currentMount is null)
        {
            // Drive not connected; surface the best-known path for display only.
            return new DestinationResolution(
                LivePath: null, IsConnected: false, LetterChanged: false,
                PreviousPath: cached, MetadataChanged: false);
        }

        string subpathPart = options.DestinationSubpath ?? "";
        string livePath = string.IsNullOrEmpty(subpathPart)
            ? currentMount
            : Path.Combine(currentMount, subpathPart);

        bool changed = !PathsEqual(livePath, cached);
        if (changed)
            options.TargetDirectory = livePath;

        return new DestinationResolution(
            LivePath: livePath,
            IsConnected: true,
            LetterChanged: changed,
            PreviousPath: cached,
            MetadataChanged: changed);
    }

    /// <summary>Path of <paramref name="fullPath"/> relative to a volume mount root.</summary>
    private static string RelativeSubpath(string mountRoot, string fullPath)
    {
        try
        {
            string rel = Path.GetRelativePath(mountRoot, fullPath);
            // GetRelativePath returns "." when the two are equal (path == root).
            return rel == "." ? "" : rel;
        }
        catch
        {
            return "";
        }
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return string.Equals(
            a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
