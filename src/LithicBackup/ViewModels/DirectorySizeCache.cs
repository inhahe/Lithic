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
/// </list>
///
/// Storage: in-memory <see cref="Dictionary{TKey,TValue}"/> loaded from a
/// SQLite database at construction time. Dirty entries are flushed to disk
/// periodically (every 500 writes) and on <see cref="Flush"/>.
/// </summary>
public sealed class DirectorySizeCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly HashSet<string> _dirtyPaths;
    private readonly string _dbPath;

    public DirectorySizeCache()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LithicBackup");
        Directory.CreateDirectory(appDataDir);
        _dbPath = Path.Combine(appDataDir, "sizecache.db");

        _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        _dirtyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadFromDisk();
    }

    /// <summary>
    /// Look up a cached direct-file-size entry for <paramref name="path"/>.
    /// Returns <c>null</c> if the path is not in the cache.
    /// </summary>
    public (long DirectFileSize, int DirectFileCount, DateTime DirLastWriteUtc)? TryGet(string path)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var entry))
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
            if (_cache.TryGetValue(path, out var entry) && entry.RecursiveSize >= 0)
                return (entry.RecursiveSize, entry.RecursiveFileCount);
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
            if (_cache.TryGetValue(path, out var existing))
            {
                // Preserve existing recursive totals when only updating direct sizes.
                _cache[path] = existing with
                {
                    DirectFileSize = directFileSize,
                    DirectFileCount = directFileCount,
                    DirLastWriteUtc = dirLastWriteUtc,
                };
            }
            else
            {
                _cache[path] = new CacheEntry(directFileSize, directFileCount, dirLastWriteUtc, -1, -1);
            }
            _dirtyPaths.Add(path);

            if (_dirtyPaths.Count >= 500)
                FlushInternal();
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
            if (_cache.TryGetValue(path, out var existing))
            {
                _cache[path] = existing with
                {
                    RecursiveSize = recursiveSize,
                    RecursiveFileCount = recursiveFileCount,
                };
                _dirtyPaths.Add(path);

                if (_dirtyPaths.Count >= 500)
                    FlushInternal();
            }
        }
    }

    /// <summary>Write any pending changes to disk.</summary>
    public void Flush()
    {
        lock (_lock)
            FlushInternal();
    }

    public void Dispose() => Flush();

    // -----------------------------------------------------------------

    private void LoadFromDisk()
    {
        if (!File.Exists(_dbPath))
            return;

        try
        {
            using var conn = Open();
            EnsureTable(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Path, DirectFileSize, DirectFileCount, DirLastWriteUtc,
                       RecursiveSize, RecursiveFileCount
                FROM DirectorySizeCache
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var size = reader.GetInt64(1);
                var count = reader.GetInt32(2);
                var lastWrite = DateTime.Parse(
                    reader.GetString(3), null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                var recSize = reader.IsDBNull(4) ? -1L : reader.GetInt64(4);
                var recCount = reader.IsDBNull(5) ? -1 : reader.GetInt32(5);
                _cache[path] = new CacheEntry(size, count, lastWrite, recSize, recCount);
            }
        }
        catch
        {
            // Cache is non-critical — if load fails, start fresh.
            _cache.Clear();
        }
    }

    private void FlushInternal()
    {
        if (_dirtyPaths.Count == 0)
            return;

        var toSave = new List<(string Path, CacheEntry Entry)>();
        foreach (var path in _dirtyPaths)
        {
            if (_cache.TryGetValue(path, out var entry))
                toSave.Add((path, entry));
        }
        _dirtyPaths.Clear();

        try
        {
            using var conn = Open();
            EnsureTable(conn);

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO DirectorySizeCache
                    (Path, DirectFileSize, DirectFileCount, DirLastWriteUtc,
                     RecursiveSize, RecursiveFileCount)
                VALUES ($path, $size, $count, $lastWrite, $recSize, $recCount)
                """;
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
            var pLw = cmd.Parameters.Add("$lastWrite", SqliteType.Text);
            var pRecSize = cmd.Parameters.Add("$recSize", SqliteType.Integer);
            var pRecCount = cmd.Parameters.Add("$recCount", SqliteType.Integer);

            foreach (var (path, entry) in toSave)
            {
                pPath.Value = path;
                pSize.Value = entry.DirectFileSize;
                pCount.Value = entry.DirectFileCount;
                pLw.Value = entry.DirLastWriteUtc.ToString("O");
                pRecSize.Value = entry.RecursiveSize >= 0 ? entry.RecursiveSize : DBNull.Value;
                pRecCount.Value = entry.RecursiveFileCount >= 0 ? entry.RecursiveFileCount : DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            // Non-critical — worst case we recompute next session.
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
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
                RecursiveFileCount INTEGER
            )
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing databases that lack newer columns.
        TryAddColumn(conn, "DirectFileCount", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(conn, "RecursiveSize", "INTEGER");
        TryAddColumn(conn, "RecursiveFileCount", "INTEGER");
    }

    private static void TryAddColumn(SqliteConnection conn, string column, string type)
    {
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE DirectorySizeCache ADD COLUMN {column} {type}";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists — ignore.
        }
    }

    private sealed record CacheEntry(
        long DirectFileSize, int DirectFileCount, DateTime DirLastWriteUtc,
        long RecursiveSize, int RecursiveFileCount);
}
