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
    /// Row count above which the oldest entries are pruned. Set well above a
    /// plausible working set (~1.5M rows covers every directory on this machine)
    /// so pruning only ever fires on genuinely pathological growth: an evicted
    /// row costs a full recursive rescan of that directory, which is far more
    /// expensive than the disk space it frees.
    /// </summary>
    private const long MaxRows = 3_000_000;
    private const long PruneTargetRows = 2_000_000;
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

    public DirectorySizeCache()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LithicBackup");
        Directory.CreateDirectory(appDataDir);
        _dbPath = Path.Combine(appDataDir, "sizecache.db");

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
    public (long FilteredSize, int FilteredFileCount)? TryGetFilteredRecursive(
        string path, string filterSignature)
    {
        lock (_lock)
        {
            if (Read(path) is { FilteredRecursiveSize: >= 0 } entry
                && entry.FilteredFilterSignature == filterSignature)
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
    public void SetFilteredRecursive(
        string path, long filteredSize, int filteredFileCount, string filterSignature)
    {
        lock (_lock)
        {
            var existing = Read(path);
            Write(path, existing is null
                ? new CacheEntry(0, 0, DateTime.MinValue, -1, -1,
                                 filteredSize, filteredFileCount, filterSignature)
                : existing with
                {
                    FilteredRecursiveSize = filteredSize,
                    FilteredRecursiveFileCount = filteredFileCount,
                    FilteredFilterSignature = filterSignature,
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

    /// <summary>Read one row, memoising both hits and misses. Caller holds the lock.</summary>
    private CacheEntry? Read(string path)
    {
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
            reader.IsDBNull(7) ? null : reader.GetString(7));
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
            EnsureTable(conn);

            var select = conn.CreateCommand();
            select.CommandText = """
                SELECT DirectFileSize, DirectFileCount, DirLastWriteUtc,
                       RecursiveSize, RecursiveFileCount,
                       FilteredRecursiveSize, FilteredRecursiveFileCount,
                       FilteredFilterSignature
                FROM DirectorySizeCache WHERE Path = $path
                """;
            _selectPath = select.Parameters.Add("$path", SqliteType.Text);
            select.Prepare();

            _conn = conn;
            _selectCmd = select;
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
                     FilteredFilterSignature)
                VALUES ($path, $size, $count, $lastWrite, $recSize, $recCount,
                        $filtSize, $filtCount, $filtSig)
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
    /// Bound the database's growth. Nothing ever deleted rows before, so the
    /// file grew monotonically with every directory ever scanned.
    ///
    /// <para>Runs on its own connection, on a background thread, holding no lock:
    /// counting and deleting from a multi-million-row table takes seconds, and
    /// doing it under <see cref="_lock"/> would resurrect exactly the stall this
    /// class was rewritten to remove. Deletion is chunked and ordered by
    /// <c>rowid</c>, which is both the cheapest possible scan (rowid *is* the
    /// table's order) and a good proxy for staleness, because
    /// <c>INSERT OR REPLACE</c> gives every rewritten row a fresh rowid.</para>
    ///
    /// <para>The file itself will not shrink — freed pages are reused by later
    /// inserts instead. Reclaiming them needs a VACUUM, which rewrites the whole
    /// database under an exclusive lock and is not worth doing behind the user's
    /// back.</para>
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

                using (var count = conn.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*) FROM DirectorySizeCache";
                    if (Convert.ToInt64(count.ExecuteScalar()) <= MaxRows)
                        return;
                }

                long remaining;
                do
                {
                    using (var del = conn.CreateCommand())
                    {
                        del.CommandText = $"""
                            DELETE FROM DirectorySizeCache WHERE rowid IN (
                                SELECT rowid FROM DirectorySizeCache
                                ORDER BY rowid LIMIT {PruneChunkRows})
                            """;
                        del.ExecuteNonQuery();
                    }

                    using var recount = conn.CreateCommand();
                    recount.CommandText = "SELECT COUNT(*) FROM DirectorySizeCache";
                    remaining = Convert.ToInt64(recount.ExecuteScalar());
                }
                while (remaining > PruneTargetRows);
            }
            catch
            {
                // Housekeeping only — an over-sized cache is still a correct one.
            }
        });
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void EnsureTable(SqliteConnection conn)
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
                FilteredFilterSignature TEXT
            )
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

    private sealed record CacheEntry(
        long DirectFileSize, int DirectFileCount, DateTime DirLastWriteUtc,
        long RecursiveSize, int RecursiveFileCount,
        long FilteredRecursiveSize, int FilteredRecursiveFileCount,
        string? FilteredFilterSignature);
}
