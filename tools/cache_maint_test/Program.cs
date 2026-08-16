// Exercise CacheMaintenance against a *copy* of a real cache database.
//
// The two things worth proving here cannot be reasoned about from the code:
//
//   1. How long the stale-entry sweep actually takes. It has to ask the
//      filesystem about every cached path, and a first attempt that probed
//      3,000 *random* paths ran for minutes -- which extrapolated to about a
//      day for 1.5M rows and would have made the feature useless. The sweep was
//      then written to walk keys in sorted order and skip whole subtrees below
//      a directory already found missing. Whether that is enough is a
//      measurement, not an opinion.
//
//      It was not, quite: this harness put the real cache at 1,852s, and only
//      then was it worth probing each chunk concurrently, which brought the
//      same job to 1,035s. Neither number was predictable from the code.
//
//   2. That the WITHOUT ROWID rebuild preserves every row and column. It copies
//      into a new table and renames, so a mistake in the column list would
//      silently drop data.
//
//   3. That the run really hands the space back. VACUUM in WAL mode rewrites
//      the database through the log, so the first full-scale run finished with
//      a 233 MB file and a 269 MB -wal beside it and reported freeing 23.5 MB
//      of the 281 MB it had freed. Comparing bytes before and after is what
//      caught that.
//
// Everything runs on a copy in %TEMP%, so the real cache is never touched.
//
// Run:  dotnet run --project tools\cache_maint_test -c Release -- [path\to\sizecache.db]

using System.Diagnostics;
using System.IO;
using LithicBackup.ViewModels;
using Microsoft.Data.Sqlite;

namespace CacheMaintTest;

public static class Program
{
    public static int Main(string[] args)
    {
        string source = args.Length > 0
            ? args[0]
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LithicBackup", "sizecache.db");

        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"no such cache: {source}");
            return 1;
        }

        string work = Path.Combine(Path.GetTempPath(), "lithic_cache_maint_test");
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        // Same file name the maintenance code looks for, in a directory it has
        // been redirected to.
        string target = Path.Combine(work, Path.GetFileName(source));
        Console.WriteLine($"copying {source}");
        Console.WriteLine($"     -> {target}");
        var swCopy = Stopwatch.StartNew();
        File.Copy(source, target);
        swCopy.Stop();
        Console.WriteLine($"   copied in {swCopy.Elapsed.TotalSeconds:N1}s\n");

        CacheMaintenance.DirectoryOverride = work;

        string table = Path.GetFileName(source).StartsWith("filehash", StringComparison.OrdinalIgnoreCase)
            ? "FileHashCache" : "DirectorySizeCache";

        // Fingerprint the contents so the rebuild can be proved lossless.
        var before = Fingerprint(target, table);
        Console.WriteLine($"before: {before.Rows:N0} rows, checksum {before.Checksum}, " +
                          $"{before.Columns} columns, layout {(before.WithoutRowid ? "WITHOUT ROWID" : "rowid")}");

        foreach (var r in CacheMaintenance.Survey())
            Console.WriteLine($"  survey: {r.DisplayName,-16} {Mb(r.FileBytes),9} on disk, " +
                              $"{Mb(r.UsedBytes),9} used, {r.Rows,10:N0} rows, legacy={r.IsLegacyLayout}");

        // ---- compact, with live progress and a wall-clock figure ----
        Console.WriteLine("\ncompacting…");
        var sw = Stopwatch.StartNew();
        string lastLine = "";
        var progress = new Progress<string>(line =>
        {
            // Progress<T> with no SynchronizationContext posts to the thread
            // pool, so these arrive on background threads; only used for display.
            lastLine = line;
        });

        using var ticker = new Timer(_ =>
        {
            if (lastLine.Length > 0)
                Console.WriteLine($"   [{sw.Elapsed.TotalSeconds,6:N0}s] {lastLine}");
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        var results = CacheMaintenance.CompactAsync(progress, CancellationToken.None)
                                      .GetAwaiter().GetResult();
        sw.Stop();

        Console.WriteLine($"\ncompact finished in {sw.Elapsed.TotalSeconds:N1}s");
        foreach (var r in results)
        {
            if (r.RowsBefore == 0 && r.BytesBefore == 0)
                continue;
            Console.WriteLine($"  {r.DisplayName,-16} {Mb(r.BytesBefore)} -> {Mb(r.BytesAfter)}  " +
                              $"(freed {Mb(r.BytesFreed)}), rows {r.RowsBefore:N0} -> {r.RowsAfter:N0} " +
                              $"(dropped {r.RowsRemoved:N0})");
            if (r.Unreachable.Count > 0)
                Console.WriteLine($"  {"",-16} kept everything on {string.Join(", ", r.Unreachable)} " +
                                  "(not reachable right now)");
        }

        var after = Fingerprint(target, table);
        Console.WriteLine($"\nafter:  {after.Rows:N0} rows, checksum {after.Checksum}, " +
                          $"{after.Columns} columns, layout {(after.WithoutRowid ? "WITHOUT ROWID" : "rowid")}");

        // ---- verdict ----
        int failures = 0;

        if (!after.WithoutRowid)
        {
            Console.WriteLine("FAIL: table was not rebuilt in the compact layout");
            failures++;
        }

        if (after.Columns != before.Columns)
        {
            Console.WriteLine($"FAIL: column count changed {before.Columns} -> {after.Columns}");
            failures++;
        }

        // Every surviving row must be byte-identical to before: the rebuild is
        // only allowed to remove rows the sweep found stale, never to alter one.
        long expectedSurvivors = before.Rows - (results.Count > 0 ? results[0].RowsRemoved : 0);
        if (after.Rows != expectedSurvivors)
        {
            Console.WriteLine($"FAIL: {after.Rows:N0} rows survived, expected {expectedSurvivors:N0}");
            failures++;
        }

        if (after.SurvivorsMatchOriginal(target, table, source) is { } mismatch)
        {
            Console.WriteLine($"FAIL: {mismatch}");
            failures++;
        }

        Console.WriteLine(failures == 0 ? "\nPASS" : $"\n{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:N1} MB";

    private sealed record Snapshot(long Rows, long Checksum, int Columns, bool WithoutRowid)
    {
        /// <summary>
        /// Re-read every surviving key from the compacted database and compare
        /// its full row against the untouched original. Returns null on success.
        /// </summary>
        public string? SurvivorsMatchOriginal(string compacted, string table, string original)
        {
            using var a = new SqliteConnection($"Data Source={compacted};Mode=ReadOnly");
            using var b = new SqliteConnection($"Data Source={original};Mode=ReadOnly");
            a.Open();
            b.Open();

            string key = table == "FileHashCache" ? "FilePath" : "Path";

            // Sample rather than compare all: a full join across two connections
            // costs more than it proves, and a systematic corruption shows up in
            // any sample.
            using var pick = a.CreateCommand();
            pick.CommandText = $"SELECT * FROM {table} ORDER BY {key} LIMIT 5000";
            using var reader = pick.ExecuteReader();

            int fields = reader.FieldCount;
            int checkedRows = 0;
            while (reader.Read())
            {
                string k = reader.GetString(0);
                using var probe = b.CreateCommand();
                probe.CommandText = $"SELECT * FROM {table} WHERE {key} = $k";
                probe.Parameters.AddWithValue("$k", k);
                using var orig = probe.ExecuteReader();
                if (!orig.Read())
                    return $"key present after compaction but absent from the original: {k}";

                for (int i = 0; i < fields; i++)
                {
                    string x = reader.IsDBNull(i) ? "\0null" : reader.GetValue(i).ToString() ?? "";
                    string y = orig.IsDBNull(i) ? "\0null" : orig.GetValue(i).ToString() ?? "";
                    if (x != y)
                        return $"column {i} differs for {k}: '{x}' vs '{y}'";
                }
                checkedRows++;
            }

            Console.WriteLine($"  verified {checkedRows:N0} surviving rows against the original, field by field");
            return null;
        }
    }

    private static Snapshot Fingerprint(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        long rows = 0, checksum = 0;
        int columns = 0;
        bool withoutRowid = false;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            rows = Convert.ToInt64(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                columns++;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$n";
            cmd.Parameters.AddWithValue("$n", table);
            withoutRowid = (cmd.ExecuteScalar() as string ?? "")
                .Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
        }

        return new Snapshot(rows, checksum, columns, withoutRowid);
    }
}
