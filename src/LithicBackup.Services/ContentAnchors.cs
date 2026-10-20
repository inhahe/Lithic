using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.Services;

/// <summary>
/// The plain copies a file-level reference (<c>.fileref</c>) resolves to by
/// content hash.
/// </summary>
public static class ContentAnchors
{
    /// <summary>
    /// Hash every live plain copy that a <c>.fileref</c> might depend on and that
    /// has no hash recorded, and record it. Both guards that keep a reference's
    /// last copy - retention's and Cleanup's - match by hash, and a seed with Skip
    /// hashing records plain copies without one, so such a copy could be deleted
    /// while a reference still needed its bytes. Only copies whose size equals a
    /// live reference's are read, which a reference to them must share.
    /// </summary>
    /// <param name="targetDir">The set's destination, which stored paths are relative to.</param>
    /// <param name="among">Only these record ids; null for the whole set.</param>
    /// <returns>The hash recorded, by record id, for each copy that was given one.</returns>
    public static async Task<IReadOnlyDictionary<long, string>> HashUnhashedAnchorsAsync(
        ICatalogRepository catalog, int backupSetId, string targetDir,
        IReadOnlyCollection<long>? among, CancellationToken ct)
    {
        var hashed = new Dictionary<long, string>();
        foreach (var (id, discPath) in await catalog.GetUnhashedPossibleAnchorsAsync(backupSetId, among, ct))
        {
            ct.ThrowIfCancellationRequested();
            string? hash = await TryHashAsync(Path.Combine(targetDir, discPath), ct);
            if (hash is not null && await catalog.SetHashIfMissingAsync(backupSetId, id, hash, ct))
                hashed[id] = hash;
        }
        return hashed;
    }

    /// <summary>Lower-case hex SHA-256 of a file, or null when it cannot be read.</summary>
    private static async Task<string?> TryHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, bufferSize: 1 << 20, useAsync: true);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
