namespace LithicBackup.Core.Models;

/// <summary>
/// Filesystem type to use when creating a disc image.
/// </summary>
public enum FilesystemType
{
    /// <summary>Standard ISO 9660 — most compatible, 8.3 filenames, 8-level directory depth.</summary>
    ISO9660,

    /// <summary>Joliet extension — long filenames (up to 64 chars), Unicode.</summary>
    Joliet,

    /// <summary>Universal Disc Format — long paths, large files, required for Blu-Ray.</summary>
    UDF,
}
