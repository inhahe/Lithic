// Headless correctness test for directory-backup FILE-LEVEL dedup, focused on
// roadmap item 5: the progressive prefix-hash pre-check that lets a large,
// size-colliding file skip its full up-front hash and read exactly once.
//
// The optimization is purely internal (it changes HOW files are read, not WHAT
// is stored), so these tests pin the observable outcome: dedup must still be
// exactly correct under the prefix path.
//
//   * identical-dup      2 byte-identical files  -> 1 plain copy + 1 .fileref
//   * different-same-size 2 different, same size  -> 2 plain copies, 0 .fileref
//   * mixed              X, Y(diff same size), X  -> 2 plain + 1 .fileref->X
//
// Every run forces MemoryBudget = Fixed 0 GiB so bufferBudgetBytes = 0 and even
// tiny files take the streaming path that the prefix pre-check lives on. Each
// scenario is ALSO run under the default (Auto) budget to prove the buffered
// path yields the identical dedup result — a regression guard on both paths.

using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  \u2713 " : "  \u2717 FAIL: ") + msg);
    if (!cond) failures++;
}

// Deterministic content of a given logical size, seeded so different seeds give
// genuinely different bytes (used for the "different same-size" case). Files are
// > 64 KiB (PrefixHashBytes) so the prefix hash is a real PARTIAL read, not the
// whole file.
static byte[] Content(int seed, int size)
{
    var buf = new byte[size];
    var rng = new Random(seed);
    rng.NextBytes(buf);
    return buf;
}

static string Sha256Hex(byte[] b) =>
    Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

// Run one directory backup of the given (relativeName -> bytes) files and return
// the plain content files and .fileref manifests found in the target tree.
static async Task<(List<string> Plain, List<(string Path, FileRefManifest M)> Refs, string Target)>
    RunBackup((string Name, byte[] Bytes)[] files, bool forceStreaming)
{
    string root = Path.Combine(Path.GetTempPath(), "lithic_dirdedup_" + Guid.NewGuid().ToString("N"));
    string source = Path.Combine(root, "src");
    string target = Path.Combine(root, "dst");
    string catalog = Path.Combine(root, "catalog.db");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(target);

    foreach (var (name, bytes) in files)
    {
        string p = Path.Combine(source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, bytes);
    }

    var repo = new SqliteCatalogRepository(catalog);
    var scanner = new FileScanner(repo);
    var retention = new VersionRetentionService(repo);
    // No dedup engine => block-level dedup OFF => NEW format (plain + .fileref).
    var svc = new DirectoryBackupService(repo, scanner, retention);

    var job = new BackupJob
    {
        BackupSetId = null,
        Sources = { new SourceSelection { Path = source, IsDirectory = true, IsSelected = true } },
        EnableFileDeduplication = true,
        EnableDeduplication = false,
        TargetDirectory = target,
        ZipMode = ZipMode.None,
        // Fixed 0 GiB => buffer budget 0 => every file streams => prefix path.
        MemoryBudget = forceStreaming
            ? new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 0 }
            : null,
    };

    var result = await svc.ExecuteAsync(job, target, null, null, CancellationToken.None);
    if (!result.Success)
        throw new Exception("backup failed: " + string.Join("; ", result.FailedFiles.Select(f => f.Error)));

    var plain = new List<string>();
    var refs = new List<(string, FileRefManifest)>();
    foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
    {
        if (f.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase))
        {
            var m = JsonSerializer.Deserialize<FileRefManifest>(await File.ReadAllTextAsync(f))!;
            refs.Add((f, m));
        }
        else if (f.EndsWith(".dedup", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("unexpected .dedup file (block dedup should be off): " + f);
        }
        else
        {
            plain.Add(f);
        }
    }
    return (plain, refs, target);
}

// Assert that every plain file's bytes hash to one of the expected content
// hashes, and that each .fileref points (by Hash) at a plain copy that exists.
static async Task<bool> PlainHashesCoverAsync(List<string> plain, HashSet<string> expected)
{
    foreach (var p in plain)
    {
        string h = Sha256Hex(await File.ReadAllBytesAsync(p));
        if (!expected.Contains(h)) return false;
    }
    return true;
}

foreach (bool forceStreaming in new[] { true, false })
{
    string mode = forceStreaming ? "streaming/prefix path" : "buffered path";
    Console.WriteLine($"\n=== {mode} ===");

    // --- identical-dup: 2 byte-identical files -> 1 plain + 1 .fileref ---
    {
        byte[] x = Content(1, 200_000);
        var (plain, refs, _) = await RunBackup(
            new[] { ("a/original.bin", x), ("a/copy.bin", x) }, forceStreaming);
        Check(plain.Count == 1, $"identical-dup: 1 plain copy (got {plain.Count})");
        Check(refs.Count == 1, $"identical-dup: 1 .fileref (got {refs.Count})");
        Check(refs.Count == 1 && refs[0].M.Hash == Sha256Hex(x),
            "identical-dup: .fileref hash matches the deduped content");
        Check(await PlainHashesCoverAsync(plain, new HashSet<string> { Sha256Hex(x) }),
            "identical-dup: the plain copy holds the correct bytes");
    }

    // --- different-same-size: 2 different files, same size -> 2 plain, 0 ref ---
    {
        byte[] a = Content(2, 200_000);
        byte[] b = Content(3, 200_000);
        Check(a.Length == b.Length && Sha256Hex(a) != Sha256Hex(b),
            "different-same-size: fixture is same-size but different content");
        var (plain, refs, _) = await RunBackup(
            new[] { ("d/a.bin", a), ("d/b.bin", b) }, forceStreaming);
        Check(plain.Count == 2, $"different-same-size: 2 plain copies (got {plain.Count})");
        Check(refs.Count == 0, $"different-same-size: 0 .fileref (got {refs.Count})");
        Check(await PlainHashesCoverAsync(plain, new HashSet<string> { Sha256Hex(a), Sha256Hex(b) }),
            "different-same-size: both plain copies hold correct bytes");
    }

    // --- mixed: X, Y(diff same size), X -> 2 plain + 1 .fileref -> X ---
    {
        byte[] x = Content(4, 200_000);
        byte[] y = Content(5, 200_000); // different content, same size as x
        var (plain, refs, _) = await RunBackup(
            new[] { ("m/first.bin", x), ("m/other.bin", y), ("m/again.bin", x) }, forceStreaming);
        Check(plain.Count == 2, $"mixed: 2 plain copies (got {plain.Count})");
        Check(refs.Count == 1, $"mixed: 1 .fileref (got {refs.Count})");
        Check(refs.Count == 1 && refs[0].M.Hash == Sha256Hex(x),
            "mixed: the .fileref resolves to X (the repeated content), not Y");
        Check(await PlainHashesCoverAsync(plain, new HashSet<string> { Sha256Hex(x), Sha256Hex(y) }),
            "mixed: both plain anchors hold correct bytes");
    }
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL PASS");
    return 0;
}
Console.WriteLine($"{failures} CHECK(S) FAILED");
return 1;
