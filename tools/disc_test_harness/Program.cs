// Headless disc backup/restore test harness.
//
// Exercises the full optical-disc pipeline (BackupOrchestrator + SimulatedDiscBurner
// + RestoreService) end to end, without real hardware or the WPF UI. Covers the
// happy paths (single-disc, multi-disc spanning, file-level dedup, zip-all, file
// splitting, disc integrity verify) AND every failure-injection knob the simulated
// burner exposes (no recorder, no media, per-file write failure, catastrophic
// mid-burn failure, post-burn verify failure, erase failure, metadata-only restore).
//
//   disc_test_harness            -> run the full matrix
//
// Exit code 0 = every case met its expectation; 1 = one or more cases failed.

using System.Security.Cryptography;
using LithicBackup.Core.Exceptions;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Burning;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

string root = Path.Combine(Path.GetTempPath(), "lithic-disc-harness");
if (Directory.Exists(root))
{
    try { Directory.Delete(root, true); } catch { /* best effort */ }
}
Directory.CreateDirectory(root);

var runner = new TestRunner(root);

// ------------------------------------------------------------------
// Happy paths
// ------------------------------------------------------------------

await runner.Run("happy-single-disc", async ws =>
{
    var srcs = ws.MakeTree(("docs/a.txt", 40_000), ("docs/b.bin", 120_000), ("readme.md", 2_000));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "backup should succeed");
    ws.Assert(result.DiscsWritten == 1, $"expected 1 disc, got {result.DiscsWritten}");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("happy-multi-disc-span", async ws =>
{
    // 5 files x ~80 KB with a 200 KB disc => spans 3+ discs.
    var srcs = ws.MakeTree(
        ("set/f1.bin", 80_000), ("set/f2.bin", 80_000), ("set/f3.bin", 80_000),
        ("set/f4.bin", 80_000), ("set/f5.bin", 80_000));
    var (burner, result) = await ws.Backup(srcs, capacityBytes: 200_000);
    ws.Assert(result.Success, "backup should succeed");
    ws.Assert(result.DiscsWritten >= 3, $"expected spanning >=3 discs, got {result.DiscsWritten}");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("happy-file-dedup", async ws =>
{
    // Two byte-identical files => second stored as a .fileref; restore must
    // resolve it back to the plain copy.
    byte[] dup = TestRunner.Gen(999, 60_000);
    var srcs = ws.MakeTreeBytes(("dup/original.bin", dup), ("dup/copy.bin", dup));
    srcs.AddRange(ws.MakeTree(("unique.bin", 30_000)));
    var (burner, result) = await ws.Backup(srcs, fileDedup: true);
    ws.Assert(result.Success, "backup should succeed");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("happy-zip-all", async ws =>
{
    var srcs = ws.MakeTree(("z/one.txt", 50_000), ("z/two.dat", 90_000));
    var (burner, result) = await ws.Backup(srcs, zipMode: ZipMode.All);
    ws.Assert(result.Success, "backup should succeed");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("happy-file-splitting", async ws =>
{
    // One file bigger than a single disc => split into chunks and reassembled on
    // restore. NOTE: today the chunks all land on ONE disc (see known-issues.md:
    // "Oversized file split into chunks does not span physical discs"); this case
    // verifies the split/reassemble round-trip is byte-correct, and additionally
    // flags the capacity overflow so the harness keeps the limitation visible.
    var srcs = ws.MakeTree(("big/huge.bin", 300_000), ("big/small.txt", 5_000));
    var (burner, result) = await ws.Backup(srcs, capacityBytes: 150_000, allowSplitting: true);
    ws.Assert(result.Success, "backup should succeed");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");

    // Diagnostic (non-fatal): report any disc whose staged bytes exceed capacity.
    long overflow = ws.MaxDiscOverflowBytes(burner, 150_000);
    if (overflow > 0)
        Console.WriteLine($"  [known-issue] a disc overflowed capacity by {overflow} bytes " +
            "(oversized-file chunks did not span discs)");
});

await runner.Run("verify-disc-integrity", async ws =>
{
    var srcs = ws.MakeTree(("v/a.bin", 70_000), ("v/b.bin", 40_000));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "backup should succeed");

    // Content-verify every disc in the set against the catalog.
    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Count >= 1, "expected at least one disc recorded");
    foreach (var disc in discs)
    {
        string discRoot = ws.DiscRootForLabel(burner, disc.Label);
        var restore = new RestoreService(ws.Catalog);
        var vr = await restore.VerifyDiscAsync(disc.Id, discRoot, verifyContents: true);
        ws.Assert(vr.Success, $"disc {disc.Label} failed integrity: {string.Join("; ", vr.Issues.Select(i => $"{i.SourcePath}:{i.Kind}"))}");
    }
});

// ------------------------------------------------------------------
// Failure injection
// ------------------------------------------------------------------

await runner.Run("fail-no-recorder", async ws =>
{
    var srcs = ws.MakeTree(("x.txt", 1_000));
    // No recorder => ExecuteAsync returns Success=false (does not throw).
    var (_, result) = await ws.Backup(srcs, configure: b => b.SimulateNoRecorder = true, expectThrow: false);
    ws.Assert(!result.Success, "expected Success=false when no recorder present");
});

await runner.Run("fail-no-media", async ws =>
{
    var srcs = ws.MakeTree(("x.txt", 1_000));
    // No media => GetMediaInfoAsync throws IOException during planning.
    var ex = await ws.ExpectThrow(() => ws.Backup(srcs,
        configure: b => b.SimulateNoMedia = true, useCapacityOverride: false));
    ws.Assert(ex is IOException, $"expected IOException, got {ex?.GetType().Name ?? "none"}");
});

await runner.Run("fail-per-file-write", async ws =>
{
    var srcs = ws.MakeTree(("a.bin", 20_000), ("b.bin", 20_000));
    // Every file fails to write => BurnAsync throws IOException.
    var ex = await ws.ExpectThrow(() => ws.Backup(srcs,
        configure: b => b.FileFailureProbability = 1.0));
    ws.Assert(ex is IOException, $"expected IOException, got {ex?.GetType().Name ?? "none"}");
});

await runner.Run("fail-catastrophic-mid-burn", async ws =>
{
    var srcs = ws.MakeTree(
        ("c/f1.bin", 30_000), ("c/f2.bin", 30_000), ("c/f3.bin", 30_000), ("c/f4.bin", 30_000));
    // Laser failure at 50% through the disc => IOException mid-burn.
    var ex = await ws.ExpectThrow(() => ws.Backup(srcs,
        configure: b => b.CatastrophicFailureAtPercent = 50));
    ws.Assert(ex is IOException, $"expected IOException, got {ex?.GetType().Name ?? "none"}");
});

await runner.Run("fail-post-burn-verify", async ws =>
{
    var srcs = ws.MakeTree(("a.bin", 40_000));
    // Data written, then post-burn read-back reports a mismatch => BurnException.
    var ex = await ws.ExpectThrow(() => ws.Backup(srcs,
        configure: b => b.SimulateVerifyFailure = true));
    ws.Assert(ex is BurnException, $"expected BurnException, got {ex?.GetType().Name ?? "none"}");
});

await runner.Run("fail-erase", async ws =>
{
    // EraseAsync is a direct burner call (not routed through the orchestrator).
    var burner = ws.NewBurner(b => b.SimulateEraseFail = true);
    var ex = await ws.ExpectThrow(async () =>
    {
        await burner.EraseAsync("SIM_RECORDER_0");
        return 0;
    });
    ws.Assert(ex is IOException, $"expected IOException, got {ex?.GetType().Name ?? "none"}");
});

await runner.Run("metadata-only-restore-cannot-reconstruct", async ws =>
{
    // StoreFileContents=false: the burn succeeds (structure + hashes recorded) but
    // the shelf holds tiny stubs, so restored bytes must NOT match the source.
    var srcs = ws.MakeTree(("m/a.bin", 50_000), ("m/b.bin", 50_000));
    var (burner, result) = await ws.Backup(srcs, configure: b => b.StoreFileContents = false);
    ws.Assert(result.Success, "metadata-only backup should still succeed");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == srcs.Count,
        $"expected all {srcs.Count} restores to mismatch (stubs), but {srcs.Count - r.Mismatches} matched");
});

return runner.Report();

// ======================================================================
// Test framework
// ======================================================================

sealed class TestRunner
{
    private readonly string _root;
    private readonly List<(string Name, bool Passed, string Detail)> _results = new();

    public TestRunner(string root) => _root = root;

    public async Task Run(string name, Func<Workspace, Task> body)
    {
        Console.WriteLine($"\n=== {name} ===");
        var ws = new Workspace(Path.Combine(_root, name));
        try
        {
            await body(ws);
            _results.Add((name, true, ws.FirstFailure ?? ""));
            if (ws.FirstFailure is null)
                Console.WriteLine($"  PASS");
            else
            {
                _results[^1] = (name, false, ws.FirstFailure);
                Console.WriteLine($"  FAIL: {ws.FirstFailure}");
            }
        }
        catch (Exception ex)
        {
            _results.Add((name, false, $"unexpected exception: {ex.GetType().Name}: {ex.Message}"));
            Console.WriteLine($"  FAIL: unexpected exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ws.Dispose();
        }
    }

    public int Report()
    {
        int passed = _results.Count(r => r.Passed);
        Console.WriteLine($"\n================ SUMMARY ================");
        foreach (var (name, ok, detail) in _results)
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(ok ? "" : $"  -- {detail}")}");
        Console.WriteLine($"----------------------------------------");
        Console.WriteLine($"  {passed}/{_results.Count} passed");
        return passed == _results.Count ? 0 : 1;
    }

    public static byte[] Gen(int seed, int len)
    {
        var r = new Random(seed);
        var b = new byte[len];
        r.NextBytes(b);
        return b;
    }
}

sealed class Workspace : IDisposable
{
    private readonly string _dir;
    private readonly string _sourceDir;
    private readonly string _shelfDir;
    private readonly string _restoreDir;
    private readonly string _catalogPath;
    private int _seed = 1;

    public SqliteCatalogRepository Catalog { get; }
    public int BackupSetId { get; private set; }
    public string? FirstFailure { get; private set; }

    public Workspace(string dir)
    {
        _dir = dir;
        _sourceDir = Path.Combine(dir, "source");
        _shelfDir = Path.Combine(dir, "shelf");
        _restoreDir = Path.Combine(dir, "restore");
        _catalogPath = Path.Combine(dir, "catalog.db");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_shelfDir);
        Directory.CreateDirectory(_restoreDir);
        Catalog = new SqliteCatalogRepository(_catalogPath);
    }

    public void Assert(bool condition, string message)
    {
        if (!condition && FirstFailure is null)
            FirstFailure = message;
    }

    // -- source tree -------------------------------------------------

    public List<string> MakeTree(params (string RelPath, int Size)[] files)
    {
        var made = new List<string>();
        foreach (var (rel, size) in files)
            made.Add(MakeFile(rel, TestRunner.Gen(_seed++, size)));
        return made;
    }

    public List<string> MakeTreeBytes(params (string RelPath, byte[] Content)[] files)
    {
        var made = new List<string>();
        foreach (var (rel, content) in files)
            made.Add(MakeFile(rel, content));
        return made;
    }

    private string MakeFile(string rel, byte[] content)
    {
        string full = Path.GetFullPath(Path.Combine(_sourceDir, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    // -- burner + pipeline wiring ------------------------------------

    public SimulatedDiscBurner NewBurner(Action<SimulatedDiscBurner>? configure = null)
    {
        var burner = new SimulatedDiscBurner
        {
            ShelfDirectory = _shelfDir,
            SpeedMultiplier = 10_000.0, // run fast; we don't care about timing here
            StoreFileContents = true,
        };
        configure?.Invoke(burner);
        return burner;
    }

    /// <summary>
    /// Plan + execute a backup. Returns the burner (needed for restore disc
    /// lookup) and the result. When <paramref name="expectThrow"/> is false the
    /// caller expects a non-throwing outcome (e.g. Success=false).
    /// </summary>
    public async Task<(SimulatedDiscBurner Burner, BackupResult Result)> Backup(
        List<string> sources,
        long? capacityBytes = 4_700_000_000L,
        bool useCapacityOverride = true,
        bool fileDedup = false,
        ZipMode zipMode = ZipMode.None,
        bool allowSplitting = false,
        Action<SimulatedDiscBurner>? configure = null,
        bool expectThrow = true)
    {
        var burner = NewBurner(configure);

        var set = await Catalog.CreateBackupSetAsync(new BackupSet
        {
            Name = "harness-set",
            SourceRoots = new List<string> { _sourceDir },
            CreatedUtc = DateTime.UtcNow,
        });
        BackupSetId = set.Id;

        var orchestrator = BuildOrchestrator(burner);

        var job = new BackupJob
        {
            BackupSetId = set.Id,
            Sources = sources.Select(s => new SourceSelection
            {
                Path = s,
                IsDirectory = false,
                IsSelected = true,
            }).ToList(),
            IncludeCatalogOnDisc = false,
            EnableFileDeduplication = fileDedup,
            EnableDeduplication = false,
            ZipMode = zipMode,
            AllowFileSplitting = allowSplitting,
            VerifyAfterBurn = true,
            CapacityOverrideBytes = useCapacityOverride ? capacityBytes : null,
        };

        var plan = await orchestrator.PlanAsync(job);
        Console.WriteLine($"  [plan] cap={job.CapacityOverrideBytes} allocs={plan.DiscAllocations.Count} " +
            $"totBytes={plan.TotalBytes} newFiles={plan.Diff.NewFiles.Count} " +
            $"allocSizes=[{string.Join(",", plan.DiscAllocations.Select(a => a.TotalBytes))}]");
        var result = await orchestrator.ExecuteAsync(plan, progress: null, onFailure: null,
            ct: CancellationToken.None);
        return (burner, result);
    }

    private BackupOrchestrator BuildOrchestrator(IDiscBurner burner)
    {
        var scanner = new FileScanner(Catalog);
        var packer = new BinPacker();
        var zipHandler = new ZipHandler();
        var fileSplitter = new FileSplitter();
        var sessionStrategy = new DiscSessionStrategy(burner, Catalog);
        return new BackupOrchestrator(Catalog, burner, scanner, packer, zipHandler,
            fileSplitter, sessionStrategy, fileSystemMonitor: null);
    }

    // -- restore + verify --------------------------------------------

    public string DiscRootForLabel(SimulatedDiscBurner burner, string label)
    {
        // Label "Disc-003" => disc number 3 => shelf disc-3 for the sole recorder.
        int n = int.Parse(new string(label.Where(char.IsDigit).ToArray()));
        return burner.GetDiscPath("SIM_RECORDER_0", n);
    }

    /// <summary>
    /// Largest amount by which any burned disc's actual on-shelf bytes exceed the
    /// given capacity (0 if every disc is within capacity). Used to surface the
    /// oversized-file-splitting overflow limitation.
    /// </summary>
    public long MaxDiscOverflowBytes(SimulatedDiscBurner burner, long capacity)
    {
        long worst = 0;
        foreach (var discPath in burner.GetAllDiscPaths())
        {
            long bytes = Directory.EnumerateFiles(discPath, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase))
                .Sum(f => new FileInfo(f).Length);
            worst = Math.Max(worst, bytes - capacity);
        }
        return worst;
    }

    public async Task<(int Restored, int Mismatches)> RestoreAndVerify(
        SimulatedDiscBurner burner, List<string> sources)
    {
        var restore = new RestoreService(Catalog)
        {
            DiscInsertCallback = label => Task.FromResult<string?>(DiscRootForLabel(burner, label)),
        };

        var restorable = await restore.GetRestorableFilesAsync(BackupSetId);

        // Route every drive letter seen in the set to our restore output dir.
        var driveDests = restorable
            .Select(rf => DriveKey(rf.Record.SourcePath))
            .Distinct()
            .ToDictionary(k => k, _ => _restoreDir, StringComparer.OrdinalIgnoreCase);

        var result = await restore.RestoreAsync(restorable, driveDests);

        int mismatches = 0;
        foreach (var src in sources)
        {
            string restored = Path.Combine(_restoreDir, RelToDriveRoot(src));
            if (!File.Exists(restored) || !HashEquals(src, restored))
                mismatches++;
        }
        return (result.FilesRestored, mismatches);
    }

    private static string DriveKey(string path) =>
        path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]).ToString() : "_";

    private static string RelToDriveRoot(string path)
    {
        string r = Path.GetPathRoot(path) ?? "";
        return path[r.Length..];
    }

    private static bool HashEquals(string a, string b)
    {
        using var sha = SHA256.Create();
        byte[] ha, hb;
        using (var sa = File.OpenRead(a)) ha = sha.ComputeHash(sa);
        using (var sb = File.OpenRead(b)) hb = sha.ComputeHash(sb);
        return ha.SequenceEqual(hb);
    }

    // -- exception expectations --------------------------------------

    public async Task<Exception?> ExpectThrow(Func<Task<object>> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    public async Task<Exception?> ExpectThrow(
        Func<Task<(SimulatedDiscBurner, BackupResult)>> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    public void Dispose() => Catalog.Dispose();
}
