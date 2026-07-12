namespace LithicBackup.Core.Models;

/// <summary>
/// Records the stable volume-GUID identity of one of a backup set's source
/// drives, so the set can follow that drive across Windows drive-letter
/// reassignments — the source analogue of
/// <see cref="JobOptions.DestinationVolumeId"/>.
/// </summary>
public class SourceVolumeMapping
{
    /// <summary>Last-known drive-letter root of the volume, e.g. <c>"E:"</c>.</summary>
    public string DriveLetter { get; set; } = string.Empty;

    /// <summary>
    /// Stable volume GUID path (<c>\\?\Volume{GUID}\</c>) of the source drive.
    /// Survives drive-letter reassignments.
    /// </summary>
    public string VolumeId { get; set; } = string.Empty;
}
