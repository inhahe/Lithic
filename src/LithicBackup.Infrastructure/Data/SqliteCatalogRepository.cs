using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using LithicBackup.Core;
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

    /// <inheritdoc />
    public string DataDirectory { get; }

    private readonly ConcurrentDictionary<int, SqliteSetDatabase> _sets = new();
    private readonly object _setsLock = new();

    // Disc-id -> set-id routing cache. The mapping is immutable for a disc's
    // lifetime (a disc belongs to exactly one set; deletion removes it
    // entirely), so cached entries never need invalidation.
    private readonly ConcurrentDictionary<int, int> _discToSet = new();

    // GetBackupSetsChangeStampAsync's value. PRAGMA data_version reports commits
    // by every OTHER connection and never this one's, so the stamp is bumped by
    // this instance's own BackupSets writes as well as whenever data_version
    // moves. Both fields are touched only under _masterGate.
    private long _backupSetsStamp;
    private long _lastDataVersion = -1;

    public SqliteCatalogRepository(string databasePath)
    {
        _master = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        _master.Open();

        ExecuteMaster("PRAGMA journal_mode=WAL");
        ExecuteMaster("PRAGMA foreign_keys=ON");
        ExecuteMaster("PRAGMA busy_timeout=15000");

        InitializeSchema();

        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        DataDirectory = dir;
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

    /// <summary>
    /// Sets deleted in this session. A reader that read the set list before the
    /// delete must not re-create the deleted set's database by asking for it.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _deletedSets = new();

    private SqliteSetDatabase GetSet(int setId)
    {
        if (_deletedSets.ContainsKey(setId))
            throw new BackupSetDeletedException(setId);
        if (_sets.TryGetValue(setId, out var db))
            return db;

        // Checked again under the lock the delete marks the set under, so a
        // database is never opened for a set deleted between the two.
        lock (_setsLock)
        {
            if (_deletedSets.ContainsKey(setId))
                throw new BackupSetDeletedException(setId);
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

    /// <summary>Allocate <paramref name="count"/> disc ids for one set in a single master transaction.</summary>
    private async Task<IReadOnlyList<int>> AllocateDiscIdsAsync(int setId, int count, CancellationToken ct)
    {
        var ids = new List<int>(count);
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _master.BeginTransaction();
            using var cmd = _master.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO DiscOwners (SetId) VALUES ($setId);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$setId", setId);
            for (int i = 0; i < count; i++)
                ids.Add(Convert.ToInt32(cmd.ExecuteScalar()));
            tx.Commit();
        }
        finally
        {
            _masterGate.Release();
        }

        foreach (int id in ids)
            _discToSet[id] = setId;
        return ids;
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
            _backupSetsStamp++;
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
            // Sorted here, not by ORDER BY: SQLite sorts whole rows, a row holds
            // the set's selection tree, and once the rows outgrow its cache the
            // sort goes through a temp file. That turned each 9.2 MB read into
            // 18.4 MB read and 9.2 MB written. Ordinal order is the BINARY
            // collation's order, short of characters beyond U+FFFF.
            cmd.CommandText = "SELECT * FROM BackupSets";
            var list = new List<BackupSet>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    list.Add(ReadBackupSet(r));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task<IReadOnlyList<BackupSetOptions>> GetAllBackupSetOptionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, JobOptionsJson FROM BackupSets";
            var list = new List<BackupSetOptions>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new BackupSetOptions(
                        r.GetInt32(0),
                        r.GetString(1),
                        r.IsDBNull(2) ? null : JsonSerializer.Deserialize<JobOptions>(r.GetString(2))));
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task<long> GetBackupSetsChangeStampAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            cmd.CommandText = "PRAGMA data_version";
            long dataVersion = Convert.ToInt64(cmd.ExecuteScalar());
            if (dataVersion != _lastDataVersion)
            {
                _lastDataVersion = dataVersion;
                _backupSetsStamp++;
            }
            return _backupSetsStamp;
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
            _backupSetsStamp++;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    public async Task UpdateBackupSetMetadataAsync(BackupSet set, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _master.CreateCommand();
            // Same statement as UpdateBackupSetAsync minus SourceSelectionJson.
            // SourceRoots is kept: it is derived settings the user edits alongside
            // the destination, not the selection tree, and nothing that calls
            // this changes it.
            cmd.CommandText = """
                UPDATE BackupSets SET
                    Name = $name,
                    SourceRoots = $roots,
                    MaxIncrementalDiscs = $maxDiscs,
                    DefaultMediaType = $mediaType,
                    DefaultFilesystemType = $fsType,
                    CapacityOverrideBytes = $capOverride,
                    LastBackupUtc = $lastBackup,
                    JobOptionsJson = $jobJson
                WHERE Id = $id
                """;
            cmd.Parameters.AddWithValue("$id", set.Id);
            BindBackupSetWriteParameters(cmd, set);
            cmd.ExecuteNonQuery();
            _backupSetsStamp++;
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
                        $"Cannot update file record {file.Id}: disc {file.DiscId} has no owning set."
                        + (file.DiscId == 0
                            ? " DiscId is 0, which usually means this record was not read in full"
                              + " (e.g. it came from GetActiveFilesForClassificationAsync, which"
                              + " loads five columns). UpdateFileRecordAsync rewrites every column,"
                              + " so a partial record must never be passed to it — use"
                              + " MarkFileRecordsDeletedByIdsAsync or re-read the row first."
                            : string.Empty));
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

    public Task<long> GetFileRecordCountAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetFileRecordCountAsync(backupSetId, ct);

    public Task<IReadOnlyList<FileRecord>> GetAllFilesForBackupSetAsync(
        int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null,
        bool inPathOrder = true)
        => GetSet(backupSetId).GetAllFilesForBackupSetAsync(backupSetId, ct, rowProgress, inPathOrder);

    public Task ForEachDiscPathEntryAsync(
        int backupSetId,
        Action<string, bool, string> onEntry,
        CancellationToken ct = default,
        IProgress<int>? rowProgress = null)
        => GetSet(backupSetId).ForEachDiscPathEntryAsync(backupSetId, onEntry, ct, rowProgress);

    public Task<IReadOnlyList<FileRecord>> GetActiveFilesForClassificationAsync(
        int backupSetId,
        CancellationToken ct = default,
        IProgress<int>? rowProgress = null)
        => GetSet(backupSetId).GetActiveFilesForClassificationAsync(backupSetId, ct, rowProgress);

    public Task<IReadOnlyList<FileRecord>> GetActivePreviousVersionsAsync(
        int backupSetId, Action? onIndexing = null, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePreviousVersionsAsync(backupSetId, onIndexing, ct);

    public Task<Dictionary<long, string>> GetLiveDiscPathsAsync(
        int backupSetId, IReadOnlyCollection<long> ids, CancellationToken ct = default)
        => GetSet(backupSetId).GetLiveDiscPathsAsync(ids, ct);

    public Task<string?> FindLiveRecordsOutsideAsync(int backupSetId, string backupRoot, CancellationToken ct = default)
        => GetSet(backupSetId).FindLiveRecordsOutsideAsync(backupSetId, backupRoot, ct);

    public Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetLatestVersionInfoAsync(backupSetId, ct);

    public Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoForPathsAsync(
        int backupSetId, IReadOnlyCollection<string> paths, CancellationToken ct = default)
        => GetSet(backupSetId).GetLatestVersionInfoForPathsAsync(backupSetId, paths, ct);

    public Task<(FileVersionInfo Version, bool IsDeleted, int HighestVersion)?> GetLastVersionAsync(
        int backupSetId, string path, CancellationToken ct = default)
        => GetSet(backupSetId).GetLastVersionAsync(backupSetId, path, ct);

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

    public Task<IReadOnlyList<string>> GetActivePlainContentPathsByHashAsync(int backupSetId, string hash, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePlainContentPathsByHashAsync(backupSetId, hash, ct);

    public Task<IReadOnlyList<string>> DeleteSeededReferencesWithoutContentAsync(int backupSetId, int discId, CancellationToken ct = default)
        => GetSet(backupSetId).DeleteSeededReferencesWithoutContentAsync(backupSetId, discId, ct);

    public Task<IReadOnlyList<FileRecord>> GetRetiredPlainCopiesAsync(
        int backupSetId, IReadOnlyCollection<string> hashes, CancellationToken ct = default)
        => GetSet(backupSetId).GetRetiredPlainCopiesAsync(backupSetId, hashes, ct);

    public Task<bool> HasLiveReferenceAsync(int backupSetId, string hash, CancellationToken ct = default)
        => GetSet(backupSetId).HasLiveReferenceAsync(backupSetId, hash, ct);

    public Task<HashSet<long>> GetActivePlainContentSizesAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePlainContentSizesAsync(backupSetId, ct);

    public Task<HashSet<long>> GetActivePlainSizesPresentAsync(
        int backupSetId, IReadOnlyCollection<long> sizes, CancellationToken ct = default)
        => GetSet(backupSetId).GetActivePlainSizesPresentAsync(backupSetId, sizes, ct);

    public Task<IReadOnlyList<FileRecord>> GetActiveRecordsByHashAsync(int backupSetId, string hash, CancellationToken ct = default)
        => GetSet(backupSetId).GetActiveRecordsByHashAsync(backupSetId, hash, ct);

    public Task<HashSet<long>> GetNeededContentCopiesAsync(
        int backupSetId, IReadOnlyCollection<long> candidateIds,
        IReadOnlyCollection<long>? keepableIds = null, CancellationToken ct = default)
        => GetSet(backupSetId).GetNeededContentCopiesAsync(backupSetId, candidateIds, keepableIds, ct);

    public Task<IReadOnlyList<RestoreTreeChild>> GetRestoreChildrenAsync(int backupSetId, string parentPrefix, CancellationToken ct = default)
        => GetSet(backupSetId).GetRestoreChildrenAsync(parentPrefix, ct);

    public Task<(int FileCount, long TotalBytes)> GetActiveSubtreeStatsAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
        => GetSet(backupSetId).GetActiveSubtreeStatsAsync(directoryPrefix, ct);

    public Task<IReadOnlyList<FileRecord>> GetActiveLatestRecordsUnderPrefixAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
        => GetSet(backupSetId).GetActiveLatestRecordsUnderPrefixAsync(directoryPrefix, ct);

    public Task<FileRecord?> GetActiveLatestRecordByPathAsync(int backupSetId, string sourcePath, CancellationToken ct = default)
        => GetSet(backupSetId).GetActiveLatestRecordByPathAsync(sourcePath, ct);


    public Task<int> MarkFilesDeletedBySourcePathsAsync(int backupSetId, IEnumerable<string> sourcePaths, CancellationToken ct = default)
        => GetSet(backupSetId).MarkFilesDeletedBySourcePathsAsync(backupSetId, sourcePaths, ct);

    public Task<IReadOnlyList<(long Id, string DiscPath)>> GetUnhashedPossibleAnchorsAsync(
        int backupSetId, IReadOnlyCollection<long>? among, CancellationToken ct = default)
        => GetSet(backupSetId).GetUnhashedPossibleAnchorsAsync(among, ct);

    public Task<HashSet<long>> RetireRecordsKeepingNeededCopiesAsync(
        int backupSetId, IReadOnlyCollection<long> candidateIds,
        IReadOnlyCollection<long>? keepableIds = null, CancellationToken ct = default)
        => GetSet(backupSetId).RetireRecordsKeepingNeededCopiesAsync(backupSetId, candidateIds, keepableIds, ct);

    public Task<int> MarkFileRecordsLiveByIdsAsync(int backupSetId, IReadOnlyCollection<long> fileIds, CancellationToken ct = default)
        => GetSet(backupSetId).MarkFileRecordsLiveByIdsAsync(fileIds, ct);

    public Task<bool> SetHashIfMissingAsync(int backupSetId, long fileId, string hash, CancellationToken ct = default)
        => GetSet(backupSetId).SetHashIfMissingAsync(fileId, hash, ct);

    public Task<bool> FlipFileRefToPlainIfUnchangedAsync(
        int backupSetId, long fileId, string expectedDiscPath, string newDiscPath, CancellationToken ct = default)
        => GetSet(backupSetId).FlipFileRefToPlainIfUnchangedAsync(fileId, expectedDiscPath, newDiscPath, ct);

    public Task<bool> MarkFileRecordDeletedIfUnchangedAsync(
        int backupSetId, long fileId, string expectedDiscPath, CancellationToken ct = default)
        => GetSet(backupSetId).MarkFileRecordDeletedIfUnchangedAsync(fileId, expectedDiscPath, ct);

    public Task<int> MarkFileRecordsDeletedByIdsAsync(
        int backupSetId, IReadOnlyList<long> fileIds, CancellationToken ct = default)
        => GetSet(backupSetId).MarkFileRecordsDeletedByIdsAsync(backupSetId, fileIds, ct);

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
            FileSearchResult? partial;
            try
            {
                partial = await GetSet(set.Id).SearchAsync(pathSubstring, ct).ConfigureAwait(false);
            }
            catch (BackupSetDeletedException)
            {
                continue;   // deleted since the list was read
            }
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


    // ---------------------------------------------------------------
    // Cleanup coordination
    // ---------------------------------------------------------------

    public Task<IReadOnlyList<(string SourcePath, string DiscPath)>> GetRecordsWrittenSinceAsync(
        int backupSetId, DateTime sinceUtc, CancellationToken ct = default)
        => GetSet(backupSetId).GetRecordsWrittenSinceAsync(sinceUtc, ct);

    public Task<IReadOnlyList<string>> FindDiscPathsInUseAsync(
        int backupSetId, IReadOnlyCollection<string> discPaths, CancellationToken ct = default)
        => GetSet(backupSetId).FindDiscPathsInUseAsync(discPaths, ct);

    /// <summary>How often a held activity marker is refreshed, and how old one may get.</summary>
    private static readonly TimeSpan ActivityMarkerRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivityMarkerStale = TimeSpan.FromSeconds(30);

    private const string CleaningSuffix = ".cleaning";
    private const string BackingUpSuffix = ".backingup";
    private const string InAppSuffix = ".inapp";

    public IDisposable HoldSetForCleanup(int backupSetId) => new ActivityMarker(SetDbPath(backupSetId) + CleaningSuffix);

    public bool IsSetBeingCleaned(int backupSetId) => IsMarkerLive(SetDbPath(backupSetId) + CleaningSuffix);

    public IDisposable HoldSetInApp(int backupSetId) => new ActivityMarker(SetDbPath(backupSetId) + InAppSuffix);

    public bool IsSetHeldInApp(int backupSetId) => IsMarkerLive(SetDbPath(backupSetId) + InAppSuffix);

    public IDisposable HoldSetForBackgroundBackup(int backupSetId) => new ActivityMarker(SetDbPath(backupSetId) + BackingUpSuffix);

    public bool IsSetBeingBackedUpInBackground(int backupSetId) => IsMarkerLive(SetDbPath(backupSetId) + BackingUpSuffix);

    private static bool IsMarkerLive(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && DateTime.UtcNow - fi.LastWriteTimeUtc < ActivityMarkerStale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The marker file of <see cref="HoldSetForCleanup"/> and
    /// <see cref="HoldSetForBackgroundBackup"/>: created at once, touched every
    /// <see cref="ActivityMarkerRefresh"/> so a reader can tell a live hold from
    /// one whose process died, deleted on dispose. Best effort: a marker that
    /// cannot be written is no worse than the time before markers existed, and
    /// must not stop the work it describes.
    /// </summary>
    private sealed class ActivityMarker : IDisposable
    {
        private readonly string _path;
        private readonly Timer _timer;
        // A refresh and the release are serialised: a refresh that had passed the
        // released check as the marker was deleted would write it again, and the
        // marker would count as live for a while after the hold had ended.
        private readonly object _gate = new();
        private bool _disposed;

        public ActivityMarker(string path)
        {
            _path = path;
            Touch();
            _timer = new Timer(_ => Touch(), null, ActivityMarkerRefresh, ActivityMarkerRefresh);
        }

        private void Touch()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    if (File.Exists(_path)) File.SetLastWriteTimeUtc(_path, DateTime.UtcNow);
                    else File.WriteAllText(_path, Environment.ProcessId.ToString());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { File.Delete(_path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _timer.Dispose();
        }
    }

    // ---------------------------------------------------------------
    // Backup history transfer (set export/import)
    // ---------------------------------------------------------------

    public Task<bool> HasFileRecordsAsync(int backupSetId, CancellationToken ct = default)
        => GetSet(backupSetId).HasFileRecordsAsync(ct);

    public long GetSetHistorySizeBytes(int backupSetId)
    {
        // The pages in use, as of the latest commit (WAL included): what a
        // snapshot copies. The files' sizes overstate it - a write-ahead log
        // keeps its largest size until checkpointed and truncated, and a purge
        // leaves free pages - by as much as double after a big transaction.
        string path = SetDbPath(backupSetId);
        if (!File.Exists(path))
            return 0;
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            long Pragma(string name)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA {name}";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            return (Pragma("page_count") - Pragma("freelist_count")) * Pragma("page_size");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return FileLength.OrZero(path);   // only an estimate: the file's size will do
        }
    }

    public async Task ExportSetHistoryAsync(int backupSetId, string destinationPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Make sure the set database exists (an untouched set has no file yet).
        GetSet(backupSetId);

        if (File.Exists(destinationPath))
            File.Delete(destinationPath); // VACUUM INTO refuses an existing file

        // A connection of our own rather than the set's: VACUUM INTO reads a
        // consistent snapshot inside one read transaction, which in WAL mode
        // neither blocks nor is blocked by a writer in this process or the
        // Worker. Going through the set's connection would hold its gate for the
        // whole copy (minutes, for a multi-GB set) and could not run at all while
        // that connection had a backup's transaction open.
        await Task.Run(() =>
        {
            using (var conn = new SqliteConnection(
                       $"Data Source={SetDbPath(backupSetId)};Mode=ReadOnly;Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM INTO $path";
                cmd.Parameters.AddWithValue("$path", destinationPath);
                // One statement that can run for minutes: a cancel stops it part-way
                // rather than waiting for the whole copy. Safe to abort: it runs on a
                // connection of its own, in no transaction.
                using (SqliteCancellation.Watch(conn, ct))
                    SqliteCancellation.Interruptible(cmd.ExecuteNonQuery, ct);
            }
            ct.ThrowIfCancellationRequested();

            // A self-contained file: no -wal/-shm companions to lose in transit.
            using (var conn = new SqliteConnection($"Data Source={destinationPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=DELETE";
                cmd.ExecuteNonQuery();
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> ImportSetHistoryAsync(
        int backupSetId, string sourceDatabasePath,
        IProgress<(long Done, long Total)>? rowsImported = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // The snapshot's disc ids, to re-allocate. Read through a connection of
        // its own; it is a plain file, not one of ours.
        var oldDiscIds = new List<long>();
        await Task.Run(() =>
        {
            using var conn = new SqliteConnection($"Data Source={sourceDatabasePath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Discs ORDER BY Id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                oldDiscIds.Add(r.GetInt64(0));
        }, ct).ConfigureAwait(false);

        if (oldDiscIds.Count == 0)
            return 0;

        var db = GetSet(backupSetId);
        if (await db.HasFileRecordsAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "This backup set already has backup history; history can only be imported into an empty set.");

        var map = new Dictionary<long, int>(oldDiscIds.Count);
        try
        {
            // Directory sets get a disc record per run, continuous runs included,
            // so a long-lived set can have tens of thousands: allocate them in one
            // master transaction rather than one commit each.
            var newIds = await AllocateDiscIdsAsync(backupSetId, oldDiscIds.Count, ct).ConfigureAwait(false);
            for (int i = 0; i < oldDiscIds.Count; i++)
                map[oldDiscIds[i]] = newIds[i];

            return await Task.Run(() => db.ImportHistoryFromAsync(sourceDatabasePath, map, rowsImported, ct), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Nothing was imported (the copy is one transaction), so give back the
            // disc ids reserved for it rather than leave owners with no discs.
            await ReleaseDiscIdsAsync(map.Values).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReleaseDiscIdsAsync(IEnumerable<int> discIds)
    {
        var ids = discIds.ToList();
        if (ids.Count == 0) return;

        await _masterGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var tx = _master.BeginTransaction();
            using var cmd = _master.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM DiscOwners WHERE DiscId = $id";
            var p = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (int id in ids)
            {
                p.Value = id;
                cmd.ExecuteNonQuery();
                _discToSet.TryRemove(id, out _);
            }
            tx.Commit();
        }
        catch { /* best effort - an unused owner row is harmless */ }
        finally
        {
            _masterGate.Release();
        }
    }

    // ---------------------------------------------------------------
    // Delete / clear / copy backup set
    // ---------------------------------------------------------------

    public async Task DeleteBackupSetAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Remove the master's rows FIRST and only drop the per-set database file
        // once they are committed. The reverse order — which this used to use —
        // destroys data on failure: the set's entire catalog file is deleted, and
        // if the master delete then throws, the set is still listed but is now
        // empty and unrecoverable. That is not hypothetical; it is the likely
        // reason the one set that hit the FOREIGN KEY error below had already
        // lost its set-NN.db by the time the failure was diagnosed.
        //
        // This way round, a failed delete changes nothing at all, and the only
        // cost of failing after the commit is an orphaned file on disk - which
        // is harmless, since set ids come from AUTOINCREMENT and are never
        // reused, so no future set can collide with it.
        await _masterGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _master.BeginTransaction();
            using var cmd = _master.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$setId", backupSetId);

            // Clear this set's rows from the master's legacy tables first
            // (Discs/Files/FileChunks/DeduplicationBlocks, from before the per-set
            // databases): Discs.BackupSetId references BackupSets(Id), so a set that
            // still has rows there cannot be deleted otherwise. Children before
            // parents, for the same reason:
            //   FileChunks/DeduplicationBlocks -> Files -> Discs -> BackupSets.
            //
            // Only the tables that exist: they may not, in this session. Whether
            // the legacy migration has run (user_version) does not say whether rows
            // remain, so the tables are looked for rather than assumed.
            var legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cmd.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name IN ('FileChunks', 'DeduplicationBlocks', 'Files', 'Discs')
                """;
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    legacy.Add(r.GetString(0));

            bool hasDiscs = legacy.Contains("Discs");
            foreach (string table in new[] { "FileChunks", "DeduplicationBlocks", "Files" })
            {
                if (!hasDiscs || !legacy.Contains(table))
                    continue;
                cmd.CommandText = $"""
                    DELETE FROM {table}
                    WHERE DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
                    """;
                cmd.ExecuteNonQuery();
            }

            if (hasDiscs)
            {
                cmd.CommandText = "DELETE FROM Discs WHERE BackupSetId = $setId";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText = "DELETE FROM DiscOwners WHERE SetId = $setId";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "DELETE FROM BackupSets WHERE Id = $setId";
            cmd.ExecuteNonQuery();

            // One transaction, so a failure part-way cannot strand a set's discs
            // with no set, or a set with half its records gone.
            tx.Commit();
            _backupSetsStamp++;

            // Committed - the set no longer exists: nothing may open its
            // database again (see GetSet), and it goes below. Its discs are
            // unknown from now on, as they are once it has gone.
            lock (_setsLock)
                _deletedSets[backupSetId] = 0;
            foreach (var kvp in _discToSet)
                if (kvp.Value == backupSetId)
                    _discToSet.TryRemove(kvp.Key, out _);
        }
        finally
        {
            _masterGate.Release();
        }

        // Outside the master gate: closing waits for a read of the set under way,
        // and a flow holding the set's gate may be waiting for the master one.
        await RemoveSetDatabaseFileAsync(backupSetId).ConfigureAwait(false);
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

    /// <summary>
    /// Close a deleted set's database - once whatever is reading it has finished -
    /// and remove its files. The set is already marked deleted, so nothing can
    /// open it again meanwhile.
    /// </summary>
    private async Task RemoveSetDatabaseFileAsync(int setId)
    {
        if (_sets.TryRemove(setId, out var db))
            await db.CloseAsync().ConfigureAwait(false);

        var path = SetDbPath(setId);
        // ".writelock" is the cross-process write-lock marker file created beside
        // the set DB by SqliteSetDatabase; remove it too so it isn't orphaned.
        foreach (var suffix in new[] { "", "-wal", "-shm", ".writelock", CleaningSuffix, BackingUpSuffix, InAppSuffix })
        {
            try { if (File.Exists(path + suffix)) File.Delete(path + suffix); }
            catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------
    // Transactions (routed)
    // ---------------------------------------------------------------

    public Task<ICatalogTransaction> BeginTransactionAsync(
        int backupSetId, CancellationToken ct = default, Action? onWaitingForOtherProcess = null)
        => GetSet(backupSetId).BeginTransactionAsync(ct, onWaitingForOtherProcess);

    public bool ShouldGiveWayToWaitingWriter(int backupSetId) => GetSet(backupSetId).ShouldGiveWayToWaitingWriter;

    /// <summary>
    /// Make this process's writes step aside for a writer in another process that
    /// is waiting for the same set (see <c>SqliteSetDatabase.YieldToWaitingWriters</c>).
    /// Set once at startup by the Worker service, so a backup the user starts in the
    /// GUI gets in at the Worker's next commit instead of after its whole run.
    /// </summary>
    public static bool YieldWritesToWaitingProcesses
    {
        get => SqliteSetDatabase.YieldToWaitingWriters;
        set => SqliteSetDatabase.YieldToWaitingWriters = value;
    }

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
