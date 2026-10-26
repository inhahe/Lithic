using System.Security.Cryptography;
using LithicBackup.Core;
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
    /// Give a broken reference its content back from a retired copy still on the
    /// drive. Of the candidate records, the newest retired plain copy of each
    /// content is made live, if a live <c>.fileref</c> needs that content, no live
    /// plain copy whose file is there holds it, and the candidate's file still hashes
    /// to it. That mends the reference without copying anything, and it is the only
    /// way back for one whose source is gone. A copy made live is a live record like
    /// any other: Cleanup lists one whose source is gone, and its guard keeps it while
    /// a reference needs it.
    /// <para>A candidate at a file's current location (not under <c>_prev</c>) is
    /// taken only if it is that file's newest record. An older record there describes
    /// bytes that the next backup of the file may write over without knowing of it.</para>
    /// <para>The caller holds the set's write transaction, so no backup changes a
    /// copy between its check and its revival; called without one, it throws.</para>
    /// </summary>
    /// <param name="targetDir">The set's backup folder, which stored paths are relative to.</param>
    /// <param name="candidates">Retired plain records that may hold such content.</param>
    /// <returns>The copies made live, at most one per content.</returns>
    public static async Task<IReadOnlyList<FileRecord>> ReviveCopiesAsync(
        ICatalogRepository catalog, int backupSetId, string targetDir,
        IEnumerable<FileRecord> candidates, CancellationToken ct)
    {
        if (!catalog.OwnsWriteTransaction(backupSetId))
            throw new InvalidOperationException(
                "Retired copies are revived inside the set's write transaction, so that no backup "
                + "changes one between its check and its revival.");
        var revived = new List<FileRecord>();
        // Contents decided, either way: one copy each is enough.
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in candidates
                     .Where(c => c.IsDeleted && !c.IsFileRef && !c.IsDeduped && !c.IsSplit && !c.IsZipped
                                 && !string.IsNullOrEmpty(c.Hash) && !EmptyContent.Is(c.Hash))
                     .OrderByDescending(c => c.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (settled.Contains(copy.Hash))
                continue;
            if (!await catalog.HasLiveReferenceAsync(backupSetId, copy.Hash, ct)
                || await PlainContent.FirstExistingAsync(catalog, backupSetId, copy.Hash, targetDir, ct) is not null)
            {
                settled.Add(copy.Hash);
                continue;
            }
            if (!HistoryPaths.IsPreviousVersion(copy.DiscPath)
                && (await catalog.GetLastVersionAsync(backupSetId, copy.SourcePath, ct))?.HighestVersion != copy.Version)
                continue;
            string? hash = await TryHashAsync(Path.Combine(targetDir, copy.DiscPath), ct);
            if (hash is null || !string.Equals(hash, copy.Hash, StringComparison.OrdinalIgnoreCase))
                continue;
            await catalog.MarkFileRecordsLiveByIdsAsync(backupSetId, [copy.Id], ct);
            copy.IsDeleted = false;
            revived.Add(copy);
            settled.Add(copy.Hash);
        }
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
