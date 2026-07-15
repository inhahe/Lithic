// No-drift test for the dedup-aware "actual backup size" estimator (roadmap
// item 6). For each scenario it runs BOTH the real DirectoryBackupService AND
// DedupSizeEstimator over the same files/job, then asserts the estimate's
// StoredBytes equals the bytes the real backup physically wrote:
//   * file-level dedup  -> sum of plain file sizes in the target tree
//   * block-level dedup -> sum of plain file sizes + sum of _blocks/*.blk sizes
// (.fileref / .dedup manifests are tiny metadata the estimate intentionally
// ignores.) It also checks the estimate reflects real dedup (Stored < Raw) and
// that an all-redundant incremental run estimates to zero new bytes.

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.Deduplication;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  \u2713 " : "  \u2717 FAIL: ") + msg);
    if (!cond) failures++;
}

static byte[] Rand(int seed, int size)
{
    var b = new byte[size];
    new Random(seed).NextBytes(b);
    return b;
}

// Sum of bytes the real backup physically wrote: plain named copies plus any
// content-addressed blocks. Manifests (.fileref/.dedup) are ignored.
static long ActualStoredBytes(string target)
{
    long total = 0;
    foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
    {
        if (f.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase)) continue;
        if (f.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase)) continue;
        total += new FileInfo(f).Length; // plain files and _blocks/*.blk both count
    }
    return total;
}

// Run a real directory backup + the estimator over the same inputs and return
// both the estimate and the actual on-disk stored size.
static async Task<(DedupSizeEstimate Est, long Actual, long Raw)> RunAsync(
    (string Name, byte[] Bytes)[] files,
    bool fileDedup,
    bool blockDedup,
    int? reuseSetId = null,
    string? reuseTarget = null,
    string? reuseCatalog = null)
{
    string root = Path.Combine(Path.GetTempPath(), "lithic_est_" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "src");
    string target = reuseTarget ?? Path.Combine(root, "dst");
    string catalogPath = reuseCatalog ?? Path.Combine(root, "catalog.db");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(target);

    foreach (var (name, bytes) in files)
    {
        string p = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, bytes);
    }

    var repo = new SqliteCatalogRepository(catalogPath);
    var scanner = new FileScanner(repo);
    var retention = new VersionRetentionService(repo);
    IDeduplicationEngine? engine = blockDedup ? new BlockDeduplicationEngine() : null;

    var sources = new List<SourceSelection>
    {
        new() { Path = source, IsDirectory = true, IsSelected = true },
    };
    var scanned = await scanner.ScanAsync(sources, null, CancellationToken.None, null);

    var job = new BackupJob
    {
        BackupSetId = reuseSetId,
        Sources = sources,
        EnableFileDeduplication = fileDedup,
        EnableDeduplication = blockDedup,
        TargetDirectory = target,
        ZipMode = ZipMode.None,
    };

    // Estimate BEFORE running the backup (so the block store reflects only
    // pre-existing content, exactly like a pre-backup estimate would).
    var estimator = new DedupSizeEstimator(repo, engine, hashCache: null);
    var est = await estimator.EstimateAsync(job, scanned, target, null, CancellationToken.None);

    // Now run the real backup and measure what it wrote.
    var svc = new DirectoryBackupService(repo, scanner, retention, engine);
    var result = await svc.ExecuteAsync(job, target, null, null, CancellationToken.None);
    if (!result.Success)
        throw new Exception("backup failed: " + string.Join("; ", result.FailedFiles.Select(f => f.Error)));

    long raw = scanned.Sum(s => s.SizeBytes);
    return (est, ActualStoredBytes(target), raw);
}

// 64 KiB building blocks for the block-dedup scenarios.
byte[] P = Rand(10, 64 * 1024);
byte[] Q = Rand(11, 64 * 1024);
byte[] R = Rand(12, 64 * 1024);
byte[] tail = Rand(13, 10 * 1024);
static byte[] Cat(params byte[][] parts)
{
    var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p);
    return ms.ToArray();
}

Console.WriteLine("=== no dedup: stored == raw ===");
{
    var (est, actual, raw) = await RunAsync(
        new[] { ("a.bin", Rand(1, 100_000)), ("b.bin", Rand(2, 100_000)) },
        fileDedup: false, blockDedup: false);
    Check(est.StoredBytes == raw, $"estimate stored ({est.StoredBytes}) == raw ({raw})");
    Check(est.StoredBytes == actual, $"estimate ({est.StoredBytes}) == actual written ({actual})");
    Check(est.BytesRead == 0, "no dedup reads nothing");
}

Console.WriteLine("\n=== file-level dedup: dup + unique + same-size-different ===");
{
    byte[] x = Rand(3, 200_000);
    byte[] u = Rand(4, 150_000);
    byte[] a = Rand(5, 100_000);
    byte[] b = Rand(6, 100_000); // same size as a, different content
    var (est, actual, raw) = await RunAsync(
        new[] { ("x1.bin", x), ("x2.bin", x), ("u.bin", u), ("a.bin", a), ("b.bin", b) },
        fileDedup: true, blockDedup: false);
    long expected = 200_000 + 150_000 + 100_000 + 100_000; // x once, u, a, b
    Check(est.StoredBytes == expected, $"estimate stored ({est.StoredBytes}) == {expected}");
    Check(est.StoredBytes == actual, $"estimate ({est.StoredBytes}) == actual written ({actual})");
    Check(est.StoredBytes < raw, $"dedup reduces vs raw ({est.StoredBytes} < {raw})");
    Check(!est.BlockLevel, "file-level path reported (not block level)");
}

Console.WriteLine("\n=== block-level dedup: files share a block ===");
{
    byte[] fileA = Cat(P, Q, tail); // P, Q, tail
    byte[] fileB = Cat(P, R);       // P (shared with A), R
    var (est, actual, raw) = await RunAsync(
        new[] { ("A.bin", fileA), ("B.bin", fileB) },
        fileDedup: false, blockDedup: true);
    Check(est.BlockLevel, "block-level path reported");
    Check(est.StoredBytes == actual, $"estimate ({est.StoredBytes}) == actual written ({actual})");
    Check(est.StoredBytes < raw, $"block dedup reduces vs raw ({est.StoredBytes} < {raw})");
    Check(est.FilesFullyRead == 2, $"block estimate read every file (got {est.FilesFullyRead})");
}

Console.WriteLine("\n=== incremental: re-estimating an all-redundant run stores 0 ===");
{
    // First back up two files into a real set, then estimate a second run over
    // the identical sources against that set — everything already has a plain
    // copy, so the next run writes nothing.
    string root = Path.Combine(Path.GetTempPath(), "lithic_est_inc_" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "src");
    string target = Path.Combine(root, "dst");
    string catalogPath = Path.Combine(root, "catalog.db");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(target);

    byte[] f1 = Rand(21, 120_000);
    byte[] f2 = Rand(22, 90_000);
    await File.WriteAllBytesAsync(Path.Combine(source, "f1.bin"), f1);
    await File.WriteAllBytesAsync(Path.Combine(source, "f2.bin"), f2);

    var repo = new SqliteCatalogRepository(catalogPath);
    var scanner = new FileScanner(repo);
    var retention = new VersionRetentionService(repo);
    var set = await repo.CreateBackupSetAsync(new BackupSet
    {
        Name = "inc-set",
        SourceRoots = new List<string> { source },
        CreatedUtc = DateTime.UtcNow,
    });

    var sources = new List<SourceSelection>
    {
        new() { Path = source, IsDirectory = true, IsSelected = true },
    };
    var job = new BackupJob
    {
        BackupSetId = set.Id,
        Sources = sources,
        EnableFileDeduplication = true,
        EnableDeduplication = false,
        TargetDirectory = target,
        ZipMode = ZipMode.None,
    };

    var svc = new DirectoryBackupService(repo, scanner, retention);
    var r1 = await svc.ExecuteAsync(job, target, null, null, CancellationToken.None);
    if (!r1.Success) throw new Exception("first backup failed");

    var scanned2 = await scanner.ScanAsync(sources, null, CancellationToken.None, null);
    var estimator = new DedupSizeEstimator(repo, null, null);
    var est = await estimator.EstimateAsync(job, scanned2, target, null, CancellationToken.None);
    Check(est.StoredBytes == 0, $"all-redundant re-run estimates 0 new bytes (got {est.StoredBytes})");
    Check(est.RawBytes == f1.Length + f2.Length, "raw still reflects full source");
}

Console.WriteLine();
if (failures == 0) { Console.WriteLine("ALL PASS"); return 0; }
Console.WriteLine($"{failures} CHECK(S) FAILED");
return 1;
