namespace LithicBackup.Core;

/// <summary>
/// The content of an empty file. It has no bytes, so restoring it needs no stored
/// copy. A backup stores an empty file plain, never as a <c>.fileref</c>, and a
/// reference to empty content that an older build wrote is never broken: restore
/// writes the empty file, and Verify, the seed and the revival of copies pass it by.
/// </summary>
public static class EmptyContent
{
    /// <summary>Lower-case hex SHA-256 of no bytes, the form every writer records hashes in.</summary>
    public const string Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Whether a recorded content hash is the empty file's.</summary>
    public static bool Is(string? hash) => string.Equals(hash, Hash, StringComparison.OrdinalIgnoreCase);
}
