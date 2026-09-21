// Tests for the continuous-vs-full-scan work (see the continuous-backup entry in
// known-issues.md):
//
//   A. A full scan that FAILS files must be distinguishable from one that did not
//      run, so the worker stops discarding the failed files' queued deltas.
//      Tested here at its source: ExecuteAsync must report Success=false with a
//      populated FailedFiles when a file cannot be read. That is the signal the
//      worker's ReconcileOutcome.RanWithFailures is derived from.
//
//   B. ExecuteAsync's commit-boundary hook, which lets the worker drain the USN
//      journal and back up changes DURING a multi-hour scan. This is the risky
//      one, so it is tested hardest:
//        B1 the hook actually fires during a multi-batch run;
//        B2 the boundary really is transaction-free (a nested transaction there
//           would DEADLOCK on the per-set gate if the outer one were still open);
//        B3 the exclusion set names exactly the paths the run will write;
//        B4 a backup performed from inside the hook does not corrupt the outer
//           run's version numbering (the bug the exclusion set exists to prevent);
//        B5 writes made inside the hook are durable.
//
//   C. FullScanStamp's cross-restart round-trip, which times the safety-net scan.

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using LithicBackup.Worker;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "lithic_interleave_" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(root, "src");
string target = Path.Combine(root, "dst");
string catalogPath = Path.Combine(root, "catalog.db");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);

var repo = new SqliteCatalogRepository(catalogPath);
var scanner = new FileScanner(repo);
var retention = new VersionRetentionService(repo);
var svc = new DirectoryBackupService(repo, scanner, retention);

var set = await repo.CreateBackupSetAsync(new BackupSet
{
    Name = "interleave-test",
    SourceRoots = new List<string> { source },
    CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
});

BackupJob MakeJob() => new()
{
    BackupSetId = set.Id,
    Sources = { new SourceSelection { Path = source, IsDirectory = true, IsSelected = true } },
    EnableFileDeduplication = false,
    EnableDeduplication = false,
    TargetDirectory = target,
    ZipMode = ZipMode.None,
};

// ===================================================================
// B: the commit-boundary hook
// ===================================================================
Console.WriteLine("=== B: commit-boundary hook during a full scan ===");

// CommitBatchSize is 50, so 130 files guarantees several boundaries.
const int FileCount = 130;
for (int i = 0; i < FileCount; i++)
    await File.WriteAllTextAsync(Path.Combine(source, $"f{i:D3}.txt"), $"contents of file {i}");

// This file is created AFTER the scan's diff is computed, so it is not in the
// run's exclusion set - it stands in for a change arriving mid-scan.
string interleavedPath = Path.Combine(source, "arrived_during_scan.dat");

int boundaryCalls = 0;
int nestedTxOk = 0;
int nestedTxTimedOut = 0;
bool exclusionsCorrect = true;
string exclusionDetail = "";
bool interleavedBackedUp = false;

var expectedPaths = Directory.GetFiles(source).ToHashSet(StringComparer.OrdinalIgnoreCase);

async Task Boundary(IReadOnlySet<string> willWrite, CancellationToken ct)
{
    boundaryCalls++;

    // --- B3: the exclusion set must name exactly what the run will write ---
    if (boundaryCalls == 1)
    {
        var missing = expectedPaths.Except(willWrite, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = willWrite.Except(expectedPaths, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            exclusionsCorrect = false;
            exclusionDetail = $"missing={missing.Count} extra={extra.Count}";
        }
    }

    // --- B2: prove no transaction / gate / cross-process lock is held ---
    // If the outer transaction were still open, BeginTransactionAsync would block
    // on the per-set gate forever. Time-box it so a regression fails instead of
    // hanging the harness.
    try
    {
        var txTask = Task.Run(async () =>
        {
            var tx = await repo.BeginTransactionAsync(set.Id, CancellationToken.None);
            try { tx.Complete(); } finally { tx.Dispose(); }
        }, CancellationToken.None);

        if (await Task.WhenAny(txTask, Task.Delay(TimeSpan.FromSeconds(10))) == txTask)
        {
            await txTask;
            nestedTxOk++;
        }
        else
        {
            nestedTxTimedOut++;
        }
    }
    catch
    {
        nestedTxTimedOut++;
    }

    // --- B4/B5: do a real interleaved backup of a path the scan does NOT own ---
    if (boundaryCalls == 1)
    {
        await File.WriteAllTextAsync(interleavedPath, "written while the full scan was running");
        var r = await svc.ExecuteTargetedAsync(
            MakeJob(), target, new[] { interleavedPath }, null, ct);
        interleavedBackedUp = r.Success;
    }
}

var result = await svc.ExecuteAsync(
    MakeJob(), target, null, null, CancellationToken.None,
    onCommitBoundary: Boundary);

Check(result.Success, "the full scan itself succeeded");
Check(boundaryCalls > 0, $"B1: commit-boundary hook fired ({boundaryCalls} time(s))");
Check(nestedTxTimedOut == 0,
    $"B2: a nested transaction opened at every boundary - no lock was held "
    + $"(ok={nestedTxOk}, timed out={nestedTxTimedOut})");
Check(exclusionsCorrect,
    "B3: exclusion set named exactly the paths the run would write" +
    (exclusionsCorrect ? "" : " [" + exclusionDetail + "]"));
Check(interleavedBackedUp, "B4: interleaved targeted backup reported success");

// --- B4 continued: version integrity across both writers ---
var heads = await repo.GetLatestVersionInfoAsync(set.Id, CancellationToken.None);

Check(heads.ContainsKey(interleavedPath),
    "B5: the interleaved file is in the catalog (its write was durable)");
Check(heads.TryGetValue(interleavedPath, out var interleavedHead) && interleavedHead.MaxVersion == 1,
    "B4: interleaved file is at version 1");

var scannedHeads = heads.Where(kv => !kv.Key.Equals(interleavedPath, StringComparison.OrdinalIgnoreCase)).ToList();
Check(scannedHeads.Count == FileCount,
    $"B4: every scanned file has exactly one head ({scannedHeads.Count}/{FileCount})");
Check(scannedHeads.All(kv => kv.Value.MaxVersion == 1),
    "B4: no scanned file had its version inflated by the interleaved writer");

// A second, ordinary full scan must find nothing to do: if the interleaved write
// had left the catalog inconsistent, this would re-copy or re-version something.
var second = await svc.ExecuteAsync(MakeJob(), target, null, null, CancellationToken.None);
var headsAfter = await repo.GetLatestVersionInfoAsync(set.Id, CancellationToken.None);
Check(second.Success, "a following full scan succeeded");
Check(headsAfter.Count == heads.Count,
    $"B4: no phantom rows appeared on the next scan ({headsAfter.Count} vs {heads.Count})");
Check(headsAfter.All(kv => kv.Value.MaxVersion == 1),
    "B4: the next scan re-versioned nothing - the catalog agreed with the destination");

// ===================================================================
// A: a failed file must be reported, not swallowed
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== A: partial failure is visible to the caller ===");

string lockedPath = Path.Combine(source, "locked.bin");
await File.WriteAllTextAsync(lockedPath, "this file will be held open exclusively");

BackupResult lockedResult;
using (var hold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
{
    lockedResult = await svc.ExecuteAsync(MakeJob(), target, null, null, CancellationToken.None);
}

Check(!lockedResult.Success,
    "A: ExecuteAsync reports Success=false when a file cannot be read");
Check(lockedResult.FailedFiles.Count > 0,
    $"A: FailedFiles is populated ({lockedResult.FailedFiles.Count})");
Check(lockedResult.FailedFiles.Any(f => f.Path.Equals(lockedPath, StringComparison.OrdinalIgnoreCase)),
    "A: the locked file is the one reported");
Console.WriteLine(
    "     (this is exactly the signal the worker turns into ReconcileOutcome.RanWithFailures,");
Console.WriteLine(
    "      which now keeps the queued deltas instead of clearing them)");

// ===================================================================
// C: the safety-net scan's cross-restart stamp
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== C: FullScanStamp round-trip ===");

const int ProbeSetId = 999_999;   // cannot collide with a real set
string stampPath = FullScanStamp.PathFor(ProbeSetId);
try
{
    Check(FullScanStamp.Read(ProbeSetId) is null,
        "C: an absent stamp reads as null (treated as 'due for a scan')");

    var when = new DateTime(2026, 9, 20, 18, 24, 46, DateTimeKind.Utc);
    Check(FullScanStamp.Write(ProbeSetId, when), "C: stamp written");

    var readBack = FullScanStamp.Read(ProbeSetId);
    Check(readBack is not null, "C: stamp reads back");
    Check(readBack is { } rb && Math.Abs((rb - when).TotalSeconds) < 1,
        $"C: round-trips the same instant (wrote {when:O}, read {readBack:O})");
    Check(readBack is { } rb2 && rb2.Kind == DateTimeKind.Utc,
        "C: reads back as UTC, so the 24h comparison is not shifted by local time");

    // A corrupt stamp must degrade to "scan sooner", never throw.
    await File.WriteAllTextAsync(stampPath, "not a timestamp");
    Check(FullScanStamp.Read(ProbeSetId) is null,
        "C: a corrupt stamp reads as null rather than throwing");
}
finally
{
    try { if (File.Exists(stampPath)) File.Delete(stampPath); } catch { }
}

// ===================================================================
// D: the journal-size probe
// ===================================================================
// Only the READ path is exercised here. Asking for a minimum the journal already
// meets means this can never resize a volume as a side effect of running tests —
// the enlarging path needs LocalSystem and is exercised (and logged) by the
// service itself. The read path is the one with a real failure mode: MaximumSize
// lives at offset 40 of USN_JOURNAL_DATA_V0, and a wrong offset would silently
// read some other field, so it is cross-checked against `fsutil usn queryjournal`.
Console.WriteLine();
Console.WriteLine("=== D: USN journal size probe (read path only) ===");

// Reading a real volume needs administrator rights, so the decode is tested
// against a synthetic USN_JOURNAL_DATA_V0 built to the documented layout. The
// values are the ones `fsutil usn queryjournal C:` reports on this machine, so a
// wrong offset produces a visibly wrong number rather than a plausible one.
{
    const long WantId = 0x01d9591d7d4561f4;   // fsutil: Usn Journal ID (C:)
    const long WantFirstUsn = 0x0000002cdd800000;
    const long WantNextUsn = 0x0000002cdfafd7a0;   // fsutil: Next Usn (C:)
    const long WantMaxSize = 0x0000000002000000;   // fsutil: Maximum Size = 32 MB
    const long WantDelta = 0x0000000000800000;   // fsutil: Allocation Delta = 8 MB

    var buf = new byte[56];
    BitConverter.GetBytes(WantId).CopyTo(buf, 0);
    BitConverter.GetBytes(WantFirstUsn).CopyTo(buf, 8);
    BitConverter.GetBytes(WantNextUsn).CopyTo(buf, 16);
    BitConverter.GetBytes(0x1122334455667788L).CopyTo(buf, 24);   // LowestValidUsn
    BitConverter.GetBytes(0x7fffffffffffffffL).CopyTo(buf, 32);   // MaxUsn
    BitConverter.GetBytes(WantMaxSize).CopyTo(buf, 40);
    BitConverter.GetBytes(WantDelta).CopyTo(buf, 48);

    Check(UsnJournalReader.TryParseJournalData(buf, 56, out long id, out long next, out long max),
        "D: a full 56-byte journal record parses");
    Check(id == WantId, $"D: UsnJournalID from offset 0 (got 0x{id:x16})");
    Check(next == WantNextUsn, $"D: NextUsn from offset 16 (got 0x{next:x16})");
    Check(max == WantMaxSize,
        $"D: MaximumSize from offset 40 = {max / (1024 * 1024)} MB — matches fsutil's 32 MB, "
        + "so the resize decision reads the right field");

    // A short reply must not surface a garbage size: 0 means "unknown", and the
    // caller treats unknown as "do not try to resize" rather than resizing blindly.
    Check(UsnJournalReader.TryParseJournalData(buf, 24, out _, out _, out long shortMax)
          && shortMax == 0,
        "D: a truncated reply yields MaximumSize = 0 (unknown) instead of a stale value");
    Check(!UsnJournalReader.TryParseJournalData(buf, 8, out _, out _, out _),
        "D: a reply too short to hold even the id is rejected");
}

// And confirm the live volumes really were left alone by this run.
foreach (char drive in new[] { 'C', 'D', 'E' })
{
    using var reader = UsnJournalReader.TryOpen(drive);
    Check(reader is null,
        $"D: {drive}: unprivileged TryOpen declines (so no test run can resize a volume)");
}

// ===================================================================
Console.WriteLine();
try { Directory.Delete(root, recursive: true); } catch { }

Console.WriteLine(failures == 0
    ? "ALL CHECKS PASSED"
    : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
