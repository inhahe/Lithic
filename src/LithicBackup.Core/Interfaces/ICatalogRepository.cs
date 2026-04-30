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
    Task<IReadOnlyList<FileRecord>> GetFilesBySourcePathAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>
    /// Get all files across all discs in a backup set.
    /// Caution: for large backup sets this can load hundreds of thousands of
    /// FileRecord objects into memory. Prefer <see cref="GetLatestVersionInfoAsync"/>
    /// when only version/format metadata is needed.
    /// </summary>
    Task<IReadOnlyList<FileRecord>> GetAllFilesForBackupSetAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Get lightweight version info for the latest version of each file in a
    /// backup set. Returns one entry per unique SourcePath with the max version
    /// and its metadata. Much cheaper than <see cref="GetAllFilesForBackupSetAsync"/>.
    /// </summary>
    Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default);

    /// <summary>
    /// Get a single file record by backup set, source path, and version number.
    /// Used for on-demand lookup when updating a specific record's DiscPath.
    /// </summary>
    Task<FileRecord?> GetFileRecordByPathAndVersionAsync(int backupSetId, string sourcePath, int version, CancellationToken ct = default);

    // --- Bad disc management ---
    Task MarkDiscAsBadAsync(int discId, CancellationToken ct = default);
    Task<IReadOnlyList<FileRecord>> GetFilesForReplacementAsync(int badDiscId, CancellationToken ct = default);

    // --- File Chunks (split files) ---
    Task CreateFileChunkAsync(FileChunk chunk, CancellationToken ct = default);
    Task<IReadOnlyList<FileChunk>> GetChunksForFileAsync(long fileRecordId, CancellationToken ct = default);

    // --- Deduplication Blocks ---
    Task<DeduplicationBlock?> FindBlockByHashAsync(string hash, CancellationToken ct = default);
    Task CreateBlockAsync(DeduplicationBlock block, CancellationToken ct = default);
    Task IncrementBlockReferenceAsync(long blockId, CancellationToken ct = default);

    // --- Orphaned directory cleanup ---

    /// <summary>
    /// Mark all non-deleted files in a backup set whose SourcePath starts with
    /// <paramref name="directoryPrefix"/> as deleted.  Returns the number of
    /// file records affected.
    /// </summary>
    Task<int> MarkFilesDeletedByDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default);

    // --- Database export ---

    /// <summary>
    /// Export the catalog database to a file (for writing onto a disc).
    /// </summary>
    Task ExportDatabaseAsync(string destinationPath, CancellationToken ct = default);

    // --- Transactions ---
    Task<IDisposable> BeginTransactionAsync(CancellationToken ct = default);
}
