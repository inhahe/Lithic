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
/// Follows the same pattern as <see cref="DirectorySizeCache"/>: in-memory
/// dictionary loaded from SQLite at construction, dirty-tracking with
/// periodic batch flushes.
///
/// Shared between dedup analysis (in the source selection view) and the
/// actual backup service so hashes computed during analysis are reused
/// without re-reading files.
/// </summary>
public sealed class FileHashCache : IFileHashLookup, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly HashSet<string> _dirtyPaths;
    private readonly string _dbPath;
    private long _lastFlushTick;

    /// <summary>Auto-flush interval in milliseconds.</summary>
    private const long FlushIntervalMs = 10_000;

    public FileHashCache()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LithicBackup");
        Directory.CreateDirectory(appDataDir);
        _dbPath = Path.Combine(appDataDir, "filehashcache.db");

        _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        _dirtyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadFromDisk();
    }

    /// <summary>
    /// Look up a cached SHA-256 hash for the given file. Returns <c>null</c>
    /// if the entry is missing or stale (size/timestamp mismatch).
    /// </summary>
    public string? TryGetHash(string filePath, long currentSize, DateTime currentLastWriteUtc)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(filePath, out var entry)
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
            _cache[filePath] = new CacheEntry(fileSize, lastWriteUtc, sha256Hex);
            _dirtyPaths.Add(filePath);

            long now = Environment.TickCount64;
            if (_dirtyPaths.Count > 0 && now - _lastFlushTick >= FlushIntervalMs)
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
            cmd.CommandText = "SELECT FilePath, FileSize, LastWriteUtc, Sha256Hash FROM FileHashCache";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var size = reader.GetInt64(1);
                var lastWrite = DateTime.Parse(
                    reader.GetString(2), null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                var hash = reader.GetString(3);
                _cache[path] = new CacheEntry(size, lastWrite, hash);
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
        _lastFlushTick = Environment.TickCount64;

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
