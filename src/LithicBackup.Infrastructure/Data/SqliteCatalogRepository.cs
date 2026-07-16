using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Data;

/// <summary>
/// SQLite implementation of <see cref="ICatalogRepository"/>.
///
/// <para>
/// The catalog is split across a <b>master</b> database (the file passed to the
/// constructor) and one <b>per-set</b> database per backup set
/// (<c>sets/set-{id}.db</c> beside the master).  The master holds the
/// cross-cutting tables — <c>BackupSets</c>, <c>UsnCursors</c>, and
/// <c>DiscOwners</c> — while each set's discs, files, chunks, and dedup blocks
/// live in its own database.  This lets two backup sets run concurrently
/// without contending on a single shared connection or write lock.
/// </para>
/// <para>
/// Disc IDs are globally unique (allocated from the master <c>DiscOwners</c>
/// table), so every disc-keyed call routes to the owning set's database via
/// <see cref="ResolveSetForDiscAsync"/> without needing the set id in its
/// signature.  File/chunk/block IDs are local to each set database.
/// </para>
/// </summary>
public class SqliteCatalogRepository : ICatalogRepository
{
    private readonly SqliteConnection _master;
    private readonly SemaphoreSlim _masterGate = new(1, 1);
    private readonly string _setsDir;

    private readonly ConcurrentDictionary<int, SqliteSetDatabase> _sets = new();
    private readonly object _setsLock = new();

    // Disc-id -> set-id routing cache. The mapping is immutable for a disc's
    // lifetime (a disc belongs to exactly one set; deletion removes it
    // entirely), so cached entries never need invalidation.
    private readonly ConcurrentDictionary<int, int> _discToSet = new();

    public SqliteCatalogRepository(string databasePath)
    {
        _master = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        _master.Open();

        ExecuteMaster("PRAGMA journal_mode=WAL");
        ExecuteMaster("PRAGMA foreign_keys=ON");
        ExecuteMaster("PRAGMA busy_timeout=15000");

        InitializeSchema();

        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        _setsDir = Path.Combine(dir, "sets");
        Directory.CreateDirectory(_setsDir);

        MigrateLegacyDataIfNeeded();
    }

    private void InitializeSchema()
    {
        var assembly = typeof(SqliteCatalogRepository).Assembly;

        ApplyMigration(assembly, "LithicBackup.Infrastructure.Data.Migrations.001_InitialSchema.sql");

        int currentVersion = 0;
        using (var cmd = _master.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion";
            try { currentVersion = Convert.ToInt32(cmd.ExecuteScalar()); }
            catch { /* Table may not exist before 001 applies. */ }
        }

        try { ExecuteMaster("DELETE FROM SchemaVersion WHERE Version < (SELECT MAX(Version) FROM SchemaVersion)"); }
        catch { /* Harmless if table is empty or has a single row. */ }

        var migrations = new (int Version, string ResourceName)[]
        {
            (2, "LithicBackup.Infrastructure.Data.Migrations.002_BackupSetConfig.sql"),
            (3, "LithicBackup.Infrastructure.Data.Migrations.003_CatalogQueryIndex.sql"),
            (4, "LithicBackup.Infrastructure.Data.Migrations.004_UsnCursors.sql"),
            (5, "LithicBackup.Infrastructure.Data.Migrations.005_DiscOwners.sql"),
        };

        foreach (var (version, resourceName) in migrations)
        {
            if (currentVersion >= version)
                continue;
            ApplyMigration(assembly, resourceName);
        }
    }

    private void ApplyMigration(System.Reflection.Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return;
        using var reader = new StreamReader(stream);
        ExecuteMaster(reader.ReadToEnd());
    }

    // ---------------------------------------------------------------
    // Per-set database access + disc routing
    // ---------------------------------------------------------------

    private string SetDbPath(int setId) => Path.Combine(_setsDir, $"set-{setId}.db");

    private SqliteSetDatabase GetSet(int setId)
    {
        if (_sets.TryGetValue(setId, out var db))
            return db;

        lock (_setsLock)
        {
            return _sets.GetOrAdd(setId, id => new SqliteSetDatabase(id, SetDbPath(id)));
        }
    }

    /// <summary>Allocate a new globally-unique disc id owned by the given set.</summary>
    private async Task<int> AllocateDiscIdAsync(int setId, CancellationToken ct)
    {
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DiscOwners (SetId) VALUES ($setId);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$setId", setId);
            int discId = Convert.ToInt32(cmd.ExecuteScalar());
            _discToSet[discId] = setId;
            return discId;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    /// <summary>Resolve which set owns a disc, or null if the disc is unknown.</summary>
    private async Task<int?> ResolveSetForDiscAsync(int discId, CancellationToken ct)
    {
        if (_discToSet.TryGetValue(discId, out var cached))
            return cached;

        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "SELECT SetId FROM DiscOwners WHERE DiscId = $id";
            cmd.Parameters.AddWithValue("$id", discId);
            var result = cmd.ExecuteScalar();
            if (result is null || result is DBNull)
                return null;
            int setId = Convert.ToInt32(result);
            _discToSet[discId] = setId;
            return setId;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    // ---------------------------------------------------------------
    // Backup Sets (master)
    // ---------------------------------------------------------------

    public async Task<BackupSet> CreateBackupSetAsync(BackupSet set, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = """
                INSERT INTO BackupSets
                    (Name, SourceRoots, MaxIncrementalDiscs, DefaultMediaType,
                     DefaultFilesystemType, CapacityOverrideBytes, CreatedUtc, LastBackupUtc,
                     SourceSelectionJson, JobOptionsJson)
                VALUES
                    ($name, $roots, $maxDiscs, $mediaType,
                     $fsType, $capOverride, $created, $lastBackup,
                     $selJson, $jobJson);
                SELECT last_insert_rowid();
                """;
            BindBackupSetWriteParameters(cmd, set);
            set.Id = Convert.ToInt32(cmd.ExecuteScalar());
            return set;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task<BackupSet?> GetBackupSetAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "SELECT * FROM BackupSets WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadBackupSet(r) : null;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task<IReadOnlyList<BackupSet>> GetAllBackupSetsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "SELECT * FROM BackupSets ORDER BY Name";
            var list = new List<BackupSet>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadBackupSet(r));
            return list;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task UpdateBackupSetAsync(BackupSet set, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = """
                UPDATE BackupSets SET
                    Name = $name,
                    SourceRoots = $roots,
                    MaxIncrementalDiscs = $maxDiscs,
                    DefaultMediaType = $mediaType,
                    DefaultFilesystemType = $fsType,
                    CapacityOverrideBytes = $capOverride,
                    LastBackupUtc = $lastBackup,
                    SourceSelectionJson = $selJson,
                    JobOptionsJson = $jobJson
                WHERE Id = $id
                """;
            cmd.Parameters.AddWithValue("$id", set.Id);
            BindBackupSetWriteParameters(cmd, set);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _masterGate.Release();
        }
    }

    private static void BindBackupSetWriteParameters(SqliteCommand cmd, BackupSet set)
    {
        cmd.Parameters.AddWithValue("$name", set.Name);
        cmd.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(set.SourceRoots));
        cmd.Parameters.AddWithValue("$maxDiscs", set.MaxIncrementalDiscs);
        cmd.Parameters.AddWithValue("$mediaType", (int)set.DefaultMediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)set.DefaultFilesystemType);
        cmd.Parameters.AddWithValue("$capOverride", (object?)set.CapacityOverrideBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", set.CreatedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$lastBackup",
            set.LastBackupUtc.HasValue ? set.LastBackupUtc.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$selJson",
            set.SourceSelections is not null
                ? JsonSerializer.Serialize(set.SourceSelections) : DBNull.Value);
        cmd.Parameters.AddWithValue("$jobJson",
            set.JobOptions is not null
                ? JsonSerializer.Serialize(set.JobOptions) : DBNull.Value);
    }

    private static BackupSet ReadBackupSet(SqliteDataReader r)
    {
        var set = new BackupSet
        {
            Id = r.GetInt32(r.GetOrdinal("Id")),
            Name = r.GetString(r.GetOrdinal("Name")),
            SourceRoots = JsonSerializer.Deserialize<List<string>>(
                r.GetString(r.GetOrdinal("SourceRoots"))) ?? [],
            MaxIncrementalDiscs = r.GetInt32(r.GetOrdinal("MaxIncrementalDiscs")),
            DefaultMediaType = (MediaType)r.GetInt32(r.GetOrdinal("DefaultMediaType")),
            DefaultFilesystemType = (FilesystemType)r.GetInt32(r.GetOrdinal("DefaultFilesystemType")),
            CapacityOverrideBytes = r.IsDBNull(r.GetOrdinal("CapacityOverrideBytes"))
                ? null : r.GetInt64(r.GetOrdinal("CapacityOverrideBytes")),
            CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc")), null, DateTimeStyles.RoundtripKind),
            LastBackupUtc = r.IsDBNull(r.GetOrdinal("LastBackupUtc"))
                ? null : DateTime.Parse(r.GetString(r.GetOrdinal("LastBackupUtc")), null, DateTimeStyles.RoundtripKind),
        };

        try
        {
            int selOrd = r.GetOrdinal("SourceSelectionJson");
            if (!r.IsDBNull(selOrd))
                set.SourceSelections = JsonSerializer.Deserialize<List<SourceSelection>>(
                    r.GetString(selOrd));
        }
        catch (IndexOutOfRangeException) { }

        try
        {
            int jobOrd = r.GetOrdinal("JobOptionsJson");
            if (!r.IsDBNull(jobOrd))
                set.JobOptions = JsonSerializer.Deserialize<JobOptions>(
                    r.GetString(jobOrd));
        }
        catch (IndexOutOfRangeException) { }

        return set;
    }

    // ---------------------------------------------------------------
    // Discs (routed)
    // ---------------------------------------------------------------

    public async Task<DiscRecord> CreateDiscAsync(DiscRecord disc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        disc.Id = await AllocateDiscIdAsync(disc.BackupSetId, ct).ConfigureAwait(false);
        await GetSet(disc.BackupSetId).InsertDiscAsync(disc, ct).ConfigureAwait(false);
        return disc;
    }

    public async Task<DiscRecord?> GetDiscAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(id, ct).ConfigureAwait(false);
        if (setId is null)
            return null;
        return await GetSet(setId.Value).GetDiscAsync(id, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DiscRecord>> GetDiscsForBackupSetAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetDiscsForBackupSetAsync(backupSetId, ct);

    public Task UpdateDiscAsync(DiscRecord disc, CancellationToken ct = default)
        => GetSet(disc.BackupSetId).UpdateDiscAsync(disc, ct);

    public Task<int> GetIncrementalDiscCountAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetIncrementalDiscCountAsync(backupSetId, ct);

    public async Task MarkDiscAsBadAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(discId, ct).ConfigureAwait(false);
        if (setId is null)
            return;
        await GetSet(setId.Value).MarkDiscAsBadAsync(discId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesForReplacementAsync(int badDiscId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(badDiscId, ct).ConfigureAwait(false);
        if (setId is null)
            return [];
        return await GetSet(setId.Value).GetFilesForReplacementAsync(badDiscId, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------
    // Files (routed)
    // ---------------------------------------------------------------

    public async Task<FileRecord> CreateFileRecordAsync(FileRecord file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(file.DiscId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Cannot create file record: disc {file.DiscId} has no owning set.");
        return await GetSet(setId).CreateFileRecordAsync(file, ct).ConfigureAwait(false);
    }

    public async Task UpdateFileRecordAsync(FileRecord file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(file.DiscId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Cannot update file record: disc {file.DiscId} has no owning set.");
        await GetSet(setId).UpdateFileRecordAsync(file, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesOnDiscAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(discId, ct).ConfigureAwait(false);
        if (setId is null)
            return [];
        return await GetSet(setId.Value).GetFilesOnDiscAsync(discId, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<FileRecord>> GetAllFilesForBackupSetAsync(int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null)
        => GetSet(backupSetId).GetAllFilesForBackupSetAsync(backupSetId, ct, rowProgress);

    public Task<IReadOnlyList<(string DiscPath, bool IsDeleted, string SourcePath)>> GetDiscPathEntriesForBackupSetAsync(int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null)
        => GetSet(backupSetId).GetDiscPathEntriesForBackupSetAsync(backupSetId, ct, rowProgress);

    public Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetLatestVersionInfoAsync(backupSetId, ct);

    public Task<Dictionary<string, FileVersionInfo>> GetOrphanedVersionInfoAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetOrphanedVersionInfoAsync(backupSetId, ct);

    public Task<int> GetFileCountForBackupSetAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetFileCountForBackupSetAsync(backupSetId, ct);

    public Task<FileRecord?> GetFileRecordByPathAndVersionAsync(int backupSetId, string sourcePath, int version, CancellationToken ct = default)
        => GetSet(backupSetId).GetFileRecordByPathAndVersionAsync(backupSetId, sourcePath, version, ct);

    public Task<IReadOnlyList<FileRecord>> GetFileRecordsByPathAsync(int backupSetId, string sourcePath, CancellationToken ct = default)
        => GetSet(backupSetId).GetFileRecordsByPathAsync(backupSetId, sourcePath, ct);

    public Task<IReadOnlyList<FileRecord>> GetFileRecordsUnderDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
        => GetSet(backupSetId).GetFileRecordsUnderDirectoryAsync(backupSetId, directoryPrefix, ct);

    public Task<HashSet<string>> GetActivePlainHashesAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePlainHashesAsync(backupSetId, ct);

    public Task<Dictionary<string, string>> GetActivePlainContentPathsAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePlainContentPathsAsync(backupSetId, ct);

    public Task<HashSet<long>> GetActivePlainContentSizesAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePlainContentSizesAsync(backupSetId, ct);

    public Task<IReadOnlyList<FileRecord>> GetActiveRecordsByHashAsync(int backupSetId, string hash, CancellationToken ct = default)
        => GetSet(backupSetId).GetActiveRecordsByHashAsync(backupSetId, hash, ct);

    public Task<int> MarkFilesDeletedByDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
        => GetSet(backupSetId).MarkFilesDeletedByDirectoryAsync(backupSetId, directoryPrefix, ct);

    public Task<int> MarkFilesDeletedBySourcePathsAsync(int backupSetId, IEnumerable<string> sourcePaths, CancellationToken ct = default)
        => GetSet(backupSetId).MarkFilesDeletedBySourcePathsAsync(backupSetId, sourcePaths, ct);

    public Task<int> CountFilesUnderSourcePrefixAsync(int backupSetId, string sourcePrefix, CancellationToken ct = default)
        => GetSet(backupSetId).CountFilesUnderSourcePrefixAsync(backupSetId, sourcePrefix, ct);

    public Task<int> RemapSourcePathPrefixAsync(int backupSetId, string oldPrefix, string newPrefix, CancellationToken ct = default)
        => GetSet(backupSetId).RemapSourcePathPrefixAsync(backupSetId, oldPrefix, newPrefix, ct);

    // ---------------------------------------------------------------
    // File chunks (routed)
    // ---------------------------------------------------------------

    public async Task CreateFileChunkAsync(FileChunk chunk, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(chunk.DiscId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Cannot create file chunk: disc {chunk.DiscId} has no owning set.");
        await GetSet(setId).CreateFileChunkAsync(chunk, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileChunk>> GetChunksForFileAsync(int discId, long fileRecordId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var setId = await ResolveSetForDiscAsync(discId, ct).ConfigureAwait(false);
        if (setId is null)
            return [];
        return await GetSet(setId.Value).GetChunksForFileAsync(fileRecordId, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------
    // Cross-set search (fan-out)
    // ---------------------------------------------------------------

    public async Task<IReadOnlyList<FileSearchResult>> SearchFilesAcrossSetsAsync(
        string pathSubstring, CancellationToken ct = default)
    {
        var sets = await GetAllBackupSetsAsync(ct).ConfigureAwait(false);
        var list = new List<FileSearchResult>();
        foreach (var set in sets)
        {
            ct.ThrowIfCancellationRequested();
            var partial = await GetSet(set.Id).SearchAsync(pathSubstring, ct).ConfigureAwait(false);
            if (partial is null)
                continue;
            list.Add(new FileSearchResult
            {
                BackupSetId = set.Id,
                BackupSetName = set.Name,
                MatchingFileCount = partial.MatchingFileCount,
                TotalSizeBytes = partial.TotalSizeBytes,
                LatestVersion = partial.LatestVersion,
                LastBackedUpUtc = partial.LastBackedUpUtc,
            });
        }
        list.Sort((a, b) => string.Compare(a.BackupSetName, b.BackupSetName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // ---------------------------------------------------------------
    // Database export (routed)
    // ---------------------------------------------------------------

    public Task ExportDatabaseAsync(int backupSetId, string destinationPath, CancellationToken ct = default)
        => GetSet(backupSetId).ExportDatabaseAsync(destinationPath, ct);

    // ---------------------------------------------------------------
    // Delete / clear / copy backup set
    // ---------------------------------------------------------------

    public async Task DeleteBackupSetAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Drop the per-set database file (close its connection first), then
        // remove the master's routing rows and the set record itself.
        RemoveSetDatabaseFile(backupSetId);

        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "DELETE FROM DiscOwners WHERE SetId = $setId";
            cmd.Parameters.AddWithValue("$setId", backupSetId);
            cmd.ExecuteNonQuery();

            cmd.CommandText = "DELETE FROM BackupSets WHERE Id = $setId";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _masterGate.Release();
        }

        // Forget cached disc->set entries for this set.
        foreach (var kvp in _discToSet)
            if (kvp.Value == backupSetId)
                _discToSet.TryRemove(kvp.Key, out _);
    }

    public async Task ClearBackupSetCatalogAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await GetSet(backupSetId).ClearCatalogAsync(ct).ConfigureAwait(false);

        // Drop the routing rows so the disc ids are released; the set keeps its
        // configuration and behaves as if it had never run a backup.
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "DELETE FROM DiscOwners WHERE SetId = $setId";
            cmd.Parameters.AddWithValue("$setId", backupSetId);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _masterGate.Release();
        }

        foreach (var kvp in _discToSet)
            if (kvp.Value == backupSetId)
                _discToSet.TryRemove(kvp.Key, out _);
    }

    public async Task CopyBackupSetCatalogAsync(int sourceSetId, int destSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = await GetSet(sourceSetId).ReadSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.Discs.Count == 0)
            return;

        var destDb = GetSet(destSetId);

        // Allocate fresh global disc ids for the destination and remap.
        var discIdMap = new Dictionary<long, int>();
        foreach (var disc in snapshot.Discs)
            discIdMap[disc.Id] = await AllocateDiscIdAsync(destSetId, ct).ConfigureAwait(false);

        var tx = await destDb.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var disc in snapshot.Discs)
            {
                var copy = CloneDisc(disc);
                copy.Id = discIdMap[disc.Id];
                copy.BackupSetId = destSetId;
                await destDb.InsertDiscAsync(copy, ct).ConfigureAwait(false);
            }

            var fileIdMap = new Dictionary<long, long>();
            foreach (var file in snapshot.Files)
            {
                var copy = CloneFile(file);
                copy.Id = 0; // new local autoincrement id
                copy.DiscId = discIdMap[file.DiscId];
                await destDb.CreateFileRecordAsync(copy, ct).ConfigureAwait(false);
                fileIdMap[file.Id] = copy.Id;
            }

            foreach (var chunk in snapshot.Chunks)
            {
                var copy = new FileChunk
                {
                    FileRecordId = fileIdMap.TryGetValue(chunk.FileRecordId, out var nf) ? nf : chunk.FileRecordId,
                    DiscId = discIdMap.TryGetValue(chunk.DiscId, out var nd) ? nd : chunk.DiscId,
                    Sequence = chunk.Sequence,
                    Offset = chunk.Offset,
                    Length = chunk.Length,
                    DiscFilename = chunk.DiscFilename,
                };
                await destDb.CreateFileChunkAsync(copy, ct).ConfigureAwait(false);
            }

            tx.Complete();
        }
        finally
        {
            tx.Dispose();
        }
    }

    private static DiscRecord CloneDisc(DiscRecord d) => new()
    {
        Id = d.Id,
        BackupSetId = d.BackupSetId,
        Label = d.Label,
        SequenceNumber = d.SequenceNumber,
        MediaType = d.MediaType,
        FilesystemType = d.FilesystemType,
        Capacity = d.Capacity,
        BytesUsed = d.BytesUsed,
        RewriteCount = d.RewriteCount,
        IsMultisession = d.IsMultisession,
        IsBad = d.IsBad,
        Status = d.Status,
        CreatedUtc = d.CreatedUtc,
        LastWrittenUtc = d.LastWrittenUtc,
    };

    private static FileRecord CloneFile(FileRecord f) => new()
    {
        Id = f.Id,
        DiscId = f.DiscId,
        SourcePath = f.SourcePath,
        DiscPath = f.DiscPath,
        SizeBytes = f.SizeBytes,
        Hash = f.Hash,
        IsZipped = f.IsZipped,
        IsSplit = f.IsSplit,
        IsDeduped = f.IsDeduped,
        IsFileRef = f.IsFileRef,
        Version = f.Version,
        IsDeleted = f.IsDeleted,
        SourceLastWriteUtc = f.SourceLastWriteUtc,
        BackedUpUtc = f.BackedUpUtc,
    };

    private void RemoveSetDatabaseFile(int setId)
    {
        if (_sets.TryRemove(setId, out var db))
            db.Dispose();

        var path = SetDbPath(setId);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(path + suffix)) File.Delete(path + suffix); }
            catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------
    // Transactions (routed)
    // ---------------------------------------------------------------

    public Task<ICatalogTransaction> BeginTransactionAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).BeginTransactionAsync(ct);

    // ---------------------------------------------------------------
    // USN change-journal cursors (master)
    // ---------------------------------------------------------------

    public async Task<UsnCursor?> GetUsnCursorAsync(string volumeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText =
                "SELECT VolumeId, JournalId, NextUsn, UpdatedUtc FROM UsnCursors WHERE VolumeId = $vol";
            cmd.Parameters.AddWithValue("$vol", volumeId);

            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;

            return new UsnCursor(
                r.GetString(0),
                r.GetInt64(1),
                r.GetInt64(2),
                DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task SaveUsnCursorAsync(UsnCursor cursor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = """
                INSERT INTO UsnCursors (VolumeId, JournalId, NextUsn, UpdatedUtc)
                VALUES ($vol, $journal, $next, $updated)
                ON CONFLICT(VolumeId) DO UPDATE SET
                    JournalId = excluded.JournalId,
                    NextUsn = excluded.NextUsn,
                    UpdatedUtc = excluded.UpdatedUtc
                """;
            cmd.Parameters.AddWithValue("$vol", cursor.VolumeId);
            cmd.Parameters.AddWithValue("$journal", cursor.JournalId);
            cmd.Parameters.AddWithValue("$next", cursor.NextUsn);
            cmd.Parameters.AddWithValue("$updated", cursor.UpdatedUtc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _masterGate.Release();
        }
    }

    // ---------------------------------------------------------------
    // One-time migration from the old combined catalog
    // ---------------------------------------------------------------

    /// <summary>
    /// PRAGMA <c>user_version</c> value stamped on the master DB once the
    /// legacy per-set migration and cleanup have completed.  Distinct from the
    /// app's <c>SchemaVersion</c> table — this is the otherwise-unused free
    /// integer slot SQLite gives every database, used here purely to gate the
    /// one-time migration so cleanup + VACUUM run exactly once.
    /// </summary>
    private const int LegacyMigrationDoneVersion = 1;

    /// <summary>
    /// If the master database still contains the old combined catalog's disc
    /// rows (from before the per-set split), move each set's discs/files/chunks
    /// and disc-bound dedup blocks into per-set databases, preserving primary
    /// keys, and seed the DiscOwners routing index.  Idempotent: discs already
    /// recorded in DiscOwners are skipped, and the per-set import uses
    /// INSERT OR IGNORE.
    ///
    /// <para>
    /// Once every legacy disc is confirmed owned by a per-set database, the
    /// now-redundant legacy tables (<c>Files</c>, <c>FileChunks</c>,
    /// <c>DeduplicationBlocks</c>, <c>Discs</c>) are dropped and the space
    /// reclaimed, and <c>PRAGMA user_version</c> is stamped so the whole pass
    /// is skipped on subsequent launches.  If verification fails the legacy
    /// tables are left intact and the version is not stamped, so the next
    /// launch retries.  (Migration 001 unconditionally recreates empty shells
    /// of those tables in the master DB; they are harmless and ignored.)
    /// </para>
    /// </summary>
    private void MigrateLegacyDataIfNeeded()
    {
        // Gate: the migration + cleanup pass runs exactly once.
        if (GetUserVersion() >= LegacyMigrationDoneVersion)
            return;

        // Read all legacy discs and the set of already-migrated disc ids.
        List<DiscRecord> legacyDiscs;
        try
        {
            legacyDiscs = ReadLegacyDiscs();
        }
        catch
        {
            // No legacy Discs table at all — fresh master DB. Nothing to
            // migrate; stamp the version so we never probe again.
            SetUserVersion(LegacyMigrationDoneVersion);
            return;
        }

        if (legacyDiscs.Count == 0)
        {
            // Legacy tables exist but are empty (fresh install built straight
            // on the per-set schema). Drop the empty shells and mark done.
            DropLegacyTables();
            SetUserVersion(LegacyMigrationDoneVersion);
            return;
        }

        var alreadyOwned = ReadOwnedDiscIds();

        // Discs needing migration, grouped by their owning set.
        var pending = legacyDiscs.Where(d => !alreadyOwned.Contains(d.Id)).ToList();

        var bySet = pending.GroupBy(d => d.BackupSetId);
        foreach (var group in bySet)
        {
            int setId = group.Key;
            var discs = group.ToList();
            var discIds = discs.Select(d => d.Id).ToHashSet();

            var files = ReadLegacyFilesForDiscs(discIds);
            var chunks = ReadLegacyChunksForDiscs(discIds);
            var blocks = ReadLegacyBlocksForDiscs(discIds);

            var setDb = GetSet(setId);
            setDb.ImportLegacyRecordsAsync(discs, files, chunks, blocks)
                 .GetAwaiter().GetResult();

            // Seed routing rows only after the per-set import has committed.
            SeedDiscOwners(setId, discIds);
        }

        // Only drop the originals once every legacy disc is confirmed owned by
        // a per-set database. If anything is missing, leave the legacy tables
        // intact and don't stamp the version, so the next launch retries.
        var owned = ReadOwnedDiscIds();
        if (legacyDiscs.All(d => owned.Contains(d.Id)))
        {
            DropLegacyTables();
            SetUserVersion(LegacyMigrationDoneVersion);
        }
    }

    /// <summary>
    /// Drop the master DB's now-redundant legacy catalog tables (child-first
    /// to respect foreign keys) and reclaim the freed space.  After the per-set
    /// split these tables live in each <c>sets/set-{id}.db</c>; the copies in
    /// the master DB are dead weight.
    /// </summary>
    private void DropLegacyTables()
    {
        ExecuteMaster("DROP TABLE IF EXISTS FileChunks");
        ExecuteMaster("DROP TABLE IF EXISTS Files");
        ExecuteMaster("DROP TABLE IF EXISTS DeduplicationBlocks");
        ExecuteMaster("DROP TABLE IF EXISTS Discs");
        ExecuteMaster("VACUUM");
    }

    private int GetUserVersion()
    {
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void SetUserVersion(int version)
    {
        // PRAGMA user_version doesn't accept bound parameters; the value is an
        // internal constant, so interpolation is safe here.
        ExecuteMaster($"PRAGMA user_version = {version}");
    }

    private List<DiscRecord> ReadLegacyDiscs()
    {
        var list = new List<DiscRecord>();
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "SELECT * FROM Discs";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DiscRecord
            {
                Id = r.GetInt32(r.GetOrdinal("Id")),
                BackupSetId = r.GetInt32(r.GetOrdinal("BackupSetId")),
                Label = r.GetString(r.GetOrdinal("Label")),
                SequenceNumber = r.GetInt32(r.GetOrdinal("SequenceNumber")),
                MediaType = (MediaType)r.GetInt32(r.GetOrdinal("MediaType")),
                FilesystemType = (FilesystemType)r.GetInt32(r.GetOrdinal("FilesystemType")),
                Capacity = r.GetInt64(r.GetOrdinal("Capacity")),
                BytesUsed = r.GetInt64(r.GetOrdinal("BytesUsed")),
                RewriteCount = r.GetInt32(r.GetOrdinal("RewriteCount")),
                IsMultisession = r.GetInt32(r.GetOrdinal("IsMultisession")) != 0,
                IsBad = r.GetInt32(r.GetOrdinal("IsBad")) != 0,
                Status = (BurnSessionStatus)r.GetInt32(r.GetOrdinal("Status")),
                CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc")), null, DateTimeStyles.RoundtripKind),
                LastWrittenUtc = r.IsDBNull(r.GetOrdinal("LastWrittenUtc"))
                    ? null : DateTime.Parse(r.GetString(r.GetOrdinal("LastWrittenUtc")), null, DateTimeStyles.RoundtripKind),
            });
        }
        return list;
    }

    private HashSet<int> ReadOwnedDiscIds()
    {
        var set = new HashSet<int>();
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "SELECT DiscId FROM DiscOwners";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(r.GetInt32(0));
        return set;
    }

    private List<FileRecord> ReadLegacyFilesForDiscs(HashSet<int> discIds)
    {
        var list = new List<FileRecord>();
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int discId = r.GetInt32(r.GetOrdinal("DiscId"));
            if (!discIds.Contains(discId))
                continue;
            list.Add(new FileRecord
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                DiscId = discId,
                SourcePath = r.GetString(r.GetOrdinal("SourcePath")),
                DiscPath = r.GetString(r.GetOrdinal("DiscPath")),
                SizeBytes = r.GetInt64(r.GetOrdinal("SizeBytes")),
                Hash = r.GetString(r.GetOrdinal("Hash")),
                IsZipped = r.GetInt32(r.GetOrdinal("IsZipped")) != 0,
                IsSplit = r.GetInt32(r.GetOrdinal("IsSplit")) != 0,
                IsDeduped = r.GetInt32(r.GetOrdinal("IsDeduped")) != 0,
                IsFileRef = r.GetInt32(r.GetOrdinal("IsFileRef")) != 0,
                Version = r.GetInt32(r.GetOrdinal("Version")),
                IsDeleted = r.GetInt32(r.GetOrdinal("IsDeleted")) != 0,
                SourceLastWriteUtc = DateTime.Parse(r.GetString(r.GetOrdinal("SourceLastWriteUtc")), null, DateTimeStyles.RoundtripKind),
                BackedUpUtc = DateTime.Parse(r.GetString(r.GetOrdinal("BackedUpUtc")), null, DateTimeStyles.RoundtripKind),
            });
        }
        return list;
    }

    private List<FileChunk> ReadLegacyChunksForDiscs(HashSet<int> discIds)
    {
        var list = new List<FileChunk>();
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "SELECT * FROM FileChunks";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int discId = r.GetInt32(r.GetOrdinal("DiscId"));
            if (!discIds.Contains(discId))
                continue;
            list.Add(new FileChunk
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                FileRecordId = r.GetInt64(r.GetOrdinal("FileRecordId")),
                DiscId = discId,
                Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
                Offset = r.GetInt64(r.GetOrdinal("Offset")),
                Length = r.GetInt64(r.GetOrdinal("Length")),
                DiscFilename = r.GetString(r.GetOrdinal("DiscFilename")),
            });
        }
        return list;
    }

    private List<DeduplicationBlock> ReadLegacyBlocksForDiscs(HashSet<int> discIds)
    {
        // Only disc-bound blocks (DiscId > 0) can be attributed to a set.
        // Directory-backup blocks use DiscId = 0 and are not migrated: the
        // block table is only a dedup-decision index, and the authoritative
        // _blocks/ store on disk is untouched, so the next backup harmlessly
        // re-registers them.
        var list = new List<DeduplicationBlock>();
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "SELECT * FROM DeduplicationBlocks WHERE DiscId > 0";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int discId = r.GetInt32(r.GetOrdinal("DiscId"));
            if (!discIds.Contains(discId))
                continue;
            list.Add(new DeduplicationBlock
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                Hash = r.GetString(r.GetOrdinal("Hash")),
                SizeBytes = r.GetInt32(r.GetOrdinal("SizeBytes")),
                ReferenceCount = r.GetInt32(r.GetOrdinal("ReferenceCount")),
                DiscId = discId,
            });
        }
        return list;
    }

    private void SeedDiscOwners(int setId, IEnumerable<int> discIds)
    {
        using var cmd = _master.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO DiscOwners (DiscId, SetId) VALUES ($disc, $set)";
        var pDisc = cmd.Parameters.Add("$disc", SqliteType.Integer);
        var pSet = cmd.Parameters.Add("$set", SqliteType.Integer);
        pSet.Value = setId;
        foreach (var id in discIds)
        {
            pDisc.Value = id;
            cmd.ExecuteNonQuery();
            _discToSet[id] = setId;
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private void ExecuteMaster(string sql)
    {
        using var cmd = _master.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var db in _sets.Values)
            db.Dispose();
        _sets.Clear();
        _master.Dispose();
        _masterGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
