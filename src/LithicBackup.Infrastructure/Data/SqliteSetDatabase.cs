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
/// its whole lifetime; calls made <i>inside</i> it, by the flow that began it,
/// must not re-acquire the gate (the semaphore is not reentrant — that would
/// self-deadlock), so they skip it. Only that flow: it carries a token
/// (<see cref="_flow"/>, an <see cref="AsyncLocal{T}"/>) that the open
/// transaction names as its owner (<see cref="_txOwner"/>). Any other flow -
/// the editor's plan check while a seed commits, Backup Coverage or a Cleanup scan
/// while a backup runs - waits at the gate for the transaction to end instead of
/// running its commands on the shared connection, inside someone else's
/// transaction, from another thread.
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

    /// <summary>
    /// The token of the flow this code runs in, once it has begun a transaction on
    /// this set. It follows the flow through awaits and into the work it calls, so
    /// everything that flow does inside its transaction carries it.
    /// </summary>
    private readonly AsyncLocal<FlowToken?> _flow = new();

    /// <summary>The token of the flow whose transaction is open; null when none is.</summary>
    private volatile FlowToken? _txOwner;

    /// <summary>Set by <see cref="CloseAsync"/>: the set was deleted.</summary>
    private volatile bool _closed;

    /// <summary>Identifies one flow of work to <see cref="_txOwner"/>; nothing else.</summary>
    private sealed class FlowToken;

    /// <summary>Whether the open transaction, if any, was begun by the flow asking.</summary>
    private bool OwnsTransaction => _txOwner is { } owner && ReferenceEquals(_flow.Value, owner);

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

        // Stamp a new database's schema version. Checked with a read first: an
        // INSERT takes SQLite's write lock even when it inserts nothing, so doing it
        // unconditionally made every open of an existing set wait for the other
        // process's write transaction - with continuous backup committing back to
        // back, until its run ended or the 30 s busy timeout threw.
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = "SELECT EXISTS (SELECT 1 FROM SchemaVersion)";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
                Execute("INSERT INTO SchemaVersion (Version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion)");
        }
    }

    // ---------------------------------------------------------------
    // Gate + transactions
    // ---------------------------------------------------------------

    private async Task<Releaser> LockAsync(CancellationToken ct)
    {
        await new OffUiThread();

        // Reentrant short-circuit: the flow that holds this set's transaction
        // already holds the gate. Any other flow waits for it.
        if (OwnsTransaction)
            return default;

        await EnterGateAsync(ct).ConfigureAwait(false);
        return new Releaser(this);
    }

    /// <summary>
    /// Take the gate, and give it straight back with
    /// <see cref="BackupSetDeletedException"/> if the set has been deleted
    /// meanwhile (<see cref="CloseAsync"/>). Every way into the gate is this one.
    /// </summary>
    private async Task EnterGateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (_closed)
        {
            _gate.Release();
            throw new BackupSetDeletedException(SetId);
        }
    }

    /// <summary>
    /// The set has been deleted: wait for whatever holds the gate to finish (a
    /// read under way, an open transaction), then close the connection so the
    /// files can go. Every call after that is refused with
    /// <see cref="BackupSetDeletedException"/> rather than reading a disposed
    /// connection. The gate itself is left alive for those late callers to take.
    /// </summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closed = true;
            _connection.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Awaited first by every call that takes the gate, so the work after it runs
    /// on the thread pool when the call came from the UI thread (a
    /// SynchronizationContext): the catalog's async methods do their SQLite work
    /// before they first truly await, and that work can be a read of millions of
    /// rows. The transaction's owner (an AsyncLocal) flows with the hop; the gate
    /// and the write lock are not bound to a thread.
    /// </summary>
    private readonly struct OffUiThread : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        public OffUiThread GetAwaiter() => this;
        public bool IsCompleted => SynchronizationContext.Current is null;
        public void GetResult() { }
        public void OnCompleted(Action continuation) =>
            ThreadPool.QueueUserWorkItem(static c => ((Action)c!)(), continuation);
        public void UnsafeOnCompleted(Action continuation) =>
            ThreadPool.UnsafeQueueUserWorkItem(static c => ((Action)c!)(), continuation);
    }

    /// <summary>
    /// Acquire the in-process gate <b>plus</b> the cross-process write lock for a
    /// standalone write statement.  Used by every method that mutates the set DB
    /// (as opposed to <see cref="LockAsync"/>, used by reads).  When already inside
    /// a transaction the flow already owns both locks, so this is a no-op.
    /// </summary>
    private async Task<WriteReleaser> WriteLockAsync(CancellationToken ct)
    {
        await new OffUiThread();

        // Reentrant short-circuit: the flow that holds this set's transaction
        // already holds both the file lock and the gate.
        if (OwnsTransaction)
            return default;

        // Cross-process first, then in-process, so the release order (gate then
        // file) is the exact reverse of acquisition.
        var fileLock = await AcquireCrossProcessWriteAsync(ct).ConfigureAwait(false);
        try
        {
            await EnterGateAsync(ct).ConfigureAwait(false);
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
    /// <param name="onWaiting">
    /// Called once, the first time the lock turns out to be held elsewhere, so a
    /// caller with a progress display can say it is waiting.
    /// </param>
    private async Task<FileStream> AcquireCrossProcessWriteAsync(
        CancellationToken ct, Action? onWaiting = null)
    {
        // A background writer lets a writer that is already waiting in another
        // process go first (see YieldToWaitingWriters).
        await WaitWhileAnotherProcessWaitsAsync(ct).ConfigureAwait(false);

        string? marker = null;
        var markerTouched = DateTime.MinValue;
        try
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another process (or another writer in this one) holds the
                    // lock, or a transient sharing/ACL race during creation. Poll
                    // until it is released; the wait is unbounded by design so
                    // legitimate contention never degrades into a "database is
                    // locked".
                }

                if (marker is null)
                {
                    onWaiting?.Invoke();
                    marker = WaitMarkerPath(Environment.ProcessId);
                }

                // Say that we are waiting, and keep saying it, so a background
                // holder steps aside at its next commit instead of re-taking the
                // lock the instant it lets go - which, polled every 25 ms against a
                // re-take measured in microseconds, it effectively always won.
                // Only the process that steps aside reads these, so a background
                // writer does not bother to leave one.
                if (!YieldToWaitingWriters && DateTime.UtcNow - markerTouched > MarkerRefresh)
                {
                    try
                    {
                        File.WriteAllText(marker, "");
                        markerTouched = DateTime.UtcNow;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Best effort: without the marker we just wait as before.
                    }
                }

                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (marker is not null && !YieldToWaitingWriters)
            {
                try { File.Delete(marker); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // ---------------------------------------------------------------
    // Cross-process fairness
    // ---------------------------------------------------------------

    /// <summary>
    /// When true, this process steps aside for a writer in another process that is
    /// waiting for the same set: before taking the set's write lock it waits until
    /// that writer has had its turn. The Worker service sets it. Its continuous
    /// backup commits every 50 files or 30 s and takes the lock back within
    /// microseconds of letting it go, while a waiter polls every 25 ms, so without
    /// this a backup the user started could not get in until the Worker's whole
    /// run was over - which on a large run is hours ("Checking 23,780 file(s)
    /// against the catalog..." until then). The GUI does not step aside: the work
    /// the user is watching goes first, and the Worker carries on after it.
    /// </summary>
    internal static bool YieldToWaitingWriters { get; set; }

    /// <summary>How often a waiter refreshes its marker.</summary>
    private static readonly TimeSpan MarkerRefresh = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A marker older than this is ignored: its process gave up or died without
    /// removing it. Several refresh periods, so a busy waiter never looks stale.
    /// </summary>
    private static readonly TimeSpan MarkerStale = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest a background writer steps aside at one acquisition, a safety
    /// valve against a marker that stays fresh while its owner never takes the lock.
    /// A real waiter gets in within a poll or two.
    /// </summary>
    private static readonly TimeSpan MaxYield = TimeSpan.FromMinutes(2);

    private string WaitMarkerPath(int processId) => $"{_writeLockPath}.wait-{processId}";

    /// <summary>
    /// For a background writer (<see cref="YieldToWaitingWriters"/>): wait while a
    /// writer in another process is waiting for this set's lock. Its marker goes
    /// when it gets the lock, so this returns as soon as it is in.
    /// </summary>
    private async Task WaitWhileAnotherProcessWaitsAsync(CancellationToken ct)
    {
        if (!YieldToWaitingWriters)
            return;

        var giveUp = DateTime.UtcNow + MaxYield;
        while (AnotherProcessIsWaiting() && DateTime.UtcNow < giveUp)
            await Task.Delay(25, ct).ConfigureAwait(false);
    }

    private bool AnotherProcessIsWaiting()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_writeLockPath);
            if (dir is null)
                return false;
            string prefix = Path.GetFileName(_writeLockPath) + ".wait-";
            foreach (var file in Directory.EnumerateFiles(dir, prefix + "*"))
            {
                if (!int.TryParse(Path.GetFileName(file).AsSpan(prefix.Length), out int pid)
                    || pid == Environment.ProcessId)
                    continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MarkerStale)
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell: don't hold anything up over it.
        }
        return false;
    }

    /// <summary>
    /// Begin a transaction owned by the calling flow (see the class remarks).
    /// </summary>
    /// <remarks>
    /// Not <c>async</c>, on purpose: an <see cref="AsyncLocal{T}"/> set in an async
    /// method is gone when that method returns, while set here it lands in the
    /// caller's own context, so the code that goes on to use the transaction carries
    /// the token. A flow that already has one keeps it - a backup re-taking its
    /// transaction after each commit does so from a helper, and the token is the
    /// same object the rest of the run carries.
    /// </remarks>
    public Task<ICatalogTransaction> BeginTransactionAsync(
        CancellationToken ct, Action? onWaitingForOtherProcess = null)
    {
        var token = _flow.Value ??= new FlowToken();
        return BeginTransactionCoreAsync(token, ct, onWaitingForOtherProcess);
    }

    private async Task<ICatalogTransaction> BeginTransactionCoreAsync(
        FlowToken token, CancellationToken ct, Action? onWaitingForOtherProcess)
    {
        ct.ThrowIfCancellationRequested();

        // A transaction is a write: take the cross-process lock before the gate so
        // no other process can be mid-write on this set while we hold SQLite's
        // write lock for the whole transaction.
        var fileLock = await AcquireCrossProcessWriteAsync(ct, onWaitingForOtherProcess)
            .ConfigureAwait(false);
        try
        {
            await EnterGateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }

        try
        {
            var tx = _connection.BeginTransaction();
            _txOwner = token;
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
    /// clears the transaction's owner and releases the owning set's gate.
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
                _owner._txOwner = null;
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
        foreach (var r in Rows(cmd, ct))
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

    /// <summary>
    /// The rows <paramref name="cmd"/> - a read - returns, ending in
    /// <see cref="OperationCanceledException"/> as soon as <paramref name="ct"/> is
    /// cancelled: between rows, and inside SQLite while it works
    /// (<see cref="SqliteCancellation"/>). A read ordered by source path sorts the
    /// whole set before its first row (a temp B-tree: the indexes do not give that
    /// order), and that sort can be most of the read. Reads only: these run on the
    /// set's connection, often inside a transaction, where an aborted write would
    /// roll the whole transaction back. The reader is disposed when the loop ends.
    /// </summary>
    private static IEnumerable<SqliteDataReader> Rows(SqliteCommand cmd, CancellationToken ct)
    {
        System.Diagnostics.Debug.Assert(
            cmd.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase),
            "Rows is for reads: an aborted write rolls back the caller's transaction.");
        ct.ThrowIfCancellationRequested();
        using var watch = SqliteCancellation.Watch(cmd.Connection!, ct);
        using var r = SqliteCancellation.Interruptible(cmd.ExecuteReader, ct);
        Func<bool> read = r.Read;
        while (SqliteCancellation.Interruptible(read, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return r;
        }
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
        foreach (var r in Rows(cmd, ct))
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
        foreach (var r in Rows(cmd, ct))
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
        foreach (var r in Rows(cmd, ct))
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

    public async Task<IReadOnlyList<FileRecord>> GetActiveFilesForClassificationAsync(
        int backupSetId, CancellationToken ct = default, IProgress<int>? rowProgress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        // Only the columns the classifier reads, and tombstones filtered in SQL
        // rather than in memory. The old path selected f.* for every row and then
        // did .Where(!IsDeleted) on the result, so both the tombstones and every
        // 64-character Hash were materialised and held for the whole scan.
        //
        // WARNING: the FileRecords returned here are PARTIAL. DiscId, Hash,
        // Version, SourceLastWriteUtc and the IsZipped/IsSplit/IsDeduped/IsFileRef
        // flags are all left at their defaults. Never hand one to
        // UpdateFileRecordAsync — that rewrites every column, so it would zero the
        // storage flags that tell restore how to read the file's bytes. Cleanup's
        // purge did exactly that; it was caught only because DiscId = 0 fails the
        // disc-to-set routing before any write happens. Use
        // MarkFileRecordsDeletedByIdsAsync, or re-read the row in full first.
        //
        // ORDER BY is load-bearing, not cosmetic: callers depend on rows for one
        // directory being contiguous and versions of one path being adjacent.
        cmd.CommandText = """
            SELECT f.Id, f.SourcePath, f.DiscPath, f.SizeBytes, f.BackedUpUtc
            FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId AND f.IsDeleted = 0
            ORDER BY f.SourcePath, f.Version DESC
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var list = new List<FileRecord>();
        foreach (var r in Rows(cmd, ct))
        {
            list.Add(new FileRecord
            {
                Id = r.GetInt64(0),
                SourcePath = r.GetString(1),
                DiscPath = r.GetString(2),
                SizeBytes = r.GetInt64(3),
                BackedUpUtc = DateTime.TryParse(
                    r.GetString(4), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var b)
                        ? b : default,
            });
            if (rowProgress is not null && list.Count % 5000 == 0)
                rowProgress.Report(list.Count);
        }
        rowProgress?.Report(list.Count);
        return list;
    }

    public async Task ForEachDiscPathEntryAsync(
        int backupSetId,
        Action<string, bool, string> onEntry,
        CancellationToken ct = default,
        IProgress<int>? rowProgress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        // Only the three columns the destination walk needs, and NO ORDER BY so
        // rows stream as the indexes yield them. Handed to the caller one at a
        // time so nothing accumulates a second copy of the set: the previous
        // list-returning version cost 1,751 MB on a 2.81M-row set against 754 MB
        // for the aggregate the caller actually wanted.
        // Columns are read positionally to avoid a per-row GetOrdinal lookup.
        cmd.CommandText = """
            SELECT f.DiscPath, f.IsDeleted, f.SourcePath FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        long rows = 0;
        foreach (var r in Rows(cmd, ct))
        {
            onEntry(
                r.GetString(0),
                r.GetInt32(1) != 0,
                r.IsDBNull(2) ? "" : r.GetString(2));
            rows++;
            if (rowProgress is not null && rows % 5000 == 0)
                rowProgress.Report((int)rows);
        }
        rowProgress?.Report((int)rows);
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
        foreach (var r in Rows(cmd, ct))
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

    // ---------------------------------------------------------------------
    // Path-scoped forms of the "latest version per path" query.
    //
    // GetLatestVersionInfoAsync builds a map for the WHOLE set. That means a
    // full table scan plus a window-function sort
    // over every row — measured at ~12.5 s / 2.2M rows on a real 1.9 GB set
    // database. Continuous backup only ever needs the entries for the handful
    // of paths the USN journal just reported (typically 1-30), so paying the
    // whole-set cost once per wake-up pegged a CPU core and drove ~110 MB/s of
    // pure reads in the Worker service. See the "Worker pegged a CPU core"
    // entry in known-issues.md.
    //
    // These do one indexed seek per path instead:
    //   ... WHERE SourcePath = $path COLLATE NOCASE ORDER BY Version DESC, Id DESC LIMIT 1
    // which is the same row the ROW_NUMBER() ... rn = 1 formulation picks, but
    // resolved through IX_Files_SourcePath_NoCase / the partial
    // IX_Files_Active_SourcePath_NoCase rather than by scanning and sorting.
    // Cost is O(paths x log n), so a 30-path wake-up is microseconds.
    // ---------------------------------------------------------------------

    public async Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoForPathsAsync(
        int backupSetId, IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dict = new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return dict;

        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.SourcePath, f.Version, f.SizeBytes, f.SourceLastWriteUtc,
                   f.IsDeduped, f.IsFileRef, f.Hash
            FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.IsDeleted = 0
              AND f.SourcePath = $path COLLATE NOCASE
            ORDER BY f.Version DESC, f.Id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        var pathParam = cmd.Parameters.Add("$path", SqliteType.Text);

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (dict.ContainsKey(path))
                continue;
            pathParam.Value = path;
            using var r = cmd.ExecuteReader();
            if (r.Read())
                dict[r.GetString(0)] = ReadVersionInfo(r);
        }
        return dict;
    }

    public async Task<(FileVersionInfo Version, bool IsDeleted, int HighestVersion)?> GetLastVersionAsync(
        int backupSetId, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        // Live records sort first (IsDeleted = 0), so this is the newest live
        // record when the path has one and otherwise its newest deleted one. The
        // highest number is taken over every record, deleted ones included: a
        // tombstoned newer record still owns its number.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.SourcePath, f.Version, f.SizeBytes, f.SourceLastWriteUtc,
                   f.IsDeduped, f.IsFileRef, f.Hash, f.IsDeleted,
                   (SELECT MAX(f2.Version) FROM Files f2
                    INNER JOIN Discs d2 ON f2.DiscId = d2.Id
                    WHERE d2.BackupSetId = $setId
                      AND f2.SourcePath = $path COLLATE NOCASE)
            FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.SourcePath = $path COLLATE NOCASE
            ORDER BY f.IsDeleted, f.Version DESC, f.Id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$path", path);

        using var r = cmd.ExecuteReader();
        return r.Read() ? (ReadVersionInfo(r), r.GetInt32(7) != 0, r.GetInt32(8)) : null;
    }

    private static FileVersionInfo ReadVersionInfo(SqliteDataReader r) => new(
        MaxVersion: r.GetInt32(1),
        SizeBytes: r.GetInt64(2),
        SourceLastWriteUtc: DateTime.Parse(r.GetString(3), null, DateTimeStyles.RoundtripKind),
        IsDeduped: r.GetInt32(4) != 0,
        IsFileRef: r.GetInt32(5) != 0,
        Hash: r.IsDBNull(6) ? "" : r.GetString(6));

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
            ORDER BY f.IsDeleted, f.Id DESC
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
        foreach (var r in Rows(cmd, ct))
            list.Add(ReadFile(r));
        return list;
    }

    public async Task<IReadOnlyList<FileRecord>> GetFileRecordsUnderDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        // Everything under the path as a range, and the path itself (a toggled
        // file), each a search of IX_Files_SourcePath_NoCase - Windows paths
        // ignore case. Shaped for the planner, which otherwise reads every file
        // of the set: two queries, not one with an OR, which it scans; an EXISTS
        // for the set, not a join, which it walks by disc; and no ORDER BY,
        // which it serves by walking the whole BINARY path index
        // (path_range_test checks the cost).
        string prefix = directoryPrefix.TrimEnd('\\') + "\\";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f INDEXED BY IX_Files_SourcePath_NoCase
            WHERE f.SourcePath >= $prefix COLLATE NOCASE AND f.SourcePath < $upper COLLATE NOCASE
              AND EXISTS (SELECT 1 FROM Discs d WHERE d.Id = f.DiscId AND d.BackupSetId = $setId)
            UNION ALL
            SELECT f.* FROM Files f INDEXED BY IX_Files_SourcePath_NoCase
            WHERE f.SourcePath = $exact COLLATE NOCASE
              AND EXISTS (SELECT 1 FROM Discs d WHERE d.Id = f.DiscId AND d.BackupSetId = $setId)
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$upper", PathRangeEnd(prefix));
        cmd.Parameters.AddWithValue("$exact", directoryPrefix);

        var list = new List<FileRecord>();
        foreach (var r in Rows(cmd, ct))
            list.Add(ReadFile(r));
        return list;
    }

    // ---------------------------------------------------------------
    // Lazy restore browser
    // ---------------------------------------------------------------

    /// <summary>
    /// The exclusive upper bound of "every path under <paramref name="prefix"/>",
    /// a folder's path ending in its separator: the prefix with that '\' raised
    /// to ']', the next character in NOCASE order (which folds ASCII letters
    /// only) as in binary. So 'P\' &lt;= path &lt; 'P]' holds exactly the paths
    /// under P, whatever characters follow - no sentinel above "every character"
    /// is needed, and none exists that a file name cannot hold. The empty prefix
    /// (the roots) is bounded above every first character a source path has: a
    /// drive letter, or a UNC path's '\'.
    /// </summary>
    private static string PathRangeEnd(string prefix)
    {
        if (prefix.Length == 0)
            return "\u0080";
        System.Diagnostics.Debug.Assert(prefix[^1] == '\\', "a range's prefix ends in its separator");
        return prefix[..^1] + "]";
    }

    /// <summary>
    /// The least string above <paramref name="path"/> that is not a version of
    /// it: no source path holds a control character, so nothing lies between.
    /// </summary>
    private static string JustAfter(string path) => path + "\u0001";

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

        var dirs = new List<RestoreTreeChild>();
        var files = new List<RestoreTreeChild>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT SourcePath, SizeBytes, BackedUpUtc
            FROM Files INDEXED BY IX_Files_Active_SourcePath_NoCase
            WHERE IsDeleted = 0
              AND SourcePath >= $cursor COLLATE NOCASE
              AND SourcePath < $upper COLLATE NOCASE
            ORDER BY SourcePath COLLATE NOCASE, Version DESC
            LIMIT 1
            """;
        // The next child is the first path at or above the cursor; after each,
        // the cursor moves just past it (a file, all its versions) or past the
        // whole subtree under it (a folder).
        string cursor = prefix;
        var pCursor = cmd.Parameters.AddWithValue("$cursor", cursor);
        cmd.Parameters.AddWithValue("$upper", PathRangeEnd(prefix));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pCursor.Value = cursor;
            string before = cursor;

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
                cursor = JustAfter(sp);
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
                    cursor = PathRangeEnd(share + "\\");
                }
                else
                {
                    cursor = JustAfter(sp);
                }
            }
            else
            {
                string name = rest.Substring(0, sep);
                string full = prefix + name;
                dirs.Add(new RestoreTreeChild(name, full, true, 0, null));
                cursor = PathRangeEnd(full + "\\");
            }

            // The listing ends only by running out of rows, so a cursor that did
            // not move would list the same child forever.
            if (cursor == before)
                throw new InvalidOperationException($"The restore listing of '{prefix}' stopped advancing at '{sp}'.");
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
        string upper = PathRangeEnd(prefix);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(SizeBytes), 0)
            FROM (
                SELECT SizeBytes,
                       ROW_NUMBER() OVER (
                           PARTITION BY SourcePath COLLATE NOCASE
                           ORDER BY Version DESC, Id DESC
                       ) AS rn
                FROM Files INDEXED BY IX_Files_Active_SourcePath_NoCase
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
        string upper = PathRangeEnd(prefix);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM (
                SELECT f.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY f.SourcePath COLLATE NOCASE
                           ORDER BY f.Version DESC, f.Id DESC
                       ) AS rn
                FROM Files f INDEXED BY IX_Files_Active_SourcePath_NoCase
                WHERE f.IsDeleted = 0
                  AND f.SourcePath > $low COLLATE NOCASE
                  AND f.SourcePath < $upper COLLATE NOCASE
            )
            WHERE rn = 1
            """;
        cmd.Parameters.AddWithValue("$low", prefix);
        cmd.Parameters.AddWithValue("$upper", upper);

        var list = new List<FileRecord>();
        foreach (var r in Rows(cmd, ct))
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
        foreach (var r in Rows(cmd, ct))
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
        foreach (var r in Rows(cmd, ct))
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    /// <summary>
    /// Single-hash variant of <see cref="GetActivePlainContentPathsAsync"/>.
    /// Returns the destination-relative path of an active PLAIN copy of the
    /// given content hash, or null when no such copy exists.
    /// </summary>
    /// <remarks>
    /// The whole-set version GROUP BYs every active plain row in the set — a
    /// full scan of a multi-million-row table on every backup run. A directory
    /// backup only asks about the hashes of the files it is actually writing,
    /// so it looks them up one at a time through IX_DeduplicationBlocks-style
    /// seeks on IX_Files_Active_Hash instead. MIN(DiscPath) matches the
    /// whole-set query's choice of representative copy exactly.
    /// </remarks>
    public async Task<string?> GetActivePlainContentPathByHashAsync(
        int backupSetId, string hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(hash))
            return null;

        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(f.DiscPath) FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE f.Hash = $hash
              AND d.BackupSetId = $setId
              AND f.IsDeleted = 0
              AND f.IsFileRef = 0
              AND f.IsDeduped = 0
              AND f.IsSplit = 0
              AND f.IsZipped = 0
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$hash", hash);

        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
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
        foreach (var r in Rows(cmd, ct))
            sizes.Add(r.GetInt64(0));
        return sizes;
    }

    /// <summary>
    /// Probe just the sizes the caller cares about. See
    /// <see cref="ICatalogRepository.GetActivePlainSizesPresentAsync"/> for why the
    /// whole-set form above is unusable on the backup hot path.
    /// </summary>
    public async Task<HashSet<long>> GetActivePlainSizesPresentAsync(
        int backupSetId, IReadOnlyCollection<long> sizes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var present = new HashSet<long>();
        if (sizes.Count == 0)
            return present;

        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE f.SizeBytes = $size
              AND d.BackupSetId = $setId
              AND f.IsDeleted = 0
              AND f.IsFileRef = 0
              AND f.IsDeduped = 0
              AND f.IsSplit = 0
              AND f.IsZipped = 0
              AND f.Hash <> ''
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        var sizeParam = cmd.Parameters.Add("$size", SqliteType.Integer);

        foreach (long size in sizes)
        {
            ct.ThrowIfCancellationRequested();
            if (present.Contains(size))
                continue;

            sizeParam.Value = size;
            if (cmd.ExecuteScalar() is not null)
                present.Add(size);
        }

        return present;
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
        foreach (var r in Rows(cmd, ct))
            list.Add(ReadFile(r));
        return list;
    }

    /// <summary>See <see cref="ICatalogRepository.GetUnhashedPossibleAnchorsAsync"/>.</summary>
    public async Task<IReadOnlyList<(long Id, string DiscPath)>> GetUnhashedPossibleAnchorsAsync(
        IReadOnlyCollection<long>? among, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (among is { Count: 0 })
            return [];
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.Id, p.DiscPath FROM Files p
            WHERE p.IsDeleted = 0 AND p.Hash = ''
              AND p.IsFileRef = 0 AND p.IsDeduped = 0 AND p.IsSplit = 0 AND p.IsZipped = 0
              AND p.SizeBytes IN (SELECT r.SizeBytes FROM Files r WHERE r.IsDeleted = 0 AND r.IsFileRef = 1)
            """ + (among is null ? "" : " AND p.Id IN (SELECT value FROM json_each($ids))");
        if (among is not null)
            cmd.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(among));
        var list = new List<(long, string)>();
        foreach (var r in Rows(cmd, ct))
            list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>See <see cref="ICatalogRepository.SetHashIfMissingAsync"/>.</summary>
    public async Task<bool> SetHashIfMissingAsync(long fileId, string hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Files SET Hash = $hash WHERE Id = $id AND Hash = ''";
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$hash", hash);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>See <see cref="ICatalogRepository.GetNeededContentCopiesAsync"/>.</summary>
    public async Task<HashSet<long>> GetNeededContentCopiesAsync(
        int backupSetId, IReadOnlyCollection<long> candidateIds,
        IReadOnlyCollection<long>? keepableIds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (candidateIds.Count == 0)
            return [];
        using var _ = await LockAsync(ct).ConfigureAwait(false);
        return NeededContentCopies(backupSetId, candidateIds, keepableIds, ct);
    }

    /// <summary>See <see cref="ICatalogRepository.RetireRecordsKeepingNeededCopiesAsync"/>.</summary>
    public async Task<HashSet<long>> RetireRecordsKeepingNeededCopiesAsync(
        int backupSetId, IReadOnlyCollection<long> candidateIds,
        IReadOnlyCollection<long>? keepableIds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (candidateIds.Count == 0)
            return [];
        // One write lock for both: nothing can add or retire a reference between
        // deciding what is needed and retiring the rest.
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);
        var kept = NeededContentCopies(backupSetId, candidateIds, keepableIds, ct);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Files SET IsDeleted = 1 WHERE IsDeleted = 0 AND Id IN (SELECT value FROM json_each($ids))";
        cmd.Parameters.AddWithValue("$ids",
            System.Text.Json.JsonSerializer.Serialize(candidateIds.Where(id => !kept.Contains(id)).ToList()));
        cmd.ExecuteNonQuery();
        return kept;
    }

    /// <summary>
    /// Which of the candidates are the last plain copy of content a live
    /// <c>.fileref</c> still needs, with the candidates gone; the caller holds the
    /// gate. A cancel stops the search; the temp-table writes around it are not
    /// watched, as this also runs inside a transaction (see <see cref="Rows"/>).
    /// </summary>
    private HashSet<long> NeededContentCopies(
        int backupSetId, IReadOnlyCollection<long> candidateIds, IReadOnlyCollection<long>? keepableIds,
        CancellationToken ct)
    {
        var keep = new HashSet<long>();

        // The candidates go into a TEMP table: connection-local, in the temp
        // database, so filling it takes no write lock on the set and waits on no
        // other process. Tens of thousands of ids are normal (every earlier
        // version of every file in a tier set that keeps none).
        Execute("CREATE TEMP TABLE IF NOT EXISTS DeleteCandidates (Id INTEGER PRIMARY KEY)");
        Execute("CREATE TEMP TABLE IF NOT EXISTS KeepableCandidates (Id INTEGER PRIMARY KEY)");
        Execute("DELETE FROM temp.DeleteCandidates");
        Execute("DELETE FROM temp.KeepableCandidates");
        try
        {
            // One statement, not a loop in a transaction of our own: this
            // connection may already be inside one (a backup in this process),
            // and SQLite transactions do not nest.
            using (var ins = _connection.CreateCommand())
            {
                ins.CommandText = """
                    INSERT OR IGNORE INTO temp.DeleteCandidates (Id)
                    SELECT value FROM json_each($ids)
                    """;
                ins.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(candidateIds));
                ins.ExecuteNonQuery();

                ins.CommandText = """
                    INSERT OR IGNORE INTO temp.KeepableCandidates (Id)
                    SELECT value FROM json_each($ids)
                    """;
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("$ids",
                    System.Text.Json.JsonSerializer.Serialize(keepableIds ?? candidateIds));
                ins.ExecuteNonQuery();
            }

            // "Plain" means what can anchor a .fileref: real bytes under the
            // file's own name - the same definition as the retention guard and
            // GetActiveRecordsByHashAsync (split and zipped copies are outside
            // the file-level dedup pool). A surviving copy (q) must also be the
            // newest live row of its file at its stored path, and not the
            // candidate's own path: an older row there is stale - its bytes were
            // overwritten in place, as copies that keep no history were before
            // their replaced rows were retired - and the candidate's path is the
            // candidate's file. Hash = is a BINARY compare on purpose:
            // every writer stores lower-case hex, and it lets IX_Files_Active_Hash
            // serve both EXISTS probes.
            // CROSS JOIN pins the candidates as the outer loop: with no
            // statistics SQLite rates both tables alike, and driving from Files
            // in hash order would walk every live row to check a handful.
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT MAX(p.Id)
                FROM temp.KeepableCandidates c
                CROSS JOIN Files p ON c.Id = p.Id
                WHERE p.IsDeleted = 0 AND p.Hash <> ''
                  AND p.IsFileRef = 0 AND p.IsDeduped = 0 AND p.IsSplit = 0 AND p.IsZipped = 0
                  AND EXISTS (SELECT 1 FROM Discs d WHERE d.Id = p.DiscId AND d.BackupSetId = $setId)
                  AND EXISTS (
                      SELECT 1 FROM Files r
                      WHERE r.Hash = p.Hash AND r.IsDeleted = 0 AND r.IsFileRef = 1
                        AND r.Id NOT IN (SELECT Id FROM temp.DeleteCandidates))
                  AND NOT EXISTS (
                      SELECT 1 FROM Files q
                      WHERE q.Hash = p.Hash AND q.IsDeleted = 0
                        AND q.IsFileRef = 0 AND q.IsDeduped = 0 AND q.IsSplit = 0 AND q.IsZipped = 0
                        AND q.DiscPath <> p.DiscPath
                        AND q.Id NOT IN (SELECT Id FROM temp.DeleteCandidates)
                        AND NOT EXISTS (
                            SELECT 1 FROM Files n
                            WHERE n.SourcePath = q.SourcePath COLLATE NOCASE AND n.IsDeleted = 0
                              AND n.DiscPath = q.DiscPath AND n.Id > q.Id))
                GROUP BY p.Hash
                """;
            cmd.Parameters.AddWithValue("$setId", backupSetId);
            foreach (var r in Rows(cmd, ct))
                keep.Add(r.GetInt64(0));
        }
        finally
        {
            Execute("DELETE FROM temp.DeleteCandidates");
            Execute("DELETE FROM temp.KeepableCandidates");
        }
        return keep;
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
              AND SourcePath = $path COLLATE NOCASE
              AND EXISTS (SELECT 1 FROM Discs d
                          WHERE d.Id = Files.DiscId AND d.BackupSetId = $setId)
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

    /// <summary>
    /// Tombstone rows by id, touching only <c>IsDeleted</c>.
    ///
    /// <para>No Discs ownership predicate here, and deliberately: <c>Files.Id</c> is
    /// this database's own primary key, so "belongs to this set" is established by
    /// which database the call was routed to, not by anything the statement could
    /// check. (Ids are per-database AUTOINCREMENT, so an id from another set is not
    /// a foreign row here — it is a different, valid row. The guard against that is
    /// the caller passing its own set's ids, which is why this takes the set id at
    /// all.)</para>
    /// </summary>
    public async Task<int> MarkFileRecordsDeletedByIdsAsync(
        int backupSetId, IReadOnlyList<long> fileIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (fileIds.Count == 0)
            return 0;

        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

        // Chunked to stay clear of SQLite's bound-variable ceiling.
        const int ChunkSize = 500;
        int total = 0;

        for (int offset = 0; offset < fileIds.Count; offset += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            int len = Math.Min(ChunkSize, fileIds.Count - offset);
            using var cmd = _connection.CreateCommand();

            var names = new string[len];
            for (int i = 0; i < len; i++)
            {
                names[i] = "$id" + i;
                cmd.Parameters.AddWithValue(names[i], fileIds[offset + i]);
            }

            cmd.CommandText =
                "UPDATE Files SET IsDeleted = 1 WHERE IsDeleted = 0 AND Id IN ("
                + string.Join(",", names) + ")";

            total += cmd.ExecuteNonQuery();
        }

        return total;
    }

    /// <summary>
    /// Turn a live <c>.fileref</c> row into a plain one at
    /// <paramref name="newDiscPath"/> - only if it is still the live .fileref at
    /// <paramref name="expectedDiscPath"/> that the caller read. Two columns, not
    /// the whole row: reconcile acts on records read when it analyzed, and
    /// writing those back whole undid whatever happened since - a cleanup's
    /// retirement of the row, a backup moving it into the history.
    /// </summary>
    /// <returns>Whether the row was changed.</returns>
    public async Task<bool> FlipFileRefToPlainIfUnchangedAsync(
        long fileId, string expectedDiscPath, string newDiscPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET IsFileRef = 0, DiscPath = $new
            WHERE Id = $id AND IsDeleted = 0 AND IsFileRef = 1 AND DiscPath = $old
            """;
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$old", expectedDiscPath);
        cmd.Parameters.AddWithValue("$new", newDiscPath);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Retire a live row, only if it still stores its content at
    /// <paramref name="expectedDiscPath"/>, as when the caller read it. See
    /// <see cref="FlipFileRefToPlainIfUnchangedAsync"/>.
    /// </summary>
    /// <returns>Whether the row was changed.</returns>
    public async Task<bool> MarkFileRecordDeletedIfUnchangedAsync(
        long fileId, string expectedDiscPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Files SET IsDeleted = 1 WHERE Id = $id AND IsDeleted = 0 AND DiscPath = $old";
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$old", expectedDiscPath);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>See <see cref="ICatalogRepository.MarkFileRecordsLiveByIdsAsync"/>.</summary>
    public async Task<int> MarkFileRecordsLiveByIdsAsync(IReadOnlyCollection<long> fileIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (fileIds.Count == 0)
            return 0;
        using var _ = await WriteLockAsync(ct).ConfigureAwait(false);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Files SET IsDeleted = 0 WHERE IsDeleted = 1 AND Id IN (SELECT value FROM json_each($ids))";
        cmd.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(fileIds));
        return cmd.ExecuteNonQuery();
    }

    public async Task<int> CountFilesUnderSourcePrefixAsync(
        int backupSetId, string sourcePrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        var prefix = sourcePrefix.TrimEnd('\\') + "\\";

        // A range on IX_Files_SourcePath_NoCase, like the other reads of a
        // folder: no LIKE pattern, so nothing in the path to escape.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM Files INDEXED BY IX_Files_SourcePath_NoCase
            WHERE SourcePath >= $prefix COLLATE NOCASE AND SourcePath < $upper COLLATE NOCASE
              AND EXISTS (SELECT 1 FROM Discs d
                          WHERE d.Id = Files.DiscId AND d.BackupSetId = $setId)
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$upper", PathRangeEnd(prefix));
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
        // SUBSTR is 1-based and counts characters as length() does - not as
        // .NET's Length, which counts a character beyond U+FFFF twice. The paths
        // are found by a range on IX_Files_SourcePath_NoCase, as for the reads
        // of a folder.
        cmd.CommandText = """
            UPDATE Files INDEXED BY IX_Files_SourcePath_NoCase
            SET SourcePath = $new || SUBSTR(SourcePath, length($prefix) + 1)
            WHERE SourcePath >= $prefix COLLATE NOCASE AND SourcePath < $upper COLLATE NOCASE
              AND EXISTS (SELECT 1 FROM Discs d
                          WHERE d.Id = Files.DiscId AND d.BackupSetId = $setId)
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$new", newP);
        cmd.Parameters.AddWithValue("$prefix", oldP);
        cmd.Parameters.AddWithValue("$upper", PathRangeEnd(oldP));
        return cmd.ExecuteNonQuery();
    }

    private static FileChunk ReadChunk(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        FileRecordId = r.GetInt64(r.GetOrdinal("FileRecordId")),
        DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
        Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
        Offset = r.GetInt64(r.GetOrdinal("Offset")),
        Length = r.GetInt64(r.GetOrdinal("Length")),
        DiscFilename = r.GetString(r.GetOrdinal("DiscFilename")),
    };

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
        foreach (var r in Rows(cmd, ct))
            list.Add(ReadChunk(r));
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
    // Catalog clearing
    // ---------------------------------------------------------------

    /// <summary>
    /// Source and stored paths of every record written at or after
    /// <paramref name="sinceUtc"/>. BackedUpUtc is stored as round-trip ("o")
    /// UTC text, which sorts as it reads, so a text comparison is a time
    /// comparison. A full scan, but of one narrow column, once per purge.
    /// </summary>
    public async Task<IReadOnlyList<(string SourcePath, string DiscPath)>> GetRecordsWrittenSinceAsync(
        DateTime sinceUtc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT SourcePath, DiscPath FROM Files WHERE BackedUpUtc >= $since";
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToUniversalTime().ToString("o"));
        var list = new List<(string, string)>();
        foreach (var r in Rows(cmd, ct))
            list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// Which of <paramref name="discPaths"/> a live record still stores its
    /// content at. Paths compare without regard to case or slash direction; the
    /// result holds them as given. One pass over the live rows' DiscPath column,
    /// checked against the set in memory: DiscPath has no index, so asking
    /// about the paths in batches would scan the table once per batch.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindDiscPathsInUseAsync(
        IReadOnlyCollection<string> discPaths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (discPaths.Count == 0)
            return [];

        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in discPaths)
            wanted.TryAdd(p.Replace('/', '\\'), p);

        using var _ = await LockAsync(ct).ConfigureAwait(false);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DiscPath FROM Files WHERE IsDeleted = 0";
        var found = new List<string>();
        foreach (var r in Rows(cmd, ct))
        {
            if (wanted.Remove(r.GetString(0).Replace('/', '\\'), out var given))
                found.Add(given);
        }
        return found;
    }

    /// <summary>Whether this set holds any file record, deleted ones included.</summary>
    public async Task<bool> HasFileRecordsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var _ = await LockAsync(ct).ConfigureAwait(false);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM Files)";
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>
    /// Copy every disc, file, chunk and dedup-block row out of an exported history
    /// database into this one, in one transaction. This database must be empty:
    /// file, chunk and block ids are kept as they are (so FileChunks.FileRecordId
    /// stays valid without remapping), which only an empty table can guarantee.
    /// Disc ids are global and come from <paramref name="discIdMap"/> (old → new),
    /// allocated by the caller from the master catalog.
    /// </summary>
    /// <returns>The number of file records copied.</returns>
    /// <param name="rowsImported">
    /// Records copied so far, of all the snapshot's file records, file chunks and
    /// dedup blocks, after each batch.
    /// </param>
    public async Task<int> ImportHistoryFromAsync(
        string sourceDatabasePath, IReadOnlyDictionary<long, int> discIdMap,
        IProgress<(long Done, long Total)>? rowsImported = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // A write like any other: cross-process lock, then the gate. Not through
        // BeginTransactionAsync, because ATTACH and DETACH must run outside a
        // transaction on this same connection.
        var fileLock = await AcquireCrossProcessWriteAsync(ct).ConfigureAwait(false);
        try
        {
            await EnterGateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }

        try
        {
            using (var check = _connection.CreateCommand())
            {
                check.CommandText = "SELECT EXISTS (SELECT 1 FROM Files) OR EXISTS (SELECT 1 FROM Discs)";
                if (Convert.ToInt64(check.ExecuteScalar()) != 0)
                    throw new InvalidOperationException(
                        "This backup set already has backup history; history can only be imported into an empty set.");
            }

            using (var attach = _connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS src";
                attach.Parameters.AddWithValue("$path", sourceDatabasePath);
                attach.ExecuteNonQuery();
            }

            try
            {
                using var tx = _connection.BeginTransaction();

                Execute("CREATE TEMP TABLE DiscIdMap (OldId INTEGER PRIMARY KEY, NewId INTEGER NOT NULL)");
                using (var ins = _connection.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO temp.DiscIdMap (OldId, NewId) VALUES ($old, $new)";
                    var pOld = ins.Parameters.Add("$old", SqliteType.Integer);
                    var pNew = ins.Parameters.Add("$new", SqliteType.Integer);
                    foreach (var (oldId, newId) in discIdMap)
                    {
                        pOld.Value = oldId;
                        pNew.Value = newId;
                        ins.ExecuteNonQuery();
                    }
                }

                // Explicit column lists on both sides, here and below, so an export
                // from a build whose tables carry extra columns still imports.
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO main.Discs
                            (Id, BackupSetId, Label, SequenceNumber, MediaType, FilesystemType,
                             Capacity, BytesUsed, RewriteCount, IsMultisession, IsBad, Status,
                             CreatedUtc, LastWrittenUtc)
                        SELECT m.NewId, $setId, d.Label, d.SequenceNumber, d.MediaType, d.FilesystemType,
                               d.Capacity, d.BytesUsed, d.RewriteCount, d.IsMultisession, d.IsBad, d.Status,
                               d.CreatedUtc, d.LastWrittenUtc
                        FROM src.Discs d
                        INNER JOIN temp.DiscIdMap m ON m.OldId = d.Id
                        """;
                    cmd.Parameters.AddWithValue("$setId", SetId);
                    cmd.ExecuteNonQuery();
                }

                // The bulk of a history, copied a range of source row ids at a time
                // so the count can be reported and a cancel lands between batches.
                // All inside the one transaction: a cancel or failure rolls back
                // everything, and the import stays all or nothing.
                // Each table's id range and row count, read once: the counts make
                // the total, the ranges drive the batches.
                var ranges = new Dictionary<string, (long First, long Last, long Rows)>();
                foreach (string table in new[] { "src.Files", "src.FileChunks", "src.DeduplicationBlocks" })
                {
                    using var range = _connection.CreateCommand();
                    range.CommandText = $"SELECT MIN(Id), MAX(Id), COUNT(*) FROM {table}";
                    using var r = range.ExecuteReader();
                    r.Read();
                    ranges[table] = r.IsDBNull(0) ? (0, -1, 0) : (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
                }
                long total = ranges.Values.Sum(t => t.Rows), done = 0;
                rowsImported?.Report((0, total));

                int files = (int)CopyInBatches("src.Files", """
                    INSERT INTO main.Files
                        (Id, DiscId, SourcePath, DiscPath, SizeBytes, Hash,
                         IsZipped, IsSplit, IsDeduped, IsFileRef, Version, IsDeleted,
                         SourceLastWriteUtc, BackedUpUtc)
                    SELECT f.Id, m.NewId, f.SourcePath, f.DiscPath, f.SizeBytes, f.Hash,
                           f.IsZipped, f.IsSplit, f.IsDeduped, f.IsFileRef, f.Version, f.IsDeleted,
                           f.SourceLastWriteUtc, f.BackedUpUtc
                    FROM src.Files f
                    INNER JOIN temp.DiscIdMap m ON m.OldId = f.DiscId
                    WHERE f.Id > $after AND f.Id <= $upTo
                    """);

                CopyInBatches("src.FileChunks", """
                    INSERT INTO main.FileChunks
                        (Id, FileRecordId, DiscId, Sequence, Offset, Length, DiscFilename)
                    SELECT c.Id, c.FileRecordId, m.NewId, c.Sequence, c.Offset, c.Length, c.DiscFilename
                    FROM src.FileChunks c
                    INNER JOIN temp.DiscIdMap m ON m.OldId = c.DiscId
                    WHERE c.FileRecordId IN (SELECT Id FROM main.Files)
                      AND c.Id > $after AND c.Id <= $upTo
                    """);

                // Directory backups register blocks with DiscId 0 (no disc), which
                // has no mapping and stays 0.
                CopyInBatches("src.DeduplicationBlocks", """
                    INSERT INTO main.DeduplicationBlocks (Id, Hash, SizeBytes, ReferenceCount, DiscId)
                    SELECT b.Id, b.Hash, b.SizeBytes, b.ReferenceCount, COALESCE(m.NewId, b.DiscId)
                    FROM src.DeduplicationBlocks b
                    LEFT JOIN temp.DiscIdMap m ON m.OldId = b.DiscId
                    WHERE b.Id > $after AND b.Id <= $upTo
                    """);

                // Run an INSERT ... SELECT over sourceTable's row ids a range at a
                // time ($after, $upTo); returns the rows inserted.
                long CopyInBatches(string sourceTable, string insert)
                {
                    const long IdsPerBatch = 50_000;
                    var (first, last, rowsInTable) = ranges[sourceTable];
                    if (rowsInTable == 0)
                        return 0;

                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = insert;
                    var after = cmd.Parameters.Add("$after", SqliteType.Integer);
                    var upTo = cmd.Parameters.Add("$upTo", SqliteType.Integer);
                    long inserted = 0, doneBefore = done;
                    for (long from = first - 1; from < last; from += IdsPerBatch)
                    {
                        ct.ThrowIfCancellationRequested();
                        after.Value = from;
                        upTo.Value = from + IdsPerBatch;
                        inserted += cmd.ExecuteNonQuery();
                        done = doneBefore + Math.Min(inserted, rowsInTable);
                        rowsImported?.Report((done, total));
                    }
                    // Rows the joins left out still count as dealt with.
                    done = doneBefore + rowsInTable;
                    rowsImported?.Report((done, total));
                    return inserted;
                }

                Execute("DROP TABLE temp.DiscIdMap");
                tx.Commit();
                return files;
            }
            finally
            {
                // A failed transaction has rolled back by now (the using above),
                // so DETACH is allowed.
                try { Execute("DETACH DATABASE src"); }
                catch { /* the connection is still usable; the file is a temp copy */ }
            }
        }
        finally
        {
            _gate.Release();
            fileLock.Dispose();
        }
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
            foreach (var r in Rows(cmd, ct))
                discs.Add(ReadDisc(r));
        }

        var files = new List<FileRecord>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM Files ORDER BY Id";
            foreach (var r in Rows(cmd, ct))
                files.Add(ReadFile(r));
        }

        var chunks = new List<FileChunk>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM FileChunks ORDER BY Id";
            foreach (var r in Rows(cmd, ct))
                chunks.Add(ReadChunk(r));
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
