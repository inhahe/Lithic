using System.IO.Compression;
using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Zips files for disc storage — either because their names/paths are
/// incompatible with the disc filesystem, or because the user wants all
/// files zipped.
/// </summary>
public class ZipHandler : IZipHandler
{
    public async Task<string> ZipFileAsync(
        string sourceFilePath,
        string outputDirectory,
        CancellationToken ct = default)
    {
        string zipName = GenerateZipFilename(sourceFilePath, outputDirectory);
        string zipPath = Path.Combine(outputDirectory, zipName);

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(Path.GetFileName(sourceFilePath), CompressionLevel.Optimal);

        await using var entryStream = entry.Open();
        await using var sourceStream = new FileStream(
            sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        await sourceStream.CopyToAsync(entryStream, ct);

        return zipPath;
    }

    public async Task<string> ZipFilesAsync(
        IReadOnlyList<string> sourceFilePaths,
        string outputDirectory,
        CancellationToken ct = default)
    {
        string zipName = $"discburn-batch-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        zipName = EnsureUniqueFilename(zipName, outputDirectory);
        string zipPath = Path.Combine(outputDirectory, zipName);

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        // Track entry names to avoid duplicates within the zip.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in sourceFilePaths)
        {
            ct.ThrowIfCancellationRequested();

            string entryName = Path.GetFileName(filePath);
            if (!usedNames.Add(entryName))
            {
                // Duplicate filename — prepend a counter.
                int counter = 2;
                string baseName = Path.GetFileNameWithoutExtension(filePath);
                string ext = Path.GetExtension(filePath);
                do
                {
                    entryName = $"{baseName}_{counter}{ext}";
                    counter++;
                } while (!usedNames.Add(entryName));
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            await using var entryStream = entry.Open();
            await using var sourceStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            await sourceStream.CopyToAsync(entryStream, ct);
        }

        return zipPath;
    }

    public bool IsPathCompatible(string filePath, FilesystemType filesystemType)
        => PathCompatibility.CheckCompatibility(filePath, filesystemType) is null;

    /// <summary>
    /// Generate a collision-safe zip filename based on the source file's hash.
    /// </summary>
    private static string GenerateZipFilename(string sourceFilePath, string directory)
    {
        // Use a truncated hash of the full source path to generate a unique name.
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(sourceFilePath);
        var hash = SHA256.HashData(pathBytes);
        string hashHex = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        string zipName = $"{hashHex}.zip";

        return EnsureUniqueFilename(zipName, directory);
    }

    private static string EnsureUniqueFilename(string filename, string directory)
    {
        if (!File.Exists(Path.Combine(directory, filename)))
            return filename;

        string baseName = Path.GetFileNameWithoutExtension(filename);
        string ext = Path.GetExtension(filename);
        int counter = 2;
        string candidate;

        do
        {
            candidate = $"{baseName}_{counter}{ext}";
            counter++;
        } while (File.Exists(Path.Combine(directory, candidate)));

        return candidate;
    }
}
