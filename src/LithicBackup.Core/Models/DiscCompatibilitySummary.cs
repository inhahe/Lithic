namespace LithicBackup.Core.Models;

/// <summary>
/// Result of a plan-time pass that checks each planned file's disc-relative path
/// against the selected <see cref="FilesystemType"/>'s name/path/depth limits. Files
/// that violate the limits are auto-zipped at burn time under
/// <see cref="ZipMode.IncompatibleOnly"/> (the default), so this summary tells the
/// user, before the burn starts, how much of the set will be silently zipped — and
/// lets them reconsider the format (e.g. switch to the more permissive UDF).
/// </summary>
/// <param name="FilesystemType">The format the compatibility was evaluated against.</param>
/// <param name="TotalFiles">Total planned files considered.</param>
/// <param name="TotalBytes">Total size of all planned files.</param>
/// <param name="IncompatibleFiles">Files whose disc path is incompatible with the format.</param>
/// <param name="IncompatibleBytes">Total size of the incompatible files.</param>
public sealed record DiscCompatibilitySummary(
    FilesystemType FilesystemType,
    int TotalFiles,
    long TotalBytes,
    int IncompatibleFiles,
    long IncompatibleBytes)
{
    /// <summary>True when at least one planned file would be zipped for compatibility.</summary>
    public bool HasIncompatible => IncompatibleFiles > 0;

    /// <summary>Fraction (0..1) of planned files that are incompatible.</summary>
    public double IncompatibleFileFraction =>
        TotalFiles > 0 ? (double)IncompatibleFiles / TotalFiles : 0.0;

    /// <summary>Fraction (0..1) of planned bytes that are incompatible.</summary>
    public double IncompatibleByteFraction =>
        TotalBytes > 0 ? (double)IncompatibleBytes / TotalBytes : 0.0;
}
