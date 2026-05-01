using System.IO;
using Microsoft.Data.Sqlite;

namespace LithicBackup.ViewModels;

/// <summary>
/// Persistent cache of per-directory file sizes. Each entry stores the total
/// size of files directly in that directory (not recursive) along with the
/// directory's <see cref="DirectoryInfo.LastWriteTimeUtc"/> at the time the
/// size was computed. On subsequent lookups the timestamp is compared — if it
/// still matches the filesystem, the cached value is reused; otherwise files
/// are re-enumerated and the cache is updated.
///
/// The recursive total for a directory is computed by summing its cached
/// direct file size with the recursively-computed sizes of all subdirectories.
/// This means only directories whose direct contents actually changed need
/// to have their files enumerated — unchanged subtrees are served from cache
/// after a single timestamp check per directory.
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
    /// Store or update a cache entry. Auto-flushes to disk every 500 writes.
    /// </summary>
    public void Set(string path, long directFileSize, int directFileCount, DateTime dirLastWriteUtc)
    {
        lock (_lock)
        {
            _cache[path] = new CacheEntry(directFileSize, directFileCount, dirLastWriteUtc);
            _dirtyPaths.Add(path);

            if (_dirtyPaths.Count >= 500)
                FlushInternal();
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
            cmd.CommandText = "SELECT Path, DirectFileSize, DirectFileCount, DirLastWriteUtc FROM DirectorySizeCache";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var size = reader.GetInt64(1);
                var count = reader.GetInt32(2);
                var lastWrite = DateTime.Parse(
                    reader.GetString(3), null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                _cache[path] = new CacheEntry(size, count, lastWrite);
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
                INSERT OR REPLACE INTO DirectorySizeCache (Path, DirectFileSize, DirectFileCount, DirLastWriteUtc)
                VALUES ($path, $size, $count, $lastWrite)
                """;
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
            var pLw = cmd.Parameters.Add("$lastWrite", SqliteType.Text);

            foreach (var (path, entry) in toSave)
            {
                pPath.Value = path;
                pSize.Value = entry.DirectFileSize;
                pCount.Value = entry.DirectFileCount;
                pLw.Value = entry.DirLastWriteUtc.ToString("O");
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
                DirLastWriteUtc TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing databases that lack the DirectFileCount column.
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE DirectorySizeCache ADD COLUMN DirectFileCount INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // Column already exists — ignore.
        }
    }

    private sealed record CacheEntry(long DirectFileSize, int DirectFileCount, DateTime DirLastWriteUtc);
}
