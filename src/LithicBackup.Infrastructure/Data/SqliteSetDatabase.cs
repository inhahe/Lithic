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
/// <b>In-process concurrency.</b>  A single <see cref="SqliteConnection"/> is not
/// safe for concurrent command execution, so every operation is serialised
/// through a <see cref="SemaphoreSlim"/> gate.  A transaction holds the gate for
/// its whole lifetime; calls made <i>inside</i> a transaction must not re-acquire
/// the gate (the semaphore is not reentrant — that would self-deadlock), so a
/// <see cref="_inTransaction"/> flag short-circuits the lock for them.  The flag
/// is only correct under the application's invariant that at most one logical
/// operation touches a given set at a time <i>within one process</i> (the GUI
/// gates per-set commands so a set that is backing up cannot also be
/// restored/cleaned).
/// </para>
/// <para>
/// <b>Cross-process concurrency (GUI + Worker).</b>  The interactive GUI and the
/// LocalSystem Worker service are separate processes that open their own
/// connections to the <i>same</i> set database file (both under
/// <c>C:\ProgramData\LithicBackup\sets</c>).  WAL mode lets their readers run
/// concurrently, but two <i>writers</i> on the same set would collide: the Worker
/// holds a write transaction for up to ~30&#160;s per commit batch during a
/// continuous backup, which is longer than any reasonable SQLite busy timeout, so
/// a GUI write (cleanup, reconcile, clear) that landed mid-batch used to fail with
/// "database is locked" (SQLITE_BUSY).  To make that impossible, every write —
/// each transaction and each standalone write statement — first takes a
/// <b>cross-process exclusive lock</b> on a per-set lock file
/// (<see cref="_writeLockPath"/>) via <see cref="AcquireCrossProcessWriteAsync"/>.
/// Only one process can hold that file open with <see cref="FileShare.None"/> at a
/// time, so writers on a given set serialise across processes and the SQLite write
/// lock is never actually contended.  The wait is cancellable and unbounded
/// (the holder always releases when its transaction ends, and the OS releases the
/// file automatically if a process crashes), so no arbitrary timeout can ever turn
/// legitimate contention back into a failure.  Reads deliberately do <i>not</i>
/// take the file lock, preserving WAL's concurrent-reader benefit.
/// </para>
/// </summary>
internal sealed class SqliteSetDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _inTransaction;

    /// <summary>
    /// Path of the per-set cross-process write-lock file.  A writer opens this
    /// with <see cref="FileShare.None"/> for the duration of its transaction /
    /// statement; a second process opening it fails until the first releases, so
    /// writers on this set serialise machine-wide.  It lives beside the database
    /// (not inside it) so holding it never touches SQLite's own locks.
    /// </summary>
    private readonly string _writeLockPath;

    public int SetId { get; }
    public string DatabasePath { get; }

    public SqliteSetDatabase(int setId, string databasePath)
    {
        SetId = setId;
        DatabasePath = databasePath;
        _writeLockPath = databasePath + ".writelock";

        // Pooling=False so the file handle is released promptly on Dispose,
        // which lets DeleteBackupSetAsync remove the set's .db file.
        _connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA foreign_keys=ON");
        // Backstop only: the cross-process write-lock file (see class remarks)
        // prevents writer-vs-writer contention, so this timeout just covers brief
        // WAL-checkpoint edge cases rather than long transactions.
        Execute("PRAGMA busy_timeout=30000");

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

    /// <summary>
    /// Acquire the in-process gate <b>plus</b> the cross-process write lock for a
    /// standalone write statement.  Used by every method that mutates the set DB
    /// (as opposed to <see cref="LockAsync"/>, used by reads).  When already inside
    /// a transaction the flow already owns both locks, so this is a no-op.
    /// </summary>
    private async Task<WriteReleaser> WriteLockAsync(CancellationToken ct)
    {
        // Reentrant short-circuit: an inside-transaction write already holds both
        // the file lock and the gate.
        if (_inTransaction)
            return default;

        // Cross-process first, then in-process, so the release order (gate then
        // file) is the exact reverse of acquisition.
        var fileLock = await AcquireCrossProcessWriteAsync(ct).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
        return new WriteReleaser(this, fileLock);
    }

    /// <summary>
    /// Open the per-set write-lock file with <see cref="FileShare.None"/>, waiting
    /// (cancellably, without any timeout) until no other process holds it.  The OS
    /// releases the handle automatically if the holder crashes, so this can never
    /// dead-wait on a dead process.  Returns the owning stream; disposing it frees
    /// the lock for the next writer.
    /// </summary>
    private async Task<FileStream> AcquireCrossProcessWriteAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _writeLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                // Another process (or another writer in this one) holds the lock.
                // Poll until it is released; the wait is unbounded by design so
                // legitimate contention never degrades into a "database is locked".
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                // Transient sharing/ACL race during creation — retry.
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<ICatalogTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // A transaction is a write: take the cross-process lock before the gate so
        // no other process can be mid-write on this set while we hold SQLite's
        // write lock for the whole transaction.
        var fileLock = await AcquireCrossProcessWriteAsync(ct).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }

        try
        {
            var tx = _connection.BeginTransaction();
            _inTransaction = true;
            return new TransactionScope(this, tx, fileLock);
        }
        catch
        {
            _gate.Release();
            fileLock.Dispose();
            throw;
        }
    }

    private readonly struct Releaser(SqliteSetDatabase? owner) : IDisposable
    {
        public void Dispose() => owner?._gate.Release();
    }

    /// <summary>
    /// Releases a standalone write: the in-process gate first, then the
    /// cross-process file lock (reverse of acquisition order).
    /// </summary>
    private readonly struct WriteReleaser(SqliteSetDatabase? owner, FileStream? fileLock) : IDisposable
    {
        public void Dispose()
        {
            owner?._gate.Release();
            fileLock?.Dispose();
        }
    }

    /// <summary>
    /// Commits on <see cref="Complete"/> + Dispose, otherwise rolls back, then
    /// clears the in-transaction flag and releases the owning set's gate.
    /// </summary>
    private sealed class TransactionScope : ICatalogTransaction
    {
        private readonly SqliteSetDatabase _owner;
        private SqliteTransaction? _transaction;
        private FileStream? _fileLock;
        private bool _completed;

        public TransactionScope(SqliteSetDatabase owner, SqliteTransaction transaction, FileStream fileLock)
        {
            _owner = owner;
            _transaction = transaction;
            _fileLock = fileLock;
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
                // Release the cross-process write lock last (reverse of the
                // acquisition order in BeginTransactionAsync).
                _fileLock?.Dispose();
                _fileLock = null;
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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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

    public async Task<IReadOnlyList<(string DiscPath, bool IsDeleted, string SourcePath)>>
        GetDiscPathEntriesForBackupSetAsync(int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        // Only the three columns the destination walk needs, and NO ORDER BY:
        // an unsorted read streams rows as the disc/file indexes yield them, so
        // rowProgress advances immediately instead of the whole set having to be
        // materialised and sorted (the cause of a large set appearing to hang for
        // minutes before the walk even started). Columns are read positionally to
        // avoid a per-row GetOrdinal lookup.
        cmd.CommandText = """
            SELECT f.DiscPath, f.IsDeleted, f.SourcePath FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var list = new List<(string, bool, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            list.Add((
                r.GetString(0),
                r.GetInt32(1) != 0,
                r.IsDBNull(2) ? "" : r.GetString(2)));
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
                           -- COLLATE NOCASE: the Windows filesystem is
                           -- case-insensitive, so "Foo\bar.txt" and
                           -- "foo\bar.txt" are the SAME file and must share one
                           -- version chain.  Without NOCASE, a case-only rename
                           -- forks the chain (two "current" rows) and breaks the
                           -- _prev repoint, orphaning old versions on disk.
                           PARTITION BY f.SourcePath COLLATE NOCASE
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

    public async Task<Dictionary<string, FileVersionInfo>> GetOrphanedVersionInfoAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        // Latest record per SourcePath across ALL rows (deleted included),
        // keeping only paths whose newest row is soft-deleted. A path with a
        // live (IsDeleted = 0) latest row is already covered by
        // GetLatestVersionInfoAsync and is intentionally excluded here so the
        // two dictionaries never overlap. COLLATE NOCASE mirrors that method:
        // the Windows filesystem is case-insensitive, so all casings of a path
        // share one version chain.
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
                       f.IsDeleted,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.SourcePath COLLATE NOCASE
                           ORDER BY f.Version DESC, f.Id DESC
                       ) AS rn
                FROM Files f
                INNER JOIN Discs d ON f.DiscId = d.Id
                WHERE d.BackupSetId = $setId
            )
            WHERE rn = 1 AND IsDeleted = 1
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
            SELECT COUNT(DISTINCT f.SourcePath COLLATE NOCASE)
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
              AND f.SourcePath = $path COLLATE NOCASE
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
              AND f.SourcePath = $path COLLATE NOCASE
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
              AND (f.SourcePath LIKE $prefix ESCAPE '\' OR f.SourcePath = $exact COLLATE NOCASE)
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

    // ---------------------------------------------------------------
    // Lazy restore browser
    // ---------------------------------------------------------------

    // A string that compares greater than any real source path sharing a given
    // prefix, used as the exclusive upper bound of a "everything under P" range.
    // U+FFFF is a non-character never present in a Windows path, and COLLATE
    // NOCASE (which only case-folds ASCII) leaves it maximal, so "P" .. "P\uFFFF"
    // brackets exactly the paths under P on the NOCASE index.
    private const string PathUpperSentinel = "\uFFFF";

    /// <summary>
    /// List the direct children (subdirectories + files directly inside) of a
    /// directory via a loose-index skip-scan: seek to the first active path under
    /// the prefix, emit the child it belongs to, then jump the cursor past that
    /// child's whole subtree and repeat.  O(direct children) index seeks — never a
    /// full subtree scan — so expanding a node stays cheap even on a huge set.
    /// </summary>
    public async Task<IReadOnlyList<RestoreTreeChild>> GetRestoreChildrenAsync(
        string parentPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        // Normalise to a directory prefix WITH a trailing separator so a child is
        // simply the next path segment after it.  Empty parent = list the roots.
        string prefix = parentPrefix.Length == 0 ? "" : parentPrefix.TrimEnd('\\') + "\\";
        string upper = prefix + PathUpperSentinel;

        var dirs = new List<RestoreTreeChild>();
        var files = new List<RestoreTreeChild>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT SourcePath, SizeBytes, BackedUpUtc
            FROM Files
            WHERE IsDeleted = 0
              AND SourcePath > $cursor COLLATE NOCASE
              AND SourcePath < $upper COLLATE NOCASE
            ORDER BY SourcePath COLLATE NOCASE, Version DESC
            LIMIT 1
            """;
        string cursor = prefix;                       // first child is strictly > prefix
        var pCursor = cmd.Parameters.AddWithValue("$cursor", cursor);
        cmd.Parameters.AddWithValue("$upper", upper);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pCursor.Value = cursor;

            string sp;
            long size;
            string backedUp;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read())
                    break;
                sp = r.GetString(0);
                size = r.GetInt64(1);
                backedUp = r.GetString(2);
            }

            string rest = sp.Substring(prefix.Length);
            int sep = rest.IndexOf('\\');
            if (sep < 0)
            {
                // Direct file child.
                files.Add(new RestoreTreeChild(
                    rest, sp, false, size,
                    DateTime.Parse(backedUp, null, DateTimeStyles.RoundtripKind)));
                cursor = sp;                          // skip all versions of this path
            }
            else if (sep == 0)
            {
                // A leading separator. At the root prefix this is a UNC path
                // (\\server\share\...), which groups under its share root just like
                // a drive letter does. Anywhere else it is a malformed double
                // separator we skip past so the loop keeps advancing.
                string? share = prefix.Length == 0 ? TryGetUncShareRoot(rest) : null;
                if (share is not null)
                {
                    dirs.Add(new RestoreTreeChild(share, share, true, 0, null));
                    cursor = share + "\\" + PathUpperSentinel; // jump past this share's subtree
                }
                else
                {
                    cursor = sp;
                }
            }
            else
            {
                string name = rest.Substring(0, sep);
                string full = prefix + name;
                dirs.Add(new RestoreTreeChild(name, full, true, 0, null));
                cursor = full + "\\" + PathUpperSentinel; // jump past this subtree
            }
        }

        // Directories first, then files, each alphabetical (case-insensitive).
        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var result = new List<RestoreTreeChild>(dirs.Count + files.Count);
        result.AddRange(dirs);
        result.AddRange(files);
        return result;
    }

    /// <summary>
    /// The UNC share root (<c>\\server\share</c>) of a path that starts with a
    /// separator, or null if the path is not a complete UNC path (e.g. just
    /// <c>\\server</c>). Used to give network-share backups a single root node in
    /// the restore browser, mirroring how a drive letter roots a local path.
    /// </summary>
    private static string? TryGetUncShareRoot(string path)
    {
        int i = 0;
        while (i < path.Length && path[i] == '\\')
            i++;
        if (i == 0)
            return null;                                   // no leading separator after all
        int serverEnd = path.IndexOf('\\', i);
        if (serverEnd < 0)
            return null;                                   // only "\\server", no share yet
        int shareEnd = path.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0 ? path : path.Substring(0, shareEnd);
    }

    /// <summary>
    /// Count of distinct active source paths under a directory prefix, and the sum
    /// of their latest-version sizes.  Bounded by the NOCASE active-path index.
    /// </summary>
    public async Task<(int FileCount, long TotalBytes)> GetActiveSubtreeStatsAsync(
        string directoryPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        string prefix = directoryPrefix.Length == 0 ? "" : directoryPrefix.TrimEnd('\\') + "\\";
        string upper = prefix + PathUpperSentinel;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(SizeBytes), 0)
            FROM (
                SELECT SizeBytes,
                       ROW_NUMBER() OVER (
                           PARTITION BY SourcePath COLLATE NOCASE
                           ORDER BY Version DESC, Id DESC
                       ) AS rn
                FROM Files
                WHERE IsDeleted = 0
                  AND SourcePath > $low COLLATE NOCASE
                  AND SourcePath < $upper COLLATE NOCASE
            )
            WHERE rn = 1
            """;
        cmd.Parameters.AddWithValue("$low", prefix);
        cmd.Parameters.AddWithValue("$upper", upper);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ((int)r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }

    /// <summary>
    /// The latest active record for each distinct source path under a directory
    /// prefix (the concrete files a fully-selected directory expands to at restore).
    /// </summary>
    public async Task<IReadOnlyList<FileRecord>> GetActiveLatestRecordsUnderPrefixAsync(
        string directoryPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        string prefix = directoryPrefix.Length == 0 ? "" : directoryPrefix.TrimEnd('\\') + "\\";
        string upper = prefix + PathUpperSentinel;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM (
                SELECT f.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.SourcePath COLLATE NOCASE
                           ORDER BY f.Version DESC, f.Id DESC
                       ) AS rn
                FROM Files f
                WHERE f.IsDeleted = 0
                  AND f.SourcePath > $low COLLATE NOCASE
                  AND f.SourcePath < $upper COLLATE NOCASE
            )
            WHERE rn = 1
            """;
        cmd.Parameters.AddWithValue("$low", prefix);
        cmd.Parameters.AddWithValue("$upper", upper);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    /// <summary>The latest active record for one exact source path, or null.</summary>
    public async Task<FileRecord?> GetActiveLatestRecordByPathAsync(
        string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM Files
            WHERE IsDeleted = 0 AND SourcePath = $path COLLATE NOCASE
            ORDER BY Version DESC, Id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$path", sourcePath);

        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFile(r) : null;
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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

        var prefix = directoryPrefix.TrimEnd('\\') + "\\";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET IsDeleted = 1
            WHERE IsDeleted = 0
              AND DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND (SourcePath LIKE $prefix ESCAPE '\' OR SourcePath = $exact COLLATE NOCASE)
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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

        int totalRows = 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET IsDeleted = 1
            WHERE IsDeleted = 0
              AND DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND SourcePath = $path COLLATE NOCASE
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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

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
