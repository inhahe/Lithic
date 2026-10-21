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
//      C5 a full backup that throws is not started again at once;
//      C8 while the app works on a set, the Worker starts no run on it, stops
//         one that is going, and carries on after.
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
    if (Run("B"))
    {
#if !OLD_CODE
        PartB();
#endif
        PartWatchRoots();
    }
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

#if !OLD_CODE
    // SQLite still pages through each selection to reach the options column
    // stored after it; what the options-only read saves is parsing the trees.
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var options = await repo.GetAllBackupSetOptionsAsync();
    long optionsAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    _ = await repo.GetAllBackupSetsAsync();
    long setsAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    Check(options.Select(o => o.Name).SequenceEqual(names)
          && options.All(o => o.JobOptions?.Schedule?.Mode == ScheduleMode.Continuous),
        "A3 the options-only read returns every set, in name order, with its options");
    Check(optionsAllocated < 1024 * 1024,
        $"A3 and parses no selection tree ({optionsAllocated:N0} bytes allocated, against {setsAllocated:N0} for the whole sets)");
#endif
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
        // Left expanded in the editor: where the user looked.
        D(@"R:\z", null, true,
            Expanded(D(@"R:\z\debug", null, true,
                D(@"R:\z\debug\pin", true, true)))),
    };

    static SourceSelection Expanded(SourceSelection node)
    {
        node.IsExpanded = true;
        return node;
    }

    int nodesBeforeCount = Flatten(roots).Count();
    int counted = SourceSelection.CountRedundantEntriesInExcludedFolders(roots, PrunesDebug);
    Check(counted == 6 && Flatten(roots).Count() == nodesBeforeCount,
        $"B1 counting finds the six and changes nothing (counted {counted})");

    int removed = SourceSelection.RemoveRedundantEntriesInExcludedFolders(roots, PrunesDebug);
    var left = Flatten(roots).Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    Check(removed == 6, $"B1 removes the six redundant entries (removed {removed})");
    Check(left.Contains(@"R:\z\debug") && !left.Contains(@"R:\z\debug\pin"),
        "B1 a folder left expanded in the editor stays, though what it lists can go");
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
    int mismatches = 0, outsideRemovals = 0, trials = 4000, totalRemoved = 0, uncovered = 0;
    string? firstMismatch = null;
    var probes = RandomTrees.Probes();

    bool InsideDebug(string path, bool isDirectory)
    {
        string? d = isDirectory ? path : Path.GetDirectoryName(path);
        for (; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (PrunesDebug(d)) return true;
        return false;
    }

    for (int t = 0; t < trials; t++)
    {
        var tree = RandomTrees.Generate(rng);
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
        uncovered += RandomTrees.UncoveredByWatchRoots(tree, probes);
    }

    Check(totalRemoved > trials / 4, $"B2 the random trees exercise removal ({totalRemoved} nodes removed over {trials} trees)");
    Check(mismatches == 0, $"B2 no path changes inclusion ({mismatches} changed{(firstMismatch is null ? "" : "; first: " + firstMismatch)})");
    Check(outsideRemovals == 0, $"B2 nothing outside an excluded folder is removed ({outsideRemovals} removed)");
    Check(uncovered == 0, $"B2 after removal, the watch roots still cover every included path ({uncovered} not covered)");
}
#endif

// B4: compiles against the old code too.
void PartWatchRoots()
{
    Console.WriteLine("B4. Watch roots cover every path the selection includes");
    var rng = new Random(4);
    var probes = RandomTrees.Probes();
    int uncovered = 0, trials = 4000;
    string? first = null;
    for (int t = 0; t < trials; t++)
    {
        var tree = RandomTrees.Generate(rng);
        int n = RandomTrees.UncoveredByWatchRoots(tree, probes, out var example);
        uncovered += n;
        first ??= example;
    }
    Check(uncovered == 0,
        $"B4 every included path lies under a watch root, so its drive is read and a fallback watcher sees it "
        + $"({uncovered} not covered over {trials} trees{(first is null ? "" : "; first: " + first)})");
}
#if !OLD_CODE

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
        // The safety net was due and a reconcile was pending: one scan serves both.
        await Task.Delay(TimeSpan.FromSeconds(8));
        int scans = rig.Log.Count("Starting backup for");
        Check(scans == 1, $"C3 one full scan settles both the overdue safety net and the pending reconcile ({scans} scans)");

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
        rig.Destination.Connected = true;
        rig.Sources.Throw = true;

        await rig.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(14));

        int attempts = rig.Sources.Calls;
        Console.WriteLine($"     over 14 s with every full backup throwing: {attempts} attempt(s)");
        Check(attempts == 1, $"C5 a full backup that throws is not started again at once ({attempts} attempts in 14 s)");
#if !OLD_CODE
        Check(rig.Log.Count("no full backup of it starts for 1 minute") == 1, "C5 the wait is logged with the failure");

        // An edit to the set ends the hold.
        rig.Sources.Throw = false;
        var edited = (await rig.Repo.GetBackupSetAsync(rig.SetId))!;
        edited.JobOptions!.ExcludedExtensions.Add("*.never");
        await rig.Repo.UpdateBackupSetMetadataAsync(edited);
        bool retried = await WaitUntil(() => rig.Log.Count("Starting backup for") > 0, TimeSpan.FromSeconds(20));
        Check(retried && rig.Log.Count("was edited") == 1,
            "C5 editing the set lets its full backup start again at once, without waiting out the hold");
#endif
        await rig.DisposeAsync();
    }

    // C7 --------------------------------------------------------------------
    // A set with no history whose selection is a partial folder that
    // auto-includes new ones, sharing its destination with a set that backs up
    // another part of that folder: the untracked-destination probe must look
    // only where this set writes, not under the whole folder.
    {
        string dir = Path.Combine(root, "c7");
        string src = Path.Combine(dir, "src");
        string mine = Path.Combine(src, "mine");
        string other = Path.Combine(src, "other");
        string dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(mine, "a.txt"), "mine");

        // Another set's backup of src\other, where the engine lays it out.
        string drive = char.ToUpperInvariant(other[0]).ToString();
        string otherCopy = Path.Combine(dst, drive, other.Substring(3));
        Directory.CreateDirectory(otherCopy);
        File.WriteAllText(Path.Combine(otherCopy, "x.txt"), "theirs");
        bool laidOut = DirectoryBackupService.FindBackupContent(dst, new[] { other }) is not null;

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
        {
            set.SourceSelections =
            [
                new SourceSelection
                {
                    Path = src, IsDirectory = true, IsSelected = null, AutoIncludeNewSubdirectories = true,
                    Children =
                    [
                        new SourceSelection { Path = mine, IsDirectory = true, IsSelected = true },
                        new SourceSelection { Path = other, IsDirectory = true, IsSelected = false },
                    ],
                },
            ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = dst;
        });
        rig.Destination.Connected = true;

        await rig.Worker.StartAsync(CancellationToken.None);
        bool ran = await WaitUntil(() => rig.Log.Count("Starting backup for") > 0, TimeSpan.FromSeconds(30));
        int refused = rig.Log.Count("Not backing up");
        Check(laidOut, "C7 the other set's copy sits where the untracked probe would look for it");
        Check(ran && refused == 0,
            $"C7 a destination shared with another set is not taken for this set's own untracked backup (ran {ran}, refused {refused})");
        await rig.DisposeAsync();
    }

    // C6 --------------------------------------------------------------------
    {
        string dir = Path.Combine(root, "c6");
        string srcA = Path.Combine(dir, "a"), srcB = Path.Combine(dir, "b");
        foreach (var d in new[] { srcA, srcB, srcA + "-dst", srcB + "-dst" })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(srcA, "one.txt"), "a");
        File.WriteAllText(Path.Combine(srcB, "one.txt"), "b");

        void Configure(BackupSet set, string name, string src)
        {
            set.Name = name;
            set.SourceSelections = [ new SourceSelection { Path = src, IsDirectory = true, IsSelected = true } ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = src + "-dst";
        }

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, s => Configure(s, "c6-a", srcA));
        await rig.AddSetAsync(stampedSetIds, s => Configure(s, "c6-b", srcB));
        rig.Destination.Connected = true;

        await rig.Worker.StartAsync(CancellationToken.None);
        bool settled = await WaitUntil(
            () => rig.Log.Count("Backup completed for \"c6-a\"") + rig.Log.Count("Nothing to back up for \"c6-a\"") > 0
                  && rig.Log.Count("Backup completed for \"c6-b\"") + rig.Log.Count("Nothing to back up for \"c6-b\"") > 0,
            TimeSpan.FromSeconds(60));
        await Task.Delay(TimeSpan.FromSeconds(6));

        int watchA0 = rig.Log.Count("Watching \"c6-a\" via file-system watcher fallback");
        int watchB0 = rig.Log.Count("Watching \"c6-b\" via file-system watcher fallback");
        if (watchA0 == 0 || watchB0 == 0)
        {
            Console.WriteLine("     C6 skipped: this volume has a readable change journal (run unelevated to use the fallback)");
        }
        else
        {
            Directory.Delete(srcA, recursive: true);
            await Task.Delay(TimeSpan.FromSeconds(8));
            int watchB1 = rig.Log.Count("Watching \"c6-b\" via file-system watcher fallback");
            Check(settled && watchB1 == watchB0,
                $"C6 a fallback root that goes costs the other set no reconcile ({watchB1 - watchB0} flagged)");

            // Back again: its set reconciles (its watcher's error, when the folder
            // went, already flagged that), and it is watched once more, so a file
            // written after the reconcile is backed up by the continuous path.
            Directory.CreateDirectory(srcA);
            File.WriteAllText(Path.Combine(srcA, "back.txt"), "again");
            bool reconciled = await WaitUntil(() => FindFile(srcA + "-dst", "back.txt") is not null, TimeSpan.FromSeconds(60));
            File.WriteAllText(Path.Combine(srcA, "later.txt"), "later");
            bool watched = await WaitUntil(() => FindFile(srcA + "-dst", "later.txt") is not null, TimeSpan.FromSeconds(30));
            int watchB2 = rig.Log.Count("Watching \"c6-b\" via file-system watcher fallback");
            Check(reconciled && watched && watchB2 == watchB0,
                $"C6 when it returns it is reconciled and watched again, and the other set is left alone "
                + $"(reconciled {reconciled}, watched {watched}, other set flagged {watchB2 - watchB0} times)");
        }

        await rig.DisposeAsync();
    }

#if !OLD_CODE
    // C8 --------------------------------------------------------------------
    // While the app works on a set (ICatalogRepository.HoldSetInApp) the Worker
    // gives way: it starts no run on the set, stops one that is going, keeps the
    // work, and carries on after, instead of waiting for the set's write lock,
    // which the app does not give up: a wait as long as the app's backup, with
    // every other set's backup held behind it.
    {
        string dir = Path.Combine(root, "c8");
        string src = Path.Combine(dir, "src");
        string dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        const int fileCount = 4000;
        for (int i = 0; i < fileCount; i++)
            File.WriteAllText(Path.Combine(src, $"f{i:D5}.txt"), new string('x', 512) + i);
        int Copied() => Directory.EnumerateFiles(dst, "*.txt", SearchOption.AllDirectories).Count();

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
        {
            set.SourceSelections = [ new SourceSelection { Path = src, IsDirectory = true, IsSelected = true } ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = dst;
        });
        rig.Destination.Connected = true;

        // Held from the start: nothing runs, and nothing is even announced.
        int announced = 0;
        var markerWatch = new FileSystemWatcher(rig.Repo.DataDirectory, "*.backingup") { IncludeSubdirectories = true };
        markerWatch.Created += (_, _) => Interlocked.Increment(ref announced);
        markerWatch.EnableRaisingEvents = true;
        var hold = rig.Repo.HoldSetInApp(rig.SetId);
        await rig.Worker.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(12));
        markerWatch.Dispose();
        int started = rig.Log.Count("Starting backup for");
        int waits = rig.Log.Count("wait: Lithic Backup is working on it");
        Check(started == 0 && Copied() == 0,
            $"C8 while the app holds the set, the Worker starts no backup of it ({started} started, {Copied()} copied)");
        Check(waits == 1, $"C8 the wait is logged once ({waits} times)");
        Check(announced == 0, $"C8 and announces no run it then drops ({announced} run markers created)");

        // Let go: the full backup runs. Take the set again while it is copying.
        hold.Dispose();
        bool copying = await WaitUntil(() => Copied() >= 200, TimeSpan.FromSeconds(90));
        hold = rig.Repo.HoldSetInApp(rig.SetId);
        bool paused = await WaitUntil(() => rig.Log.Count("paused: Lithic Backup is working on it") > 0, TimeSpan.FromSeconds(10));
        int copiedAtPause = Copied();
        await Task.Delay(TimeSpan.FromSeconds(4));
        int copiedLater = Copied();
        Check(copying && paused, $"C8 a backup that is going stops within seconds when the app takes the set (copying {copying}, paused {paused})");
        Check(copiedLater == copiedAtPause && copiedAtPause < fileCount,
            $"C8 and copies nothing more meanwhile ({copiedAtPause}, then {copiedLater}, of {fileCount})");
        Check(rig.Log.Count("Backup failed for") == 0, "C8 a stop for the app is not counted as a failure");

        // Let go again: it carries on and finishes.
        hold.Dispose();
        bool finished = await WaitUntil(() => Copied() == fileCount, TimeSpan.FromSeconds(180));
        Check(finished, $"C8 when the app lets go, the backup carries on and finishes ({Copied()} of {fileCount})");
        Check(rig.Log.Count("is done with") >= 1, "C8 and the Worker says the app is done with the set");
        await rig.DisposeAsync();
    }

    // C9 --------------------------------------------------------------------
    // A resolver's change (a volume identity, a drive letter) is saved on the
    // catalog's current copy of the set, not the Worker's, which can be hours
    // old. Written back whole, the Worker's copy undid what had been saved since.
    {
        string dir = Path.Combine(root, "c9");
        string src = Path.Combine(dir, "src");
        string dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "one.txt"), "one");

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
        {
            set.SourceSelections = [ new SourceSelection { Path = src, IsDirectory = true, IsSelected = true } ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = dst;
        });
        rig.Destination.Connected = true;
        bool edited = false;
        rig.Destination.Mutate = options =>
        {
            if (options.DestinationVolumeId == "moved")
                return false;
            if (!edited)
            {
                // The user saves an edit after the Worker read the set.
                edited = true;
                var saved = rig.Repo.GetBackupSetAsync(rig.SetId).GetAwaiter().GetResult()!;
                saved.JobOptions!.ExcludedExtensions.Add("*.edited-meanwhile");
                rig.Repo.UpdateBackupSetAsync(saved).GetAwaiter().GetResult();
            }
            options.DestinationVolumeId = "moved";
            return true;
        };

        await rig.Worker.StartAsync(CancellationToken.None);
        bool ran = await WaitUntil(() => edited && rig.Log.Count("Starting backup for") > 0, TimeSpan.FromSeconds(60));
        await rig.DisposeAsync();
        using var check = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
        var after = (await check.GetBackupSetAsync(rig.SetId))!.JobOptions!;
        Check(ran && after.DestinationVolumeId == "moved", $"C9 the resolver's change is saved (\"{after.DestinationVolumeId}\")");
        Check(after.ExcludedExtensions.Contains("*.edited-meanwhile"),
            "C9 and an edit saved after the Worker read the set survives it");
    }

    // C10 -------------------------------------------------------------------
    // A set with no history whose destination already holds its backup is
    // refused - and held, so its row in the app says its automated backups are
    // paused. A log line alone left it unbacked-up with nothing on screen.
    {
        string dir = Path.Combine(root, "c10");
        string src = Path.Combine(dir, "src");
        string dst = Path.Combine(dir, "dst");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "mine");
        string drive = char.ToUpperInvariant(src[0]).ToString();
        string copy = Path.Combine(dst, drive, src.Substring(3));
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "a.txt"), "an older backup of it");

        var rig = await Rig.CreateAsync(Path.Combine(dir, "catalog.db"), stampedSetIds, set =>
        {
            set.SourceSelections = [ new SourceSelection { Path = src, IsDirectory = true, IsSelected = true } ];
            set.SourceRoots = [src];
            set.JobOptions!.TargetDirectory = dst;
        });
        rig.Destination.Connected = true;
        await rig.Worker.StartAsync(CancellationToken.None);
        bool refused = await WaitUntil(() => rig.Log.Count("Not backing up") > 0, TimeSpan.FromSeconds(60));
        bool held = await WaitUntil(
            () => rig.Repo.GetBackupSetAsync(rig.SetId).GetAwaiter().GetResult()!.JobOptions!.HoldAutomatedBackups,
            TimeSpan.FromSeconds(10));
        Check(refused && rig.Log.Count("Starting backup for") == 0, "C10 a set with no history over a destination holding its backup is refused");
        Check(held, "C10 and held, so the app shows its automated backups as paused");
        Check(File.ReadAllText(Path.Combine(copy, "a.txt")) == "an older backup of it", "C10 nothing was copied over the backup");
        await rig.DisposeAsync();
    }
#endif
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

/// <summary>Random selection trees over a small namespace, and every path in it.</summary>
static class RandomTrees
{
    private static readonly string[] Names = ["a", "debug", "b"];
    private const string Root = @"R:\r";

    /// <summary>A random tree rooted at R:\r: any state, any auto-include flag,
    /// files, an occasional expanded folder, and now and then a file listed two
    /// levels down without its folder.</summary>
    public static List<SourceSelection> Generate(Random rng) => [Dir(rng, Root, 0)];

    private static SourceSelection Dir(Random rng, string path, int depth)
    {
        var node = new SourceSelection
        {
            Path = path, IsDirectory = true,
            IsSelected = rng.Next(3) switch { 0 => true, 1 => false, _ => null },
            AutoIncludeNewSubdirectories = rng.Next(2) == 0,
            IsExpanded = rng.Next(8) == 0,
        };
        if (depth < 3)
        {
            foreach (var n in Names)
                if (rng.Next(2) == 0) node.Children.Add(Dir(rng, $@"{path}\{n}", depth + 1));
            if (rng.Next(3) == 0)
                node.Children.Add(new SourceSelection { Path = $@"{path}\f.txt", IsDirectory = false, IsSelected = rng.Next(2) == 0 });
            if (rng.Next(6) == 0)
                node.Children.Add(new SourceSelection { Path = $@"{path}\debug\g.txt", IsDirectory = false, IsSelected = true });
        }
        return node;
    }

    /// <summary>Every folder to depth 4 under R:\r, and two files in each.</summary>
    public static List<string> Probes()
    {
        var probes = new List<string>();
        void Add(string dir, int depth)
        {
            probes.Add(dir);
            probes.Add($@"{dir}\f.txt");
            probes.Add($@"{dir}\g.txt");
            if (depth == 4) return;
            foreach (var n in Names) Add($@"{dir}\{n}", depth + 1);
        }
        Add(Root, 0);
        return probes;
    }

    public static int UncoveredByWatchRoots(List<SourceSelection> tree, List<string> probes)
        => UncoveredByWatchRoots(tree, probes, out _);

    /// <summary>How many probes the tree includes that lie under none of its
    /// watch roots (<see cref="SourceSelection.CollectSelectedRoots"/>).</summary>
    public static int UncoveredByWatchRoots(List<SourceSelection> tree, List<string> probes, out string? example)
    {
        var roots = SourceSelection.CollectSelectedRoots(tree);
        int uncovered = 0;
        example = null;
        foreach (var p in probes)
        {
            if (!SourceSelection.IsPathIncluded(tree, p))
                continue;
            bool covered = roots.Any(r => p.Equals(r, StringComparison.OrdinalIgnoreCase)
                                          || p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!covered)
            {
                uncovered++;
                example ??= p;
            }
        }
        return uncovered;
    }
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

        var set = Template();
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

    /// <summary>A continuous set with a 5 s poll, before the test configures it.</summary>
    private static BackupSet Template() => new()
    {
        Name = "idle-test",
        CreatedUtc = DateTime.UtcNow,
        DefaultMediaType = MediaType.Directory,
        JobOptions = new JobOptions
        {
            Schedule = new BackupSchedule { Enabled = true, Mode = ScheduleMode.Continuous, PollIntervalSeconds = 5 },
        },
    };

    /// <summary>Add another set to the catalog; call before starting the Worker.</summary>
    public async Task AddSetAsync(List<int> stampedSetIds, Action<BackupSet> configure)
    {
        var set = Template();
        configure(set);
        set = await Repo.CreateBackupSetAsync(set);
        stampedSetIds.Add(set.Id);
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

/// <summary>Every set's destination, connected or not as the test says; when
/// connected, each set's own target directory.</summary>
sealed class SwitchableDestination : IDestinationResolver
{
    private int _calls;
    public volatile bool Connected;
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Applied to the options before answering, as a volume-identity backfill would be; true when it changed them.</summary>
    public Func<JobOptions, bool>? Mutate;

    public DestinationResolution Resolve(JobOptions options)
    {
        Interlocked.Increment(ref _calls);
        bool changed = Mutate?.Invoke(options) ?? false;
        return Connected
            ? new DestinationResolution(options.TargetDirectory, IsConnected: true, LetterChanged: false, options.TargetDirectory, MetadataChanged: changed)
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
