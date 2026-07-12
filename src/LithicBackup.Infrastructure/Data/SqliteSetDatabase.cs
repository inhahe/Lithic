using System.Globalization;
using Microsoft.Data.Sqlite;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Data;

/// <summary>
/// Per-set catalog database (sets/set-{id}.db).  Owns a single SQLite
/// connection holding one backup set's discs, files, chunks, and dedup blocks.
///
/// <para>
/// <b>Concurrency.</b>  A single <see cref="SqliteConnection"/> is not safe for
/// concurrent command execution, so every operation is serialised through a
/// <see cref="SemaphoreSlim"/> gate.  A transaction holds the gate for its
/// whole lifetime; calls made <i>inside</i> a transaction must not re-acquire
/// the gate (the semaphore is not reentrant — that would self-deadlock), so a
/// <see cref="_inTransaction"/> flag short-circuits the lock for them.
/// </para>
/// <para>
/// The flag is only correct under the application's invariant that at most one
/// logical operation touches a given set at a time (the GUI gates per-set
/// commands so a set that is backing up cannot also be restored/cleaned).  The
/// concurrency this split unlocks is <i>between</i> sets, which is inherently
/// safe because each set has its own connection and its own SQLite write lock.
/// Cross-process sharing (GUI + Worker) relies on WAL mode plus a busy timeout.
/// </para>
/// </summary>
internal sealed class SqliteSetDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _inTransaction;

    public int SetId { get; }
    public string DatabasePath { get; }

    public SqliteSetDatabase(int setId, string databasePath)
    {
        SetId = setId;
        DatabasePath = databasePath;

        // Pooling=False so the file handle is released promptly on Dispose,
        // which lets DeleteBackupSetAsync remove the set's .db file.
        _connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA foreign_keys=ON");
        Execute("PRAGMA busy_timeout=15000");

        var assembly = typeof(SqliteSetDatabase).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "LithicBackup.Infrastructure.Data.SetSchema.sql");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            Execute(reader.ReadToEnd());
        }
    }

    // ---------------------------------------------------------------
    // Gate + transactions
    // ---------------------------------------------------------------

    private async Task<Releaser> LockAsync(CancellationToken ct)
    {
        // Reentrant short-circuit: a call made while this set's transaction is
        // open is on the transaction-owning flow and must not block on the gate.
        if (_inTransaction)
            return default;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(this);
    }

    public async Task<ICatalogTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = _connection.BeginTransaction();
            _inTransaction = true;
            return new TransactionScope(this, tx);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private readonly struct Releaser(SqliteSetDatabase? owner) : IDisposable
    {
        public void Dispose() => owner?._gate.Release();
    }

    /// <summary>
    /// Commits on <see cref="Complete"/> + Dispose, otherwise rolls back, then
    /// clears the in-transaction flag and releases the owning set's gate.
    /// </summary>
    private sealed class TransactionScope : ICatalogTransaction
    {
        private readonly SqliteSetDatabase _owner;
        private SqliteTransaction? _transaction;
        private bool _completed;

        public TransactionScope(SqliteSetDatabase owner, SqliteTransaction transaction)
        {
            _owner = owner;
            _transaction = transaction;
        }

        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (_transaction is null)
                return;

            try
            {
                if (_completed)
                    _transaction.Commit();
                else
                    _transaction.Rollback();
            }
            finally
            {
                _transaction.Dispose();
                _transaction = null;
                _owner._inTransaction = false;
                _owner._gate.Release();
            }
        }
    }

    // ---------------------------------------------------------------
    // Discs
    // ---------------------------------------------------------------

    /// <summary>
    /// Insert a disc using its already-assigned (global) <see cref="DiscRecord.Id"/>.
    /// The caller allocates the Id from the master DiscOwners table first.
    /// </summary>
    public async Task InsertDiscAsync(DiscRecord disc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Discs
                (Id, BackupSetId, Label, SequenceNumber, MediaType, FilesystemType,
                 Capacity, BytesUsed, RewriteCount, IsMultisession, IsBad, Status,
                 CreatedUtc, LastWrittenUtc)
            VALUES
                ($id, $setId, $label, $seq, $mediaType, $fsType,
                 $capacity, $used, $rewrites, $multi, $isBad, $status,
                 $created, $lastWritten)
            """;
        cmd.Parameters.AddWithValue("$id", disc.Id);
        cmd.Parameters.AddWithValue("$setId", disc.BackupSetId);
        cmd.Parameters.AddWithValue("$label", disc.Label);
        cmd.Parameters.AddWithValue("$seq", disc.SequenceNumber);
        cmd.Parameters.AddWithValue("$mediaType", (int)disc.MediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)disc.FilesystemType);
        cmd.Parameters.AddWithValue("$capacity", disc.Capacity);
        cmd.Parameters.AddWithValue("$used", disc.BytesUsed);
        cmd.Parameters.AddWithValue("$rewrites", disc.RewriteCount);
        cmd.Parameters.AddWithValue("$multi", disc.IsMultisession ? 1 : 0);
        cmd.Parameters.AddWithValue("$isBad", disc.IsBad ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (int)disc.Status);
        cmd.Parameters.AddWithValue("$created", disc.CreatedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$lastWritten",
            disc.LastWrittenUtc.HasValue ? disc.LastWrittenUtc.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public async Task<DiscRecord?> GetDiscAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Discs WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDisc(r) : null;
    }

    public async Task<IReadOnlyList<DiscRecord>> GetDiscsForBackupSetAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Discs WHERE BackupSetId = $setId ORDER BY SequenceNumber";
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var list = new List<DiscRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDisc(r));
        return list;
    }

    public async Task UpdateDiscAsync(DiscRecord disc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Discs SET
                Label = $label,
                SequenceNumber = $seq,
                MediaType = $mediaType,
                FilesystemType = $fsType,
                Capacity = $capacity,
                BytesUsed = $used,
                RewriteCount = $rewrites,
                IsMultisession = $multi,
                IsBad = $isBad,
                Status = $status,
                LastWrittenUtc = $lastWritten
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", disc.Id);
        cmd.Parameters.AddWithValue("$label", disc.Label);
        cmd.Parameters.AddWithValue("$seq", disc.SequenceNumber);
        cmd.Parameters.AddWithValue("$mediaType", (int)disc.MediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)disc.FilesystemType);
        cmd.Parameters.AddWithValue("$capacity", disc.Capacity);
        cmd.Parameters.AddWithValue("$used", disc.BytesUsed);
        cmd.Parameters.AddWithValue("$rewrites", disc.RewriteCount);
        cmd.Parameters.AddWithValue("$multi", disc.IsMultisession ? 1 : 0);
        cmd.Parameters.AddWithValue("$isBad", disc.IsBad ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (int)disc.Status);
        cmd.Parameters.AddWithValue("$lastWritten",
            disc.LastWrittenUtc.HasValue ? disc.LastWrittenUtc.Value.ToString("o") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public async Task<int> GetIncrementalDiscCountAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Discs WHERE BackupSetId = $setId";
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public async Task MarkDiscAsBadAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Discs SET IsBad = 1 WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", discId);
        cmd.ExecuteNonQuery();
    }

    private static DiscRecord ReadDisc(SqliteDataReader r) => new()
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
    };

    // ---------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------

    public async Task<FileRecord> CreateFileRecordAsync(FileRecord file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Files
                (DiscId, SourcePath, DiscPath, SizeBytes, Hash,
                 IsZipped, IsSplit, IsDeduped, IsFileRef, Version, IsDeleted,
                 SourceLastWriteUtc, BackedUpUtc)
            VALUES
                ($discId, $srcPath, $discPath, $size, $hash,
                 $zipped, $split, $deduped, $fileRef, $version, $deleted,
                 $srcWrite, $backedUp);
            SELECT last_insert_rowid();
            """;
        BindFileParameters(cmd, file);
        file.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return file;
    }

    public async Task UpdateFileRecordAsync(FileRecord file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET
                DiscId = $discId,
                SourcePath = $srcPath,
                DiscPath = $discPath,
                SizeBytes = $size,
                Hash = $hash,
                IsZipped = $zipped,
                IsSplit = $split,
                IsDeduped = $deduped,
                IsFileRef = $fileRef,
                Version = $version,
                IsDeleted = $deleted,
                SourceLastWriteUtc = $srcWrite,
                BackedUpUtc = $backedUp
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", file.Id);
        BindFileParameters(cmd, file);
        cmd.ExecuteNonQuery();
    }

    private static void BindFileParameters(SqliteCommand cmd, FileRecord file)
    {
        cmd.Parameters.AddWithValue("$discId", file.DiscId);
        cmd.Parameters.AddWithValue("$srcPath", file.SourcePath);
        cmd.Parameters.AddWithValue("$discPath", file.DiscPath);
        cmd.Parameters.AddWithValue("$size", file.SizeBytes);
        cmd.Parameters.AddWithValue("$hash", file.Hash);
        cmd.Parameters.AddWithValue("$zipped", file.IsZipped ? 1 : 0);
        cmd.Parameters.AddWithValue("$split", file.IsSplit ? 1 : 0);
        cmd.Parameters.AddWithValue("$deduped", file.IsDeduped ? 1 : 0);
        cmd.Parameters.AddWithValue("$fileRef", file.IsFileRef ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", file.Version);
        cmd.Parameters.AddWithValue("$deleted", file.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$srcWrite", file.SourceLastWriteUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$backedUp", file.BackedUpUtc.ToString("o"));
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesOnDiscAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files WHERE DiscId = $discId ORDER BY SourcePath";
        cmd.Parameters.AddWithValue("$discId", discId);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    public async Task<IReadOnlyList<FileRecord>> GetFilesForReplacementAsync(int badDiscId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files WHERE DiscId = $discId ORDER BY SourcePath";
        cmd.Parameters.AddWithValue("$discId", badDiscId);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    public async Task<IReadOnlyList<FileRecord>> GetAllFilesForBackupSetAsync(int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
            ORDER BY f.SourcePath, f.Version DESC
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadFile(r));
            // Reporting every few thousand rows keeps a large-set load (which can
            // read hundreds of thousands of rows over many seconds) from looking
            // like a hang, without paying callback overhead per row.
            if (rowProgress is not null && list.Count % 5000 == 0)
                rowProgress.Report(list.Count);
        }
        rowProgress?.Report(list.Count);
        return list;
    }

    public async Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT SourcePath, Version, SizeBytes, SourceLastWriteUtc, IsDeduped, IsFileRef, Hash
            FROM (
                SELECT f.SourcePath,
                       f.Version,
                       f.SizeBytes,
                       f.SourceLastWriteUtc,
                       f.IsDeduped,
                       f.IsFileRef,
                       f.Hash,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.SourcePath
                           ORDER BY f.Version DESC, f.Id DESC
                       ) AS rn
                FROM Files f
                INNER JOIN Discs d ON f.DiscId = d.Id
                WHERE d.BackupSetId = $setId AND f.IsDeleted = 0
            )
            WHERE rn = 1
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var dict = new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string path = r.GetString(0);
            dict[path] = new FileVersionInfo(
                MaxVersion: r.GetInt32(1),
                SizeBytes: r.GetInt64(2),
                SourceLastWriteUtc: DateTime.Parse(r.GetString(3), null, DateTimeStyles.RoundtripKind),
                IsDeduped: r.GetInt32(4) != 0,
                IsFileRef: r.GetInt32(5) != 0,
                Hash: r.IsDBNull(6) ? "" : r.GetString(6));
        }
        return dict;
    }

    public async Task<int> GetFileCountForBackupSetAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT f.SourcePath)
            FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId AND f.IsDeleted = 0
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        var result = cmd.ExecuteScalar();
        return result is long v ? (int)v : 0;
    }

    public async Task<FileRecord?> GetFileRecordByPathAndVersionAsync(int backupSetId, string sourcePath, int version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.SourcePath = $path
              AND f.Version = $ver
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$path", sourcePath);
        cmd.Parameters.AddWithValue("$ver", version);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFile(r) : null;
    }

    public async Task<IReadOnlyList<FileRecord>> GetFileRecordsByPathAsync(int backupSetId, string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.SourcePath = $path
            ORDER BY f.Version
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$path", sourcePath);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    public async Task<IReadOnlyList<FileRecord>> GetFileRecordsUnderDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        var prefix = directoryPrefix.TrimEnd('\\') + "\\";
        // Escape the escape character (backslash) FIRST — Windows source paths
        // are full of separators, and under ESCAPE '\' every un-doubled '\'
        // would swallow the following character, so an un-escaped prefix like
        // "D:\some\dir\%" matches NOTHING.  Order matters: doubling backslashes
        // after adding the "\[", "\%", "\_" escapes would corrupt those.
        var escaped = prefix
            .Replace("\\", "\\\\")
            .Replace("[", "\\[")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND (f.SourcePath LIKE $prefix ESCAPE '\' OR f.SourcePath = $exact)
            ORDER BY f.SourcePath, f.Version
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$prefix", escaped + "%");
        cmd.Parameters.AddWithValue("$exact", directoryPrefix);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    public async Task<HashSet<string>> GetActivePlainHashesAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT f.Hash FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.IsDeleted = 0
              AND f.IsFileRef = 0
              AND f.IsDeduped = 0
              AND f.IsSplit = 0
              AND f.IsZipped = 0
              AND f.Hash <> ''
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(r.GetString(0));
        return set;
    }

    public async Task<Dictionary<string, string>> GetActivePlainContentPathsAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.Hash, MIN(f.DiscPath) FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.IsDeleted = 0
              AND f.IsFileRef = 0
              AND f.IsDeduped = 0
              AND f.IsSplit = 0
              AND f.IsZipped = 0
              AND f.Hash <> ''
            GROUP BY f.Hash
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public async Task<HashSet<long>> GetActivePlainContentSizesAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT f.SizeBytes FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.IsDeleted = 0
              AND f.IsFileRef = 0
              AND f.IsDeduped = 0
              AND f.IsSplit = 0
              AND f.IsZipped = 0
              AND f.Hash <> ''
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var sizes = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            sizes.Add(r.GetInt64(0));
        return sizes;
    }

    public async Task<IReadOnlyList<FileRecord>> GetActiveRecordsByHashAsync(int backupSetId, string hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.Hash = $hash
              AND f.IsDeleted = 0
              AND f.IsSplit = 0
              AND f.IsZipped = 0
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$hash", hash);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    public async Task<int> MarkFilesDeletedByDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        var prefix = directoryPrefix.TrimEnd('\\') + "\\";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET IsDeleted = 1
            WHERE IsDeleted = 0
              AND DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND (SourcePath LIKE $prefix ESCAPE '\' OR SourcePath = $exact)
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        // Escape the escape character (backslash) FIRST — Windows source paths
        // are full of separators, and under ESCAPE '\' every un-doubled '\'
        // would swallow the following character, so an un-escaped prefix like
        // "D:\some\dir\%" matches NOTHING.  Order matters: doubling backslashes
        // after adding the "\[", "\%", "\_" escapes would corrupt those.
        var escaped = prefix
            .Replace("\\", "\\\\")
            .Replace("[", "\\[")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$prefix", escaped + "%");
        cmd.Parameters.AddWithValue("$exact", directoryPrefix);
        return cmd.ExecuteNonQuery();
    }

    public async Task<int> MarkFilesDeletedBySourcePathsAsync(
        int backupSetId, IEnumerable<string> sourcePaths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        int totalRows = 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET IsDeleted = 1
            WHERE IsDeleted = 0
              AND DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND SourcePath = $path
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        var pathParam = cmd.Parameters.AddWithValue("$path", "");

        foreach (var path in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            pathParam.Value = path;
            totalRows += cmd.ExecuteNonQuery();
        }
        return totalRows;
    }

    public async Task<int> CountFilesUnderSourcePrefixAsync(
        int backupSetId, string sourcePrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        var prefix = sourcePrefix.TrimEnd('\\') + "\\";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM Files
            WHERE DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND SourcePath LIKE $prefix ESCAPE '\'
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$prefix", EscapeLikePrefix(prefix) + "%");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public async Task<int> RemapSourcePathPrefixAsync(
        int backupSetId, string oldPrefix, string newPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        // Normalise both prefixes to a trailing separator so we replace whole
        // path segments only (a bare "E:" won't accidentally match "E:foo").
        var oldP = oldPrefix.TrimEnd('\\') + "\\";
        var newP = newPrefix.TrimEnd('\\') + "\\";

        using var cmd = _connection.CreateCommand();
        // Replace only the leading prefix: keep the tail after it verbatim.
        // SUBSTR is 1-based, so start at oldP.Length + 1.
        cmd.CommandText = """
            UPDATE Files
            SET SourcePath = $new || SUBSTR(SourcePath, $prefixLen + 1)
            WHERE DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND SourcePath LIKE $prefix ESCAPE '\'
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$new", newP);
        cmd.Parameters.AddWithValue("$prefixLen", oldP.Length);
        cmd.Parameters.AddWithValue("$prefix", EscapeLikePrefix(oldP) + "%");
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Escape a literal path prefix for use in a <c>LIKE ... ESCAPE '\'</c>
    /// pattern.  The escape character (backslash) must be doubled FIRST — Windows
    /// paths are full of separators and under <c>ESCAPE '\'</c> every un-doubled
    /// backslash would swallow the next character.  Then the LIKE wildcards
    /// (<c>[</c>, <c>%</c>, <c>_</c>) are escaped.  Order matters: doubling
    /// backslashes after adding those escapes would corrupt them.
    /// </summary>
    private static string EscapeLikePrefix(string prefix) => prefix
        .Replace("\\", "\\\\")
        .Replace("[", "\\[")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    private static FileRecord ReadFile(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
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
    };

    // ---------------------------------------------------------------
    // File chunks
    // ---------------------------------------------------------------

    public async Task CreateFileChunkAsync(FileChunk chunk, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FileChunks
                (FileRecordId, DiscId, Sequence, Offset, Length, DiscFilename)
            VALUES
                ($fileId, $discId, $seq, $offset, $length, $filename);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$fileId", chunk.FileRecordId);
        cmd.Parameters.AddWithValue("$discId", chunk.DiscId);
        cmd.Parameters.AddWithValue("$seq", chunk.Sequence);
        cmd.Parameters.AddWithValue("$offset", chunk.Offset);
        cmd.Parameters.AddWithValue("$length", chunk.Length);
        cmd.Parameters.AddWithValue("$filename", chunk.DiscFilename);
        chunk.Id = Convert.ToInt64(cmd.ExecuteScalar());
    }

    public async Task<IReadOnlyList<FileChunk>> GetChunksForFileAsync(long fileRecordId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM FileChunks WHERE FileRecordId = $fileId ORDER BY Sequence";
        cmd.Parameters.AddWithValue("$fileId", fileRecordId);

        var list = new List<FileChunk>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FileChunk
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                FileRecordId = r.GetInt64(r.GetOrdinal("FileRecordId")),
                DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
                Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
                Offset = r.GetInt64(r.GetOrdinal("Offset")),
                Length = r.GetInt64(r.GetOrdinal("Length")),
                DiscFilename = r.GetString(r.GetOrdinal("DiscFilename")),
            });
        }
        return list;
    }

    // ---------------------------------------------------------------
    // Search (per-set portion of cross-set search)
    // ---------------------------------------------------------------

    /// <summary>
    /// Summarise files in this set whose source path contains the substring.
    /// Returns null when the set has no matches.  The caller (master repository)
    /// supplies the set name from the master BackupSets table.
    /// </summary>
    public async Task<FileSearchResult?> SearchAsync(string pathSubstring, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*)           AS MatchingFileCount,
                SUM(f.SizeBytes)   AS TotalSizeBytes,
                MAX(f.Version)     AS LatestVersion,
                MAX(f.BackedUpUtc) AS LastBackedUpUtc
            FROM Files f
            WHERE f.SourcePath LIKE $pattern ESCAPE '\'
              AND f.IsDeleted = 0
            """;
        var escaped = pathSubstring
            .Replace("\\", "\\\\")
            .Replace("[", "\\[")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$pattern", "%" + escaped + "%");

        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0) || r.GetInt32(0) == 0)
            return null;

        return new FileSearchResult
        {
            MatchingFileCount = r.GetInt32(0),
            TotalSizeBytes = r.IsDBNull(1) ? 0 : r.GetInt64(1),
            LatestVersion = r.IsDBNull(2) ? 0 : r.GetInt32(2),
            LastBackedUpUtc = r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3), null, DateTimeStyles.RoundtripKind),
        };
    }

    // ---------------------------------------------------------------
    // Export + catalog clearing
    // ---------------------------------------------------------------

    public async Task ExportDatabaseAsync(string destinationPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", destinationPath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Delete every disc/file/chunk record in this set's database, leaving the
    /// (empty) database file in place.  Dedup blocks are also cleared since they
    /// are a per-set dedup-decision index, not authoritative storage.
    /// </summary>
    public async Task ClearCatalogAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tx = await BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            Execute("DELETE FROM FileChunks");
            Execute("DELETE FROM Files");
            Execute("DELETE FROM DeduplicationBlocks");
            Execute("DELETE FROM Discs");
            tx.Complete();
        }
        finally
        {
            tx.Dispose();
        }
    }

    /// <summary>
    /// Copy all disc/file/chunk records out of this set's database.  Used by the
    /// master repository when duplicating a set's backup history into another
    /// set.  Runs under the gate to give a consistent snapshot.
    /// </summary>
    public async Task<SetCatalogSnapshot> ReadSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        var discs = new List<DiscRecord>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Discs ORDER BY Id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                discs.Add(ReadDisc(r));
        }

        var files = new List<FileRecord>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Files ORDER BY Id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                files.Add(ReadFile(r));
        }

        var chunks = new List<FileChunk>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM FileChunks ORDER BY Id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                chunks.Add(new FileChunk
                {
                    Id = r.GetInt64(r.GetOrdinal("Id")),
                    FileRecordId = r.GetInt64(r.GetOrdinal("FileRecordId")),
                    DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
                    Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
                    Offset = r.GetInt64(r.GetOrdinal("Offset")),
                    Length = r.GetInt64(r.GetOrdinal("Length")),
                    DiscFilename = r.GetString(r.GetOrdinal("DiscFilename")),
                });
            }
        }

        return new SetCatalogSnapshot(discs, files, chunks);
    }

    // ---------------------------------------------------------------
    // One-time legacy migration
    // ---------------------------------------------------------------

    /// <summary>
    /// Import disc/file/chunk/block rows from the old combined catalog into this
    /// set database, <b>preserving their original primary-key Ids</b> so the
    /// FileChunks→Files and *→Discs references stay valid without remapping.
    /// Uses INSERT OR IGNORE so a re-run after an interrupted migration is a
    /// no-op for rows already present.
    /// </summary>
    public async Task ImportLegacyRecordsAsync(
        IReadOnlyList<DiscRecord> discs,
        IReadOnlyList<FileRecord> files,
        IReadOnlyList<FileChunk> chunks,
        IReadOnlyList<DeduplicationBlock> blocks,
        CancellationToken ct = default)
    {
        var tx = await BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var disc in discs)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO Discs
                        (Id, BackupSetId, Label, SequenceNumber, MediaType, FilesystemType,
                         Capacity, BytesUsed, RewriteCount, IsMultisession, IsBad, Status,
                         CreatedUtc, LastWrittenUtc)
                    VALUES
                        ($id, $setId, $label, $seq, $mediaType, $fsType,
                         $capacity, $used, $rewrites, $multi, $isBad, $status,
                         $created, $lastWritten)
                    """;
                cmd.Parameters.AddWithValue("$id", disc.Id);
                cmd.Parameters.AddWithValue("$setId", disc.BackupSetId);
                cmd.Parameters.AddWithValue("$label", disc.Label);
                cmd.Parameters.AddWithValue("$seq", disc.SequenceNumber);
                cmd.Parameters.AddWithValue("$mediaType", (int)disc.MediaType);
                cmd.Parameters.AddWithValue("$fsType", (int)disc.FilesystemType);
                cmd.Parameters.AddWithValue("$capacity", disc.Capacity);
                cmd.Parameters.AddWithValue("$used", disc.BytesUsed);
                cmd.Parameters.AddWithValue("$rewrites", disc.RewriteCount);
                cmd.Parameters.AddWithValue("$multi", disc.IsMultisession ? 1 : 0);
                cmd.Parameters.AddWithValue("$isBad", disc.IsBad ? 1 : 0);
                cmd.Parameters.AddWithValue("$status", (int)disc.Status);
                cmd.Parameters.AddWithValue("$created", disc.CreatedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$lastWritten",
                    disc.LastWrittenUtc.HasValue ? disc.LastWrittenUtc.Value.ToString("o") : DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            foreach (var file in files)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO Files
                        (Id, DiscId, SourcePath, DiscPath, SizeBytes, Hash,
                         IsZipped, IsSplit, IsDeduped, IsFileRef, Version, IsDeleted,
                         SourceLastWriteUtc, BackedUpUtc)
                    VALUES
                        ($id, $discId, $srcPath, $discPath, $size, $hash,
                         $zipped, $split, $deduped, $fileRef, $version, $deleted,
                         $srcWrite, $backedUp)
                    """;
                cmd.Parameters.AddWithValue("$id", file.Id);
                BindFileParameters(cmd, file);
                cmd.ExecuteNonQuery();
            }

            foreach (var chunk in chunks)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO FileChunks
                        (Id, FileRecordId, DiscId, Sequence, Offset, Length, DiscFilename)
                    VALUES
                        ($id, $fileId, $discId, $seq, $offset, $length, $filename)
                    """;
                cmd.Parameters.AddWithValue("$id", chunk.Id);
                cmd.Parameters.AddWithValue("$fileId", chunk.FileRecordId);
                cmd.Parameters.AddWithValue("$discId", chunk.DiscId);
                cmd.Parameters.AddWithValue("$seq", chunk.Sequence);
                cmd.Parameters.AddWithValue("$offset", chunk.Offset);
                cmd.Parameters.AddWithValue("$length", chunk.Length);
                cmd.Parameters.AddWithValue("$filename", chunk.DiscFilename);
                cmd.ExecuteNonQuery();
            }

            foreach (var block in blocks)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO DeduplicationBlocks
                        (Id, Hash, SizeBytes, ReferenceCount, DiscId)
                    VALUES ($id, $hash, $size, $refs, $discId)
                    """;
                cmd.Parameters.AddWithValue("$id", block.Id);
                cmd.Parameters.AddWithValue("$hash", block.Hash);
                cmd.Parameters.AddWithValue("$size", block.SizeBytes);
                cmd.Parameters.AddWithValue("$refs", block.ReferenceCount);
                cmd.Parameters.AddWithValue("$discId", block.DiscId);
                cmd.ExecuteNonQuery();
            }

            tx.Complete();
        }
        finally
        {
            tx.Dispose();
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Immutable snapshot of one set's catalog records, used when copying history.</summary>
internal sealed record SetCatalogSnapshot(
    IReadOnlyList<DiscRecord> Discs,
    IReadOnlyList<FileRecord> Files,
    IReadOnlyList<FileChunk> Chunks);
