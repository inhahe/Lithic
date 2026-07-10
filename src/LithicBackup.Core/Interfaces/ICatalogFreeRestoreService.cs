namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Disaster-recovery restore that reconstructs the original files from a backup
/// destination tree <em>alone</em>, with no SQLite catalog. Used when the catalog
/// has been lost or corrupted. It relies only on the self-describing destination
/// layout:
/// <list type="bullet">
///   <item>plain files hold their own bytes;</item>
///   <item><c>.dedup</c> manifests list the block hashes that reassemble the
///         file from the shared <c>_blocks</c> store;</item>
///   <item><c>.fileref</c> manifests carry a <c>ContentPath</c> hint to the plain
///         copy holding their content, anchored by a SHA-256 <c>Hash</c> that is
///         verified and, if the hint is stale, located by a content-hash scan of
///         the tree.</item>
/// </list>
/// The reconstructed files (with Lithic suffixes stripped) are written to an
/// output directory, mirroring the backup tree's drive/relative structure,
/// including any <c>{drive}_prev</c> version history.
/// </summary>
public interface ICatalogFreeRestoreService
{
    /// <summary>
    /// Reconstruct every file in <paramref name="backupDirectory"/> into
    /// <paramref name="outputDirectory"/> using only the destination tree.
    /// </summary>
    Task<RestoreResult> RestoreAsync(
        string backupDirectory,
        string outputDirectory,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default);
}
