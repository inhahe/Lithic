using LithicBackup.Core.Interfaces;

namespace LithicBackup.Services;

/// <summary>
/// Where a content's bytes are, for a <c>.fileref</c>, which stores none.
/// </summary>
public static class PlainContent
{
    /// <summary>
    /// The destination-relative path of an active plain copy of
    /// <paramref name="hash"/> whose file is in <paramref name="backupRoot"/>,
    /// or null: none recorded, or none whose file is still there. A copy can be
    /// recorded long after its file has gone, and another copy of the same bytes
    /// can be intact. The backup, restore, Verify and reconcile all resolve a
    /// reference this way, so they agree on which references are broken, and the
    /// backup never writes a reference to a copy that is gone.
    /// </summary>
    /// <param name="backupRoot">The set's backup folder, which the paths are relative to.</param>
    public static async Task<string?> FirstExistingAsync(
        ICatalogRepository catalog, int backupSetId, string? hash, string backupRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        foreach (string path in await catalog.GetActivePlainContentPathsByHashAsync(backupSetId, hash, ct))
        {
            if (File.Exists(Path.Combine(backupRoot, path)))
                return path;
        }
        return null;
    }

    /// <summary>
    /// Whether a reference to <paramref name="hash"/> can be restored: its content
    /// is empty, which needs no copy (<see cref="Core.EmptyContent"/>), or
    /// <see cref="FirstExistingAsync"/> finds a copy.
    /// </summary>
    public static async Task<bool> ResolvesAsync(
        ICatalogRepository catalog, int backupSetId, string? hash, string backupRoot, CancellationToken ct = default)
        => Core.EmptyContent.Is(hash)
            || await FirstExistingAsync(catalog, backupSetId, hash, backupRoot, ct) is not null;

    /// <summary>
    /// <see cref="FirstExistingAsync"/>, as a full path, for a caller that reads
    /// the copy. Empty content needs no copy, so such a caller asks
    /// <see cref="Core.EmptyContent.Is"/> first.
    /// </summary>
    public static async Task<string?> FirstExistingFullPathAsync(
        ICatalogRepository catalog, int backupSetId, string? hash, string backupRoot, CancellationToken ct = default)
        => await FirstExistingAsync(catalog, backupSetId, hash, backupRoot, ct) is { } path
            ? Path.Combine(backupRoot, path)
            : null;
}
