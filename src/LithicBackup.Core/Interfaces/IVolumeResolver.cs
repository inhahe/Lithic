namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Resolves between drive letters (which Windows can reassign at any time) and
/// the stable per-volume GUID identity that survives those reassignments.
///
/// <para>
/// Backup destinations are presented to the user as ordinary drive letters, but
/// stored durably as a volume GUID path plus a relative subpath.  At use time
/// the GUID is resolved back to its current mount point, so a drive-letter
/// change is transparent: Lithic follows the volume, notices the letter moved,
/// and updates what it shows the user.
/// </para>
/// </summary>
public interface IVolumeResolver
{
    /// <summary>
    /// Get the stable volume GUID path (<c>\\?\Volume{GUID}\</c>) for the volume
    /// that contains <paramref name="path"/>, or <c>null</c> if it cannot be
    /// determined (e.g. the path's volume is not currently mounted).
    /// </summary>
    string? GetVolumeId(string path);

    /// <summary>
    /// Get the current primary mount point (e.g. <c>"E:\"</c>) for the volume
    /// identified by <paramref name="volumeId"/> (a <c>\\?\Volume{GUID}\</c>
    /// path), or <c>null</c> if the volume is not currently connected/mounted.
    /// Prefers a drive-letter root over a mounted-folder path when both exist.
    /// </summary>
    string? GetCurrentMountPoint(string volumeId);

    /// <summary>
    /// Get a human-friendly volume label (e.g. <c>"Archive"</c>) for display,
    /// or <c>null</c> if unavailable.  Never used as an identity — only for UI.
    /// </summary>
    string? GetVolumeLabel(string volumeId);
}
