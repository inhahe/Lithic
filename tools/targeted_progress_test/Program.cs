// "Back up the folders you just added" showed no progress at all.
//
// After an edit that adds folders, the GUI backs up exactly the files it just
// found, through DirectoryBackupService.ExecuteTargetedAsync. That method handed
// ExecuteAsync `progress: null`, so the run said "Copying N file(s)…" and nothing
// else until it finished - no percentage, no current file. It now takes a
// progress reporter and a pause event and passes them through; the GUI feeds them
// to the same inline panel a normal backup uses.
//
// This drives the real backup engine against a throwaway catalog and temp
// folders, and checks what that panel depends on: per-file reports naming the
// file, byte totals, a 100% at the end, and working Pause and Cancel.
//
// Section 8: progress names every file by its FULL path. Several displays showed
// only the file name - Cleanup's "Deleting backed-up files ... : one.txt", the
// catalog-free restore's destination-relative path - which says nothing about
// where the file is.
//
// Sections 9-11: the checks before the first file is copied count through the
// files at the progress cadence. A run over 2,671,103 added files sat on
// "Checking 2,671,103 file(s) against the catalog..." at 0.0% for sixteen minutes:
// the stat of every candidate, a catalog lookup of every one - made twice - and a
// lookup of deleted history all ran behind that one line, and Pause did nothing.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.Deduplication;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

namespace TargetedProgressTest;

internal static class Program
{
    static int _failures, _checks;

    static void Check(bool cond, string msg)
    {
        _checks++;
        Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
        if (!cond) _failures++;
    }

    static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "lithic_targeted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunAsync(root);
            await FullPathTestsAsync(root);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ FAIL: unhandled " + ex);
            _failures++;
        }
        finally
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? $"ALL {_checks} CHECKS PASSED" : $"{_failures} OF {_checks} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    sealed class Recorder : IProgress<BackupProgress>
    {
        public readonly ConcurrentQueue<BackupProgress> Reports = new();
        public void Report(BackupProgress value) => Reports.Enqueue(value);
        public List<BackupProgress> Data => Reports.Where(r => string.IsNullOrEmpty(r.StatusMessage)).ToList();
        public List<string> Statuses => Reports.Where(r => !string.IsNullOrEmpty(r.StatusMessage))
                                               .Select(r => r.StatusMessage!).ToList();
    }

    /// <summary>Collects reports synchronously, as they are made.</summary>
    sealed class Collect<T>(Action<T> add) : IProgress<T>
    {
        public void Report(T value) => add(value);
    }

    /// <summary>Records each report with the time it was made.</summary>
    sealed class TimedRecorder : IProgress<BackupProgress>
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public readonly ConcurrentQueue<(long Ms, BackupProgress Report)> Reports = new();
        public void Report(BackupProgress value) => Reports.Enqueue((_clock.ElapsedMilliseconds, value));
        public List<(long Ms, BackupProgress Report)> Phase(string name)
            => Reports.Where(r => r.Report.StatusMessage == name && r.Report.PhaseFilesTotal > 0).ToList();
    }

    /// <summary>Wait until <paramref name="condition"/> holds, or the timeout passes.</summary>
    static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
                return false;
            await Task.Delay(20);
        }
        return true;
    }

    static async Task FullPathTestsAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 8. progress names each file by its full path, not just its name ===");

        // Cleanup deleting the backed-up copies of files (DestinationFilePurger).
        string target = Path.Combine(root, "purge-dest");
        string[] rels = [Path.Combine("D", "projects", "one.txt"), Path.Combine("D", "two.txt")];
        foreach (var rel in rels)
        {
            string p = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }
        var purge = new List<string>();
        DestinationFilePurger.DeleteFilesAndSweep(
            target, rels, new Collect<ProgressReport>(r => purge.Add(r.Text)));
        var deleting = purge.Where(m => m.StartsWith("Deleting backed-up files")).ToList();
        Check(deleting.Count > 0
              && deleting.All(m => rels.Any(r => m.EndsWith(": " + Path.Combine(target, r)))),
            $"deleting backed-up copies names each by its full path: \"{deleting.LastOrDefault()}\"");

        // A catalog-free restore (CatalogFreeRestoreService).
        string backup = Path.Combine(root, "cf-backup");
        string output = Path.Combine(root, "cf-output");
        foreach (var rel in new[] { Path.Combine("C", "docs", "a.txt"), Path.Combine("C", "b.txt") })
        {
            string p = Path.Combine(backup, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "restore me");
        }
        var restoring = new List<string>();
        await new CatalogFreeRestoreService().RestoreAsync(backup, output,
            new Collect<RestoreProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    restoring.Add(p.CurrentFile);
            }));
        Check(restoring.Count == 2
              && restoring.All(f => Path.IsPathRooted(f)
                                    && f.StartsWith(output, StringComparison.OrdinalIgnoreCase)
                                    && File.Exists(f)),
            $"a catalog-free restore names each file by the full path it is restored to: \"{restoring.FirstOrDefault()}\"");
    }

    /// <summary>Create <paramref name="count"/> files in a new folder; returns their paths.</summary>
    static List<string> MakeBatch(string src, string name, int count, int bytesEach)
    {
        string dir = Path.Combine(src, name);
        Directory.CreateDirectory(dir);
        var rng = new Random(name.GetHashCode());
        var paths = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[bytesEach + i * 1000];
            rng.NextBytes(bytes);
            string p = Path.Combine(dir, $"{name}_{i:D2}.bin");
            File.WriteAllBytes(p, bytes);
            paths.Add(p);
        }
        return paths;
    }

    static int CountInDest(string dest, string prefix)
        => Directory.Exists(dest)
            ? Directory.EnumerateFiles(dest, prefix + "_*", SearchOption.AllDirectories).Count()
            : 0;

    static async Task RunAsync(string root)
    {
        string src = Path.Combine(root, "src");
        string dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);

        var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "targeted-progress-test",
            SourceRoots = [src],
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = dest },
            SourceSelections =
            [
                new SourceSelection { Path = src, IsDirectory = true, IsSelected = true },
            ],
        });
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        var job = new BackupJob
        {
            BackupSetId = set.Id,
            Sources = [new SourceSelection { Path = src, IsDirectory = true, IsSelected = true }],
            TargetDirectory = dest,
        };
        var tiers = VersionRetentionService.DefaultTiers;

        // ------------------------------------------------------------ progress
        Console.WriteLine("=== 1. a targeted run reports what the progress panel needs ===");
        var batch1 = MakeBatch(src, "one", 12, 256 * 1024);
        long total1 = batch1.Sum(p => new FileInfo(p).Length);
        var rec1 = new Recorder();
        var result1 = await svc.ExecuteTargetedAsync(job, dest, batch1, tiers, CancellationToken.None, rec1);

        Check(result1.Success && result1.FailedFiles.Count == 0, "the run succeeds");
        Check(CountInDest(dest, "one") == 12, $"all 12 files are at the destination ({CountInDest(dest, "one")})");
        var first1 = rec1.Reports.FirstOrDefault();
        Check(first1 is { StatusMessage: "Checking files against the catalog", PhaseFilesDone: 0, PhaseFilesTotal: 12 },
            $"it starts by saying what it is doing (\"{first1?.StatusMessage}\", {first1?.PhaseFilesDone} of {first1?.PhaseFilesTotal})");
        var data1 = rec1.Data;
        var namedFiles = data1.Select(r => r.CurrentFile).Where(f => !string.IsNullOrEmpty(f))
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(batch1.All(namedFiles.Contains),
            $"every file is named as the current file while it is copied ({namedFiles.Count} of 12)");
        Check(data1.Count > 0 && data1.All(r => r.BytesTotalAll == total1),
            $"every report carries the run's byte total ({total1:N0})");
        var pcts = data1.Select(r => r.OverallPercentage).ToList();
        Check(pcts.Count > 1 && pcts.Zip(pcts.Skip(1)).All(p => p.Second >= p.First),
            "the percentage only ever goes up");
        Check(data1.Any(r => r.CurrentFileTotalBytes > 0), "the current file's own size is reported, for its progress bar");
        Check(rec1.Reports.Any(r => r.OverallPercentage >= 100), "and it finishes at 100%");

        // ------------------------------------------------------------ pause
        Console.WriteLine();
        Console.WriteLine("=== 2. Pause holds it; resuming finishes it ===");
        var batch2 = MakeBatch(src, "two", 6, 128 * 1024);
        var rec2 = new Recorder();
        using var pause = new ManualResetEventSlim(false);   // paused before it starts
        var run2 = Task.Run(() => svc.ExecuteTargetedAsync(job, dest, batch2, tiers, CancellationToken.None, rec2, pause));
        await Task.Delay(1500);
        Check(!run2.IsCompleted, "while paused, the run waits");
        Check(CountInDest(dest, "two") == 0 && rec2.Data.Count == 0,
            $"and copies nothing ({CountInDest(dest, "two")} file(s) at the destination)");
        pause.Set();
        var result2 = await run2;
        Check(result2.Success && CountInDest(dest, "two") == 6, "resumed, it copies all 6 and finishes");

        // ------------------------------------------------------------ cancel
        Console.WriteLine();
        Console.WriteLine("=== 3. Cancel stops it ===");
        var batch3 = MakeBatch(src, "three", 6, 128 * 1024);
        using var pause3 = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        var run3 = Task.Run(() => svc.ExecuteTargetedAsync(job, dest, batch3, tiers, cts.Token, new Recorder(), pause3));
        await Task.Delay(500);
        cts.Cancel();
        bool cancelled = false;
        try { await run3; }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancelling (even while paused) ends the run with a cancellation");
        Check(CountInDest(dest, "three") == 0, "having copied nothing");

        // ------------------------------------------------------------ worker's call
        Console.WriteLine();
        Console.WriteLine("=== 4. the continuous-backup Worker's call is unchanged ===");
        var batch4 = MakeBatch(src, "four", 3, 64 * 1024);
        var result4 = await svc.ExecuteTargetedAsync(job, dest, batch4, tiers, CancellationToken.None);
        Check(result4.Success && CountInDest(dest, "four") == 3, "without a progress reporter it still backs up");

        // ------------------------------------------------------------ never user data
        Console.WriteLine();
        Console.WriteLine("=== 5. the recycle bin and other Windows system items are never backed up ===");
        string[] never =
        [
            @"D:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RQF2JJ4\x.py",
            @"D:\$RECYCLE.BIN",
            @"C:\System Volume Information\tracking.log",
            @"C:\pagefile.sys", @"C:\hiberfil.sys", @"E:\swapfile.sys",
            @"\\server\share\$Recycle.Bin\x",
            @"C:\$Extend\$Deleted\0014\mpasbase.vdm",
        ];
        string[] always =
        [
            @"D:\projects\$Recycle.Bin\x.py",          // a user folder that happens to share the name
            @"D:\pagefile.sys.bak",
            @"D:\sub\pagefile.sys",
            @"D:\System Volume Information Notes\a.txt",
            @"D:\hiberfil.system\a",
            @"D:\$RecycleBin\x",
        ];
        var filter = DirectoryBackupService.BuildExclusionFilter([], []);
        var prune = DirectoryBackupService.BuildDirectoryPruneFilter([], []);
        foreach (var p in never)
            Check(VolumeMetadataPaths.IsVolumeMetadata(p) && filter!(p), $"excluded: {p}");
        foreach (var p in always)
            Check(!VolumeMetadataPaths.IsVolumeMetadata(p) && !filter!(p), $"still backed up: {p}");
        Check(prune!(@"D:\$Recycle.Bin") && prune(@"C:\System Volume Information"),
            "a scan does not even walk into the recycle bin or System Volume Information");
        Check(!prune(@"D:\projects"), "but walks everything else as before");

        // ------------------------------------------------------------ hashing progress
        Console.WriteLine();
        Console.WriteLine("=== 6. reading a big file to check for duplicates reports progress ===");
        const int big = 3 * 1024 * 1024;
        string fiveDir = Path.Combine(src, "five");
        string sixDir = Path.Combine(src, "six");
        Directory.CreateDirectory(fiveDir);
        Directory.CreateDirectory(sixDir);
        var bytesA = new byte[big]; new Random(1).NextBytes(bytesA);
        var bytesB = new byte[big]; new Random(2).NextBytes(bytesB);
        string fileA = Path.Combine(fiveDir, "five_a.bin"), fileB = Path.Combine(sixDir, "six_b.bin");
        // Whole-file duplicate detection on, as on both real sets: a file whose
        // size matches stored content is read and hashed before anything is copied.
        var dedupJob = new BackupJob
        {
            BackupSetId = job.BackupSetId,
            Sources = job.Sources,
            TargetDirectory = dest,
            EnableFileDeduplication = true,
        };
        File.WriteAllBytes(fileA, bytesA);
        await svc.ExecuteTargetedAsync(dedupJob, dest, [fileA], tiers, CancellationToken.None);
        File.WriteAllBytes(fileB, bytesB);   // same size as a stored file: a duplicate candidate
        var rec6 = new Recorder();
        var result6 = await svc.ExecuteTargetedAsync(dedupJob, dest, [fileB], tiers, CancellationToken.None, rec6);
        var checking = rec6.Reports.Where(r => r.CurrentFileActivity == "Checking for duplicates").ToList();
        Check(result6.Success && CountInDest(dest, "six") == 1, "the file is backed up (it is not actually a duplicate)");
        Check(checking.Count > 0 && checking.All(r => r.CurrentFile == fileB),
            $"the read before copying is reported, on the right file ({checking.Count} report(s))");
        Check(checking.Count > 0 && checking[^1].CurrentFileBytesWritten == big,
            "and it runs to the file's full size");
        Check(rec6.Data.Any(r => r.CurrentFileActivity is null && r.CurrentFile == fileB),
            "then the copy reports as a copy");

        // ------------------------------------------------------------ the check before copying
        Console.WriteLine();
        Console.WriteLine("=== 9. the check before copying counts through the files, at the progress cadence ===");
        // Candidates that no longer exist still cost a stat each and are counted -
        // the count is of the files the user is waiting on, whatever becomes of
        // them - so enough of them stretch the check over a few seconds.
        const int Ghosts = 400_000;
        string ghostDir = Path.Combine(src, "ghosts");
        Directory.CreateDirectory(ghostDir);
        var ghosts = Enumerable.Range(0, Ghosts).Select(i => Path.Combine(ghostDir, $"gone_{i:D7}.bin")).ToList();
        var batch9 = MakeBatch(src, "nine", 5, 4 * 1024);
        var candidates9 = batch9.Concat(ghosts).ToList();
        var (countingRepo, counts) = CountingCatalog.Wrap(repo);
        var countingSvc = new DirectoryBackupService(
            countingRepo, new FileScanner(countingRepo), new VersionRetentionService(countingRepo));
        var rec9 = new TimedRecorder();
        var result9 = await countingSvc.ExecuteTargetedAsync(job, dest, candidates9, tiers, CancellationToken.None, rec9);
        var check9 = rec9.Phase("Checking files against the catalog");
        Check(result9.Success && CountInDest(dest, "nine") == 5, "the 5 candidates that exist are backed up");
        Check(check9.Count > 0 && check9.All(r => r.Report.PhaseFilesTotal == candidates9.Count),
            $"the check reports its progress against all {candidates9.Count:N0} candidates ({check9.Count} report(s))");
        Check(check9.Count > 0 && check9[0].Report.PhaseFilesDone == 0, "starting at 0, before the first file");
        var done9 = check9.Select(r => r.Report.PhaseFilesDone).ToList();
        Check(done9.Count > 1 && done9.Zip(done9.Skip(1)).All(p => p.Second > p.First) && done9[^1] <= candidates9.Count,
            $"then counting up ({string.Join(", ", done9.Select(d => d.ToString("N0")))})");
        var candidateSet = candidates9.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(check9.Count > 1 && check9.Skip(1).All(r => candidateSet.Contains(r.Report.CurrentFile)),
            "each report names the candidate it has reached");
        // How long the check ran: until the first report after it.
        long start9 = check9.FirstOrDefault().Ms;
        long end9 = rec9.Reports.Where(r => r.Ms >= check9.LastOrDefault().Ms && !check9.Contains(r))
                                .Select(r => r.Ms).DefaultIfEmpty(check9.LastOrDefault().Ms).Min();
        Check(end9 - start9 >= 3 * ProgressCadence.IntervalMs,
            $"(the check ran {end9 - start9:N0} ms - long enough to judge its cadence)");
        var gaps9 = check9.Zip(check9.Skip(1)).Select(p => p.Second.Ms - p.First.Ms).ToList();
        Check(gaps9.Count > 0 && gaps9.Min() >= ProgressCadence.IntervalMs - 50,
            $"reports come no faster than the progress cadence (closest {gaps9.DefaultIfEmpty().Min()} ms apart)");
        Check(gaps9.Count > 0 && gaps9.Max() <= 3 * ProgressCadence.IntervalMs
              && end9 - check9.LastOrDefault().Ms <= 3 * ProgressCadence.IntervalMs,
            $"and the count never stands still for long (longest gap {gaps9.DefaultIfEmpty().Max()} ms)");
        Check(counts.LivePaths == 5,
            $"each candidate that exists is looked up in the catalog once, not again before copying ({counts.LivePaths} lookups for 5)");
        Check(counts.OrphanedPaths == 5,
            $"and its deleted history once ({counts.OrphanedPaths} for 5 new files)");
        Check(rec9.Phase("Looking up earlier versions").Count == 0,
            "the copy reuses what the check found instead of looking it up again");

        Console.WriteLine();
        Console.WriteLine("=== 9b. Pause holds the check ===");
        var batch9b = MakeBatch(src, "ninebee", 3, 4 * 1024);
        var candidates9b = batch9b.Concat(ghosts).ToList();
        var rec9b = new TimedRecorder();
        using var pause9 = new ManualResetEventSlim(true);
        var run9b = Task.Run(() => svc.ExecuteTargetedAsync(
            job, dest, candidates9b, tiers, CancellationToken.None, rec9b, pause9));
        long Reached9b() => rec9b.Phase("Checking files against the catalog")
                                 .Select(r => r.Report.PhaseFilesDone).DefaultIfEmpty(-1).Max();
        bool underway = await WaitForAsync(() => Reached9b() > 0, 30_000);
        pause9.Reset();
        await Task.Delay(ProgressCadence.IntervalMs);   // a report already on its way lands
        long heldAt = Reached9b();
        await Task.Delay(4 * ProgressCadence.IntervalMs);
        Check(underway && Reached9b() == heldAt && !run9b.IsCompleted,
            $"paused part-way, the count stops ({heldAt:N0} of {candidates9b.Count:N0}) and the run waits");
        pause9.Set();
        var result9b = await run9b;
        Check(result9b.Success && CountInDest(dest, "ninebee") == 3, "resumed, it finishes and backs up the 3 files");

        // ------------------------------------------------------------ the other checks
        Console.WriteLine();
        Console.WriteLine("=== 10. the other checks before copying report their progress too ===");
        // A full backup hands ExecuteAsync a diff without versions: it looks them up itself.
        var batch10 = MakeBatch(src, "ten", 4, 4 * 1024);
        var diff10 = new BackupDiff
        {
            NewFiles = batch10.Select(p => new ScannedFile
            {
                FullPath = p, SizeBytes = new FileInfo(p).Length, LastWriteUtc = File.GetLastWriteTimeUtc(p),
            }).ToList(),
            ChangedFiles = [],
            DeletedFiles = [],
        };
        var rec10 = new TimedRecorder();
        var result10 = await svc.ExecuteAsync(job, dest, tiers, rec10, CancellationToken.None, precomputedDiff: diff10);
        var lookup10 = rec10.Phase("Looking up earlier versions");
        Check(result10.Success && CountInDest(dest, "ten") == 4, "a backup handed a diff but no versions backs it up");
        Check(lookup10.Count > 0 && lookup10.All(r => r.Report.PhaseFilesTotal == 4),
            $"reporting as it looks up their earlier versions ({lookup10.Count} report(s))");

        var batch10b = MakeBatch(src, "tenbee", 3, 4 * 1024);
        var rec10b = new TimedRecorder();
        var result10b = await svc.ExecuteTargetedAsync(dedupJob, dest, batch10b, tiers, CancellationToken.None, rec10b);
        var probe10 = rec10b.Phase("Checking which files might be duplicates");
        Check(result10b.Success && probe10.Count > 0 && probe10.All(r => r.Report.PhaseFilesTotal == 3),
            $"with duplicate detection on, comparing sizes with what is stored reports too ({probe10.Count} report(s))");

        var blockSvc = new DirectoryBackupService(
            repo, new FileScanner(repo), new VersionRetentionService(repo), new BlockDeduplicationEngine());
        var blockJob = new BackupJob
        {
            BackupSetId = job.BackupSetId,
            Sources = job.Sources,
            TargetDirectory = dest,
            EnableDeduplication = true,
        };
        var batch10c = MakeBatch(src, "tencee", 3, 64 * 1024);
        var rec10c = new TimedRecorder();
        var result10c = await blockSvc.ExecuteTargetedAsync(blockJob, dest, batch10c, tiers, CancellationToken.None, rec10c);
        var analyze10 = rec10c.Phase("Analyzing files for deduplication");
        Check(result10c.Success && analyze10.Count > 0 && analyze10.All(r => r.Report.PhaseFilesTotal == 3),
            $"so does block deduplication's read of every file ({analyze10.Count} report(s))");

        // ------------------------------------------------------------ after the copy
        Console.WriteLine();
        Console.WriteLine("=== 12. verifying reports at the cadence, not once per file ===");
        var verifyJob = new BackupJob
        {
            BackupSetId = job.BackupSetId,
            Sources = job.Sources,
            TargetDirectory = dest,
            VerifyAfterBackup = true,
        };
        var batch12 = MakeBatch(src, "twelve", 300, 1024);
        var rec12 = new TimedRecorder();
        var result12 = await svc.ExecuteTargetedAsync(verifyJob, dest, batch12, tiers, CancellationToken.None, rec12);
        var verify12 = rec12.Phase("Verifying backup");
        var verifyAll12 = rec12.Reports.Where(r => r.Report.StatusMessage?.StartsWith("Verifying") == true).ToList();
        long verifyMs = verifyAll12.Count > 0 ? verifyAll12[^1].Ms - verifyAll12[0].Ms : 0;
        Check(result12.Success && CountInDest(dest, "twelve") == 300, "300 files are backed up and verified");
        Check(verify12.Count > 0 && verify12.All(r => r.Report.PhaseFilesTotal == 300),
            $"verifying counts through them ({verify12.Count} report(s))");
        Check(verifyAll12.Count <= 1 + verifyMs / (ProgressCadence.IntervalMs - 50),
            $"at the progress cadence, not once per file ({verifyAll12.Count} report(s) in {verifyMs} ms)");

        Console.WriteLine();
        Console.WriteLine("=== 13. retention reads only the files given a second version, and reports ===");
        // Keep one previous version: a file's third backup prunes its first.
        var keepOne = new List<VersionRetentionTier> { new() { MaxAge = null, MaxVersions = 1 } };
        string histDir = Path.Combine(src, "thirteen");
        Directory.CreateDirectory(histDir);
        string hist = Path.Combine(histDir, "thirteen_history.txt");
        File.WriteAllText(hist, "version one");
        await svc.ExecuteTargetedAsync(job, dest, [hist], keepOne, CancellationToken.None);
        File.WriteAllText(hist, "version two, longer");
        await svc.ExecuteTargetedAsync(job, dest, [hist], keepOne, CancellationToken.None);
        File.WriteAllText(hist, "version three, longer still");
        var batch13 = MakeBatch(src, "thirteen_new", 20, 1024);
        var (retentionRepo, retentionCounts) = CountingCatalog.Wrap(repo);
        var retentionSvc = new DirectoryBackupService(
            retentionRepo, new FileScanner(retentionRepo), new VersionRetentionService(retentionRepo));
        var rec13 = new TimedRecorder();
        var result13 = await retentionSvc.ExecuteTargetedAsync(
            job, dest, [hist, .. batch13], keepOne, CancellationToken.None, rec13);
        var records13 = await repo.GetFileRecordsByPathAsync(set.Id, hist);
        var v1 = records13.FirstOrDefault(r => r.Version == 1);
        Check(result13.Success && records13.Count == 3, $"the file has three versions ({records13.Count})");
        Check(v1 is { IsDeleted: true } && !File.Exists(Path.Combine(dest, v1.DiscPath)),
            "and its first, one past the one previous version kept, is pruned from catalog and disk");
        Check(retentionCounts.RecordReads == 1,
            $"retention read the records of that file alone, not of the 20 backed up for the first time ({retentionCounts.RecordReads} read(s))");
        var applying13 = rec13.Phase("Applying retention rules");
        var removing13 = rec13.Phase("Removing expired versions");
        Check(applying13.Count > 0 && applying13.All(r => r.Report.PhaseFilesTotal == 1)
              && removing13.Count > 0 && removing13.All(r => r.Report.PhaseFilesTotal == 1),
            $"both retention steps report their progress ({applying13.Count}, {removing13.Count} report(s))");

        await Task.Run(PanelTests);
    }

    // ------------------------------------------------------------ the panel
    static void PanelTests()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await PanelTestsAsync(); }
                catch (Exception ex) { error = ex; }
                finally { dispatcher.InvokeShutdown(); }
            });
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            Check(false, "the panel tests threw: " + error);
    }

    static BackupProgress Data(string file, int pct, string? activity = null) => new()
    {
        CurrentDisc = 1, TotalDiscs = 1, CurrentFile = file,
        BytesWrittenTotal = pct, BytesTotalAll = 100, OverallPercentage = pct,
        CurrentFileTotalBytes = 50, CurrentFileActivity = activity,
    };

    static async Task PanelTestsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 7. the progress panel never sticks on a file it has finished ===");
        var vm = new LithicBackup.ViewModels.BurnProgressViewModel { IsDirectoryMode = true };
        vm.StartBurn();

        vm.OnBackupProgress(Data(@"D:\x\small.py", 1));
        Check(vm.CurrentFile == @"D:\x\small.py", "a file shows at once");
        vm.OnBackupProgress(Data(@"D:\x\big.iso", 2));
        Check(vm.CurrentFile == @"D:\x\small.py", "the next report, inside the half-second throttle, waits its turn");
        await Task.Delay(900);
        Check(vm.CurrentFile == @"D:\x\big.iso",
            "and is shown when its turn comes - it used to be dropped, leaving small.py on screen for all of big.iso");

        await Task.Delay(600);
        vm.OnBackupProgress(Data(@"D:\x\big.iso", 3, "Checking for duplicates"));
        Check(vm.StatusText == "Checking for duplicates...", $"a read before copying says so (\"{vm.StatusText}\")");
        await Task.Delay(600);
        vm.OnBackupProgress(Data(@"D:\x\big.iso", 4));
        Check(vm.StatusText == "Copying files...", "and the copy says copying");

        await Task.Delay(600);
        vm.OnBackupProgress(Data(@"D:\x\c.txt", 5));
        vm.OnBackupProgress(Data(@"D:\x\late.txt", 6));   // held back
        vm.CompleteBurn(true, "");
        await Task.Delay(900);
        Check(vm.StatusText == "Backup completed." && vm.CurrentFile != @"D:\x\late.txt",
            "a report still waiting when the run finishes cannot overwrite the result");

        Console.WriteLine();
        Console.WriteLine("=== 11. the panel shows a phase's count, bar, file, rate and time left ===");
        var pvm = new LithicBackup.ViewModels.BurnProgressViewModel { IsDirectoryMode = true };
        pvm.StartBurn();
        static BackupProgress Phase(long done, string file = "") => new()
        {
            StatusMessage = "Checking files against the catalog", CurrentFile = file,
            PhaseFilesDone = done, PhaseFilesTotal = 2_671_103,
        };

        pvm.OnBackupProgress(Phase(0));
        Check(pvm.StatusText == "Checking files against the catalog (0 of 2,671,103)...",
            $"it says how far through how many files it is (\"{pvm.StatusText}\")");
        Check(pvm.StatsText.Contains("elapsed") && !pvm.StatsText.Contains("files/s"),
            $"with no rate before there is a second of it to measure (\"{pvm.StatsText}\")");
        await Task.Delay(1100);
        pvm.OnBackupProgress(Phase(1_000_000, @"C:\music\a.mod"));
        Check(pvm.StatusText == "Checking files against the catalog (1,000,000 of 2,671,103)...",
            $"the count moves (\"{pvm.StatusText}\")");
        Check(Math.Abs(pvm.OverallPercentage - 1_000_000 / 2_671_103.0 * 100) < 0.001,
            $"the bar shows the phase's own progress ({pvm.OverallPercentage:F1}%)");
        Check(pvm.CurrentFile == @"C:\music\a.mod", "and names the file it is on");
        Check(pvm.StatsText.Contains(" files/s") && pvm.StatsText.Contains(" remaining") && !pvm.StatsText.Contains("--:--"),
            $"and how fast it goes and how long it has left (\"{pvm.StatsText}\")");

        pvm.PauseResumeCommand.Execute(null);
        pvm.OnBackupProgress(Phase(1_000_500));
        Check(pvm.StatusText == "Paused", $"a report arriving after Pause leaves \"Paused\" up (\"{pvm.StatusText}\")");
        pvm.PauseResumeCommand.Execute(null);
        Check(pvm.StatusText == "Checking files against the catalog (1,000,500 of 2,671,103)...",
            $"and Resume shows where the phase had got to (\"{pvm.StatusText}\")");

        pvm.OnBackupProgress(new BackupProgress
        {
            StatusMessage = "Waiting for the background backup of this set to pause...",
        });
        Check(pvm.OverallPercentage == 0 && pvm.StatsText == "",
            $"a message that is not about the phase takes its bar and rate down ({pvm.OverallPercentage}%, \"{pvm.StatsText}\")");

        pvm.OnBackupProgress(Data(@"D:\x\c.txt", 10));
        Check(pvm.StatusText == "Copying files..." && pvm.StatsText.StartsWith("10 / 100  ·  "),
            $"and the copy then shows its bytes (\"{pvm.StatsText}\")");
        pvm.CompleteBurn(true, "");
    }
}

/// <summary>
/// A catalog that counts the paths each version lookup is asked about, and
/// otherwise passes every call through to the real one.
/// </summary>
public class CountingCatalog : DispatchProxy
{
    public ICatalogRepository Inner = null!;
    public long LivePaths, OrphanedPaths, RecordReads;

    public static (ICatalogRepository Catalog, CountingCatalog Counts) Wrap(ICatalogRepository inner)
    {
        var proxy = Create<ICatalogRepository, CountingCatalog>();
        var counts = (CountingCatalog)(object)proxy;
        counts.Inner = inner;
        return (proxy, counts);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(ICatalogRepository.GetLatestVersionInfoForPathsAsync))
            Interlocked.Add(ref LivePaths, ((IReadOnlyCollection<string>)args![1]!).Count);
        else if (method.Name == nameof(ICatalogRepository.GetOrphanedVersionInfoForPathsAsync))
            Interlocked.Add(ref OrphanedPaths, ((IReadOnlyCollection<string>)args![1]!).Count);
        else if (method.Name == nameof(ICatalogRepository.GetFileRecordsByPathAsync))
            Interlocked.Increment(ref RecordReads);
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
}
