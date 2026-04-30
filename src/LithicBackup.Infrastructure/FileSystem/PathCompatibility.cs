using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Checks whether file paths are compatible with a target disc filesystem.
/// </summary>
public static class PathCompatibility
{
    // Characters forbidden in ISO 9660 Level 1 (beyond what Windows already forbids)
    private static readonly char[] Iso9660ForbiddenChars = [' ', '~', '#', '%', '&', '{', '}', '+', '`', '=', '@'];

    /// <summary>Maximum path lengths per filesystem type.</summary>
    public static int MaxPathLength(FilesystemType fs) => fs switch
    {
        FilesystemType.ISO9660 => 255,    // 8.3 names, 8-level depth
        FilesystemType.Joliet  => 128,    // 64-char filenames, ~128 total path
        FilesystemType.UDF     => 1023,   // Very permissive
        _ => 255,
    };

    /// <summary>Maximum filename length per filesystem type.</summary>
    public static int MaxFilenameLength(FilesystemType fs) => fs switch
    {
        FilesystemType.ISO9660 => 12,     // 8.3
        FilesystemType.Joliet  => 64,
        FilesystemType.UDF     => 255,
        _ => 255,
    };

    /// <summary>
    /// Check if a file's path is compatible with the target filesystem.
    /// Returns null if compatible, or a reason string if not.
    /// </summary>
    public static string? CheckCompatibility(string filePath, FilesystemType fs)
    {
        var fileName = Path.GetFileName(filePath);

        if (fileName.Length > MaxFilenameLength(fs))
            return $"Filename too long ({fileName.Length} > {MaxFilenameLength(fs)})";

        if (filePath.Length > MaxPathLength(fs))
            return $"Full path too long ({filePath.Length} > {MaxPathLength(fs)})";

        if (fs == FilesystemType.ISO9660)
        {
            foreach (var c in fileName)
            {
                if (Array.IndexOf(Iso9660ForbiddenChars, c) >= 0)
                    return $"Character '{c}' not allowed in ISO 9660";
            }

            // ISO 9660 Level 1: only uppercase letters, digits, underscore, dot
            foreach (var c in fileName)
            {
                if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '_' && c != '.')
                    return $"Character '{c}' not allowed in ISO 9660 Level 1";
            }
        }

        return null;
    }
}
