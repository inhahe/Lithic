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
    /// Look up the cached filtered recursive total for <paramref name="path"/>.
    /// Returns <c>null</c> if no value is cached, or if the cached value was
    /// computed under a different filter (i.e. the signatures don't match).
    /// </summary>
    public (long FilteredSize, int FilteredFileCount)? TryGetFilteredRecursive(
        string path, string filterSignature)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var entry)
                && entry.FilteredRecursiveSize >= 0
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
                _cache[path] = new CacheEntry(
                    directFileSize, directFileCount, dirLastWriteUtc,
                    -1, -1, -1, -1, null);
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
            if (_cache.TryGetValue(path, out var existing))
            {
                _cache[path] = existing with
                {
                    FilteredRecursiveSize = filteredSize,
                    FilteredRecursiveFileCount = filteredFileCount,
                    FilteredFilterSignature = filterSignature,
                };
            }
            else
            {
                _cache[path] = new CacheEntry(
                    0, 0, DateTime.MinValue,
                    -1, -1,
                    filteredSize, filteredFileCount, filterSignature);
            }
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
            cmd.CommandText = """
                SELECT Path, DirectFileSize, DirectFileCount, DirLastWriteUtc,
                       RecursiveSize, RecursiveFileCount,
                       FilteredRecursiveSize, FilteredRecursiveFileCount,
                       FilteredFilterSignature
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
                var filtSize = reader.IsDBNull(6) ? -1L : reader.GetInt64(6);
                var filtCount = reader.IsDBNull(7) ? -1 : reader.GetInt32(7);
                var filtSig = reader.IsDBNull(8) ? null : reader.GetString(8);
                _cache[path] = new CacheEntry(
                    size, count, lastWrite,
                    recSize, recCount,
                    filtSize, filtCount, filtSig);
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
                RecursiveFileCount INTEGER,
                FilteredRecursiveSize INTEGER,
                FilteredRecursiveFileCount INTEGER,
                FilteredFilterSignature TEXT
            )
            """;
        cmd.ExecuteNonQuery();

        // Migrate existing databases that lack newer columns.
        TryAddColumn(conn, "DirectFileCount", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(conn, "RecursiveSize", "INTEGER");
        TryAddColumn(conn, "RecursiveFileCount", "INTEGER");
        TryAddColumn(conn, "FilteredRecursiveSize", "INTEGER");
        TryAddColumn(conn, "FilteredRecursiveFileCount", "INTEGER");
        TryAddColumn(conn, "FilteredFilterSignature", "TEXT");
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
        long RecursiveSize, int RecursiveFileCount,
        long FilteredRecursiveSize, int FilteredRecursiveFileCount,
        string? FilteredFilterSignature);
}
