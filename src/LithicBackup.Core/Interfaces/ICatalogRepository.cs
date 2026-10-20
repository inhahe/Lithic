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

    /// <summary>
    /// The folder the catalog lives in. A work file that becomes catalog data -
    /// an import's staged history - goes here, on the volume that has to hold the
    /// result anyway, not in the system's temp folder.
    /// </summary>
    string DataDirectory { get; }
    Task<IReadOnlyList<BackupSet>> GetAllBackupSetsAsync(CancellationToken ct = default);

    /// <summary>
    /// A number that changes whenever the backup sets may have changed: after every
    /// write this repository makes to them, and after any commit to the catalog by
    /// another connection, the other process's included. While it stays the same,
    /// <see cref="GetAllBackupSetsAsync"/> would return the same sets, so a caller
    /// that polls can skip reading them. A read parses every set's selection tree,
    /// which runs to megabytes for a set with thousands of entries.
    /// </summary>
    /// <remarks>
    /// It may change when no set did (another process wrote a different table),
    /// never the reverse. Compare stamps for equality only.
    /// </remarks>
    Task<long> GetBackupSetsChangeStampAsync(CancellationToken ct = default);

    /// <summary>
    /// Every set's id, name and options, without the selection trees, in name
    /// order. For a caller that polls the sets but needs only their options:
    /// <see cref="GetAllBackupSetsAsync"/> parses each selection tree into tens of
    /// thousands of objects for a large set. (SQLite still pages through each tree
    /// to reach the options column stored after it, so the saving is the parsing,
    /// not the reading.)
    /// </summary>
    Task<IReadOnlyList<BackupSetOptions>> GetAllBackupSetOptionsAsync(CancellationToken ct = default);

    Task UpdateBackupSetAsync(BackupSet set, CancellationToken ct = default);

    /// <summary>
    /// Update a backup set's settings and bookkeeping <b>without touching its
    /// source selection</b> — the stored <c>SourceSelectionJson</c> is left
    /// exactly as it is in the catalog.
    ///
    /// <para><see cref="UpdateBackupSetAsync"/> rewrites every column from the
    /// object it is handed, so any caller holding an older copy of the set writes
    /// that copy's selection back too. Callers that only mean to record a backup
    /// time, a destination change or a volume identity have no business
    /// carrying a selection with them: a GUI session that had read a widened
    /// selection earlier wrote it back over a corrected one minutes after the
    /// correction, and the backup that followed would have copied 1.2 TB of C:
    /// again.</para>
    ///
    /// <para>Use this whenever the change is not to the selection. Use
    /// <see cref="UpdateBackupSetAsync"/> only where the selection itself is the
    /// thing being changed.</para>
    /// </summary>
    Task UpdateBackupSetMetadataAsync(BackupSet set, CancellationToken ct = default);
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
    /// Lightweight query for the destination filesystem scan: every file row's
    /// (DiscPath, IsDeleted, SourcePath) and nothing else, handed to
    /// <paramref name="onEntry"/> one row at a time.
    ///
    /// <para>It skips the full 14-column record hydration AND the ORDER BY sort
    /// of <see cref="GetAllFilesForBackupSetAsync"/>. Being unsorted matters:
    /// SQLite streams rows straight off the disc/file indexes instead of
    /// materialising and sorting the whole result before returning the first
    /// row, so a large set no longer stalls for minutes with no progress before
    /// the read begins, and <paramref name="rowProgress"/> advances from the
    /// first batch.</para>
    ///
    /// <para>The destination scan needs one bit and (rarely) one string per disc
    /// path, not a record per row, so materialising the rows first costs about
    /// as much memory again as the structure being built. Measured on a
    /// 2,810,190-row set: list + per-path record lists = 1,751 MB, aggregating
    /// as the rows arrive = 754 MB for the same answers.</para>
    ///
    /// <para><paramref name="onEntry"/> runs on the reader's thread while the
    /// catalog lock is held, so it must not call back into the repository.</para>
    /// </summary>
    Task ForEachDiscPathEntryAsync(
        int backupSetId,
        Action<string, bool, string> onEntry,
        CancellationToken ct = default,
        IProgress<int>? rowProgress = null);

    /// <summary>
    /// Active (non-tombstoned) file records for classification, carrying ONLY the
    /// columns the classifier reads: Id, SourcePath, DiscPath, SizeBytes and
    /// BackedUpUtc. Hash in particular is a 64-character string per row that the
    /// classifier never looks at.
    /// <para>
    /// Prefer this over <see cref="GetAllFilesForBackupSetAsync"/> for any
    /// whole-set analysis. On a real 3.1M-row set that method loads every
    /// tombstone as well (325,566 of them) and every hash, for about 600 MB of
    /// records nothing goes on to read.
    /// </para>
    /// <para>
    /// Rows arrive ordered by SourcePath then Version descending, so all records
    /// for one directory are contiguous and all versions of one path are
    /// adjacent, newest first. Callers rely on that ordering.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetActiveFilesForClassificationAsync(
        int backupSetId,
        CancellationToken ct = default,
        IProgress<int>? rowProgress = null);

    /// <summary>
    /// Get lightweight version info for the latest version of each file in a
    /// backup set. Returns one entry per unique SourcePath with the max version
    /// and its metadata. Much cheaper than <see cref="GetAllFilesForBackupSetAsync"/>.
    /// </summary>
    Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Path-scoped form of <see cref="GetLatestVersionInfoAsync"/>: same
    /// semantics, but the result is restricted to <paramref name="paths"/>
    /// (matched case-insensitively, as the Windows filesystem does) and each path
    /// is resolved with an indexed seek instead of a whole-set scan-and-sort.
    ///
    /// Prefer it whenever the set of interesting paths is known up front — a
    /// continuous-backup wake-up, or any run working from a candidate list.
    /// The whole-set form costs a full table scan plus a window-function sort
    /// over every row (~12.5 s on a 2.2M-row set), which is what pegged a CPU
    /// core in the Worker service. See known-issues.md.
    /// </summary>
    Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoForPathsAsync(
        int backupSetId, IReadOnlyCollection<string> paths, CancellationToken ct = default);

    /// <summary>
    /// The version a new backup of <paramref name="path"/> follows on from: its
    /// newest live record, or, when every record of the path is soft-deleted, its
    /// newest deleted one. Null when the set has no record of the path. Matched
    /// case-insensitively, as the Windows filesystem is; one indexed seek.
    ///
    /// The deleted case is "orphaned history". A path that was deleted and has
    /// reappeared continues its version chain from it instead of looking brand-new
    /// (version 1, its old copy never moved into _prev). Editors that save
    /// atomically (write a temp file, then replace the original, e.g. KeyNote's
    /// .knt files) need this: each save briefly removes the original, which
    /// tombstones its record.
    /// </summary>
    /// <returns>
    /// That version; whether its record is deleted; and the highest version
    /// number any record of the path has, deleted or not. A new version takes the
    /// number after <c>HighestVersion</c>: a live chain can sit below a newer
    /// tombstoned record, which still owns its number.
    /// </returns>
    Task<(FileVersionInfo Version, bool IsDeleted, int HighestVersion)?> GetLastVersionAsync(
        int backupSetId, string path, CancellationToken ct = default);

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
    /// Single-hash variant of <see cref="GetActivePlainContentPathsAsync"/>:
    /// the destination-relative path of an active plain copy of
    /// <paramref name="hash"/>, or null when the set has none.
    ///
    /// A backup only needs the entries for the content it is actually writing,
    /// so it resolves them one hash at a time (an indexed seek) instead of
    /// GROUP BY-ing every active plain row in a multi-million-row set on every
    /// run. See the "Worker pegged a CPU core" entry in known-issues.md.
    /// </summary>
    Task<string?> GetActivePlainContentPathByHashAsync(int backupSetId, string hash, CancellationToken ct = default);

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
    /// Which of <paramref name="sizes"/> are present among the set's active plain
    /// content — i.e. the intersection of <paramref name="sizes"/> with
    /// <see cref="GetActivePlainContentSizesAsync"/>, computed without
    /// materialising the whole set.
    /// </summary>
    /// <remarks>
    /// The whole-set form has to DISTINCT every active plain row (252,289 sizes
    /// over 2.35M rows on a real set database — a full index scan plus a temp
    /// B-tree, ~6.6 s) purely so a backup can ask about the handful of sizes it
    /// is actually writing. A backup pass therefore probes only its own candidate
    /// sizes, one indexed seek each against <c>IX_Files_ActivePlain_Size</c>.
    /// Only the estimator, which genuinely wants the whole distribution, still
    /// uses the whole-set form. See the "Worker pegged a CPU core" entry in
    /// known-issues.md.
    /// </remarks>
    Task<HashSet<long>> GetActivePlainSizesPresentAsync(
        int backupSetId, IReadOnlyCollection<long> sizes, CancellationToken ct = default);

    /// <summary>
    /// All active (non-deleted) file records in a backup set whose content hash
    /// equals <paramref name="hash"/>.  Used to (a) resolve a <c>.fileref</c> to a
    /// concrete plain copy at restore/verify time, and (b) enforce the invariant
    /// that at least one plain copy of every referenced hash survives retention
    /// pruning.  Excludes split/zipped records (those are not part of the
    /// file-level dedup content pool).
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetActiveRecordsByHashAsync(int backupSetId, string hash, CancellationToken ct = default);

    /// <summary>
    /// Of the records in <paramref name="candidateIds"/>, which are about to be
    /// deleted together, the ones that must be kept because a <c>.fileref</c>
    /// still needs their bytes: a plain, hashed copy whose content is referenced
    /// by an active <c>.fileref</c> that is NOT a candidate, while no plain copy
    /// of that content outside the candidates survives. One is kept per such
    /// content (the newest). The bulk form of the last-plain-copy guard the
    /// backup engine's retention pass applies one record at a time.
    /// </summary>
    /// <param name="keepableIds">
    /// The candidates that may be kept back (a subset of
    /// <paramref name="candidateIds"/>); null means all of them. Cleanup's purge
    /// deletes several categories at once but can hold back only excess versions:
    /// content whose only copies are in other categories gets no keeper.
    /// </param>
    Task<HashSet<long>> GetNeededContentCopiesAsync(
        int backupSetId, IReadOnlyCollection<long> candidateIds,
        IReadOnlyCollection<long>? keepableIds = null, CancellationToken ct = default);

    // --- Lazy restore browser ---

    /// <summary>
    /// List the <b>direct</b> children (immediate subdirectories and files that
    /// live directly under the given directory) of <paramref name="parentPrefix"/>
    /// among the set's active (non-deleted) files, latest version per file.  Pass
    /// an empty string to list the top-level roots (source drives).  Implemented as
    /// a loose-index skip-scan over <c>IX_Files_Active_SourcePath_NoCase</c> so the
    /// cost is O(direct children) index seeks rather than O(subtree) — this is what
    /// lets the restore tree expand a node without loading its whole subtree (or the
    /// whole catalog) up front.  Results are sorted directories-first, then by name.
    /// </summary>
    Task<IReadOnlyList<RestoreTreeChild>> GetRestoreChildrenAsync(
        int backupSetId, string parentPrefix, CancellationToken ct = default);

    /// <summary>
    /// Count of distinct active (non-deleted) source paths at or below
    /// <paramref name="directoryPrefix"/>, and the sum of their latest-version
    /// sizes.  Used by the lazy restore browser to show a checked directory's file
    /// count (and optional aggregate size) without materialising its subtree.
    /// </summary>
    Task<(int FileCount, long TotalBytes)> GetActiveSubtreeStatsAsync(
        int backupSetId, string directoryPrefix, CancellationToken ct = default);

    /// <summary>
    /// The latest active (non-deleted) file record for each distinct source path at
    /// or below <paramref name="directoryPrefix"/>.  Used at restore time to expand
    /// a fully-selected directory into the concrete files to restore, reading only
    /// the selected subtree rather than the whole catalog.
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetActiveLatestRecordsUnderPrefixAsync(
        int backupSetId, string directoryPrefix, CancellationToken ct = default);

    /// <summary>
    /// The latest active (non-deleted) file record for one exact source path, or
    /// null if the path has no active record.  Used at restore time to resolve an
    /// individually-selected file leaf.
    /// </summary>
    Task<FileRecord?> GetActiveLatestRecordByPathAsync(
        int backupSetId, string sourcePath, CancellationToken ct = default);

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

    /// <summary>
    /// Mark specific file rows deleted <b>by row id</b>, without touching any
    /// other column.
    ///
    /// <para>Exists because <see cref="UpdateFileRecordAsync"/> rewrites the whole
    /// row — DiscId, Hash, Version and the IsZipped/IsSplit/IsDeduped/IsFileRef
    /// storage flags included — so passing it a record that was not read in full
    /// silently overwrites everything the reader did not load. Cleanup's
    /// classifier deliberately loads five columns per row for memory reasons, and
    /// routing its records through the whole-row update is a corruption waiting to
    /// happen. Callers that only mean "tombstone these rows" must say exactly
    /// that.</para>
    ///
    /// <para>Ids are the primary key of the set's own database, so
    /// <paramref name="backupSetId"/> selects the database rather than filtering
    /// within it.</para>
    /// </summary>
    /// <returns>How many rows changed (rows already deleted are not counted).</returns>
    Task<int> MarkFileRecordsDeletedByIdsAsync(
        int backupSetId, IReadOnlyList<long> fileIds, CancellationToken ct = default);

    /// <summary>
    /// Live plain copies with no content hash whose size is that of a live
    /// <c>.fileref</c>: the only copies such a reference could be resolving to
    /// that no guard can recognise, since every guard matches by hash. A seed with
    /// Skip hashing records plain copies without one. With
    /// <paramref name="among"/>, only those of these record ids.
    /// </summary>
    /// <returns>Each record's id and stored path.</returns>
    Task<IReadOnlyList<(long Id, string DiscPath)>> GetUnhashedPossibleAnchorsAsync(
        int backupSetId, IReadOnlyCollection<long>? among, CancellationToken ct = default);

    /// <summary>
    /// Retire these records, except any that is the last plain copy of content a
    /// live <c>.fileref</c> still needs once they are gone: that one stays live,
    /// so the reference keeps a copy to resolve to. Decided and done under one
    /// write lock. For work that retires a file's records wholesale - removing a
    /// folder's backups after an edit, Verify's repair, the Worker after a rename
    /// - none of which may orphan another file's reference.
    /// </summary>
    /// <param name="keepableIds">Only these may be the copy kept (a copy known to be broken cannot); null for any candidate.</param>
    /// <returns>The ids kept live; the caller must not delete their stored files.</returns>
    Task<HashSet<long>> RetireRecordsKeepingNeededCopiesAsync(
        int backupSetId, IReadOnlyCollection<long> candidateIds,
        IReadOnlyCollection<long>? keepableIds = null, CancellationToken ct = default);

    /// <summary>
    /// Undo the retirement of these records (inside the caller's transaction): a
    /// purge that has to keep a copy a reference needs, after a category it
    /// purges by source path retired every record of that copy's file.
    /// </summary>
    /// <returns>How many records were retired and are live again.</returns>
    Task<int> MarkFileRecordsLiveByIdsAsync(int backupSetId, IReadOnlyCollection<long> fileIds, CancellationToken ct = default);

    /// <summary>Give a record the content hash it lacks; a record that has one is left as it is.</summary>
    /// <returns>Whether the record was changed.</returns>
    Task<bool> SetHashIfMissingAsync(int backupSetId, long fileId, string hash, CancellationToken ct = default);

    /// <summary>
    /// Turn a live <c>.fileref</c> row into a plain one, only if it is still the
    /// live .fileref at <paramref name="expectedDiscPath"/> the caller read.
    /// Writes two columns: for a caller acting on records it read a while ago
    /// (reconcile, between Analyze and Apply), a whole-row
    /// <see cref="UpdateFileRecordAsync"/> would put back every column as it was
    /// then - un-retiring a row a cleanup has retired since, or pointing a row a
    /// backup has moved into the history back at the current copy.
    /// </summary>
    /// <returns>Whether the row was changed.</returns>
    Task<bool> FlipFileRefToPlainIfUnchangedAsync(
        int backupSetId, long fileId, string expectedDiscPath, string newDiscPath, CancellationToken ct = default);

    /// <summary>
    /// Retire a live row only if it still stores its content at
    /// <paramref name="expectedDiscPath"/>; see <see cref="FlipFileRefToPlainIfUnchangedAsync"/>.
    /// </summary>
    /// <returns>Whether the row was changed.</returns>
    Task<bool> MarkFileRecordDeletedIfUnchangedAsync(
        int backupSetId, long fileId, string expectedDiscPath, CancellationToken ct = default);

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


    // --- Cleanup coordination ---

    /// <summary>
    /// Every record (live or not) written at or after <paramref name="sinceUtc"/>:
    /// its source path and stored path. Cleanup asks this inside its purge
    /// transaction, to leave alone anything backed up again since its scan.
    /// </summary>
    Task<IReadOnlyList<(string SourcePath, string DiscPath)>> GetRecordsWrittenSinceAsync(
        int backupSetId, DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="discPaths"/> (stored paths relative to the
    /// destination) a live record still stores its content at. Cleanup asks this
    /// inside its purge transaction, after retiring what it purges: a stored file
    /// a live record points at is never deleted, however the scan listed it - a
    /// seed run since the scan records files under their original timestamps,
    /// where <see cref="GetRecordsWrittenSinceAsync"/> cannot see them.
    /// </summary>
    Task<IReadOnlyList<string>> FindDiscPathsInUseAsync(
        int backupSetId, IReadOnlyCollection<string> discPaths, CancellationToken ct = default);

    /// <summary>
    /// Mark the set as being cleaned up until the returned handle is disposed: a
    /// marker beside the set's database, refreshed while held, which the Worker
    /// checks before backing the set up and while it does
    /// (<see cref="IsSetBeingCleaned"/>). A purge deletes backed-up files after
    /// its catalog transaction commits, and a backup writing a copy at one of
    /// those paths meanwhile would lose it.
    /// </summary>
    IDisposable HoldSetForCleanup(int backupSetId);

    /// <summary>
    /// Whether a cleanup is holding the set (<see cref="HoldSetForCleanup"/>). A
    /// marker not refreshed for a while - its process gone - does not count.
    /// </summary>
    bool IsSetBeingCleaned(int backupSetId);

    /// <summary>
    /// Mark the set as held by work in the app - a backup, a seed, clearing its
    /// history, a cleanup: whatever has its row busy - until the returned handle
    /// is disposed. The Worker treats it as it does <see cref="HoldSetForCleanup"/>:
    /// it starts no run on the set, and stops one that is going. The app does not
    /// give the set's write lock up to the Worker during its own work, so a Worker
    /// run that waited for it sat for the whole of it - and since the Worker runs
    /// one backup at a time, so did every other set's. Measured: a quick update of
    /// one set, made from inside another set's full backup, waited three hours
    /// behind a backup in the app, holding the full backup with it.
    /// </summary>
    IDisposable HoldSetInApp(int backupSetId);

    /// <summary>Whether <see cref="HoldSetInApp"/> holds the set. Stale markers do not count.</summary>
    bool IsSetHeldInApp(int backupSetId);

    /// <summary>
    /// The Worker's side of the same handshake: held for each of its runs on the
    /// set. Taken BEFORE the Worker checks <see cref="IsSetBeingCleaned"/>, as a
    /// cleanup takes its own before checking
    /// <see cref="IsSetBeingBackedUpInBackground"/>, so of two that start
    /// together at least one sees the other. The Worker always gives way.
    /// </summary>
    IDisposable HoldSetForBackgroundBackup(int backupSetId);

    /// <summary>
    /// Whether the Worker is running a backup of the set
    /// (<see cref="HoldSetForBackgroundBackup"/>). Stale markers do not count.
    /// </summary>
    bool IsSetBeingBackedUpInBackground(int backupSetId);

    // --- Backup history transfer (set export/import) ---

    /// <summary>
    /// Whether the set's catalog holds any file record at all, deleted or not.
    /// A single indexed probe, cheap enough to ask before every automated run.
    /// </summary>
    Task<bool> HasFileRecordsAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Approximate on-disk size of the set's history database in bytes (the
    /// database file plus its write-ahead log), or 0 when it has none yet.
    /// </summary>
    long GetSetHistorySizeBytes(int backupSetId);

    /// <summary>
    /// Write a consistent, self-contained snapshot of the set's history database
    /// (discs, files, chunks, dedup blocks) to <paramref name="destinationPath"/>:
    /// for carrying the set to another machine, and the catalog written onto a
    /// disc. Reads through its own connection, so it never waits on, or
    /// interleaves with, a backup running on the set - and holds only what is
    /// committed.
    /// </summary>
    Task ExportSetHistoryAsync(int backupSetId, string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Load a history snapshot written by <see cref="ExportSetHistoryAsync"/> into
    /// <paramref name="backupSetId"/>, which must have no history yet. Disc ids
    /// are re-allocated (they are unique across the whole catalog, and the
    /// snapshot's may already belong to another set here); everything else keeps
    /// its numbers. All or nothing. Returns the number of file records imported.
    /// </summary>
    Task<int> ImportSetHistoryAsync(int backupSetId, string sourceDatabasePath, CancellationToken ct = default);

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
    /// <summary>
    /// Begin a write transaction on one set's database. Waits, without a timeout,
    /// while another process is writing the same set.
    /// </summary>
    /// <param name="onWaitingForOtherProcess">
    /// Called once if that wait happens, so a caller showing progress can say so
    /// rather than leave its previous status on screen.
    /// </param>
    Task<ICatalogTransaction> BeginTransactionAsync(
        int backupSetId, CancellationToken ct = default, Action? onWaitingForOtherProcess = null);
}
