using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

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

    /// <summary>
    /// Give content that live <c>.fileref</c> records need, and that no live plain
    /// copy holds, a copy again: for each such content, the newest retired plain
    /// copy whose file is still there and still hashes to it is made live. Such
    /// references resolve to nothing, and this restores them without copying
    /// anything - the only way back for one whose source is gone. A copy made live
    /// is a live record like any other: Cleanup lists one whose source is gone,
    /// and its guard keeps it while a reference needs it. Reads every record, and
    /// the file of each candidate.
    /// </summary>
    /// <param name="targetDir">The set's backup folder, which stored paths are relative to.</param>
    /// <returns>The copies made live, one per content.</returns>
    public static async Task<IReadOnlyList<FileRecord>> ReviveRetiredCopiesAsync(
        ICatalogRepository catalog, int backupSetId, string targetDir, CancellationToken ct)
    {
        var revived = new List<FileRecord>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in await catalog.GetRetiredCopiesOfReferencedContentAsync(backupSetId, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (covered.Contains(copy.Hash))
                continue;
            string? hash = await TryHashAsync(Path.Combine(targetDir, copy.DiscPath), ct);
            if (hash is null || !string.Equals(hash, copy.Hash, StringComparison.OrdinalIgnoreCase))
                continue;
            covered.Add(copy.Hash);
            revived.Add(copy);
        }
        await catalog.MarkFileRecordsLiveByIdsAsync(backupSetId, revived.Select(r => r.Id).ToList(), ct);
        foreach (var r in revived)
            r.IsDeleted = false;
        return revived;
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
