namespace LithicBackup.Core;

/// <summary>
/// Read-only lookup of pre-computed SHA-256 file hashes. Entries are
/// validated against the file's current size and last-write timestamp —
/// stale entries return <c>null</c>.
/// </summary>
public interface IFileHashLookup
{
    /// <summary>
    /// Look up a cached SHA-256 hash for the given file. Returns <c>null</c>
    /// if the entry is missing or stale (size/timestamp mismatch).
    /// </summary>
    string? TryGetHash(string filePath, long currentSize, DateTime currentLastWriteUtc);
}
