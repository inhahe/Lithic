namespace LithicBackup.Core.Models;

/// <summary>
/// Lightweight version info for a file in the backup catalog.
/// Used for version tracking and diff computation without loading
/// full <see cref="FileRecord"/> objects into memory.
/// </summary>
public readonly record struct FileVersionInfo(
    int MaxVersion,
    long SizeBytes,
    DateTime SourceLastWriteUtc,
    bool IsDeduped,
    bool IsFileRef);
