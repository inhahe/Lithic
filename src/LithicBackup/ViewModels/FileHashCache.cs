using System.IO;
using LithicBackup.Core;
using Microsoft.Data.Sqlite;

namespace LithicBackup.ViewModels;

/// <summary>
/// Persistent cache of per-file SHA-256 hashes. An entry is valid when both
/// the file's <see cref="FileInfo.Length"/> and
/// <see cref="FileInfo.LastWriteTimeUtc"/> match the cached values. If either
/// differs the entry is stale and must be re-hashed.
///
/// Follows the same pattern as <see cref="DirectorySizeCache"/>: on-demand
/// SQLite point lookups behind a bounded in-memory memo, with dirty-tracking
/// and periodic batch flushes.
///
/// <para>It used to read the entire table into a dictionary in its constructor,
/// which is called on the UI thread during application startup
/// (<c>App.OnStartup</c>). On the author's machine that database had reached
/// 130 MB and the load measured <b>1.65 s</b> of dead time before the main
/// window could appear — for data that a typical session barely touches. Now
/// nothing is read until something is asked for; <c>FilePath</c> is the primary
/// key, so each question costs one index seek.</para>
///
/// Shared between dedup analysis (in the source selection view) and the
/// actual backup service so hashes computed during analysis are reused
/// without re-reading files.
/// </summary>
public sealed class FileHashCache : IFileHashLookup, IDisposable
{
    /// <summary>Auto-flush interval in milliseconds.</summary>
    private const long FlushIntervalMs = 10_000;

    /// <summary>
    /// Cap on memoised rows, so hashing a very large backup set cannot rebuild
    /// the whole-table dictionary this class was rewritten to avoid. On
    /// overflow the memo is dropped; queued writes live in <see cref="_dirty"/>
    /// and are unaffected.
    /// </summary>
    private const int HotLimit = 200_000;

    private readonly object _lock = new();

    /// <summary>Rows read from (or written to) the database this session; a null
    /// value is a remembered miss, so a set full of never-hashed files doesn't
    /// re-query SQLite for each one on every pass.</summary>
    private readonly Dictionary<string, CacheEntry?> _hot;

    /// <summary>Rows written but not yet persisted. Held separately so dropping
    /// the memo can never lose a write.</summary>
    private readonly Dictionary<string, CacheEntry> _dirty;

    private readonly string _dbPath;
    private long _lastFlushTick;

    private SqliteConnection? _conn;
    private SqliteCommand? _selectCmd;
    private SqliteParameter? _selectPath;
    private bool _openFailed;

    public FileHashCache()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LithicBackup");
        Directory.CreateDirectory(appDataDir);
        _dbPath = Path.Combine(appDataDir, "filehashcache.db");

        // Ordinal so the memo agrees with the database's (binary) primary key —
        // see the matching note in DirectorySizeCache. Paths reach this class
        // from FileInfo.FullName, so the filesystem supplies consistent casing.
        _hot = new Dictionary<string, CacheEntry?>(StringComparer.Ordinal);
        _dirty = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        // Constructed on the UI thread during App startup, so the connection is
        // opened off it — the constructor itself must do no I/O.
        _ = Task.Run(() =>
        {
            lock (_lock)
                EnsureOpen();
        });
    }

    /// <summary>
    /// Look up a cached SHA-256 hash for the given file. Returns <c>null</c>
    /// if the entry is missing or stale (size/timestamp mismatch).
    /// </summary>
    public string? TryGetHash(string filePath, long currentSize, DateTime currentLastWriteUtc)
    {
        lock (_lock)
        {
            if (Read(filePath) is { } entry
                && entry.FileSize == currentSize
                && entry.LastWriteUtc == currentLastWriteUtc)
            {
                return entry.Sha256Hex;
            }
        }
        return null;
    }

    /// <summary>
    /// Store or update a hash entry. Auto-flushes to disk every 10 seconds.
    /// </summary>
    public void Set(string filePath, long fileSize, DateTime lastWriteUtc, string sha256Hex)
    {
        lock (_lock)
        {
            var entry = new CacheEntry(fileSize, lastWriteUtc, sha256Hex);

            if (_hot.Count >= HotLimit)
                _hot.Clear();

            _hot[filePath] = entry;
            _dirty[filePath] = entry;

            long now = Environment.TickCount64;
            if (now - _lastFlushTick >= FlushIntervalMs)
                FlushInternal();
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
    private CacheEntry? Read(string filePath)
    {
        if (_hot.TryGetValue(filePath, out var memo))
            return memo;

        CacheEntry? entry = null;
        var conn = EnsureOpen();
        if (conn is not null)
        {
            try
            {
                _selectPath!.Value = filePath;
                using var reader = _selectCmd!.ExecuteReader();
                if (reader.Read())
                {
                    entry = new CacheEntry(
                        reader.GetInt64(0),
                        DateTime.Parse(reader.GetString(1), null,
                            System.Globalization.DateTimeStyles.RoundtripKind),
                        reader.GetString(2));
                }
            }
            catch
            {
                // A stale or unreadable cache must never fail a backup: treat
                // any read error as a miss and re-hash the file.
            }
        }

        if (_hot.Count >= HotLimit)
            _hot.Clear();

        _hot[filePath] = entry;
        return entry;
    }

    /// <summary>
    /// Open the database (once) and prepare the point-lookup statement.
    /// Returns <c>null</c> if it cannot be opened, in which case the cache
    /// degrades to a session-only memo rather than throwing.
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
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "PRAGMA synchronous=NORMAL");
            Exec(conn, "PRAGMA busy_timeout=30000");
            EnsureTable(conn);

            var select = conn.CreateCommand();
            select.CommandText =
                "SELECT FileSize, LastWriteUtc, Sha256Hash FROM FileHashCache WHERE FilePath = $path";
            _selectPath = select.Parameters.Add("$path", SqliteType.Text);
            select.Prepare();

            _conn = conn;
            _selectCmd = select;
        }
        catch
        {
            _openFailed = true;
        }

        return _conn;
    }

    private void FlushInternal()
    {
        _lastFlushTick = Environment.TickCount64;

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
                INSERT OR REPLACE INTO FileHashCache (FilePath, FileSize, LastWriteUtc, Sha256Hash)
                VALUES ($path, $size, $lastWrite, $hash)
                """;
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pLw = cmd.Parameters.Add("$lastWrite", SqliteType.Text);
            var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);

            foreach (var (path, entry) in toSave)
            {
                pPath.Value = path;
                pSize.Value = entry.FileSize;
                pLw.Value = entry.LastWriteUtc.ToString("O");
                pHash.Value = entry.Sha256Hex;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            // Non-critical — worst case we recompute next time.
        }
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
            CREATE TABLE IF NOT EXISTS FileHashCache (
                FilePath     TEXT PRIMARY KEY,
                FileSize     INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL,
                Sha256Hash   TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private sealed record CacheEntry(long FileSize, DateTime LastWriteUtc, string Sha256Hex);
}
