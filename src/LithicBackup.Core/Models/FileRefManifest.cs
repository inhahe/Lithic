namespace LithicBackup.Core.Models;

/// <summary>
/// JSON schema for .fileref manifest files. Points to a canonical copy of the
/// file stored in the _filestore/{hash}.dat content-addressed store.
/// </summary>
public class FileRefManifest
{
    /// <summary>Original filename (without path).</summary>
    public string OriginalName { get; init; } = "";

    /// <summary>Original file size in bytes.</summary>
    public long OriginalSize { get; init; }

    /// <summary>SHA-256 hash of the original file contents.</summary>
    public string Hash { get; init; } = "";
}
