using System.IO;
using Microsoft.Data.Sqlite;

namespace LithicBackup.ViewModels;

/// <summary>
/// Persistent cache of per-directory file sizes. Each entry stores:
///
/// <list type="bullet">
/// <item><b>Direct sizes</b>: total size and count of files directly in the
/// directory (not recursive), validated by the directory's
/// <see cref="DirectoryInfo.LastWriteTimeUtc"/>.</item>
/// <item><b>Recursive totals</b>: the last-computed recursive size and file
/// count for the entire subtree. These are used as instant initial values
/// when displaying directories that haven't changed, avoiding the need to
/// walk the entire subtree on each load. They are re-validated by the
/// background scheduler and updated whenever a full computation runs.</item>
/// <item><b>Filtered recursive totals</b>: the last-computed recursive size
/// and file count for the subtree with an exclusion filter applied. Tagged
/// with a filter signature so the cached value is reused only when the same
/// filter is active; otherwise the filtered size is recomputed.</item>
/// </list>
///
/// <para><b>Storage: on-demand SQLite point lookups behind a bounded in-memory
/// cache.</b> This class used to read the whole table into a
/// <see cref="Dictionary{TKey,TValue}"/> at construction. That is fine for a
/// small cache and ruinous for a real one: the author's database had grown to
/// 1,493,915 rows / 538 MB, which took <b>~6 seconds</b> to load (a
/// <c>DateTime.Parse</c> per row) and held a few hundred MB of managed memory
/// for the lifetime of the process. Worse, every lookup takes the same lock, so
/// the first directory expansion after a backup-set editor opened blocked on the
/// whole load — measured 6.6 s to expand <c>D:\</c> with "show sizes" on versus
/// 39 ms with it off (tools\expand_probe). Loading lazily on a background thread,
/// as the previous version did, moved the stall off the constructor but not off
/// the first lookup, which is the one the user is actually waiting for.
///
/// <para>Now nothing is loaded up front. <c>Path</c> is the table's primary key,
/// so a single-row lookup is an index seek costing microseconds; results (and
/// misses) are memoised in <see cref="_hot"/> so a recursive scan pays the DB
/// once per directory. Writes are buffered in <see cref="_dirty"/> and flushed
/// in batches, exactly as before.</para>
/// </summary>
public sealed class DirectorySizeCache : IDisposable
{
    /// <summary>Flush pending writes once this many are buffered.</summary>
    private const int DirtyFlushThreshold = 500;

    /// <summary>
    /// Cap on memoised rows. A full scan of a large volume touches hundreds of
    /// thousands of directories, and an unbounded memo would recreate exactly the
    /// footprint this class exists to avoid. On overflow the memo is dropped
    /// wholesale (pending writes are kept — they live in <see cref="_dirty"/>);
    /// the cost of being wrong is one extra index seek per re-read.
    /// </summary>
    private const int HotLimit = 100_000;

    /// <summary>
    /// Size above which the oldest entries are pruned, measured in <em>used</em>
    /// bytes. Set well above a plausible working set (every directory on this
    /// machine is ~1.5M rows / ~510 MB) so pruning only ever fires on genuinely
    /// pathological growth: an evicted row costs a full recursive rescan of that
    /// directory, which is far more expensive than the disk space it frees.
    ///
    /// <para>This gate used to be a row count, which cost a
    /// <c>SELECT COUNT(*)</c> on every open. That is a full covering-index scan
    /// — measured at <b>580 ms</b> on the 538 MB database, i.e. reading most of
    /// the file, once per launch, purely to discover there was nothing to do.
    /// Bytes are what "pathological growth" actually means anyway, and
    /// <c>page_count</c>/<c>freelist_count</c> are header fields, so the common
    /// (nothing-to-prune) case is now free.</para>
    ///
    /// <para><c>max(rowid)</c> would also have been O(1), but is useless as a
    /// proxy: <c>INSERT OR REPLACE</c> assigns a fresh rowid on every rewrite,
    /// so this database's max rowid is 28.2M against 1.49M live rows.</para>
    /// </summary>
    private const long MaxUsedBytes = 1_073_741_824;      // 1 GiB

    /// <summary>Prune down to this before stopping. The gap from
    /// <see cref="MaxUsedBytes"/> keeps a pruning run from being re-triggered by
    /// the next launch.</summary>
    private const long PruneTargetBytes = 750_000_000;    // ~715 MiB

    private const int PruneChunkRows = 20_000;

    private readonly object _lock = new();

    /// <summary>
    /// Memo of rows read from (or written to) the database this session.
    /// A null value is a remembered <em>miss</em>, which matters: a cold scan
    /// asks about directories that have never been cached, and without negative
    /// caching each of those would re-query SQLite on every pass.
    /// </summary>
    private readonly Dictionary<string, CacheEntry?> _hot;

    /// <summary>Rows written but not yet persisted. Separate from <see cref="_hot"/>
    /// so dropping the memo can never lose a write.</summary>
    private readonly Dictionary<string, CacheEntry> _dirty;

    private readonly string _dbPath;

    private SqliteConnection? _conn;
    private SqliteCommand? _selectCmd;
    private SqliteParameter? _selectPath;
    private bool _openFailed;
    private bool _pruneStarted;

    /// <summary>
    /// Bumped by <see cref="CacheMaintenance"/> when it has changed the database
    /// underneath us. There is no registry of live instances to notify because
    /// there is no single owner: <see cref="SizeComputeScheduler"/> constructs
    /// its own, and one exists per open backup-set editor. A counter every
    /// instance checks under its own lock covers all of them without anyone
    /// having to hold a reference.
    /// </summary>
    private static long s_generation;
    private static long s_dropWritesGeneration;
    private long _seenGeneration;
    private long _seenDropWrites;

    /// <summary>
    /// Tell every live instance that the database has changed beneath it, so it
    /// drops its memo and re-opens. Called after a compaction (which can delete
    /// rows and replace the table) or a clear.
    /// </summary>
    /// <param name="dropPendingWrites">Also discard writes queued but not yet
    /// flushed. Right for a clear — otherwise a queued row lands moments after
    /// the user asked for an empty cache — and wrong for a compaction, where
    /// those writes are live data that simply has not been persisted yet.</param>
    internal static void InvalidateLiveInstances(bool dropPendingWrites)
    {
        if (dropPendingWrites)
            Interlocked.Increment(ref s_dropWritesGeneration);
        Interlocked.Increment(ref s_generation);
    }

    public DirectorySizeCache()
    {
        _dbPath = CacheMaintenance.PathFor("sizecache.db");

        // Ordinal, not OrdinalIgnoreCase, and deliberately so: the memo has to
        // agree with the database, whose primary key uses SQLite's default
        // (binary) collation. A case-insensitive memo in front of a
        // case-sensitive table would remember a miss under one casing and serve
        // it under another, turning a harmless recompute into a wrong answer
        // that persists for the session. Every key here comes from
        // DirectoryInfo.FullName, so the filesystem's own canonical casing keeps
        // them consistent; a stray case variant costs one recomputation.
        _hot = new Dictionary<string, CacheEntry?>(StringComparer.Ordinal);
        _dirty = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        // Opening the connection (schema check, WAL setup, statement prepare)
        // costs ~150 ms on a large database. That is small enough to absorb on a
        // lookup, but there is no reason to make the user's first expansion the
        // one that pays it: a fresh cache is constructed when a backup-set editor
        // opens, seconds before anything is expanded.
        _ = Task.Run(() =>
        {
            lock (_lock)
                EnsureOpen();
        });
    }

    /// <summary>
    /// Look up a cached direct-file-size entry for <paramref name="path"/>.
    /// Returns <c>null</c> if the path is not in the cache.
    /// </summary>
    public (long DirectFileSize, int DirectFileCount, DateTime DirLastWriteUtc)? TryGet(string path)
    {
        lock (_lock)
        {
            if (Read(path) is { } entry)
                return (entry.DirectFileSize, entry.DirectFileCount, entry.DirLastWriteUtc);
        }
        return null;
    }

    /// <summary>
    /// Look up the cached recursive total for <paramref name="path"/>.
    /// Returns <c>null</c> if the path is not in the cache or the recursive
    /// total has not been computed yet.
    /// </summary>
    public (long RecursiveSize, int RecursiveFileCount)? TryGetRecursive(string path)
    {
        lock (_lock)
        {
            if (Read(path) is { RecursiveSize: >= 0 } entry)
                return (entry.RecursiveSize, entry.RecursiveFileCount);
        }
        return null;
    }

    /// <summary>
    /// Look up the cached filtered recursive total for <paramref name="path"/>.
    /// Returns <c>null</c> if no value is cached, or if the cached value was
    /// computed under a different filter (i.e. the signatures don't match).
    /// </summary>
    /// <param name="currentLastWriteUtc">
    /// The directory's mtime right now.  A hit requires the stored filtered
    /// stamp to be at least this recent, so the staleness check lives here
    /// rather than being repeated (and previously got wrong) at each call site.
    /// A row with no filtered stamp — every row written before this column
    /// existed — reads as a miss and is recomputed once, which then stamps it.
    /// </param>
    public (long FilteredSize, int FilteredFileCount)? TryGetFilteredRecursive(
        string path, string filterSignature, DateTime currentLastWriteUtc)
    {
        lock (_lock)
        {
            if (Read(path) is { FilteredRecursiveSize: >= 0 } entry
                && entry.FilteredFilterSignature == filterSignature
                && entry.FilteredDirLastWriteUtc is { } stamp
                && stamp >= currentLastWriteUtc)
            {
                return (entry.FilteredRecursiveSize, entry.FilteredRecursiveFileCount);
            }
        }
        return null;
    }

    /// <summary>
    /// Store or update the direct file size for a directory.
    /// Auto-flushes to disk every 500 writes.
    /// </summary>
    public void Set(string path, long directFileSize, int directFileCount, DateTime dirLastWriteUtc)
    {
        lock (_lock)
        {
            // Preserve existing recursive totals when only updating direct sizes.
            var existing = Read(path);
            Write(path, existing is null
                ? new CacheEntry(directFileSize, directFileCount, dirLastWriteUtc,
                                 -1, -1, -1, -1, null)
                : existing with
                {
                    DirectFileSize = directFileSize,
                    DirectFileCount = directFileCount,
                    DirLastWriteUtc = dirLastWriteUtc,
                });
        }
    }

    /// <summary>
    /// Store the recursive total for a directory. Called after a full
    /// recursive computation completes for this path.
    /// </summary>
    public void SetRecursive(string path, long recursiveSize, int recursiveFileCount)
    {
        lock (_lock)
        {
            // No direct-size row yet means nothing has established this
            // directory's validity timestamp, and a recursive total with no
            // timestamp to check it against can never be served
            // (TryGetCachedRecursiveSize requires both). Dropping it matches the
            // previous behaviour.
            if (Read(path) is not { } existing)
                return;

            Write(path, existing with
            {
                RecursiveSize = recursiveSize,
                RecursiveFileCount = recursiveFileCount,
            });
        }
    }

    /// <summary>
    /// Store the filtered recursive total for a directory along with the
    /// filter signature that produced it. The signature is checked by
    /// <see cref="TryGetFilteredRecursive"/> so a cached value is reused
    /// only when the filter hasn't changed.
    /// </summary>
    /// <param name="dirLastWriteUtc">
    /// The directory's mtime as read <b>before</b> the walk that produced these
    /// totals.  Stored in <c>FilteredDirLastWriteUtc</c>, which is what
    /// <see cref="TryGetFilteredRecursive"/> validates against.
    ///
    /// <para>This parameter is the fix for a cache that could never hit. The
    /// filtered walk is the only writer on this path and it does not compute the
    /// direct-file figures, so it used to stamp <c>DirLastWriteUtc</c> —
    /// the timestamp that validates those figures — with
    /// <see cref="DateTime.MinValue"/> on a new row, and the <c>with</c>
    /// expression then preserved that MinValue forever. Validation asks
    /// <c>stamp &gt;= currentLastWrite</c>, which MinValue never satisfies, so
    /// every directory first seen by the filtered pass was re-enumerated in full
    /// on every subsequent pass, permanently. Measured on a real 2,515,306-row
    /// cache: <b>814,458 rows (32.4%)</b> were stamped MinValue and could never
    /// be served.</para>
    ///
    /// <para>Writing the real mtime into <c>DirLastWriteUtc</c> instead would
    /// have been the wrong fix — it would validate direct-file figures this pass
    /// never measured. Hence the separate column.</para>
    /// </param>
    public void SetFilteredRecursive(
        string path, long filteredSize, int filteredFileCount, string filterSignature,
        DateTime dirLastWriteUtc)
    {
        lock (_lock)
        {
            var existing = Read(path);
            Write(path, existing is null
                ? new CacheEntry(0, 0, DateTime.MinValue, -1, -1,
                                 filteredSize, filteredFileCount, filterSignature,
                                 dirLastWriteUtc)
                : existing with
                {
                    FilteredRecursiveSize = filteredSize,
                    FilteredRecursiveFileCount = filteredFileCount,
                    FilteredFilterSignature = filterSignature,
                    FilteredDirLastWriteUtc = dirLastWriteUtc,
                });
        }
    }

    /// <summary>Write any pending changes to disk.</summary>
    public void Flush()
    {
        lock (_lock)
            FlushInternal();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            FlushInternal();
            _selectCmd?.Dispose();
            _selectCmd = null;
            _conn?.Dispose();
            _conn = null;
        }
    }

    // -----------------------------------------------------------------

    /// <summary>
    /// Adopt any maintenance that happened since the last call: drop the memo
    /// (rows may have been deleted) and close the connection, because a
    /// compaction replaces the table wholesale and the prepared statement was
    /// made against the old one. Caller holds the lock.
    /// </summary>
    private void SyncGeneration()
    {
        long gen = Interlocked.Read(ref s_generation);
        if (gen == _seenGeneration)
            return;
        _seenGeneration = gen;

        _hot.Clear();

        long dropGen = Interlocked.Read(ref s_dropWritesGeneration);
        if (dropGen != _seenDropWrites)
        {
            _seenDropWrites = dropGen;
            _dirty.Clear();
        }

        _selectCmd?.Dispose();
        _selectCmd = null;
        _selectPath = null;
        _conn?.Dispose();
        _conn = null;

        // A previous failure to open says nothing about the rebuilt file.
        _openFailed = false;
    }

    /// <summary>Read one row, memoising both hits and misses. Caller holds the lock.</summary>
    private CacheEntry? Read(string path)
    {
        // Every public entry point reaches the database through here (the Set
        // overloads all read first, to merge into the existing row), so this is
        // the one place the maintenance check has to go.
        SyncGeneration();

        if (_hot.TryGetValue(path, out var memo))
            return memo;

        CacheEntry? entry = null;
        var conn = EnsureOpen();
        if (conn is not null)
        {
            try
            {
                _selectPath!.Value = path;
                using var reader = _selectCmd!.ExecuteReader();
                if (reader.Read())
                    entry = ReadEntry(reader);
            }
            catch
            {
                // A cache is never worth failing a backup over: treat any read
                // error as a miss and let the value be recomputed.
            }
        }

        // Dropped wholesale rather than evicted one-by-one: there is no
        // recency information to evict by, and rebuilding a memo entry is a
        // single index seek.
        if (_hot.Count >= HotLimit)
            _hot.Clear();

        _hot[path] = entry;
        return entry;
    }

    /// <summary>Record a row in the memo and queue it for persistence. Caller holds the lock.</summary>
    private void Write(string path, CacheEntry entry)
    {
        _hot[path] = entry;
        _dirty[path] = entry;

        if (_dirty.Count >= DirtyFlushThreshold)
            FlushInternal();
    }

    private static CacheEntry ReadEntry(SqliteDataReader reader)
    {
        var lastWrite = DateTime.Parse(
            reader.GetString(2), null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        return new CacheEntry(
            reader.GetInt64(0),
            reader.GetInt32(1),
            lastWrite,
            reader.IsDBNull(3) ? -1L : reader.GetInt64(3),
            reader.IsDBNull(4) ? -1 : reader.GetInt32(4),
            reader.IsDBNull(5) ? -1L : reader.GetInt64(5),
            reader.IsDBNull(6) ? -1 : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8)
                ? null
                : DateTime.Parse(reader.GetString(8), null,
                                 System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// Open the database (once) and prepare the point-lookup statement.
    /// Returns <c>null</c> if the database cannot be opened, in which case the
    /// cache degrades to a session-only memo rather than throwing.
    /// Caller holds the lock.
    /// </summary>
    private SqliteConnection? EnsureOpen()
    {
        if (_conn is not null || _openFailed)
            return _conn;

        try
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            // WAL so the background prune's deletes and this connection's
            // flushes don't lock each other out; NORMAL because a lost cache
            // entry after a crash costs a recompute, nothing more.
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "PRAGMA synchronous=NORMAL");
            Exec(conn, "PRAGMA busy_timeout=30000");
            bool addedFilteredStamp = EnsureTable(conn);

            var select = conn.CreateCommand();
            select.CommandText = """
                SELECT DirectFileSize, DirectFileCount, DirLastWriteUtc,
                       RecursiveSize, RecursiveFileCount,
                       FilteredRecursiveSize, FilteredRecursiveFileCount,
                       FilteredFilterSignature, FilteredDirLastWriteUtc
                FROM DirectorySizeCache WHERE Path = $path
                """;
            _selectPath = select.Parameters.Add("$path", SqliteType.Text);
            select.Prepare();

            _conn = conn;
            _selectCmd = select;
            if (addedFilteredStamp)
                StartFilteredStampBackfill(_dbPath);
            StartPruneIfNeeded();
        }
        catch
        {
            _openFailed = true;
        }

        return _conn;
    }

    private void FlushInternal()
    {
        if (_dirty.Count == 0)
            return;

        var toSave = new List<KeyValuePair<string, CacheEntry>>(_dirty);
        _dirty.Clear();

        var conn = EnsureOpen();
        if (conn is null)
            return;

        try
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO DirectorySizeCache
                    (Path, DirectFileSize, DirectFileCount, DirLastWriteUtc,
                     RecursiveSize, RecursiveFileCount,
                     FilteredRecursiveSize, FilteredRecursiveFileCount,
                     FilteredFilterSignature, FilteredDirLastWriteUtc)
                VALUES ($path, $size, $count, $lastWrite, $recSize, $recCount,
                        $filtSize, $filtCount, $filtSig, $filtLw)
                """;
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
            var pLw = cmd.Parameters.Add("$lastWrite", SqliteType.Text);
            var pRecSize = cmd.Parameters.Add("$recSize", SqliteType.Integer);
            var pRecCount = cmd.Parameters.Add("$recCount", SqliteType.Integer);
            var pFiltSize = cmd.Parameters.Add("$filtSize", SqliteType.Integer);
            var pFiltCount = cmd.Parameters.Add("$filtCount", SqliteType.Integer);
            var pFiltSig = cmd.Parameters.Add("$filtSig", SqliteType.Text);
            var pFiltLw = cmd.Parameters.Add("$filtLw", SqliteType.Text);

            foreach (var (path, entry) in toSave)
            {
                pPath.Value = path;
                pSize.Value = entry.DirectFileSize;
                pCount.Value = entry.DirectFileCount;
                pLw.Value = entry.DirLastWriteUtc.ToString("O");
                pRecSize.Value = entry.RecursiveSize >= 0 ? entry.RecursiveSize : DBNull.Value;
                pRecCount.Value = entry.RecursiveFileCount >= 0 ? entry.RecursiveFileCount : DBNull.Value;
                pFiltSize.Value = entry.FilteredRecursiveSize >= 0
                    ? entry.FilteredRecursiveSize : DBNull.Value;
                pFiltCount.Value = entry.FilteredRecursiveFileCount >= 0
                    ? entry.FilteredRecursiveFileCount : DBNull.Value;
                pFiltSig.Value = entry.FilteredFilterSignature is not null
                    ? entry.FilteredFilterSignature : DBNull.Value;
                pFiltLw.Value = entry.FilteredDirLastWriteUtc is { } flw
                    ? flw.ToString("O") : (object)DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            // Non-critical — worst case we recompute next session.
        }
    }

    /// <summary>
    /// One-time migration for databases that predate <c>FilteredDirLastWriteUtc</c>:
    /// copy <c>DirLastWriteUtc</c> into it for every row that already holds a
    /// filtered total and carries a real timestamp.
    ///
    /// <para><b>Why bother rather than let them recompute.</b> Without this every
    /// filtered row reads as a miss exactly once — correct, but on the author's
    /// cache that is 909,664 directories each needing a full recursive walk to
    /// restore a value that is already sitting in the row. Copying the stamp
    /// reproduces precisely the validation the old code performed for those rows
    /// (it checked the filtered total against <c>DirLastWriteUtc</c>), so this is
    /// not a new guarantee, just the old one written down where the new check can
    /// see it. Each row is re-stamped properly the next time it is genuinely
    /// recomputed.</para>
    ///
    /// <para>Rows stamped <see cref="DateTime.MinValue"/> are deliberately left
    /// NULL. Those are the broken ones — 814,362 of them — and they have never
    /// held a servable value, so they must recompute once.</para>
    ///
    /// <para><b>On a background thread, on its own connection, holding no lock</b>
    /// — measured at 5.85 s over 2.5M rows. Doing that inside
    /// <see cref="EnsureOpen"/> would block the first lookup, which is the exact
    /// six-second stall this class was rewritten to remove (see the type
    /// remarks). Until it finishes, lookups simply miss and recompute, which is
    /// the cache's normal degraded mode rather than a wrong answer.</para>
    /// </summary>
    private static void StartFilteredStampBackfill(string dbPath)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                Exec(conn, "PRAGMA busy_timeout=30000");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE DirectorySizeCache
                    SET FilteredDirLastWriteUtc = DirLastWriteUtc
                    WHERE FilteredRecursiveSize IS NOT NULL
                      AND FilteredDirLastWriteUtc IS NULL
                      AND DirLastWriteUtc <> $minValue
                    """;
                cmd.Parameters.AddWithValue("$minValue", DateTime.MinValue.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Best effort. Failing here costs a recompute per row, never a
                // wrong answer: an un-backfilled row reads as a miss.
            }
        });
    }

    /// <summary>
    /// Bound the database's growth. Nothing ever deleted rows before, so the
    /// file grew monotonically with every directory ever scanned.
    ///
    /// <para>Runs on its own connection, on a background thread, holding no lock:
    /// deleting from a multi-million-row table takes seconds, and doing it under
    /// <see cref="_lock"/> would resurrect exactly the stall this class was
    /// rewritten to remove.</para>
    ///
    /// <para><b>Eviction is random, deliberately.</b> The obvious cheap orderings
    /// are both wrong here. <c>rowid</c> was a decent staleness proxy — every
    /// <c>INSERT OR REPLACE</c> assigns a fresh one, so low rowids are old rows —
    /// but the table is <c>WITHOUT ROWID</c> now and has none. Ordering by the
    /// primary key instead would be the cheapest possible scan and the worst
    /// possible policy: it would evict the same alphabetical prefix over and
    /// over, so <c>C:\</c> would be permanently uncached while <c>Z:\</c> was
    /// never touched. Random eviction needs no extra column or index, cannot
    /// thrash one region, and is only ever reached in the pathological case this
    /// exists to bound — a user who wants targeted cleanup has
    /// Settings ▸ Caches, which drops entries whose directory is actually gone.</para>
    ///
    /// <para>The file itself will not shrink — freed pages go on the freelist to
    /// be reused by later inserts. Reclaiming them needs a VACUUM, which rewrites
    /// the whole database under an exclusive lock and is not worth doing behind
    /// the user's back. That is also why the loop below measures <em>used</em>
    /// bytes rather than file bytes: <c>page_count</c> alone never falls after a
    /// delete, so a loop waiting for it to drop would never terminate.</para>
    /// </summary>
    private void StartPruneIfNeeded()
    {
        if (_pruneStarted)
            return;
        _pruneStarted = true;

        var dbPath = _dbPath;
        _ = Task.Run(() =>
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                Exec(conn, "PRAGMA busy_timeout=30000");

                if (UsedBytes(conn) <= MaxUsedBytes)
                    return;

                while (true)
                {
                    int deleted;
                    using (var del = conn.CreateCommand())
                    {
                        del.CommandText = $"""
                            DELETE FROM DirectorySizeCache WHERE Path IN (
                                SELECT Path FROM DirectorySizeCache
                                ORDER BY random() LIMIT {PruneChunkRows})
                            """;
                        deleted = del.ExecuteNonQuery();
                    }

                    // Nothing left to give: stop rather than spin. Can only happen
                    // if the table empties while still over budget, which would
                    // mean the size is not coming from this table at all.
                    if (deleted == 0)
                        break;

                    if (UsedBytes(conn) <= PruneTargetBytes)
                        break;
                }
            }
            catch
            {
                // Housekeeping only — an over-sized cache is still a correct one.
            }
        });
    }

    /// <summary>
    /// Bytes the database is actually using, i.e. excluding pages already freed
    /// and waiting on the freelist to be reused. All three pragmas read fields
    /// out of the file header, so this costs no scan at any table size — the
    /// point of using it as the prune gate.
    /// </summary>
    private static long UsedBytes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ((SELECT * FROM pragma_page_count())
                  - (SELECT * FROM pragma_freelist_count()))
                 * (SELECT * FROM pragma_page_size())
            """;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <returns>
    /// <c>true</c> when <c>FilteredDirLastWriteUtc</c> was added by THIS call,
    /// i.e. the database predates the column.  The caller uses that to run the
    /// one-time backfill exactly once instead of on every open.
    /// </returns>
    private static bool EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS DirectorySizeCache (
                Path TEXT PRIMARY KEY,
                DirectFileSize INTEGER NOT NULL,
                DirectFileCount INTEGER NOT NULL DEFAULT 0,
                DirLastWriteUtc TEXT NOT NULL,
                RecursiveSize INTEGER,
                RecursiveFileCount INTEGER,
                FilteredRecursiveSize INTEGER,
                FilteredRecursiveFileCount INTEGER,
                FilteredFilterSignature TEXT,
                FilteredDirLastWriteUtc TEXT
            ) WITHOUT ROWID
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing databases that lack newer columns.  Ask which columns
        // exist rather than attempting each ALTER and swallowing the failure:
        // on an already-migrated database that threw six SqliteExceptions on
        // every open, which is both slow and indistinguishable in a debugger
        // from a real schema problem.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(DirectorySizeCache)";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        AddColumnIfMissing(conn, existing, "DirectFileCount", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, existing, "RecursiveSize", "INTEGER");
        AddColumnIfMissing(conn, existing, "RecursiveFileCount", "INTEGER");
        AddColumnIfMissing(conn, existing, "FilteredRecursiveSize", "INTEGER");
        AddColumnIfMissing(conn, existing, "FilteredRecursiveFileCount", "INTEGER");
        AddColumnIfMissing(conn, existing, "FilteredFilterSignature", "TEXT");

        bool addedFilteredStamp = !existing.Contains("FilteredDirLastWriteUtc");
        AddColumnIfMissing(conn, existing, "FilteredDirLastWriteUtc", "TEXT");
        return addedFilteredStamp;
    }

    private static void AddColumnIfMissing(
        SqliteConnection conn, HashSet<string> existing, string column, string type)
    {
        if (existing.Contains(column))
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE DirectorySizeCache ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    /// <param name="FilteredDirLastWriteUtc">
    /// Validity timestamp for the FILTERED totals only, and deliberately
    /// separate from <paramref name="DirLastWriteUtc"/>.  The two halves of a row
    /// are written by different passes: the filtered walk never establishes the
    /// direct-file figures, so it must not stamp the timestamp that validates
    /// them — doing so would serve stale direct sizes as current.  Null means
    /// "filtered total present but never validated", which reads as a miss.
    /// </param>
    private sealed record CacheEntry(
        long DirectFileSize, int DirectFileCount, DateTime DirLastWriteUtc,
        long RecursiveSize, int RecursiveFileCount,
        long FilteredRecursiveSize, int FilteredRecursiveFileCount,
        string? FilteredFilterSignature,
        DateTime? FilteredDirLastWriteUtc = null);
}
