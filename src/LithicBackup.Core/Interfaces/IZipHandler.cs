namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Handles zipping files for disc storage (incompatible paths or user preference).
/// </summary>
public interface IZipHandler
{
    /// <summary>
    /// Create a zip archive containing the specified file, stored under a
    /// disc-filesystem-compatible name. The archive filename is chosen to
    /// avoid collisions with existing files in <paramref name="outputDirectory"/>.
    /// </summary>
    /// <returns>Full path to the created zip file.</returns>
    Task<string> ZipFileAsync(
        string sourceFilePath,
        string outputDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Create a zip archive containing multiple failed files.
    /// </summary>
    /// <returns>Full path to the created zip file.</returns>
    Task<string> ZipFilesAsync(
        IReadOnlyList<string> sourceFilePaths,
        string outputDirectory,
        CancellationToken ct = default);

    /// <summary>
    /// Check whether a file's path is compatible with the target disc filesystem.
    /// </summary>
    bool IsPathCompatible(string filePath, Models.FilesystemType filesystemType);
}
