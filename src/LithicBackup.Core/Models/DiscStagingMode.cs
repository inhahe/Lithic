namespace LithicBackup.Core.Models;

/// <summary>
/// How plain (unzipped, unsplit) files are provided to the disc burner.
/// </summary>
public enum DiscStagingMode
{
    /// <summary>
    /// Copy each file to a temporary staging directory first, then burn from
    /// there. Safe and simple, but requires enough free temp space to hold a
    /// full disc's worth of data (e.g. up to ~100&#160;GB for a Blu-ray). The
    /// source is only locked briefly while it is copied.
    /// </summary>
    TemporaryCopy,

    /// <summary>
    /// Burn plain files directly from their original location, with no temporary
    /// copy. The orchestrator holds a read lock (<see cref="System.IO.FileShare.Read"/>)
    /// on each source file from the moment its size is validated until the burn
    /// finishes, so the file cannot be modified, grow, or be deleted mid-burn.
    /// Avoids needing temp space for large media, at the cost of holding source
    /// files locked for the whole burn. Zipped and split files still stage to
    /// temp because their on-disc bytes differ from the source.
    /// </summary>
    InPlace,
}
