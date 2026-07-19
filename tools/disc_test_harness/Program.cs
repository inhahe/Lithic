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
    // Clear read-only attributes first: some tests intentionally create read-only
    // source files (the item-1 reburn cleanup test), and File.Copy preserves that
    // flag into staging/shelf/restore trees. A plain Directory.Delete would throw
    // on any leftover read-only file and leak the whole tree into the next run.
    try
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            try { new FileInfo(f).IsReadOnly = false; } catch { }
    }
    catch { /* best effort */ }
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

    // Multi-disc restore must also work through the NEW lazy materialisation path.
    var lz = await ws.RestoreAndVerifyLazy(burner, srcs, LazySelectionMode.ByDirectoryPrefix);
    ws.Assert(lz.Mismatches == 0, $"lazy: {lz.Mismatches} restored file(s) had wrong content");
    ws.Assert(lz.Restored == srcs.Count, $"lazy: restored {lz.Restored}/{srcs.Count} files");
    ws.Assert(lz.Enumerated == srcs.Count, $"lazy: skip-scan enumerated {lz.Enumerated}/{srcs.Count} leaves");
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

    // The deduped .fileref must also resolve through the NEW lazy restore path.
    var lz = await ws.RestoreAndVerifyLazy(burner, srcs, LazySelectionMode.ByDirectoryPrefix);
    ws.Assert(lz.Mismatches == 0, $"lazy: {lz.Mismatches} restored file(s) had wrong content");
    ws.Assert(lz.Restored == srcs.Count, $"lazy: restored {lz.Restored}/{srcs.Count} files");
    ws.Assert(lz.Enumerated == srcs.Count, $"lazy: skip-scan enumerated {lz.Enumerated}/{srcs.Count} leaves");
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
    // One file bigger than a single disc => split into chunks that SPAN multiple
    // physical discs and are reassembled byte-exact on restore. This verifies both
    // the split/reassemble round-trip and that chunks genuinely land on >=2 discs
    // (the fix for known-issues.md: "Oversized file split into chunks does not span
    // physical discs").
    var srcs = ws.MakeTree(("big/huge.bin", 300_000), ("big/small.txt", 5_000));
    var (burner, result) = await ws.Backup(srcs, capacityBytes: 150_000, allowSplitting: true);
    ws.Assert(result.Success, "backup should succeed");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");

    // The oversized file must be split and its chunks must span >=2 distinct discs.
    var records = await ws.AllFileRecords();
    var splitRec = records.FirstOrDefault(f => f.IsSplit && f.SourcePath.EndsWith("huge.bin"));
    ws.Assert(splitRec is not null, "expected huge.bin to be recorded as a split file");
    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    var chunks = await ws.Catalog.GetChunksForFileAsync(discs[0].Id, splitRec!.Id);
    ws.Assert(chunks.Count >= 2, $"expected >=2 chunks, got {chunks.Count}");
    int distinctDiscs = chunks.Select(c => c.DiscId).Distinct().Count();
    ws.Assert(distinctDiscs >= 2, $"expected chunks to span >=2 discs, spanned {distinctDiscs}");

    // No disc may overflow its capacity now that chunks span discs.
    long overflow = ws.MaxDiscOverflowBytes(burner, 150_000);
    ws.Assert(overflow == 0, $"a disc overflowed capacity by {overflow} bytes");

    // The split file must also round-trip through the NEW lazy restore path, whose
    // MaterializeSelectionAsync hydrates chunks per-record on demand.
    var lz = await ws.RestoreAndVerifyLazy(burner, srcs, LazySelectionMode.ByFilePath);
    ws.Assert(lz.Mismatches == 0, $"lazy: {lz.Mismatches} restored file(s) had wrong content");
    ws.Assert(lz.Restored == srcs.Count, $"lazy: restored {lz.Restored}/{srcs.Count} files");
    ws.Assert(lz.Enumerated == srcs.Count, $"lazy: skip-scan enumerated {lz.Enumerated}/{srcs.Count} leaves");
});

// ------------------------------------------------------------------
// Lazy catalog restore (v1.0.36): the GUI now reads the restore tree one
// directory at a time via GetRestoreChildrenAsync and materialises only the
// selected subset via MaterializeSelectionAsync. These cases drive that exact
// path (skip-scan enumeration + on-demand materialisation) end to end.
// ------------------------------------------------------------------

await runner.Run("lazy-catalog-nested-tree-both-modes", async ws =>
{
    // A multi-level tree so the skip-scan must descend several directory levels and
    // jump past whole subtrees between siblings.
    var srcs = ws.MakeTree(
        ("docs/2024/jan/report.txt", 40_000),
        ("docs/2024/jan/notes.md", 6_000),
        ("docs/2024/feb/summary.txt", 22_000),
        ("docs/archive/old.bin", 90_000),
        ("media/img/photo1.bin", 70_000),
        ("media/img/photo2.bin", 55_000),
        ("media/clip.dat", 30_000),
        ("root-note.txt", 3_000));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "backup should succeed");

    // Selecting whole directories (prefix-range materialisation).
    var byDir = await ws.RestoreAndVerifyLazy(burner, srcs, LazySelectionMode.ByDirectoryPrefix);
    ws.Assert(byDir.Enumerated == srcs.Count,
        $"by-dir: skip-scan enumerated {byDir.Enumerated}/{srcs.Count} leaves");
    ws.Assert(byDir.Mismatches == 0, $"by-dir: {byDir.Mismatches} restored file(s) had wrong content");
    ws.Assert(byDir.Restored == srcs.Count, $"by-dir: restored {byDir.Restored}/{srcs.Count} files");

    // Selecting individual file leaves (exact-path materialisation).
    var byFile = await ws.RestoreAndVerifyLazy(burner, srcs, LazySelectionMode.ByFilePath);
    ws.Assert(byFile.Enumerated == srcs.Count,
        $"by-file: skip-scan enumerated {byFile.Enumerated}/{srcs.Count} leaves");
    ws.Assert(byFile.Mismatches == 0, $"by-file: {byFile.Mismatches} restored file(s) had wrong content");
    ws.Assert(byFile.Restored == srcs.Count, $"by-file: restored {byFile.Restored}/{srcs.Count} files");

    // GetActiveSubtreeStatsAsync (the lazy per-directory count) must agree with the
    // real subtree contents — e.g. everything under docs/2024.
    string src2024 = srcs.First(p => p.Replace('\\', '/').EndsWith("docs/2024/jan/report.txt"));
    string prefix2024 = src2024[..src2024.LastIndexOf("2024", StringComparison.Ordinal)] + "2024";
    var (cnt, bytes) = await ws.Catalog.GetActiveSubtreeStatsAsync(ws.BackupSetId, prefix2024);
    ws.Assert(cnt == 3, $"expected 3 files under docs/2024, subtree stats reported {cnt}");
    ws.Assert(bytes > 0, "expected non-zero aggregate size under docs/2024");
});

await runner.Run("verify-disc-integrity", async ws =>
{
    var srcs = ws.MakeTree(("v/a.bin", 70_000), ("v/b.bin", 40_000));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "backup should succeed");

    // Content-verify every disc in the set against the catalog.
    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Count >= 1, "expected at least one disc recorded");
    var labelToRoot = await ws.BuildLabelToRootAsync(burner);
    foreach (var disc in discs)
    {
        string discRoot = labelToRoot[disc.Label];
        var restore = new RestoreService(ws.Catalog);
        var vr = await restore.VerifyDiscAsync(disc.Id, discRoot, verifyContents: true);
        ws.Assert(vr.Success, $"disc {disc.Label} failed integrity: {string.Join("; ", vr.Issues.Select(i => $"{i.SourcePath}:{i.Kind}"))}");
    }
});

await runner.Run("reburn-staging-cleanup-handles-readonly-source", async ws =>
{
    // Roadmap item 1: staging cleanup must clear the read-only attribute before
    // deleting the temp staging directory (ForceDeleteDirectory). The re-burn /
    // consolidate paths stage via File.Copy, which PRESERVES the source's read-only
    // flag — so a read-only SOURCE file (git object/pack files, anything copied
    // from read-only media) lands as a read-only STAGED copy. A plain
    // Directory.Delete then throws UnauthorizedAccessException and, because the
    // cleanup is wrapped in catch{}, the reburn still "succeeds" while leaking its
    // staging tree in %TEMP%\LithicBackup\reburn-* forever. (The MAIN backup path
    // stages with a manual stream copy, which creates a fresh non-read-only file,
    // so only the File.Copy-based reburn/consolidate paths hit this.) This test
    // drives ReplaceDiscFilesAsync on a read-only source and pins the observable:
    // no reburn staging directory is left behind.
    var srcs = ws.MakeTree(("ro/locked.bin", 50_000), ("ro/normal.bin", 30_000));
    new FileInfo(srcs[0]).IsReadOnly = true;

    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "initial backup should succeed");

    var disc = (await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId)).Single();
    var files = await ws.Catalog.GetFilesOnDiscAsync(disc.Id);
    var lockedId = files.Single(f => f.SourcePath.EndsWith("locked.bin")).Id;

    // Snapshot existing reburn-* staging dirs (the name carries a GUID) so we only
    // judge THIS reburn's cleanup, immune to anything a prior test may have left.
    string lithicTmp = Path.Combine(Path.GetTempPath(), "LithicBackup");
    HashSet<string> Reburns() => Directory.Exists(lithicTmp)
        ? Directory.GetDirectories(lithicTmp, "reburn-*").ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var before = Reburns();

    var orch = ws.BuildOrchestrator(burner);
    int reburned = await orch.ReplaceDiscFilesAsync(disc.Id, new[] { lockedId }, Workspace.RecorderId);
    ws.Assert(reburned == 1, $"expected the read-only file re-burned, got {reburned}");

    var leaked = Reburns();
    leaked.ExceptWith(before);
    ws.Assert(leaked.Count == 0,
        $"reburn staging dir leaked (read-only cleanup failed): [{string.Join(", ", leaked.Select(Path.GetFileName))}]");

    // The re-burned read-only file must still restore byte-for-byte.
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");

    // Leave the source tree deletable for the harness's best-effort root cleanup.
    try { new FileInfo(srcs[0]).IsReadOnly = false; } catch { }
});

await runner.Run("readonly-source-main-path-no-staging-leak", async ws =>
{
    // Roadmap item 1, main-burn-path angle: a plain disc backup of read-only
    // source files (git objects, anything off read-only media) must succeed,
    // restore byte-for-byte, and leave NO staging directory behind in
    // %TEMP%\LithicBackup (disc-*/spill-*). The main path stages via a fresh
    // stream copy (so staged files aren't read-only), and all seven cleanup
    // sites route through ForceDeleteDirectory — this pins that a whole set of
    // read-only content burns and cleans up without leaking temp.
    var srcs = ws.MakeTree(
        ("ro/a.bin", 60_000), ("ro/b.bin", 40_000), ("ro/c.bin", 50_000));
    foreach (var s in srcs) new FileInfo(s).IsReadOnly = true;

    string lithicTmp = Path.Combine(Path.GetTempPath(), "LithicBackup");
    HashSet<string> Staging() => Directory.Exists(lithicTmp)
        ? Directory.GetDirectories(lithicTmp, "*")
            .Where(d => { var n = Path.GetFileName(d); return n.StartsWith("disc-") || n.StartsWith("spill-"); })
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var before = Staging();

    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "read-only-source backup should succeed");

    var leaked = Staging();
    leaked.ExceptWith(before);
    ws.Assert(leaked.Count == 0,
        $"main-path staging dir leaked on read-only source: [{string.Join(", ", leaked.Select(Path.GetFileName))}]");

    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");

    // Leave the source tree deletable for the harness's best-effort root cleanup.
    foreach (var s in srcs) { try { new FileInfo(s).IsReadOnly = false; } catch { } }
});

// ------------------------------------------------------------------
// Failure injection
// ------------------------------------------------------------------

await runner.Run("fail-no-recorder", async ws =>
{
    var srcs = ws.MakeTree(("x.txt", 1_000));
    // No recorder => ExecuteAsync returns Success=false (does not throw).
    var (_, result) = await ws.Backup(srcs, configure: b => b.SimulateNoRecorder = true);
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

// ------------------------------------------------------------------
// ISO-incompatibility -> proactive zip (no user prompts)
// ------------------------------------------------------------------

await runner.Run("iso-incompatible-path-forces-zip", async ws =>
{
    // Lowercase filenames are illegal under ISO 9660 Level 1 (uppercase-only).
    // With ZipMode.IncompatibleOnly the orchestrator must proactively zip them
    // BEFORE any failure path, so the burn succeeds, every record is stored
    // zipped, and the failure callback is never invoked.
    var srcs = ws.MakeTree(("dir/lower.txt", 40_000), ("dir/other.dat", 60_000));
    int prompts = 0;
    var (burner, result) = await ws.Backup(srcs,
        filesystemType: FilesystemType.ISO9660,
        zipMode: ZipMode.IncompatibleOnly,
        onFailure: (_, _, _) => { prompts++; return Task.FromResult(new FailureDecision { Action = BurnFailureAction.Skip }); });

    ws.Assert(result.Success, "backup should succeed by zipping incompatible files");
    ws.Assert(prompts == 0, $"failure callback should never fire for proactive zip, fired {prompts}x");

    var records = await ws.AllFileRecords();
    ws.Assert(records.Count == srcs.Count, $"expected {srcs.Count} records, got {records.Count}");
    ws.Assert(records.All(f => f.IsZipped), "every incompatible file should be stored zipped");

    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("many-incompatible-files-no-repeat-prompt", async ws =>
{
    // A whole volume of ISO-incompatible files must NOT force the user to answer
    // a prompt over and over: proactive zipping means zero callbacks regardless
    // of how many files are incompatible.
    var spec = Enumerable.Range(0, 40)
        .Select(i => ($"vol/file{i}.txt", 2_000 + i))
        .ToArray();
    var srcs = ws.MakeTree(spec);
    int prompts = 0;
    var (burner, result) = await ws.Backup(srcs,
        filesystemType: FilesystemType.ISO9660,
        zipMode: ZipMode.IncompatibleOnly,
        onFailure: (_, _, _) => { prompts++; return Task.FromResult(new FailureDecision { Action = BurnFailureAction.Skip }); });

    ws.Assert(result.Success, "backup of many incompatible files should succeed");
    ws.Assert(prompts == 0, $"expected 0 prompts for {srcs.Count} incompatible files, got {prompts}");

    var records = await ws.AllFileRecords();
    ws.Assert(records.All(f => f.IsZipped), "every file should be zipped");
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
});

await runner.Run("plan-time-compat-summary-counts-incompatible", async ws =>
{
    // Plan-time compatibility summary (roadmap item 4): mixed set of ISO 9660
    // Level-1-illegal filenames (lowercase) and legal 8.3 uppercase names.
    // Under ISO 9660 the summary must count exactly the incompatible files; under
    // UDF (permissive) nothing is incompatible.
    var srcs = ws.MakeTree(
        ("d/lower1.txt", 10_000),   // lowercase -> ISO-incompatible
        ("d/lower2.txt", 20_000),   // lowercase -> ISO-incompatible
        ("d/FILEA.TXT", 30_000),    // uppercase 8.3 -> ISO-compatible
        ("d/FILEB.TXT", 40_000));   // uppercase 8.3 -> ISO-compatible

    var (orch, plan) = await ws.PlanFor(srcs, FilesystemType.ISO9660);

    var iso = orch.SummarizeCompatibility(plan, FilesystemType.ISO9660);
    ws.Assert(iso.TotalFiles == 4, $"expected 4 planned files, got {iso.TotalFiles}");
    ws.Assert(iso.IncompatibleFiles == 2,
        $"expected 2 ISO-incompatible files, got {iso.IncompatibleFiles}");
    ws.Assert(iso.IncompatibleBytes == 30_000,
        $"expected 30,000 incompatible bytes, got {iso.IncompatibleBytes}");
    ws.Assert(iso.HasIncompatible, "summary should flag incompatibility");

    var udf = orch.SummarizeCompatibility(plan, FilesystemType.UDF);
    ws.Assert(udf.IncompatibleFiles == 0,
        $"UDF is permissive; expected 0 incompatible, got {udf.IncompatibleFiles}");
    ws.Assert(!udf.HasIncompatible, "UDF summary should flag nothing");
});

await runner.Run("udf-warning-decision-yes-no-cancel", async ws =>
{
    // Roadmap item 4, the DIALOG side: the WPF warning is a thin shell over
    // DiscCompatibilityAdvisor, which the view-model and this test share. Here we
    // drive the three user answers programmatically (no MessageBox) and pin the
    // outcomes: Yes flips the job to UDF and proceeds, No keeps the format and
    // proceeds (files will be zipped at burn), Cancel aborts.
    //
    // 30 lowercase files => ISO-incompatible; well past the significance floor
    // (>=5% files / >=5% bytes / >=20 files) so ShouldWarn fires.
    var spec = Enumerable.Range(0, 30).Select(i => ($"vol/file{i}.txt", 3_000 + i)).ToArray();
    var srcs = ws.MakeTree(spec);
    var (orch, plan) = await ws.PlanFor(srcs, FilesystemType.ISO9660);
    var summary = orch.SummarizeCompatibility(plan, FilesystemType.ISO9660);

    // Significance gate: fires for ISO with many incompatible files...
    ws.Assert(DiscCompatibilityAdvisor.ShouldWarn(ZipMode.IncompatibleOnly, FilesystemType.ISO9660, summary),
        "warning should fire for ISO 9660 with 30 incompatible files");
    // ...never for UDF (nothing more permissive to suggest)...
    var udfSummary = orch.SummarizeCompatibility(plan, FilesystemType.UDF);
    ws.Assert(!DiscCompatibilityAdvisor.ShouldWarn(ZipMode.IncompatibleOnly, FilesystemType.UDF, udfSummary),
        "warning must never fire when the format is already UDF");
    // ...and never when zipping isn't the compatibility fallback.
    ws.Assert(!DiscCompatibilityAdvisor.ShouldWarn(ZipMode.All, FilesystemType.ISO9660, summary),
        "warning must not fire under ZipMode.All (unconditional zip, not a compat fallback)");

    // Yes -> switch this run to UDF, proceed.
    var jobYes = new BackupJob { FilesystemType = FilesystemType.ISO9660 };
    bool proceedYes = DiscCompatibilityAdvisor.ApplyChoice(jobYes, UdfWarningChoice.SwitchToUdf);
    ws.Assert(proceedYes, "Yes should proceed with the burn");
    ws.Assert(jobYes.FilesystemType == FilesystemType.UDF, "Yes should switch the job to UDF");

    // No -> keep the selected format, proceed (incompatible files get zipped).
    var jobNo = new BackupJob { FilesystemType = FilesystemType.ISO9660 };
    bool proceedNo = DiscCompatibilityAdvisor.ApplyChoice(jobNo, UdfWarningChoice.KeepFormat);
    ws.Assert(proceedNo, "No should still proceed with the burn");
    ws.Assert(jobNo.FilesystemType == FilesystemType.ISO9660, "No should leave the format unchanged");

    // Cancel -> abort, format untouched.
    var jobCancel = new BackupJob { FilesystemType = FilesystemType.ISO9660 };
    bool proceedCancel = DiscCompatibilityAdvisor.ApplyChoice(jobCancel, UdfWarningChoice.Cancel);
    ws.Assert(!proceedCancel, "Cancel should abort the backup");
    ws.Assert(jobCancel.FilesystemType == FilesystemType.ISO9660, "Cancel should leave the format unchanged");
});

await runner.Run("udf-warning-below-threshold-stays-silent", async ws =>
{
    // The warning must NOT interrupt for a trivial amount of incompatibility: a
    // single lowercase (ISO-incompatible) file among 40 legal 8.3 uppercase names
    // is below the 5%-files / 5%-bytes / 20-files floor, so ShouldWarn is false and
    // the burn's IncompatibleOnly fallback just zips that one file silently.
    var spec = Enumerable.Range(0, 41).Select(i =>
        (i == 0 ? "vol/lower.txt" : $"vol/FILE{i}.TXT", 3_000)).ToArray();
    var srcs = ws.MakeTree(spec);
    var (orch, plan) = await ws.PlanFor(srcs, FilesystemType.ISO9660);
    var summary = orch.SummarizeCompatibility(plan, FilesystemType.ISO9660);

    ws.Assert(summary.IncompatibleFiles == 1,
        $"fixture should have exactly 1 incompatible file, got {summary.IncompatibleFiles}");
    ws.Assert(!DiscCompatibilityAdvisor.ShouldWarn(ZipMode.IncompatibleOnly, FilesystemType.ISO9660, summary),
        "1 incompatible file out of 41 is below every threshold; must not warn");
});

// ------------------------------------------------------------------
// Disc-fault repair: re-burn failed files onto a fresh disc
// ------------------------------------------------------------------

await runner.Run("replace-disc-files-repair", async ws =>
{
    // Simulate finding a fault in some files on a burned disc, then repairing
    // just those files onto a new supplementary disc via ReplaceDiscFilesAsync.
    var srcs = ws.MakeTree(("r/a.bin", 40_000), ("r/b.bin", 50_000), ("r/c.bin", 30_000));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "initial backup should succeed");

    var disc = (await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId)).Single();
    var files = await ws.Catalog.GetFilesOnDiscAsync(disc.Id);
    var badIds = files.Where(f => f.SourcePath.EndsWith("a.bin") || f.SourcePath.EndsWith("b.bin"))
        .Select(f => f.Id).ToList();

    // Re-burn on the SAME burner so the shelf's session counter continues.
    var orch = ws.BuildOrchestrator(burner);
    int reburned = await orch.ReplaceDiscFilesAsync(disc.Id, badIds, Workspace.RecorderId);
    ws.Assert(reburned == badIds.Count, $"expected {badIds.Count} files re-burned, got {reburned}");

    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Any(d => d.Label.EndsWith("-Repair")), "expected a new -Repair disc");

    // Originals for the repaired files must be superseded (IsDeleted) so restore
    // resolves to the fresh copies; the untouched file stays live.
    var after = await ws.Catalog.GetFilesOnDiscAsync(disc.Id);
    ws.Assert(after.Where(f => badIds.Contains(f.Id)).All(f => f.IsDeleted),
        "repaired originals should be marked deleted");
    ws.Assert(after.Single(f => f.SourcePath.EndsWith("c.bin")).IsDeleted == false,
        "untouched file should remain live");

    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("replace-disc-files-source-deleted", async ws =>
{
    // If a source file has since been deleted, the repair must skip it gracefully
    // (no crash), leave its original record intact, and report it wasn't re-burned.
    var srcs = ws.MakeTree(("d/keep.bin", 40_000), ("d/gone.bin", 40_000));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "initial backup should succeed");

    var disc = (await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId)).Single();
    var files = await ws.Catalog.GetFilesOnDiscAsync(disc.Id);
    var goneRec = files.Single(f => f.SourcePath.EndsWith("gone.bin"));

    // Delete the live source, then try to repair it.
    File.Delete(goneRec.SourcePath);

    var orch = ws.BuildOrchestrator(burner);
    int reburned = await orch.ReplaceDiscFilesAsync(disc.Id, new[] { goneRec.Id }, Workspace.RecorderId);
    ws.Assert(reburned == 0, $"expected 0 re-burned (source deleted), got {reburned}");

    var after = await ws.Catalog.GetFilesOnDiscAsync(disc.Id);
    ws.Assert(after.Single(f => f.Id == goneRec.Id).IsDeleted == false,
        "deleted-source original must stay live (nothing replaced it)");
    ws.Assert(!(await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId)).Any(d => d.Label.EndsWith("-Repair")),
        "no repair disc should be created when nothing could be staged");
});

await runner.Run("replace-disc-files-source-grown", async ws =>
{
    // If a source file has grown since backup, the repair disc must capture the
    // NEW bytes and record the NEW size (regression guard for the stale-size bug).
    byte[] original = TestRunner.Gen(7, 40_000);
    byte[] grown = TestRunner.Gen(8, 90_000);
    var srcs = ws.MakeTreeBytes(("g/data.bin", original));
    var (burner, result) = await ws.Backup(srcs);
    ws.Assert(result.Success, "initial backup should succeed");

    var disc = (await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId)).Single();
    var rec = (await ws.Catalog.GetFilesOnDiscAsync(disc.Id)).Single();

    // Grow the live source, then repair.
    File.WriteAllBytes(rec.SourcePath, grown);

    var orch = ws.BuildOrchestrator(burner);
    int reburned = await orch.ReplaceDiscFilesAsync(disc.Id, new[] { rec.Id }, Workspace.RecorderId);
    ws.Assert(reburned == 1, $"expected 1 file re-burned, got {reburned}");

    // The new record on the repair disc must carry the grown size, not the stale one.
    var repairDisc = (await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId))
        .Single(d => d.Label.EndsWith("-Repair"));
    var newRec = (await ws.Catalog.GetFilesOnDiscAsync(repairDisc.Id)).Single();
    ws.Assert(newRec.SizeBytes == grown.Length,
        $"repair record size should be grown size {grown.Length}, got {newRec.SizeBytes}");

    // Restore must yield the grown content (the version actually on the repair disc).
    var expected = new Dictionary<string, byte[]> { [srcs[0]] = grown };
    var r = await ws.RestoreAndVerify(burner, srcs, expected);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
});

// ------------------------------------------------------------------
// Media that over-reports its capacity -> graceful re-plan onto more discs
// ------------------------------------------------------------------

await runner.Run("disc-over-reports-capacity", async ws =>
{
    // Plan thinks everything fits on one 200 KB disc, but the media's REAL
    // writable capacity is only 100 KB. The burn fails partway through with a
    // DiscCapacityExceededException; the orchestrator must catch it, discard the
    // partial (uncatalogued) disc, cap all remaining discs to the OBSERVED
    // capacity, re-pack the not-yet-burned files, and continue — so the backup
    // succeeds across more (smaller) discs instead of aborting.
    var srcs = ws.MakeTree(("o/a.bin", 60_000), ("o/b.bin", 60_000), ("o/c.bin", 30_000));
    var (burner, result) = await ws.Backup(srcs,
        capacityBytes: 200_000,
        configure: b => b.ActualCapacityBytes = 100_000);

    ws.Assert(result.Success, "backup should succeed by re-planning onto more discs at the observed capacity");

    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Count >= 2, $"150 KB of data at 100 KB/disc should need >= 2 discs, got {discs.Count}");

    // No burned disc may exceed the media's TRUE writable capacity.
    long overflow = ws.MaxDiscOverflowBytes(burner, 100_000);
    ws.Assert(overflow == 0, $"a disc exceeded the observed 100 KB capacity by {overflow} bytes");

    // Every file must still restore with correct content after the re-plan.
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("file-grows-between-plan-and-burn", async ws =>
{
    // A file grows AFTER the plan is fixed but BEFORE staging. The burn must not
    // stage a stale (too-small) size and overflow the disc: the grown file is
    // re-detected, re-queued at its true size, and bumped to the next disc. The
    // restore must yield the GROWN content (staging reads the current bytes under
    // a read lock), and no disc may exceed its capacity.
    var srcs = ws.MakeTree(("g/a.bin", 80_000), ("g/b.bin", 80_000), ("g/c.bin", 80_000));
    string grownPath = srcs[0];
    var grown = TestRunner.Gen(9999, 250_000);
    var (burner, result) = await ws.Backup(srcs,
        capacityBytes: 300_000,
        betweenPlanAndExecute: () => { File.WriteAllBytes(grownPath, grown); return Task.CompletedTask; });

    ws.Assert(result.Success, "backup should succeed despite the file growing");

    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Count >= 2, $"grown file should have been bumped to a 2nd disc, got {discs.Count}");

    long overflow = ws.MaxDiscOverflowBytes(burner, 300_000);
    ws.Assert(overflow == 0, $"a disc overflowed capacity by {overflow} bytes");

    // The catalog must record the grown file at its TRUE (grown) size, not the plan.
    var records = await ws.AllFileRecords();
    var rec = records.First(r => r.SourcePath == grownPath && !r.IsDeleted);
    ws.Assert(rec.SizeBytes == grown.Length,
        $"recorded size {rec.SizeBytes} should equal grown size {grown.Length}");

    // Restore must reproduce the grown content (RestoreAndVerify compares to the
    // current source, which is now the grown file).
    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

// ------------------------------------------------------------------
// Burn-in-place: plain files burned directly from source (no temp copy)
// ------------------------------------------------------------------

await runner.Run("burn-in-place-basic", async ws =>
{
    // Several plain files spanning two discs, burned in-place (no temp copy).
    // The burn must read straight from the source under a held read lock, and
    // restore must reproduce every file byte-for-byte.
    var srcs = ws.MakeTree(
        ("p/a.bin", 120_000), ("p/b.bin", 120_000),
        ("p/c.bin", 120_000), ("p/d.bin", 120_000));
    var (burner, result) = await ws.Backup(srcs,
        capacityBytes: 300_000,
        stagingMode: DiscStagingMode.InPlace);

    ws.Assert(result.Success, "in-place backup should succeed");

    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Count >= 2, $"4x120KB over 300KB discs should need >=2 discs, got {discs.Count}");

    long overflow = ws.MaxDiscOverflowBytes(burner, 300_000);
    ws.Assert(overflow == 0, $"a disc overflowed capacity by {overflow} bytes");

    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == srcs.Count, $"restored {r.Restored}/{srcs.Count} files");
});

await runner.Run("burn-in-place-growth-safe", async ws =>
{
    // The growth-safety guarantee must also hold in burn-in-place mode: a file
    // that grows after planning is re-checked under the read lock taken at
    // staging time, re-queued at its true size, and bumped to a later disc so
    // no disc overflows. Restore yields the grown content read from the source.
    var srcs = ws.MakeTree(("g/a.bin", 80_000), ("g/b.bin", 80_000), ("g/c.bin", 80_000));
    string grownPath = srcs[0];
    var grown = TestRunner.Gen(4242, 250_000);
    var (burner, result) = await ws.Backup(srcs,
        capacityBytes: 300_000,
        stagingMode: DiscStagingMode.InPlace,
        betweenPlanAndExecute: () => { File.WriteAllBytes(grownPath, grown); return Task.CompletedTask; });

    ws.Assert(result.Success, "in-place backup should succeed despite the file growing");

    var discs = await ws.Catalog.GetDiscsForBackupSetAsync(ws.BackupSetId);
    ws.Assert(discs.Count >= 2, $"grown file should have been bumped to a 2nd disc, got {discs.Count}");

    long overflow = ws.MaxDiscOverflowBytes(burner, 300_000);
    ws.Assert(overflow == 0, $"a disc overflowed capacity by {overflow} bytes");

    var records = await ws.AllFileRecords();
    var rec = records.First(r => r.SourcePath == grownPath && !r.IsDeleted);
    ws.Assert(rec.SizeBytes == grown.Length,
        $"recorded size {rec.SizeBytes} should equal grown size {grown.Length}");

    var r = await ws.RestoreAndVerify(burner, srcs);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
});

// ------------------------------------------------------------------
// Multisession: append a second run onto an existing disc
// ------------------------------------------------------------------

await runner.Run("multisession-append-restores-both-sessions", async ws =>
{
    // Two backup runs to the SAME set on the SAME (non-blank, room-to-spare) disc.
    // The second run must APPEND via multisession — a new disc record with a fresh,
    // non-colliding label — and restore must reproduce files from BOTH sessions.
    // This exercises the multisession orchestration end to end: the session
    // decision, cross-run disc numbering, and per-session restore. (The IMAPI2
    // ImportFileSystem step that makes this work on real hardware is exercised only
    // by code review; the simulator models each session as its own disc surface.)
    var burner = ws.NewBurner();

    // --- Session 1: two files onto a blank disc. ---
    var first = ws.MakeTree(("s1/a.bin", 60_000), ("s1/b.bin", 40_000));
    var (_, r1) = await ws.Backup(first, burner: burner);
    ws.Assert(r1.Success, "first backup should succeed");
    int setId = ws.BackupSetId;
    var discs1 = await ws.Catalog.GetDiscsForBackupSetAsync(setId);
    ws.Assert(discs1.Count == 1, $"expected 1 disc after session 1, got {discs1.Count}");

    // --- Session 2: incremental run to the SAME set/burner appends via multisession. ---
    var second = ws.MakeTree(("s2/c.bin", 50_000), ("s2/d.bin", 30_000));
    var (_, r2) = await ws.Backup(second, burner: burner, existingSetId: setId);
    ws.Assert(r2.Success, "second (append) backup should succeed");

    var discs = (await ws.Catalog.GetDiscsForBackupSetAsync(setId)).OrderBy(d => d.Id).ToList();
    ws.Assert(discs.Count == 2, $"expected 2 disc records (one per session), got {discs.Count}");

    // The append run must record only its two new files on the new session's disc.
    var session2Files = await ws.Catalog.GetFilesOnDiscAsync(discs[1].Id);
    ws.Assert(session2Files.Count == 2, $"append run should record 2 new files, got {session2Files.Count}");

    // Cross-run disc labels must be unique. Regression guard: discSequence used to
    // reset to 1 every run, so each run's first disc collided on "Disc-001", which
    // made the second session's files unrestorable (the label mapped to one disc).
    ws.Assert(discs.Select(d => d.Label).Distinct().Count() == discs.Count,
        $"disc labels collided across sessions: [{string.Join(",", discs.Select(d => d.Label))}]");

    // Restore the whole set: files from BOTH sessions must return byte-for-byte.
    var all = new List<string>();
    all.AddRange(first);
    all.AddRange(second);
    var r = await ws.RestoreAndVerify(burner, all);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
    ws.Assert(r.Restored == all.Count, $"restored {r.Restored}/{all.Count} files");
});

await runner.Run("changed-file-reburn-gets-distinct-disc-path", async ws =>
{
    // A file that CHANGES between two backup runs to the same set is re-staged as
    // a new version. On a multisession append that lands on the SAME physical disc
    // as the earlier version, both versions would otherwise map to the same disc
    // path — which (a) collides in IMAPI's AddFile after ImportFileSystem imports
    // the earlier session and (b) lets the newer file shadow the older at restore.
    // The fix gives version > 1 a distinct on-disc path (VersionedDiscPath). This
    // test asserts the invariant that makes that safe: every version of a source
    // path occupies a DISTINCT DiscPath, and restore reproduces the latest content.
    var burner = ws.NewBurner();

    // --- Run 1: original content. ---
    var v1 = ws.MakeTree(("docs/report.txt", 50_000));
    var (_, r1) = await ws.Backup(v1, burner: burner);
    ws.Assert(r1.Success, "first backup should succeed");
    int setId = ws.BackupSetId;

    // --- Change the file in place, then run 2 (incremental append). ---
    var v2Content = TestRunner.Gen(9999, 70_000);
    ws.MakeTreeBytes(("docs/report.txt", v2Content)); // overwrites same source path
    var (_, r2) = await ws.Backup(v1, burner: burner, existingSetId: setId);
    ws.Assert(r2.Success, "second (changed-file) backup should succeed");

    // Two records for the same source path: versions 1 and 2 with DISTINCT DiscPaths.
    var records = (await ws.AllFileRecords())
        .Where(r => r.SourcePath.EndsWith("report.txt", StringComparison.OrdinalIgnoreCase))
        .OrderBy(r => r.Version).ToList();
    ws.Assert(records.Count == 2, $"expected 2 versions of the changed file, got {records.Count}");
    ws.Assert(records.Select(r => r.Version).SequenceEqual(new[] { 1, 2 }),
        $"expected versions [1,2], got [{string.Join(",", records.Select(r => r.Version))}]");
    ws.Assert(records.Select(r => r.DiscPath).Distinct().Count() == 2,
        $"versions must occupy distinct disc paths, got [{string.Join(",", records.Select(r => r.DiscPath))}]");
    ws.Assert(records[1].DiscPath.Contains(".v2", StringComparison.OrdinalIgnoreCase),
        $"version 2 disc path should carry a .v2 tag, got '{records[1].DiscPath}'");

    // Restore the whole set: the current (v2) source must return byte-for-byte.
    var r = await ws.RestoreAndVerify(burner, v1);
    ws.Assert(r.Mismatches == 0, $"{r.Mismatches} restored file(s) had wrong content");
});

return runner.Report();

// ======================================================================
// Test framework
// ======================================================================

/// <summary>How a lazy restore selection is expressed to
/// <see cref="RestoreService.MaterializeSelectionAsync"/>: as whole-directory
/// prefixes (a checked directory) or as individual file paths (checked leaves).
/// Testing both covers the prefix-range and exact-path branches of the materialiser.</summary>
enum LazySelectionMode { ByDirectoryPrefix, ByFilePath }

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
    public const string RecorderId = "SIM_RECORDER_0";

    public async Task<(SimulatedDiscBurner Burner, BackupResult Result)> Backup(
        List<string> sources,
        long? capacityBytes = 4_700_000_000L,
        bool useCapacityOverride = true,
        bool fileDedup = false,
        ZipMode zipMode = ZipMode.None,
        bool allowSplitting = false,
        FilesystemType filesystemType = FilesystemType.UDF,
        FailureCallback? onFailure = null,
        SimulatedDiscBurner? burner = null,
        Action<SimulatedDiscBurner>? configure = null,
        Func<Task>? betweenPlanAndExecute = null,
        DiscStagingMode stagingMode = DiscStagingMode.TemporaryCopy,
        int? existingSetId = null)
    {
        burner ??= NewBurner(configure);

        // Reuse an existing set (for incremental / multisession runs) or create a
        // fresh one. Reusing keeps the catalog history so the scanner diffs the new
        // source tree against what's already backed up and only stages new files.
        if (existingSetId is int reuse)
        {
            BackupSetId = reuse;
        }
        else
        {
            var set = await Catalog.CreateBackupSetAsync(new BackupSet
            {
                Name = "harness-set",
                SourceRoots = new List<string> { _sourceDir },
                CreatedUtc = DateTime.UtcNow,
            });
            BackupSetId = set.Id;
        }

        var orchestrator = BuildOrchestrator(burner);

        var job = new BackupJob
        {
            BackupSetId = BackupSetId,
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
            FilesystemType = filesystemType,
            VerifyAfterBurn = true,
            CapacityOverrideBytes = useCapacityOverride ? capacityBytes : null,
            StagingMode = stagingMode,
        };

        var plan = await orchestrator.PlanAsync(job);
        Console.WriteLine($"  [plan] cap={job.CapacityOverrideBytes} allocs={plan.DiscAllocations.Count} " +
            $"totBytes={plan.TotalBytes} newFiles={plan.Diff.NewFiles.Count} " +
            $"allocSizes=[{string.Join(",", plan.DiscAllocations.Select(a => a.TotalBytes))}]");
        // Optional hook to mutate the source tree AFTER the plan is fixed but BEFORE
        // execution/staging — used to test files that grow between plan and burn.
        if (betweenPlanAndExecute is not null)
            await betweenPlanAndExecute();
        var result = await orchestrator.ExecuteAsync(plan, progress: null, onFailure: onFailure,
            ct: CancellationToken.None);
        return (burner, result);
    }

    public BackupOrchestrator BuildOrchestrator(IDiscBurner burner)
    {
        var scanner = new FileScanner(Catalog);
        var packer = new BinPacker();
        var zipHandler = new ZipHandler();
        var sessionStrategy = new DiscSessionStrategy(burner, Catalog);
        return new BackupOrchestrator(Catalog, burner, scanner, packer, zipHandler,
            sessionStrategy, fileSystemMonitor: null);
    }

    /// <summary>Plan a set of sources without executing, returning the orchestrator
    /// (for SummarizeCompatibility) and the computed plan.</summary>
    public async Task<(BackupOrchestrator Orchestrator, BackupPlan Plan)> PlanFor(
        List<string> sources,
        FilesystemType filesystemType,
        ZipMode zipMode = ZipMode.IncompatibleOnly)
    {
        var burner = NewBurner(null);
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
            BackupSetId = BackupSetId,
            Sources = sources.Select(s => new SourceSelection
            {
                Path = s,
                IsDirectory = false,
                IsSelected = true,
            }).ToList(),
            IncludeCatalogOnDisc = false,
            ZipMode = zipMode,
            FilesystemType = filesystemType,
            CapacityOverrideBytes = 4_700_000_000L,
        };
        var plan = await orchestrator.PlanAsync(job);
        return (orchestrator, plan);
    }

    // -- restore + verify --------------------------------------------

    /// <summary>
    /// Map each catalog disc label to its shelf directory. The simulated burner
    /// writes the k-th successful burn to shelf <c>disc-k</c>, and catalog disc
    /// records are created in that same burn order, so ordering the set's discs by
    /// Id and assigning shelf numbers 1..n is robust even for re-burn/repair discs
    /// whose labels don't encode their shelf number.
    /// </summary>
    /// <summary>Every file record across all discs in the current backup set.</summary>
    public async Task<List<FileRecord>> AllFileRecords()
    {
        var discs = await Catalog.GetDiscsForBackupSetAsync(BackupSetId);
        var all = new List<FileRecord>();
        foreach (var d in discs)
            all.AddRange(await Catalog.GetFilesOnDiscAsync(d.Id));
        return all;
    }

    public async Task<Dictionary<string, string>> BuildLabelToRootAsync(SimulatedDiscBurner burner)
    {
        var discs = (await Catalog.GetDiscsForBackupSetAsync(BackupSetId))
            .OrderBy(d => d.Id).ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < discs.Count; i++)
            map[discs[i].Label] = burner.GetDiscPath(RecorderId, i + 1);
        return map;
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

    /// <summary>
    /// Restore the whole set and compare each source file to its restored copy by
    /// SHA-256. <paramref name="expectedContentByRel"/> optionally overrides the
    /// bytes a given source path (relative-to-drive) should restore to — used when
    /// the live source has changed (e.g. grown) since backup and we want to compare
    /// against the version actually captured on disc rather than the current file.
    /// </summary>
    public async Task<(int Restored, int Mismatches)> RestoreAndVerify(
        SimulatedDiscBurner burner, List<string> sources,
        IReadOnlyDictionary<string, byte[]>? expectedContentByPath = null)
    {
        var labelToRoot = await BuildLabelToRootAsync(burner);
        var restore = new RestoreService(Catalog)
        {
            DiscInsertCallback = label => Task.FromResult(
                labelToRoot.TryGetValue(label, out var root) ? root : null),
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
            if (!File.Exists(restored)) { mismatches++; continue; }

            bool ok = expectedContentByPath is not null
                      && expectedContentByPath.TryGetValue(src, out var expected)
                ? HashEqualsBytes(restored, expected)
                : HashEquals(src, restored);
            if (!ok) mismatches++;
        }
        return (result.FilesRestored, mismatches);
    }

    /// <summary>
    /// Restore + verify driving the NEW lazy catalog path the GUI now uses. Walks the
    /// restore tree one directory at a time with the loose-index skip-scan
    /// (<see cref="SqliteCatalogRepository.GetRestoreChildrenAsync"/>) exactly as the
    /// GUI does on expand, expresses a "select everything" choice as either directory
    /// prefixes or individual file paths, then materialises it with
    /// <see cref="RestoreService.MaterializeSelectionAsync"/> — instead of the eager
    /// whole-catalog <c>GetRestorableFilesAsync</c>. Returns the restored count, the
    /// mismatch count, and how many leaf files the skip-scan enumerated (so a caller
    /// can assert it saw exactly the set's distinct active source paths).
    /// </summary>
    public async Task<(int Restored, int Mismatches, int Enumerated)> RestoreAndVerifyLazy(
        SimulatedDiscBurner burner, List<string> sources, LazySelectionMode mode,
        IReadOnlyDictionary<string, byte[]>? expectedContentByPath = null)
    {
        var labelToRoot = await BuildLabelToRootAsync(burner);
        var restore = new RestoreService(Catalog)
        {
            DiscInsertCallback = label => Task.FromResult(
                labelToRoot.TryGetValue(label, out var root) ? root : null),
        };

        // Walk the whole tree lazily, one directory at a time — this exercises the
        // skip-scan enumerator at every level rather than an eager whole-catalog read.
        var rootPrefixes = new List<string>();
        var leafPaths = new List<string>();
        foreach (var child in await Catalog.GetRestoreChildrenAsync(BackupSetId, ""))
        {
            if (child.IsDirectory) { rootPrefixes.Add(child.FullPath); await WalkLazyLeaves(child, leafPaths); }
            else leafPaths.Add(child.FullPath);
        }

        int enumerated = leafPaths.Count;

        // Express the selection two different ways to cover both branches of
        // MaterializeSelectionAsync (prefix-range query vs. exact-path lookup).
        IReadOnlyCollection<string> dirPrefixes, filePaths;
        if (mode == LazySelectionMode.ByDirectoryPrefix)
        {
            // "Select all" on each root drive/share = one prefix per root. Any leaf
            // sitting directly at a root (not under one of those prefixes) is listed
            // explicitly, mirroring how the tree collects a root-level file.
            dirPrefixes = rootPrefixes;
            filePaths = leafPaths.Where(p => rootPrefixes.All(
                pr => !p.StartsWith(pr.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        }
        else
        {
            dirPrefixes = System.Array.Empty<string>();
            filePaths = leafPaths;
        }

        var restorable = await restore.MaterializeSelectionAsync(BackupSetId, dirPrefixes, filePaths);

        var driveDests = restorable
            .Select(rf => DriveKey(rf.Record.SourcePath))
            .Distinct()
            .ToDictionary(k => k, _ => _restoreDir, StringComparer.OrdinalIgnoreCase);

        var result = await restore.RestoreAsync(restorable, driveDests);

        int mismatches = 0;
        foreach (var src in sources)
        {
            string restored = Path.Combine(_restoreDir, RelToDriveRoot(src));
            if (!File.Exists(restored)) { mismatches++; continue; }

            bool ok = expectedContentByPath is not null
                      && expectedContentByPath.TryGetValue(src, out var expected)
                ? HashEqualsBytes(restored, expected)
                : HashEquals(src, restored);
            if (!ok) mismatches++;
        }
        return (result.FilesRestored, mismatches, enumerated);
    }

    /// <summary>Recursively collect leaf (file) paths under a tree node using the same
    /// skip-scan enumerator the GUI drives on expand.</summary>
    private async Task WalkLazyLeaves(RestoreTreeChild node, List<string> leaves)
    {
        foreach (var child in await Catalog.GetRestoreChildrenAsync(BackupSetId, node.FullPath))
        {
            if (child.IsDirectory) await WalkLazyLeaves(child, leaves);
            else leaves.Add(child.FullPath);
        }
    }

    private static bool HashEqualsBytes(string filePath, byte[] expected)
    {
        using var sha = SHA256.Create();
        byte[] hf;
        using (var s = File.OpenRead(filePath)) hf = sha.ComputeHash(s);
        return hf.SequenceEqual(sha.ComputeHash(expected));
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
