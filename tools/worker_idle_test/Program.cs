// Tests for the Worker while it cannot back anything up, and for the selection
// bloat that made each of its passes expensive (see the 2026-10-18 entry on the
// Worker reading and writing gigabytes with no backup drive connected, in
// known-issues.md):
//
//   A. The catalog.
//      A1 GetBackupSetsChangeStampAsync moves after every write to the sets, this
//         connection's or another's, and holds still otherwise.
//      A2 GetAllBackupSetsAsync writes nothing (sorting the rows in SQLite put
//         them through a temp file) and still returns the sets in name order.
//   B. SourceSelection.RemoveRedundantEntriesInExcludedFolders.
//      B1 what it removes and what it keeps, on a hand-built tree;
//      B2 on random trees, it never changes whether any path is included, and
//         removes nothing outside an excluded folder;
//      B3 with --catalog <copy of a real catalog.db>, the same on real sets.
//   C. The Worker's loop, run for real on a temp catalog with a destination that
//      can be switched off.
//      C1 a missing destination is logged once and looked at once per poll, the
//         sets are not re-read at every pass, and file changes do not wake a loop
//         that can back nothing up;
//      C2 new folders inside an excluded tree are not pinned, others still are;
//      C3 when the destination returns, the waiting backup runs;
//      C4 a source on a drive letter with nothing behind it flags no reconcile
//         and is logged once;
//      C5 a full backup that throws is not started again at once.
//
// Build with -p:OldCode=true to compile only what also builds against the code
// before the change (A2 and C), and watch it fail there.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using LithicBackup.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

// --touch <catalog> <set id>: the child process A1 starts, to write one set from
// another process.
if (args.Length == 3 && args[0] == "--touch")
{
    using var touched = new SqliteCatalogRepository(args[1]);
    var set = (await touched.GetBackupSetAsync(int.Parse(args[2])))!;
    set.LastBackupUtc = DateTime.UtcNow;
    await touched.UpdateBackupSetMetadataAsync(set);
    return 0;
}

// --catalog <path>: also run B3 on a copy of that catalog.
// --part A|B|C: run only that part (B3 runs whenever --catalog is given).
string? realCatalog = null;
string? onlyPart = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--catalog" && i + 1 < args.Length)
        realCatalog = args[++i];
    else if (args[i] == "--part" && i + 1 < args.Length)
        onlyPart = args[++i].ToUpperInvariant();
}
bool Run(string part) => onlyPart is null || onlyPart == part;

string root = Path.Combine(Path.GetTempPath(), "lithic_idle_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var stampedSetIds = new List<int>();
string Bs = "\\";

try
{
    if (Run("A"))
        await PartA();
#if !OLD_CODE
    if (Run("B"))
        PartB();
#endif
    if (Run("C"))
        await PartC();
#if !OLD_CODE
    if (realCatalog is not null)
        await PartB3(realCatalog);
#endif
}
finally
{
    // The Worker keeps its full-scan stamps in the real ProgramData folder.
    foreach (int id in stampedSetIds)
    {
        try { File.Delete(FullScanStamp.PathFor(id)); } catch { }
    }
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------------------
// A. The catalog
// ---------------------------------------------------------------------------

async Task PartA()
{
    Console.WriteLine("A. The catalog");
    string dir = Path.Combine(root, "a");
    Directory.CreateDirectory(dir);
    string catalogPath = Path.Combine(dir, "catalog.db");

    using var repo = new SqliteCatalogRepository(catalogPath);
    using var other = new SqliteCatalogRepository(catalogPath);

    var zeta = await repo.CreateBackupSetAsync(NewSet("zeta", Path.Combine(dir, "z")));
    var alpha = await repo.CreateBackupSetAsync(NewSet("Alpha", Path.Combine(dir, "a1")));

#if !OLD_CODE
    long s0 = await repo.GetBackupSetsChangeStampAsync();
    long s1 = await repo.GetBackupSetsChangeStampAsync();
    Check(s0 == s1, "A1 the stamp holds while nothing writes");

    await repo.UpdateBackupSetMetadataAsync(zeta);
    long s2 = await repo.GetBackupSetsChangeStampAsync();
    Check(s2 != s1, "A1 this connection's own write moves it (data_version does not report those)");

    long o0 = await other.GetBackupSetsChangeStampAsync();
    long o1 = await other.GetBackupSetsChangeStampAsync();
    // A real change: SQLite does not rewrite a row whose bytes are unchanged, so
    // writing a set back as it was commits nothing another connection can see.
    alpha.LastBackupUtc = DateTime.UtcNow;
    await repo.UpdateBackupSetAsync(alpha);
    long o2 = await other.GetBackupSetsChangeStampAsync();
    long o3 = await other.GetBackupSetsChangeStampAsync();
    Check(o0 == o1 && o2 != o1 && o3 == o2,
        $"A1 another connection's write moves it once, and it holds after ({o0}, {o1} -> write -> {o2}, {o3})");

    var created = await other.CreateBackupSetAsync(NewSet("made-elsewhere", Path.Combine(dir, "m")));
    long s3 = await repo.GetBackupSetsChangeStampAsync();
    Check(s3 != s2, "A1 a set created by another connection moves it");
    await other.DeleteBackupSetAsync(created.Id);
    long s4 = await repo.GetBackupSetsChangeStampAsync();
    Check(s4 != s3, "A1 a set deleted by another connection moves it");

    // The GUI and the Worker are separate processes.
    var psi = new System.Diagnostics.ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        psi.ArgumentList.Add(typeof(Rig).Assembly.Location);
    foreach (var a in new[] { "--touch", catalogPath, zeta.Id.ToString() })
        psi.ArgumentList.Add(a);
    var toucher = System.Diagnostics.Process.Start(psi)!;
    await toucher.WaitForExitAsync();
    long s5 = await repo.GetBackupSetsChangeStampAsync();
    Check(toucher.ExitCode == 0 && s5 != s4, $"A1 a write by another process moves it (child exit {toucher.ExitCode})");
#endif

    // A selection big enough that SQLite's sort could not keep it in memory:
    // 40,200 nodes, about 8 MB of JSON.
    var big = NewSet("middle", Path.Combine(dir, "big"));
    var bigRoot = big.SourceSelections![0];
    for (int i = 0; i < 200; i++)
    {
        var child = new SourceSelection { Path = $@"{bigRoot.Path}\c{i:D3}", IsDirectory = true, IsSelected = null };
        for (int j = 0; j < 200; j++)
            child.Children.Add(new SourceSelection
            {
                Path = $@"{child.Path}\some-reasonably-long-folder-name-{j:D3}",
                IsDirectory = true,
                IsSelected = true,
            });
        bigRoot.Children.Add(child);
    }
    await repo.CreateBackupSetAsync(big);
    int jsonBytes = JsonSerializer.Serialize(big.SourceSelections).Length;

    var before = Io.Counters();
    var all = await repo.GetAllBackupSetsAsync();
    var after = Io.Counters();
    ulong written = after.WriteTransferCount - before.WriteTransferCount;
    ulong read = after.ReadTransferCount - before.ReadTransferCount;

    Check(written < 64 * 1024,
        $"A2 reading the sets writes nothing ({written:N0} bytes written, {read:N0} read, for {jsonBytes:N0} bytes of selection)");
    var names = all.Select(s => s.Name).ToList();
    Check(names.SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal)) && names.Count == 3,
        $"A2 the sets still come back in name order ({string.Join(", ", names)})");
}

// ---------------------------------------------------------------------------
// B. Removing redundant entries inside excluded folders
// ---------------------------------------------------------------------------

#if !OLD_CODE
void PartB()
{
    Console.WriteLine("B. Removing redundant entries inside excluded folders");

    static bool PrunesDebug(string d)
        => string.Equals(Path.GetFileName(d.TrimEnd('\\')), "debug", StringComparison.OrdinalIgnoreCase);

    SourceSelection D(string path, bool? selected, bool auto, params SourceSelection[] children) => new()
    {
        Path = path, IsDirectory = true, IsSelected = selected, AutoIncludeNewSubdirectories = auto,
        Children = children.ToList(),
    };
    SourceSelection F(string path, bool selected) => new() { Path = path, IsDirectory = false, IsSelected = selected };

    var roots = new List<SourceSelection>
    {
        D(@"R:\proj", true, true,
            D(@"R:\proj\keep", true, true),
            D(@"R:\proj\target", null, true,
                D(@"R:\proj\target\debug", null, true,
                    D(@"R:\proj\target\debug\.fingerprint", null, true,
                        D(@"R:\proj\target\debug\.fingerprint\a1", true, true),
                        D(@"R:\proj\target\debug\.fingerprint\a2", true, true)),
                    D(@"R:\proj\target\debug\mine", false, true)))),
        // A ticked folder that backs up only what it lists.
        D(@"R:\only", true, false,
            D(@"R:\only\debug", true, true)),
        // A partial folder without auto-include.
        D(@"R:\part", null, false,
            D(@"R:\part\debug", true, true)),
        // Inside an excluded folder, but narrower than "everything".
        D(@"R:\x", null, true,
            D(@"R:\x\debug", true, false,
                D(@"R:\x\debug\a", true, true))),
        D(@"R:\y", null, true,
            D(@"R:\y\debug", true, true),
            // A tree that lists a file without its folder.
            D(@"R:\y\sub", null, true,
                F(@"R:\y\sub\debug\f.txt", true))),
    };

    int removed = SourceSelection.RemoveRedundantEntriesInExcludedFolders(roots, PrunesDebug);
    var left = Flatten(roots).Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    Check(removed == 5, $"B1 removes the five redundant entries (removed {removed})");
    Check(!left.Contains(@"R:\proj\target\debug\.fingerprint") && !left.Contains(@"R:\proj\target\debug\.fingerprint\a1"),
        "B1 pinned build folders inside debug go");
    Check(left.Contains(@"R:\proj\target\debug\mine") && left.Contains(@"R:\proj\target\debug"),
        "B1 an unticked folder inside debug stays, with the folders above it");
    Check(left.Contains(@"R:\proj\keep") && left.Contains(@"R:\proj\target"),
        "B1 entries outside any excluded folder stay");
    Check(left.Contains(@"R:\only\debug"), "B1 a child of a folder that backs up only what it lists stays");
    Check(left.Contains(@"R:\part\debug"), "B1 a child of a partial folder without auto-include stays");
    Check(left.Contains(@"R:\x\debug") && left.Contains(@"R:\x\debug\a"),
        "B1 a folder that backs up only what it lists stays, inside debug too");
    Check(!left.Contains(@"R:\y\debug") && !left.Contains(@"R:\y\sub\debug\f.txt"),
        "B1 a ticked debug folder, and a file listed without its folder, go");
    Check(SourceSelection.RemoveRedundantEntriesInExcludedFolders(roots, PrunesDebug) == 0,
        "B1 a second pass finds nothing");

    // B2: random trees. Whether a path is included must come out the same for
    // every path, and nothing outside an excluded folder may go.
    var rng = new Random(20261018);
    string[] names = ["a", "debug", "b"];
    int mismatches = 0, outsideRemovals = 0, trials = 4000, totalRemoved = 0;
    string? firstMismatch = null;

    var probes = new List<string>();
    void AddProbes(string dir, int depth)
    {
        probes.Add(dir);
        probes.Add(dir + Bs + "f.txt");
        probes.Add(dir + Bs + "g.txt");
        if (depth == 4) return;
        foreach (var n in names) AddProbes(dir + Bs + n, depth + 1);
    }
    AddProbes(@"R:\r", 0);

    SourceSelection RandomDir(string path, int depth)
    {
        var node = new SourceSelection
        {
            Path = path, IsDirectory = true,
            IsSelected = rng.Next(3) switch { 0 => true, 1 => false, _ => null },
            AutoIncludeNewSubdirectories = rng.Next(2) == 0,
        };
        if (depth < 3)
        {
            foreach (var n in names)
                if (rng.Next(2) == 0) node.Children.Add(RandomDir(path + Bs + n, depth + 1));
            if (rng.Next(3) == 0)
                node.Children.Add(new SourceSelection { Path = path + Bs + "f.txt", IsDirectory = false, IsSelected = rng.Next(2) == 0 });
            // Sometimes list something two levels down, skipping its folder.
            if (rng.Next(6) == 0)
                node.Children.Add(new SourceSelection { Path = path + Bs + "debug" + Bs + "g.txt", IsDirectory = false, IsSelected = true });
        }
        return node;
    }

    bool InsideDebug(string path, bool isDirectory)
    {
        string? d = isDirectory ? path : Path.GetDirectoryName(path);
        for (; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (PrunesDebug(d)) return true;
        return false;
    }

    for (int t = 0; t < trials; t++)
    {
        var tree = new List<SourceSelection> { RandomDir(@"R:\r", 0) };
        var nodesBefore = Flatten(tree).Select(n => (n.Path, n.IsDirectory)).ToList();
        var included = probes.Select(p => SourceSelection.IsPathIncluded(tree, p)).ToArray();

        int n = SourceSelection.RemoveRedundantEntriesInExcludedFolders(tree, PrunesDebug);
        totalRemoved += n;

        var nodesAfter = Flatten(tree).Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, isDir) in nodesBefore)
            if (!nodesAfter.Contains(path) && !InsideDebug(path, isDir))
                outsideRemovals++;

        var includedAfter = probes.Select(p => SourceSelection.IsPathIncluded(tree, p)).ToArray();
        for (int i = 0; i < probes.Count; i++)
        {
            if (included[i] != includedAfter[i])
            {
                mismatches++;
                firstMismatch ??= $"{probes[i]}: {included[i]} -> {includedAfter[i]}";
            }
        }
    }

    Check(totalRemoved > trials / 4, $"B2 the random trees exercise removal ({totalRemoved} nodes removed over {trials} trees)");
    Check(mismatches == 0, $"B2 no path changes inclusion ({mismatches} changed{(firstMismatch is null ? "" : "; first: " + firstMismatch)})");
    Check(outsideRemovals == 0, $"B2 nothing outside an excluded folder is removed ({outsideRemovals} removed)");
}

async Task PartB3(string catalogPath)
{
    Console.WriteLine($"B3. Real catalog ({catalogPath})");
    string copy = Path.Combine(root, "real-catalog.db");
    File.Copy(catalogPath, copy);
    if (File.Exists(catalogPath + "-wal"))
        File.Copy(catalogPath + "-wal", copy + "-wal");

    using var repo = new SqliteCatalogRepository(copy);
    foreach (var set in await repo.GetAllBackupSetsAsync())
    {
        if (set.SourceSelections is not { Count: > 0 } || set.JobOptions is null)
            continue;

        var prune = DirectoryBackupService.BuildDirectoryPruneFilter(
            set.JobOptions.ExcludedExtensions, set.JobOptions.TierSets) ?? (_ => false);
        string json = JsonSerializer.Serialize(set.SourceSelections);
        var original = JsonSerializer.Deserialize<List<SourceSelection>>(json)!;
        var pruned = JsonSerializer.Deserialize<List<SourceSelection>>(json)!;

        int nodes = Flatten(original).Count();
        int removed = SourceSelection.RemoveRedundantEntriesInExcludedFolders(pruned, prune);
        int jsonAfter = JsonSerializer.Serialize(pruned).Length;

        // Every listed path, a file directly in each listed folder, and one in
        // a folder nobody listed.
        int mismatches = 0, probesTried = 0;
        foreach (var node in Flatten(original))
        {
            var candidates = node.IsDirectory
                ? new[] { node.Path, node.Path + Bs + "__probe__.txt", node.Path + Bs + "__probe__" + Bs + "x.txt" }
                : new[] { node.Path };
            foreach (var p in candidates)
            {
                probesTried++;
                if (SourceSelection.IsPathIncluded(original, p) != SourceSelection.IsPathIncluded(pruned, p))
                    mismatches++;
            }
        }

        Console.WriteLine($"     \"{set.Name}\": {nodes:N0} entries, {removed:N0} removable; selection JSON {json.Length:N0} -> {jsonAfter:N0} bytes");
        Check(mismatches == 0, $"B3 \"{set.Name}\": no path changes inclusion ({mismatches} of {probesTried:N0} probes changed)");
    }
}
#endif

// ---------------------------------------------------------------------------
// C. The Worker's loop
// ---------------------------------------------------------------------------

async Task PartC()
{
    Console.WriteLine("C. The Worker's loop with its destination missing");

    // C1-C3 -----------------------------------------------------------------
    {
        string dir = Path.Combine(root, "c");
        string src = Path.Combine(dir, "src");
        string dst = Path.Combine(dir, "dst");
        string fingerprint = Path.Combine(src, "proj", "target", "debug", ".fingerprint");
        Directory.CreateDirectory(Path.Combine(src, "keep"));
        Directory.CreateDirectory(fingerprint);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "keep", "one.txt"), "one");

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
        {
            set.SourceSelections =
            [
                new SourceSelection
                {
                    Path = src, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true,
                    Children = [ new SourceSelection { Path = Path.Combine(src, "keep"), IsDirectory = true, IsSelected = true } ],
                },
            ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = dst;
            set.JobOptions.ExcludedExtensions = ["**/debug/**"];
        });
        rig.Destination.LivePath = dst;
        rig.Destination.Connected = false;

        await rig.Worker.StartAsync(CancellationToken.None);

        // Twelve seconds without a destination, with a file changing every 100 ms
        // and, part-way, a new folder and thirty new build folders.
        var end = DateTime.UtcNow.AddSeconds(12);
        int k = 0;
        while (DateTime.UtcNow < end)
        {
            File.WriteAllText(Path.Combine(src, "keep", $"churn{k % 20}.txt"), k.ToString());
            if (k == 20)
            {
                Directory.CreateDirectory(Path.Combine(src, "newdir"));
                for (int i = 0; i < 30; i++)
                    Directory.CreateDirectory(Path.Combine(fingerprint, $"fp{i:D2}"));
            }
            k++;
            await Task.Delay(100);
        }
        File.WriteAllText(Path.Combine(src, "keep", "late.txt"), "late");
        // Let the last pass with these changes finish before reading the counts.
        await Task.Delay(TimeSpan.FromSeconds(6));

        int warnings = rig.Log.Count("destination drive not connected");
        int probes = rig.Destination.Calls;
        int reads = rig.Catalog.Count(nameof(ICatalogRepository.GetAllBackupSetsAsync));
        Console.WriteLine($"     over 18 s: {warnings} warning(s), {probes} destination probe(s), {reads} read(s) of the sets, "
                          + $"{rig.Catalog.Count(nameof(ICatalogRepository.UpdateBackupSetAsync))} selection write(s)");

        Check(warnings == 1, $"C1 the missing destination is logged once ({warnings} times)");
        Check(probes <= 6, $"C1 it is looked at about once per 5 s poll ({probes} times in 18 s)");
        Check(reads <= 4, $"C1 the sets are not re-read at every pass ({reads} reads)");
#if !OLD_CODE
        int passes = rig.Catalog.Count(nameof(ICatalogRepository.GetBackupSetsChangeStampAsync));
        Check(passes <= 7, $"C1 file changes every 100 ms do not wake a loop that can back nothing up ({passes} passes in 18 s)");
#endif

        var saved = (await rig.Repo.GetBackupSetAsync(rig.SetId))!;
        var savedPaths = Flatten(saved.SourceSelections!).Select(n => n.Path).ToList();
        int debugEntries = savedPaths.Count(p => p.Contains(Bs + "debug", StringComparison.OrdinalIgnoreCase));
        Check(savedPaths.Contains(Path.Combine(src, "newdir"), StringComparer.OrdinalIgnoreCase),
            "C2 a new folder outside the excluded tree is still pinned");
        Check(debugEntries == 0, $"C2 nothing inside the excluded debug tree is pinned ({debugEntries} entries)");

        // C3: the destination comes back.
        rig.Destination.Connected = true;
        bool copied = await WaitUntil(() => FindFile(dst, "late.txt") is not null, TimeSpan.FromSeconds(60));
        Check(copied, "C3 once the destination is back, the waiting backup runs and copies a file changed while it was away");
        Check(rig.Log.Count("is connected again") == 1, "C3 its return is logged once");

        await rig.DisposeAsync();
    }

    // C4 --------------------------------------------------------------------
    {
        char? free = null;
        for (char c = 'Z'; c >= 'G'; c--)
        {
            if (!Directory.Exists($"{c}:\\")) { free = c; break; }
        }

        if (free is null)
        {
            Check(false, "C4 needs a drive letter with nothing behind it");
        }
        else
        {
            string dir = Path.Combine(root, "c4");
            string dst = Path.Combine(dir, "dst");
            Directory.CreateDirectory(dst);
            string gone = $@"{free}:\not-plugged-in";

            var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
            {
                set.SourceSelections = [ new SourceSelection { Path = gone, IsDirectory = true, IsSelected = true } ];
                set.SourceRoots = [gone];
                set.JobOptions!.TargetDirectory = dst;
            });
            rig.Destination.LivePath = dst;
            rig.Destination.Connected = true;

            await rig.Worker.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(14));

            int fallback = rig.Log.Count("file-system watcher fallback");
            int noSource = rig.Log.Count("no source location is currently available");
            Console.WriteLine($"     over 14 s with source {gone}: {fallback} fallback message(s), {noSource} no-source warning(s)");
            Check(fallback == 0, $"C4 a drive that is not there is not watched as a fallback volume, so no reconcile is flagged ({fallback} messages)");
            Check(noSource == 1, $"C4 its absence is logged once ({noSource} times)");

            await rig.DisposeAsync();
        }
    }

    // C5 --------------------------------------------------------------------
    {
        string dir = Path.Combine(root, "c5");
        string src = Path.Combine(dir, "src");
        string dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
        {
            set.SourceSelections = [ new SourceSelection { Path = src, IsDirectory = true, IsSelected = true } ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = dst;
        });
        rig.Destination.LivePath = dst;
        rig.Destination.Connected = true;
        rig.Sources.Throw = true;

        await rig.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(14));

        int attempts = rig.Sources.Calls;
        Console.WriteLine($"     over 14 s with every full backup throwing: {attempts} attempt(s)");
        Check(attempts == 1, $"C5 a full backup that throws is not started again at once ({attempts} attempts in 14 s)");
#if !OLD_CODE
        Check(rig.Log.Count("no full backup of it starts for 1 minute") == 1, "C5 the wait is logged with the failure");
#endif
        await rig.DisposeAsync();
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static BackupSet NewSet(string name, string sourceRoot) => new()
{
    Name = name,
    SourceRoots = [sourceRoot],
    CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
    SourceSelections = [ new SourceSelection { Path = sourceRoot, IsDirectory = true, IsSelected = true } ],
    JobOptions = new JobOptions
    {
        TargetDirectory = sourceRoot + "-dst",
        Schedule = new BackupSchedule { Enabled = true, Mode = ScheduleMode.Continuous, PollIntervalSeconds = 5 },
    },
};

static IEnumerable<SourceSelection> Flatten(IEnumerable<SourceSelection> nodes)
{
    foreach (var n in nodes)
    {
        yield return n;
        foreach (var c in Flatten(n.Children))
            yield return c;
    }
}

static string? FindFile(string dir, string name)
{
    try
    {
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
    catch (DirectoryNotFoundException)
    {
        return null;
    }
}

static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var end = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < end)
    {
        if (condition()) return true;
        await Task.Delay(250);
    }
    return condition();
}

/// <summary>A Worker on its own temp catalog, with fakes it can be driven through.</summary>
sealed class Rig
{
    public required SqliteCatalogRepository Repo { get; init; }
    public required CountingCatalog Catalog { get; init; }
    public required SwitchableDestination Destination { get; init; }
    public required SwitchableSources Sources { get; init; }
    public required ListLogger<BackupWorker> Log { get; init; }
    public required BackupWorker Worker { get; init; }
    public required int SetId { get; init; }

    /// <summary>
    /// Create a catalog holding one continuous set, configured by <paramref name="configure"/>,
    /// and a Worker over it. Set ids start far above any real set's, because the
    /// Worker keeps its full-scan stamps in the real ProgramData folder; the ids are
    /// added to <paramref name="stampedSetIds"/> so the caller can delete them.
    /// </summary>
    public static async Task<Rig> CreateAsync(string catalogPath, List<int> stampedSetIds, Action<BackupSet> configure)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        var repo = new SqliteCatalogRepository(catalogPath);
        using (var conn = new SqliteConnection($"Data Source={catalogPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sqlite_sequence(name, seq) VALUES ('BackupSets', $seq)";
            cmd.Parameters.AddWithValue("$seq", 900_000 + Random.Shared.Next(90_000));
            cmd.ExecuteNonQuery();
        }

        var set = new BackupSet
        {
            Name = "idle-test",
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions
            {
                Schedule = new BackupSchedule { Enabled = true, Mode = ScheduleMode.Continuous, PollIntervalSeconds = 5 },
            },
        };
        configure(set);
        set = await repo.CreateBackupSetAsync(set);
        stampedSetIds.Add(set.Id);

        var (proxy, counter) = CountingCatalog.Wrap(repo);
        var destination = new SwitchableDestination();
        var sources = new SwitchableSources(new SourceResolver(new Win32VolumeResolver()));
        var log = new ListLogger<BackupWorker>();
        var service = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        var worker = new BackupWorker(log, proxy, service, destination, sources);

        return new Rig
        {
            Repo = repo, Catalog = counter, Destination = destination, Sources = sources,
            Log = log, Worker = worker, SetId = set.Id,
        };
    }

    public async Task DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Worker.StopAsync(stop.Token);
        Worker.Dispose();
        Repo.Dispose();
    }
}

/// <summary>Forwards to a real catalog and counts calls by method name.</summary>
public class CountingCatalog : DispatchProxy
{
    private ICatalogRepository _inner = null!;
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public static (ICatalogRepository Proxy, CountingCatalog Counter) Wrap(ICatalogRepository inner)
    {
        var proxy = Create<ICatalogRepository, CountingCatalog>();
        var counter = (CountingCatalog)(object)proxy;
        counter._inner = inner;
        return (proxy, counter);
    }

    public int Count(string method) => _calls.TryGetValue(method, out int n) ? n : 0;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        _calls.AddOrUpdate(targetMethod!.Name, 1, (_, n) => n + 1);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>A destination that is connected or not, as the test says.</summary>
sealed class SwitchableDestination : IDestinationResolver
{
    private int _calls;
    public volatile bool Connected;
    public string LivePath { get; set; } = "";
    public int Calls => Volatile.Read(ref _calls);

    public DestinationResolution Resolve(JobOptions options)
    {
        Interlocked.Increment(ref _calls);
        return Connected
            ? new DestinationResolution(LivePath, IsConnected: true, LetterChanged: false, options.TargetDirectory, MetadataChanged: false)
            : new DestinationResolution(null, IsConnected: false, LetterChanged: false, options.TargetDirectory, MetadataChanged: false);
    }
}

/// <summary>The real source resolver, which can be made to throw.</summary>
sealed class SwitchableSources(ISourceResolver inner) : ISourceResolver
{
    private int _calls;
    public volatile bool Throw;
    public int Calls => Volatile.Read(ref _calls);

    public SourceResolution Resolve(BackupSet set)
    {
        Interlocked.Increment(ref _calls);
        if (Throw)
            throw new IOException("simulated failure");
        return inner.Resolve(set);
    }
}

/// <summary>Keeps every log message, and echoes warnings and errors.</summary>
sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public int Count(string fragment)
        => _entries.Count(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        string message = formatter(state, exception);
        _entries.Enqueue((logLevel, message));
        if (Environment.GetEnvironmentVariable("IDLE_TEST_VERBOSE") == "1" || logLevel >= LogLevel.Error)
            Console.WriteLine($"       [{logLevel}] {message}");
    }
}

static class Io
{
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static IoCounters Counters()
    {
        GetProcessIoCounters(GetCurrentProcess(), out var c);
        return c;
    }
}
