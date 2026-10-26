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

    /// <summary>A retired copy whose file hashed to its record's content, with the file's size and write time then.</summary>
    public sealed record CheckedCopy(FileRecord Record, long Size, DateTime LastWriteUtc);

    /// <summary>
    /// Give a broken reference its content back from a retired copy still on the
    /// drive, first step: which copies may come back. Of the candidate records, the
    /// newest retired plain copy of each content qualifies, if it is needed
    /// (<see cref="NeededAsync"/>) and its file still hashes to it. Reviving it
    /// (<see cref="ReviveCheckedAsync"/>) mends the reference without copying
    /// anything, the only way back for one whose source is gone. Run before taking
    /// the set's write lock: it reads each candidate in full.
    /// <para>A candidate at a file's current location (not under <c>_prev</c>) is
    /// taken only if it is that file's newest record. An older record there describes
    /// bytes that the next backup of the file may write over without knowing of it.</para>
    /// </summary>
    /// <param name="targetDir">The set's backup folder, which stored paths are relative to.</param>
    /// <param name="candidates">Retired plain records that may hold such content.</param>
    /// <returns>At most one copy per content.</returns>
    public static async Task<IReadOnlyList<CheckedCopy>> CheckCopiesAsync(
        ICatalogRepository catalog, int backupSetId, string targetDir,
        IEnumerable<FileRecord> candidates, CancellationToken ct)
    {
        var found = new List<CheckedCopy>();
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
            if (!await NeededAsync(catalog, backupSetId, targetDir, copy.Hash, ct))
            {
                settled.Add(copy.Hash);
                continue;
            }
            if (!await MayBeRevivedWhereItIsAsync(catalog, backupSetId, copy, ct))
                continue;
            var file = new FileInfo(Path.Combine(targetDir, copy.DiscPath));
            if (!file.Exists)
                continue;
            long size = file.Length;
            DateTime written = file.LastWriteTimeUtc;
            string? hash = await TryHashAsync(file.FullName, ct);
            file.Refresh();
            if (hash is null || !string.Equals(hash, copy.Hash, StringComparison.OrdinalIgnoreCase)
                || !file.Exists || file.Length != size || file.LastWriteTimeUtc != written)
                continue;
            found.Add(new CheckedCopy(copy, size, written));
            settled.Add(copy.Hash);
        }
        return found;
    }

    /// <summary>
    /// Second step: make live each copy <see cref="CheckCopiesAsync"/> found that
    /// is still retired as it was, still needed, still allowed where it is, and whose
    /// file has the size and write time it had when hashed. Each test is cheap, so
    /// the set's write lock is held only for them. A copy made live is a live record
    /// like any other: Cleanup lists one whose source is gone, and its guard keeps it
    /// while a reference needs it.
    /// <para>The caller holds the set's write transaction, so that no backup changes
    /// a copy between these tests and its revival; called without one, it throws.</para>
    /// </summary>
    /// <returns>The copies made live.</returns>
    public static async Task<IReadOnlyList<FileRecord>> ReviveCheckedAsync(
        ICatalogRepository catalog, int backupSetId, string targetDir,
        IReadOnlyCollection<CheckedCopy> checkedCopies, CancellationToken ct)
    {
        if (!catalog.OwnsWriteTransaction(backupSetId))
            throw new InvalidOperationException(
                "Retired copies are revived inside the set's write transaction, so that no backup "
                + "changes one between its check and its revival.");
        var revived = new List<FileRecord>();
        foreach (var (copy, size, written) in checkedCopies)
        {
            ct.ThrowIfCancellationRequested();
            var now = (await catalog.GetFileRecordsByPathAsync(backupSetId, copy.SourcePath, ct))
                .FirstOrDefault(r => r.Id == copy.Id);
            if (now is not { IsDeleted: true } || now.DiscPath != copy.DiscPath || now.Hash != copy.Hash
                || !await NeededAsync(catalog, backupSetId, targetDir, copy.Hash, ct)
                || !await MayBeRevivedWhereItIsAsync(catalog, backupSetId, copy, ct))
                continue;
            var file = new FileInfo(Path.Combine(targetDir, copy.DiscPath));
            if (!file.Exists || file.Length != size || file.LastWriteTimeUtc != written)
                continue;
            await catalog.MarkFileRecordsLiveByIdsAsync(backupSetId, [copy.Id], ct);
            now.IsDeleted = false;
            revived.Add(now);
        }
        return revived;
    }

    /// <summary>
    /// Whether content needs a copy brought back: a live <c>.fileref</c> resolves to
    /// it, and no live plain copy whose file is there holds it.
    /// </summary>
    private static async Task<bool> NeededAsync(
        ICatalogRepository catalog, int backupSetId, string targetDir, string hash, CancellationToken ct)
        => await catalog.HasLiveReferenceAsync(backupSetId, hash, ct)
           && await PlainContent.FirstExistingAsync(catalog, backupSetId, hash, targetDir, ct) is null;

    /// <summary>
    /// Whether a retired copy may be live where it is stored: anywhere under
    /// <c>_prev</c>, but at a file's current location only as the file's newest record.
    /// </summary>
    private static async Task<bool> MayBeRevivedWhereItIsAsync(
        ICatalogRepository catalog, int backupSetId, FileRecord copy, CancellationToken ct)
        => HistoryPaths.IsPreviousVersion(copy.DiscPath)
           || (await catalog.GetLastVersionAsync(backupSetId, copy.SourcePath, ct))?.HighestVersion == copy.Version;

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
