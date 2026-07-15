using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// CRUD operations for the SQLite backup catalog.
/// </summary>
public interface ICatalogRepository : IDisposable
{
    // --- Backup Sets ---
    Task<BackupSet> CreateBackupSetAsync(BackupSet set, CancellationToken ct = default);
    Task<BackupSet?> GetBackupSetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<BackupSet>> GetAllBackupSetsAsync(CancellationToken ct = default);
    Task UpdateBackupSetAsync(BackupSet set, CancellationToken ct = default);
    Task DeleteBackupSetAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Delete all catalog records of what has been backed up for a set
    /// (discs, file records, and file chunks) while keeping the backup set
    /// itself and its configuration intact.  After this, the set behaves as
    /// if it had never run a backup — the next run treats every source file
    /// as new.  Files already written to the destination are not touched, and
    /// shared deduplication blocks are left in place.
    /// </summary>
    Task ClearBackupSetCatalogAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Copy the backup history (discs, file records, and file chunks) from one
    /// backup set to another, remapping the disc/file primary keys.  Used when
    /// duplicating a set and opting to carry over its record of what is already
    /// backed up.  The destination set must already exist; its existing history
    /// is left untouched (callers should clear it first if a clean copy is
    /// desired).  Shared deduplication blocks are not duplicated.
    /// </summary>
    Task CopyBackupSetCatalogAsync(int sourceSetId, int destSetId, CancellationToken ct = default);

    // --- Discs ---
    Task<DiscRecord> CreateDiscAsync(DiscRecord disc, CancellationToken ct = default);
    Task<DiscRecord?> GetDiscAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DiscRecord>> GetDiscsForBackupSetAsync(int backupSetId, CancellationToken ct = default);
    Task UpdateDiscAsync(DiscRecord disc, CancellationToken ct = default);

    /// <summary>
    /// Count of incremental discs in a backup set, for enforcing the
    /// <see cref="BackupSet.MaxIncrementalDiscs"/> limit.
    /// </summary>
    Task<int> GetIncrementalDiscCountAsync(int backupSetId, CancellationToken ct = default);

    // --- Files ---
    Task<FileRecord> CreateFileRecordAsync(FileRecord file, CancellationToken ct = default);
    Task UpdateFileRecordAsync(FileRecord file, CancellationToken ct = default);
    Task<IReadOnlyList<FileRecord>> GetFilesOnDiscAsync(int discId, CancellationToken ct = default);

    /// <summary>
    /// Get all files across all discs in a backup set.
    /// Caution: for large backup sets this can load hundreds of thousands of
    /// FileRecord objects into memory. Prefer <see cref="GetLatestVersionInfoAsync"/>
    /// when only version/format metadata is needed.
    /// <paramref name="rowProgress"/>, if supplied, is reported periodically with
    /// the running count of records read so a caller can show live progress on a
    /// large set (the read itself is synchronous and can take many seconds).
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetAllFilesForBackupSetAsync(
        int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null);

    /// <summary>
    /// Get lightweight version info for the latest version of each file in a
    /// backup set. Returns one entry per unique SourcePath with the max version
    /// and its metadata. Much cheaper than <see cref="GetAllFilesForBackupSetAsync"/>.
    /// </summary>
    Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Get lightweight version info for paths whose ENTIRE history is soft-deleted
    /// — i.e. every catalog record for that SourcePath has <c>IsDeleted = 1</c>, so
    /// the path is absent from <see cref="GetLatestVersionInfoAsync"/>. Returns one
    /// entry per such path, carrying the max version and its metadata.
    ///
    /// This is the "orphaned history" set. It exists so a backup can *resurrect* a
    /// version chain when a previously-deleted path reappears: without it, the
    /// reappearing file looks brand-new (version resets to 1, the old copy is never
    /// moved into _prev). This matters for editors that save atomically (write a
    /// temp file then replace the original, e.g. KeyNote's .knt files): each save
    /// briefly removes the original, tombstoning its record, so the next backup
    /// must continue the chain rather than start over.
    /// </summary>
    Task<Dictionary<string, FileVersionInfo>> GetOrphanedVersionInfoAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Fast scalar count of distinct source paths in a backup set (non-deleted).
    /// Use for progress bar estimates instead of loading full file metadata.
    /// </summary>
    Task<int> GetFileCountForBackupSetAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Get a single file record by backup set, source path, and version number.
    /// Used for on-demand lookup when updating a specific record's DiscPath.
    /// </summary>
    Task<FileRecord?> GetFileRecordByPathAndVersionAsync(int backupSetId, string sourcePath, int version, CancellationToken ct = default);

    /// <summary>
    /// All catalog records (every version, including deleted history) for one
    /// exact source path in a set, ordered by version. Used to relocate a file's
    /// destination copy when its source is renamed/moved, and to decide whether
    /// relocation is safe (a single plain version) or must fall back to a re-copy.
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetFileRecordsByPathAsync(int backupSetId, string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// All catalog records (every version, including deleted history) whose
    /// SourcePath is the given directory or lies under it. Used to relocate an
    /// entire subtree's destination copies when a source directory is
    /// renamed/moved, and to gate whether that relocation is safe.
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetFileRecordsUnderDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default);

    /// <summary>
    /// Distinct content hashes in a backup set that have at least one active
    /// (non-deleted) PLAIN copy — a record that holds the real bytes under its
    /// own name (not a <c>.fileref</c> pointer and not a <c>.dedup</c> manifest).
    /// File-level deduplication seeds its "already-stored content" set from this
    /// so a later byte-identical file is written as a <c>.fileref</c> only when a
    /// real plain copy of that content is known to exist somewhere in the tree.
    /// </summary>
    Task<HashSet<string>> GetActivePlainHashesAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetActivePlainHashesAsync"/>, but maps each content hash
    /// that has an active plain copy to that copy's destination-relative
    /// <see cref="FileRecord.DiscPath"/>. File-level deduplication uses this to
    /// both decide a duplicate (hash present) and stamp a new <c>.fileref</c>'s
    /// <see cref="FileRefManifest.ContentPath"/> hint with the plain copy's
    /// location, without an extra per-file catalog query.
    /// </summary>
    Task<Dictionary<string, string>> GetActivePlainContentPathsAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// The distinct byte sizes of all content that currently has an active plain
    /// copy in the set (the same population as
    /// <see cref="GetActivePlainContentPathsAsync"/>). A directory backup uses
    /// this as a fast pre-check: a new file whose size matches none of these
    /// sizes (and no other file in the same run) cannot be a whole-file
    /// duplicate, so it can be hashed and copied in a single streaming pass
    /// instead of being read once to hash and again to copy.
    /// </summary>
    Task<HashSet<long>> GetActivePlainContentSizesAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// All active (non-deleted) file records in a backup set whose content hash
    /// equals <paramref name="hash"/>.  Used to (a) resolve a <c>.fileref</c> to a
    /// concrete plain copy at restore/verify time, and (b) enforce the invariant
    /// that at least one plain copy of every referenced hash survives retention
    /// pruning.  Excludes split/zipped records (those are not part of the
    /// file-level dedup content pool).
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetActiveRecordsByHashAsync(int backupSetId, string hash, CancellationToken ct = default);

    // --- Bad disc management ---
    Task MarkDiscAsBadAsync(int discId, CancellationToken ct = default);
    Task<IReadOnlyList<FileRecord>> GetFilesForReplacementAsync(int badDiscId, CancellationToken ct = default);

    // --- File Chunks (split files) ---
    Task CreateFileChunkAsync(FileChunk chunk, CancellationToken ct = default);

    /// <summary>
    /// Get the chunks for a split file. <paramref name="discId"/> identifies the
    /// disc (and thus the owning set's database) the file record lives on.
    /// </summary>
    Task<IReadOnlyList<FileChunk>> GetChunksForFileAsync(int discId, long fileRecordId, CancellationToken ct = default);

    // Block-level deduplication has no catalog index: the destination's
    // content-addressed _blocks/{hash}.blk store is itself the index (see
    // BlockDeduplicationEngine), so there are no block query methods here.

    // --- Orphaned directory cleanup ---

    /// <summary>
    /// Mark all non-deleted files in a backup set whose SourcePath starts with
    /// <paramref name="directoryPrefix"/> as deleted.  Returns the number of
    /// file records affected.
    /// </summary>
    Task<int> MarkFilesDeletedByDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default);

    /// <summary>
    /// Mark files as deleted by their exact source paths.  Used to clean up
    /// catalog records for files that are no longer in the source selection
    /// or are now excluded by glob patterns.
    /// </summary>
    Task<int> MarkFilesDeletedBySourcePathsAsync(int backupSetId, IEnumerable<string> sourcePaths, CancellationToken ct = default);

    // --- Source drive remap ---

    /// <summary>
    /// Count catalog file records (all versions, including deleted history)
    /// whose <see cref="FileRecord.SourcePath"/> is at or under
    /// <paramref name="sourcePrefix"/> (typically a drive root like
    /// <c>"E:\"</c>).  Used to preview how many records a source-drive remap
    /// would affect before committing to it.
    /// </summary>
    Task<int> CountFilesUnderSourcePrefixAsync(int backupSetId, string sourcePrefix, CancellationToken ct = default);

    /// <summary>
    /// Rewrite the leading <paramref name="oldPrefix"/> of every catalog file
    /// record's <see cref="FileRecord.SourcePath"/> in the set to
    /// <paramref name="newPrefix"/>.  Used to remap a source drive letter (e.g.
    /// the data that used to live on <c>E:\</c> now lives on <c>F:\</c> with the
    /// same tree) so future backups treat existing files as already backed up
    /// instead of re-copying everything.  Only <c>SourcePath</c> changes; the
    /// destination-relative <see cref="FileRecord.DiscPath"/> and the physical
    /// destination files are deliberately left untouched (they migrate naturally
    /// as files change).  Returns the number of records updated.
    /// </summary>
    Task<int> RemapSourcePathPrefixAsync(int backupSetId, string oldPrefix, string newPrefix, CancellationToken ct = default);

    // --- Cross-set search ---

    /// <summary>
    /// Search for files across all backup sets whose source path contains the
    /// given substring (case-insensitive). Returns one summary row per backup
    /// set that contains matches.
    /// </summary>
    Task<IReadOnlyList<FileSearchResult>> SearchFilesAcrossSetsAsync(
        string pathSubstring, CancellationToken ct = default);

    // --- Database export ---

    /// <summary>
    /// Export a backup set's catalog database to a file (for writing onto a disc).
    /// </summary>
    Task ExportDatabaseAsync(int backupSetId, string destinationPath, CancellationToken ct = default);

    // --- USN change-journal cursors ---

    /// <summary>
    /// Get the saved USN-journal resume point for a volume, or null if none has
    /// been persisted yet (first run for that volume).
    /// </summary>
    Task<UsnCursor?> GetUsnCursorAsync(string volumeId, CancellationToken ct = default);

    /// <summary>
    /// Persist (insert or update) the USN-journal resume point for a volume.
    /// </summary>
    Task SaveUsnCursorAsync(UsnCursor cursor, CancellationToken ct = default);

    // --- Transactions ---
    // A transaction is scoped to one set's database.
    Task<ICatalogTransaction> BeginTransactionAsync(int backupSetId, CancellationToken ct = default);
}
