using System.IO;
using Microsoft.Data.Sqlite;

namespace LithicBackup.ViewModels;

/// <summary>Where a cache stands right now.</summary>
/// <param name="DisplayName">Name shown in Settings.</param>
/// <param name="DbPath">Full path to the SQLite file.</param>
/// <param name="Exists">False before the cache has ever been written.</param>
/// <param name="FileBytes">Size on disk, including pages already freed.</param>
/// <param name="UsedBytes">Size excluding freed pages waiting on the freelist.
/// The gap between this and <paramref name="FileBytes"/> is what a compaction
/// would hand back to the filesystem.</param>
/// <param name="Rows">Number of cached entries.</param>
/// <param name="IsLegacyLayout">True while the table still stores every key
/// twice — see <see cref="CacheMaintenance"/>.</param>
public sealed record CacheReport(
    string DisplayName,
    string DbPath,
    bool Exists,
    long FileBytes,
    long UsedBytes,
    long Rows,
    bool IsLegacyLayout);

/// <summary>Before/after totals for one cache, so the UI can report what was freed.</summary>
/// <param name="SkippedRoots">Drives or shares that were not reachable during the
/// sweep. Everything cached under them was deliberately left alone, so the user
/// can be told why the cache did not shrink as much as expected.</param>
public sealed record CacheMaintenanceResult(
    string DisplayName,
    long BytesBefore,
    long BytesAfter,
    long RowsBefore,
    long RowsAfter,
    IReadOnlyList<string>? SkippedRoots = null)
{
    public long BytesFreed => Math.Max(0, BytesBefore - BytesAfter);
    public long RowsRemoved => Math.Max(0, RowsBefore - RowsAfter);
    public IReadOnlyList<string> Unreachable => SkippedRoots ?? Array.Empty<string>();
}

/// <summary>
/// Housekeeping for the on-disk caches (<c>sizecache.db</c>,
/// <c>filehashcache.db</c>), exposed in Settings ▸ Caches.
///
/// <para>Nothing here is on a hot path — every operation is explicitly asked for
/// by the user and runs with progress and cancellation. That is the whole point
/// of doing it here rather than automatically at startup: a full sweep and
/// rebuild of a multi-hundred-megabyte cache takes minutes, and the one thing
/// these caches must never do is make the user wait at launch.</para>
///
/// <para><b>Why compaction is worth offering.</b> The caches grow monotonically:
/// a row is written for every directory ever walked and never removed when that
/// directory is deleted. On the author's machine roughly a quarter of the rows
/// pointed at paths that no longer existed. Separately, the original schema
/// declared <c>Path TEXT PRIMARY KEY</c> on an ordinary rowid table, which makes
/// SQLite keep the table b-tree (keyed by rowid) <i>and</i> an automatic index
/// (keyed by Path) — storing every path, averaging 106 characters, twice.
/// Measured over 300,000 real rows: 108.0 MB against 71.0 MB for the same data
/// in a <c>WITHOUT ROWID</c> table, which is 34% smaller and marginally faster to
/// query, since the row lives in the index b-tree instead of behind a second
/// seek. New databases are created in the new shape; existing ones are rebuilt
/// by <see cref="CompactAsync"/>.</para>
/// </summary>
public static class CacheMaintenance
{
    /// <summary>Rows deleted per statement while sweeping, so a huge cache
    /// cannot build one enormous transaction (or one enormous parameter list).</summary>
    private const int DeleteBatch = 5_000;

    /// <summary>Rows read per pass of the sweep. The sweep re-queries from the
    /// last key seen rather than holding one reader open across the deletes.</summary>
    private const int ScanBatch = 50_000;

    /// <summary>Keys probed per concurrent round. Small enough that a newly
    /// discovered missing subtree is recognised (and skipped wholesale) within a
    /// chunk or two, large enough to keep every probe thread fed.</summary>
    private const int ProbeChunk = 2_048;

    /// <summary>Concurrent filesystem probes. Measured on cold scattered
    /// directories: 1 thread 21/s, 4 threads 51/s, 8 threads 96/s, 16 threads
    /// 235/s, 32 threads 282/s — so sixteen captures the win, and going higher
    /// buys 20% while making the drive that much less responsive to everything
    /// else the machine is doing.</summary>
    private const int ProbeParallelism = 16;

    private sealed record CacheDef(
        string DisplayName,
        string FileName,
        string Table,
        string PathColumn,
        Action<bool> Invalidate);

    private static readonly CacheDef[] Defs =
    {
        new("Directory sizes", "sizecache.db", "DirectorySizeCache", "Path",
            DirectorySizeCache.InvalidateLiveInstances),
        new("File hashes", "filehashcache.db", "FileHashCache", "FilePath",
            FileHashCache.InvalidateLiveInstances),
    };

    /// <summary>Set by <c>tools\cache_maint_test</c> to run the real maintenance
    /// code against a throwaway copy of a cache. Never set by the app.</summary>
    internal static string? DirectoryOverride;

    /// <summary>Directory holding the caches. Single source of truth for the
    /// cache classes as well, so a maintenance run can never end up operating on
    /// a different file from the one being served.</summary>
    public static string AppDataDirectory
    {
        get
        {
            var dir = DirectoryOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LithicBackup");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    internal static string PathFor(string fileName) => Path.Combine(AppDataDirectory, fileName);

    // -----------------------------------------------------------------
    // Reporting
    // -----------------------------------------------------------------

    /// <summary>Current size and row count of every cache. Cheap: the sizes come
    /// from file-header pragmas, and the row count is the only scan.</summary>
    public static IReadOnlyList<CacheReport> Survey()
    {
        var results = new List<CacheReport>(Defs.Length);
        foreach (var def in Defs)
            results.Add(SurveyOne(def));
        return results;
    }

    private static CacheReport SurveyOne(CacheDef def)
    {
        string dbPath = PathFor(def.FileName);
        if (!File.Exists(dbPath))
            return new CacheReport(def.DisplayName, dbPath, false, 0, 0, 0, false);

        try
        {
            using var conn = Open(dbPath);
            if (!TableExists(conn, def.Table))
                return new CacheReport(def.DisplayName, dbPath, false, FileBytes(dbPath), 0, 0, false);

            return new CacheReport(
                def.DisplayName, dbPath, true,
                FileBytes(dbPath),
                UsedBytes(conn),
                Scalar(conn, $"SELECT COUNT(*) FROM {def.Table}"),
                IsLegacyLayout(conn, def.Table));
        }
        catch
        {
            // An unreadable cache is still reportable by its file size, and is
            // exactly the case where the user most wants the Clear button.
            return new CacheReport(def.DisplayName, dbPath, true, FileBytes(dbPath), 0, 0, false);
        }
    }

    // -----------------------------------------------------------------
    // Compaction
    // -----------------------------------------------------------------

    /// <summary>
    /// Drop entries whose path no longer exists, rebuild the table in the
    /// compact layout if it is still the legacy one, then VACUUM so the freed
    /// space actually returns to the filesystem.
    /// </summary>
    /// <remarks>
    /// The sweep walks keys in sorted order and skips whole subtrees: if a
    /// directory is missing, every key beneath it is missing too and needs no
    /// filesystem call of its own. That matters — probing scattered paths
    /// individually is dominated by seek time on external drives, while sorted
    /// order both collapses deleted subtrees into a single probe and gives the
    /// OS a friendly access pattern.
    /// </remarks>
    public static Task<IReadOnlyList<CacheMaintenanceResult>> CompactAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<CacheMaintenanceResult>>(() =>
        {
            var results = new List<CacheMaintenanceResult>(Defs.Length);
            foreach (var def in Defs)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(CompactOne(def, progress, ct));
            }
            return results;
        }, ct);

    private static CacheMaintenanceResult CompactOne(
        CacheDef def, IProgress<string>? progress, CancellationToken ct)
    {
        string dbPath = PathFor(def.FileName);
        if (!File.Exists(dbPath))
            return new CacheMaintenanceResult(def.DisplayName, 0, 0, 0, 0);

        long bytesBefore = FileBytes(dbPath);
        long rowsBefore = 0;
        var roots = new RootAvailability();

        try
        {
            using var conn = Open(dbPath);
            if (!TableExists(conn, def.Table))
                return new CacheMaintenanceResult(def.DisplayName, bytesBefore, bytesBefore, 0, 0);

            rowsBefore = Scalar(conn, $"SELECT COUNT(*) FROM {def.Table}");

            long removed = SweepMissingPaths(conn, def, rowsBefore, roots, progress, ct);

            if (IsLegacyLayout(conn, def.Table))
            {
                progress?.Report($"{def.DisplayName}: rebuilding in the compact layout…");
                RebuildWithoutRowid(conn, def, ct);
            }

            progress?.Report($"{def.DisplayName}: reclaiming free space…");
            ct.ThrowIfCancellationRequested();
            Reclaim(conn);

            long rowsAfter = Scalar(conn, $"SELECT COUNT(*) FROM {def.Table}");
            _ = removed;

            // Live instances may hold memoised rows that no longer exist, and
            // their prepared statements were made against a table this may have
            // just dropped and recreated. Make them re-open. Pending writes are
            // real data and are kept.
            def.Invalidate(false);

            return new CacheMaintenanceResult(
                def.DisplayName, bytesBefore, FileBytes(dbPath), rowsBefore, rowsAfter,
                roots.Unavailable.ToArray());
        }
        catch (OperationCanceledException)
        {
            // Deletes committed so far stand; report honestly on what happened.
            def.Invalidate(false);
            return new CacheMaintenanceResult(
                def.DisplayName, bytesBefore, FileBytes(dbPath), rowsBefore,
                SafeCount(dbPath, def.Table, rowsBefore), roots.Unavailable.ToArray());
        }
        catch
        {
            return new CacheMaintenanceResult(
                def.DisplayName, bytesBefore, FileBytes(dbPath), rowsBefore, rowsBefore,
                roots.Unavailable.ToArray());
        }
    }

    /// <summary>
    /// Delete every row whose key no longer exists on disk. Returns the number
    /// removed. Walks in key order so a deleted subtree costs one probe rather
    /// than one per descendant, and probes each chunk concurrently because the
    /// cost is disk latency rather than work.
    /// </summary>
    private static long SweepMissingPaths(
        SqliteConnection conn, CacheDef def, long totalRows,
        RootAvailability roots, IProgress<string>? progress, CancellationToken ct)
    {
        long removed = 0, examined = 0;
        string? after = null;
        string? deadPrefix = null;
        var doomed = new List<string>(DeleteBatch);
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = ProbeParallelism,
            CancellationToken = ct,
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<string>(ScanBatch);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = after is null
                    ? $"SELECT {def.PathColumn} FROM {def.Table} ORDER BY {def.PathColumn} LIMIT {ScanBatch}"
                    : $"SELECT {def.PathColumn} FROM {def.Table} WHERE {def.PathColumn} > $after " +
                      $"ORDER BY {def.PathColumn} LIMIT {ScanBatch}";
                if (after is not null)
                    cmd.Parameters.AddWithValue("$after", after);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    batch.Add(reader.GetString(0));
            }

            if (batch.Count == 0)
                break;
            after = batch[^1];

            for (int start = 0; start < batch.Count; start += ProbeChunk)
            {
                ct.ThrowIfCancellationRequested();

                int count = Math.Min(ProbeChunk, batch.Count - start);
                var probed = new bool[count];
                var wasProbed = new bool[count];

                // Decide what actually needs asking, before asking any of it.
                for (int j = 0; j < count; j++)
                {
                    string key = batch[start + j];

                    // On a drive we cannot currently see, every path would
                    // answer "missing" and the whole drive's cache would be
                    // thrown away. Unreachable is not the same as gone.
                    if (!roots.IsUsable(key))
                        continue;

                    // Already known missing from a previous chunk's subtree.
                    if (deadPrefix is not null && key.StartsWith(deadPrefix, StringComparison.Ordinal))
                        continue;

                    wasProbed[j] = true;
                }

                // The probes, run concurrently. This is the whole cost of a
                // sweep and it is pure latency: a cold metadata lookup on a
                // mechanical disk measured 47 ms, of which essentially none is
                // CPU, so a single-file sweep leaves the drive idle between
                // seeks. Measured over 4,000 cold scattered directories: 21
                // probes/s on one thread against 235 on sixteen.
                Parallel.For(0, count, parallel, j =>
                {
                    if (wasProbed[j])
                        probed[j] = Exists(def, batch[start + j]);
                });

                // Verdicts in key order, because the subtree rule depends on it.
                for (int j = 0; j < count; j++)
                {
                    string key = batch[start + j];
                    examined++;

                    if (!roots.IsUsable(key))
                    {
                        deadPrefix = null;
                        continue;
                    }

                    if (deadPrefix is not null && key.StartsWith(deadPrefix, StringComparison.Ordinal))
                    {
                        doomed.Add(key);
                        continue;
                    }

                    // wasProbed is normally true here; the fallback keeps this
                    // loop correct on its own terms rather than relying on the
                    // pre-pass having predicted the same thing.
                    bool exists = wasProbed[j] ? probed[j] : Exists(def, key);

                    if (exists)
                    {
                        deadPrefix = null;
                    }
                    else
                    {
                        deadPrefix = key.EndsWith(Path.DirectorySeparatorChar) ? key : key + Path.DirectorySeparatorChar;
                        doomed.Add(key);
                    }

                    if (doomed.Count >= DeleteBatch)
                        removed += DeleteKeys(conn, def, doomed);
                }

                progress?.Report(
                    $"{def.DisplayName}: checked {examined:N0} of {totalRows:N0} entries, {removed + doomed.Count:N0} stale…");
            }
        }

        removed += DeleteKeys(conn, def, doomed);
        return removed;
    }

    /// <summary>
    /// Decides whether a key's drive or share can be asked about at all.
    ///
    /// <para>This exists because "not reachable" and "not there" are
    /// indistinguishable from a single <c>Directory.Exists</c> call, and the
    /// consequences are opposite. An external backup disk that happens to be
    /// unplugged, an empty card reader, a share that is down — each answers
    /// <c>false</c> for every path on it, which without this guard would delete
    /// that entire drive's cached rows in one sweep. Those are precisely the
    /// most expensive rows to rebuild (a disconnected backup drive's file
    /// hashes are terabytes of re-reading), and they are almost certainly still
    /// valid. So an unreachable root is skipped, never swept.</para>
    ///
    /// <para>The sweep walks keys in sorted order, so all the keys on one root
    /// arrive together: remembering just the last root answers nearly every
    /// call without re-parsing or re-probing.</para>
    /// </summary>
    private sealed class RootAvailability
    {
        private readonly Dictionary<string, bool> _known = new(StringComparer.OrdinalIgnoreCase);
        private string _lastRoot = "";
        private bool _lastUsable;

        /// <summary>Roots that were skipped, in the form shown to the user.</summary>
        public SortedSet<string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsUsable(string key)
        {
            if (_lastRoot.Length > 0 &&
                key.StartsWith(_lastRoot, StringComparison.OrdinalIgnoreCase))
                return _lastUsable;

            string root;
            try
            {
                root = Path.GetPathRoot(key) ?? "";
            }
            catch
            {
                return false;   // unparseable: not ours to delete
            }

            if (root.Length == 0)
                return false;   // relative path: nothing sane to probe

            if (!_known.TryGetValue(root, out bool usable))
            {
                usable = Probe(root);
                _known[root] = usable;
                if (!usable)
                    Unavailable.Add(root);
            }

            _lastRoot = root;
            _lastUsable = usable;
            return usable;
        }

        private static bool Probe(string root)
        {
            try
            {
                // A drive letter can exist and still not be ready (removable
                // media absent, drive spun down or offline); DriveInfo reports
                // that without needing a directory listing. Anything else — a
                // UNC share — is answered by asking for the root itself.
                if (root.Length >= 2 && root[1] == ':')
                    return new DriveInfo(root).IsReady;

                return Directory.Exists(root);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>A size-cache key is a directory; a hash-cache key is a file.</summary>
    private static bool Exists(CacheDef def, string key)
    {
        try
        {
            return def.PathColumn == "Path" ? Directory.Exists(key) : File.Exists(key);
        }
        catch
        {
            // A path we cannot even ask about is not one to delete on a guess.
            return true;
        }
    }

    private static long DeleteKeys(SqliteConnection conn, CacheDef def, List<string> keys)
    {
        if (keys.Count == 0)
            return 0;

        long n = 0;
        using (var tx = conn.BeginTransaction())
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {def.Table} WHERE {def.PathColumn} = $p";
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            foreach (var key in keys)
            {
                p.Value = key;
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        keys.Clear();
        return n;
    }

    /// <summary>
    /// Rebuild the table as <c>WITHOUT ROWID</c>, halving the space the keys
    /// take. Done by copying into a new table and renaming, inside one
    /// transaction, so an interrupted run leaves the original intact.
    /// </summary>
    private static void RebuildWithoutRowid(SqliteConnection conn, CacheDef def, CancellationToken ct)
    {
        string createSql = OriginalCreateSql(conn, def.Table)
            ?? throw new InvalidOperationException($"no schema for {def.Table}");

        // Reuse the live column list verbatim: the table has been through
        // additive migrations, so hard-coding columns here would silently drop
        // any that a newer build added.
        string columns = string.Join(", ", ColumnNames(conn, def.Table));
        string tmp = def.Table + "_compact";

        string newSql = createSql
            .Replace($"CREATE TABLE IF NOT EXISTS {def.Table}", $"CREATE TABLE {tmp}")
            .Replace($"CREATE TABLE {def.Table}", $"CREATE TABLE {tmp}")
            .TrimEnd() + " WITHOUT ROWID";

        ct.ThrowIfCancellationRequested();

        using var tx = conn.BeginTransaction();
        Exec(conn, $"DROP TABLE IF EXISTS {tmp}", tx);
        Exec(conn, newSql, tx);
        Exec(conn, $"INSERT INTO {tmp} ({columns}) SELECT {columns} FROM {def.Table}", tx);
        Exec(conn, $"DROP TABLE {def.Table}", tx);
        Exec(conn, $"ALTER TABLE {tmp} RENAME TO {def.Table}", tx);
        tx.Commit();
    }

    // -----------------------------------------------------------------
    // Clearing
    // -----------------------------------------------------------------

    /// <summary>
    /// Throw away every entry and return the space. The caches rebuild
    /// themselves on demand, so this costs time (directory sizes have to be
    /// recomputed, file hashes re-read) but never correctness.
    /// </summary>
    public static Task<IReadOnlyList<CacheMaintenanceResult>> ClearAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<CacheMaintenanceResult>>(() =>
        {
            var results = new List<CacheMaintenanceResult>(Defs.Length);
            foreach (var def in Defs)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(ClearOne(def, progress));
            }
            return results;
        }, ct);

    private static CacheMaintenanceResult ClearOne(CacheDef def, IProgress<string>? progress)
    {
        string dbPath = PathFor(def.FileName);
        if (!File.Exists(dbPath))
            return new CacheMaintenanceResult(def.DisplayName, 0, 0, 0, 0);

        long bytesBefore = FileBytes(dbPath);
        long rowsBefore = 0;

        try
        {
            progress?.Report($"{def.DisplayName}: clearing…");

            using var conn = Open(dbPath);
            if (TableExists(conn, def.Table))
            {
                rowsBefore = Scalar(conn, $"SELECT COUNT(*) FROM {def.Table}");
                Exec(conn, $"DELETE FROM {def.Table}");
            }

            // Also rebuild in the compact layout while the table is empty --
            // it costs nothing here, and it means a user who clears the cache
            // never has to compact it later.
            if (TableExists(conn, def.Table) && IsLegacyLayout(conn, def.Table))
                RebuildWithoutRowid(conn, def, CancellationToken.None);

            progress?.Report($"{def.DisplayName}: reclaiming free space…");
            Reclaim(conn);

            // Memos AND pending writes go: the user asked to forget everything,
            // and a queued write would otherwise resurrect a row moments later.
            def.Invalidate(true);

            return new CacheMaintenanceResult(
                def.DisplayName, bytesBefore, FileBytes(dbPath), rowsBefore, 0);
        }
        catch
        {
            return new CacheMaintenanceResult(
                def.DisplayName, bytesBefore, FileBytes(dbPath), rowsBefore, rowsBefore);
        }
    }

    // -----------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // Long, because a maintenance run competes with whatever the app is
        // doing: a background size scan holds brief write transactions, and
        // VACUUM needs everyone else out of the way.
        Exec(conn, "PRAGMA busy_timeout=60000");
        return conn;
    }

    /// <summary>
    /// Return the freed pages to the filesystem, and then actually fold the
    /// write-ahead log back into the database file.
    ///
    /// <para>The second half is not optional. These caches run in WAL mode, and
    /// <c>VACUUM</c> rewrites the entire database through the log — so the
    /// moment it finishes, the file has shrunk and a log just as large is
    /// sitting beside it. Measured on the real 514 MB size cache: the database
    /// fell to 233 MB and left a 269 MB <c>-wal</c>, so a run that had genuinely
    /// freed 281 MB reported 23.5 MB. Disposing the connection does not fix it,
    /// because Microsoft.Data.Sqlite pools connections and the log is only
    /// folded back when the last handle really closes.</para>
    /// </summary>
    private static void Reclaim(SqliteConnection conn)
    {
        Exec(conn, "VACUUM");
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private static long FileBytes(string dbPath)
    {
        try
        {
            long total = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var info = new FileInfo(dbPath + suffix);
                if (info.Exists)
                    total += info.Length;
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>Bytes in use, i.e. excluding pages already on the freelist.</summary>
    internal static long UsedBytes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ((SELECT * FROM pragma_page_count())
                  - (SELECT * FROM pragma_freelist_count()))
                 * (SELECT * FROM pragma_page_size())
            """;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>True while the table still keeps a separate automatic index for
    /// its text primary key — i.e. stores every key twice.</summary>
    private static bool IsLegacyLayout(SqliteConnection conn, string table)
    {
        string? sql = OriginalCreateSql(conn, table);
        return sql is not null
            && sql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static string? OriginalCreateSql(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() as string;
    }

    private static List<string> ColumnNames(SqliteConnection conn, string table)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static bool TableExists(SqliteConnection conn, string table) =>
        OriginalCreateSql(conn, table) is not null;

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long SafeCount(string dbPath, string table, long fallback)
    {
        try
        {
            using var conn = Open(dbPath);
            return Scalar(conn, $"SELECT COUNT(*) FROM {table}");
        }
        catch { return fallback; }
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
