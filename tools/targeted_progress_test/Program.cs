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
//
// Sections 12-13: verifying and retention after the copy report the same way,
// and a targeted run's retention reads only the files it gave a further version.
//
// Section 14: a version another writer (the Worker's continuous backup) adds while
// a run is under way is followed, not given the same number again.
//
// Section 18: a run that would take 62 days says "about 62 days", not
// "~1492:01:56" (whose tilde read as a minus sign), and never a negative time;
// an advisory stays on the panel; and SlowFileCreationMonitor warns once, with
// the fsutil command when the drive makes 8.3 names - a real backup spent 779 ms
// creating each file in a folder of 1.4 million on such a drive, 4.2 ms with
// short names off. ShortNameSetting is checked against the short names Windows
// actually gives a new file.

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
            await AdvisoryTestsAsync(root);
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

    static async Task AdvisoryTestsAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 18. long estimates, advisories, slow file creation ===");
        string F(TimeSpan t) => LithicBackup.ViewModels.BurnProgressViewModel.FormatRemaining(t);
        Check(F(TimeSpan.FromHours(1492) + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(56)) == "62 days",
            $"1492:01:56 left reads as 62 days (\"{F(TimeSpan.FromHours(1492))}\")");
        Check(F(TimeSpan.FromHours(30)) == "30:00:00", $"under two days it stays in hours (\"{F(TimeSpan.FromHours(30))}\")");
        Check(F(TimeSpan.FromMinutes(5)) == "05:00", "under an hour, minutes and seconds");
        Check(F(TimeSpan.FromSeconds(-90)) == "00:00", $"never a negative time (\"{F(TimeSpan.FromSeconds(-90))}\")");

        // A copy going at about a byte a second, of ten million: months.
        var dvm = new LithicBackup.ViewModels.BurnProgressViewModel { IsDirectoryMode = true };
        dvm.StartBurn();
        static BackupProgress Bytes(long done) => new()
        {
            CurrentDisc = 1, TotalDiscs = 1, CurrentFile = @"D:\x\slow.flac",
            BytesWrittenTotal = done, BytesTotalAll = 10_000_000, OverallPercentage = done / 100_000.0,
        };
        dvm.OnBackupProgress(Bytes(0));
        await Task.Delay(1100);
        dvm.OnBackupProgress(Bytes(1));
        Check(dvm.StatsText.Contains("about ") && dvm.StatsText.EndsWith(" days remaining") && !dvm.StatsText.Contains('~'),
            $"the panel says a long copy will take days, with no tilde (\"{dvm.StatsText}\")");

        string stats = dvm.StatsText, status = dvm.StatusText;
        dvm.OnBackupProgress(new BackupProgress { Advisory = "Creating files on G: is slow." });
        Check(dvm.AdvisoryText == "Creating files on G: is slow." && dvm.StatsText == stats && dvm.StatusText == status,
            "an advisory goes on its own line and leaves the rest of the panel as it was");
        await Task.Delay(1100);
        dvm.OnBackupProgress(Bytes(2));
        Check(dvm.AdvisoryText == "Creating files on G: is slow.", "it stays up as the run goes on");
        dvm.CompleteBurn(true, "");
        dvm.StartBurn();
        Check(dvm.AdvisoryText == "", "and the next run starts without it");
        dvm.CompleteBurn(true, "");

        // The monitor: a window of 32, a quarter of a second, once.
        var said = new List<string>();
        var monitor = new SlowFileCreationMonitor(said.Add, _ => true);
        for (int i = 0; i < SlowFileCreationMonitor.Window - 1; i++)
            monitor.Record($@"G:\b\samples\f{i}.flac", TimeSpan.FromMilliseconds(780));
        Check(said.Count == 0, "no warning before a full window of files");
        monitor.Record(@"G:\b\samples\f31.flac", TimeSpan.FromMilliseconds(780));
        Check(said.Count == 1, "a full window averaging 780 ms warns");
        Check(said.Count == 1 && said[0].Contains("780 ms") && said[0].Contains(@"G:\b\samples")
              && said[0].Contains("fsutil 8dot3name set G: 1"),
            $"naming the time, the folder and, as short names are on, the command that fixes it (\"{(said.Count > 0 ? said[0] : "")}\")");
        for (int i = 0; i < 100; i++)
            monitor.Record($@"G:\b\samples\g{i}.flac", TimeSpan.FromSeconds(2));
        Check(said.Count == 1 && monitor.HasWarned, "once per run");

        var quiet = new List<string>();
        var fast = new SlowFileCreationMonitor(quiet.Add, _ => true);
        for (int i = 0; i < 500; i++)
            fast.Record($@"G:\b\f{i}", TimeSpan.FromMilliseconds(i % 50 == 0 ? 3000 : 5));
        Check(quiet.Count == 0, "the odd slow file among fast ones does not warn");

        var stall = new List<string>();
        var stalled = new SlowFileCreationMonitor(stall.Add, _ => true);
        stalled.Record(@"G:\b\s0", TimeSpan.FromSeconds(8));
        for (int i = 1; i < SlowFileCreationMonitor.Window; i++)
            stalled.Record($@"G:\b\s{i}", TimeSpan.FromMilliseconds(7));
        Check(stall.Count == 0, "nor does one stall of seconds in a window of fast ones (it lifts the mean over the line)");
        var mostly = new List<string>();
        var most = new SlowFileCreationMonitor(mostly.Add, _ => true);
        for (int i = 0; i < SlowFileCreationMonitor.Window; i++)
            most.Record($@"G:\b\m{i}", TimeSpan.FromMilliseconds(i < 15 ? 7 : 300));
        Check(mostly.Count == 1 && mostly[0].Contains("300 ms"), "most of a window slow does, with their time");

        Check(ShortNameSetting.Effective(0, false, () => false) == true
              && ShortNameSetting.Effective(1, true, () => true) == false
              && ShortNameSetting.Effective(3, true, () => false) == true
              && ShortNameSetting.Effective(3, false, () => true) == false
              && ShortNameSetting.Effective(2, false, () => true) == true
              && ShortNameSetting.Effective(null, false, () => null) is null,
            "Windows' setting for every volume decides before the volume's own flag");
        Check(SlowFileCreationMonitor.Describe(@"G:\b", TimeSpan.FromMilliseconds(400), true, setForAllVolumes: true)
                  .Contains("\"fsutil 8dot3name set 1\""),
            "and when it is that setting, the advice is the command that changes it");

        var noShort = new List<string>();
        var off = new SlowFileCreationMonitor(noShort.Add, _ => false);
        for (int i = 0; i < SlowFileCreationMonitor.Window; i++)
            off.Record($@"H:\b\f{i}", TimeSpan.FromMilliseconds(600));
        Check(noShort.Count == 1 && !noShort[0].Contains("fsutil") && noShort[0].Contains("antivirus"),
            "with short names off it warns without the fsutil advice");

        // ShortNameSetting against what Windows actually does with a new long name.
        string probeDir = Path.Combine(root, "shortnames");
        Directory.CreateDirectory(probeDir);
        string longName = Path.Combine(probeDir, "a rather long file name for the short name probe.txt");
        File.WriteAllText(longName, "x");
        var buffer = new System.Text.StringBuilder(1024);
        uint got = Native.GetShortPathNameW(longName, buffer, (uint)buffer.Capacity);
        bool? reported = ShortNameSetting.IsCreationEnabled(probeDir);
        if (got == 0)
        {
            Console.WriteLine("     (GetShortPathName failed; short-name check skipped)");
        }
        else
        {
            bool actual = !string.Equals(Path.GetFileName(buffer.ToString()), Path.GetFileName(longName),
                StringComparison.OrdinalIgnoreCase);
            Check(reported == actual,
                $"ShortNameSetting says {reported} for {Path.GetPathRoot(probeDir)}, and a new long name there "
                + $"{(actual ? "got" : "did not get")} a short one (\"{Path.GetFileName(buffer.ToString())}\")");
        }
        Check(ShortNameSetting.IsCreationEnabled(Path.Combine(probeDir, "no such folder")) is null,
            "a path that is not there gets no answer rather than a wrong one");
        foreach (var drive in new[] { @"G:\", @"H:\" })
            if (Directory.Exists(drive))
                Console.WriteLine($"     ({drive} makes short names: {ShortNameSetting.IsCreationEnabled(drive)})");
    }

    static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint size);
    }

    sealed class Recorder : IProgress<BackupProgress>
    {
        public readonly ConcurrentQueue<BackupProgress> Reports = new();
        public void Report(BackupProgress value) => Reports.Enqueue(value);
        public List<BackupProgress> Data => Reports.Where(r => string.IsNullOrEmpty(r.StatusMessage)).ToList();
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
            target, rels, new SyncProgress<ProgressReport>(r => purge.Add(r.Text)));
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
            new SyncProgress<RestoreProgress>(p =>
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
        Check(check9.Count > 1 && check9[1].Report.PhaseFilesDone == 1 && check9[1].Report.CurrentFile == candidates9[0],
            "then names the first file at once, the start report not having used up the cadence");
        // After those two, the cadence.
        var gaps9 = check9.Skip(1).Zip(check9.Skip(2)).Select(p => p.Second.Ms - p.First.Ms).ToList();
        Check(gaps9.Count > 0 && gaps9.Min() >= ProgressCadence.IntervalMs - 50,
            $"reports come no faster than the progress cadence (closest {gaps9.DefaultIfEmpty().Min()} ms apart)");
        Check(gaps9.Count > 0 && gaps9.Max() <= 3 * ProgressCadence.IntervalMs
              && end9 - check9.LastOrDefault().Ms <= 3 * ProgressCadence.IntervalMs,
            $"and the count never stands still for long (longest gap {gaps9.DefaultIfEmpty().Max()} ms)");
        Check(counts.LivePaths == 5,
            $"each candidate that exists is looked up in the catalog once, with no second pass before copying ({counts.LivePaths} lookups for 5)");
        Check(counts.LastVersionReads == 5,
            $"and its last version read once more as it is written, under the write lock ({counts.LastVersionReads} for 5)");

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
        // The start, the first file, then one a cadence.
        Check(verifyAll12.Count <= 2 + verifyMs / (ProgressCadence.IntervalMs - 50),
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

        // ------------------------------------------------------------ another writer
        Console.WriteLine();
        Console.WriteLine("=== 14. a version another writer adds meanwhile is followed, not duplicated ===");
        // The Worker's continuous backup can commit a version of a file between two
        // of a run's transactions. A run that numbered its versions from a lookup
        // made earlier gave the file that same number again and moved its copy
        // over the older version's in _prev.
        string raceDir = Path.Combine(src, "fourteen");
        Directory.CreateDirectory(raceDir);

        // Between the check and the copy.
        string raced = Path.Combine(raceDir, "fourteen_check.txt");
        File.WriteAllText(raced, "one");
        await svc.ExecuteTargetedAsync(job, dest, [raced], tiers, CancellationToken.None);
        File.WriteAllText(raced, "two, edited");
        var (raceRepo, raceCounts) = CountingCatalog.Wrap(repo);
        raceCounts.AfterLiveLookup = async () =>
        {
            await svc.ExecuteTargetedAsync(job, dest, [raced], tiers, CancellationToken.None);
            File.WriteAllText(raced, "three, edited again");
        };
        var raceSvc = new DirectoryBackupService(
            raceRepo, new FileScanner(raceRepo), new VersionRetentionService(raceRepo));
        var result14 = await raceSvc.ExecuteTargetedAsync(job, dest, [raced], tiers, CancellationToken.None);
        var (intact14, detail14) = await VersionsIntactAsync(repo, set.Id, dest, raced, 3);
        Check(result14.Success && intact14,
            $"written after the check: versions 1-3, each copy holding its own content ({detail14})");

        // At a commit boundary, between two of the copy's own transactions.
        string boundary = Path.Combine(raceDir, "fourteen_boundary.txt");
        File.WriteAllText(boundary, "one");
        await svc.ExecuteTargetedAsync(job, dest, [boundary], tiers, CancellationToken.None);
        var fillers = MakeBatch(src, "fourteen_fill", 50, 512);   // one commit batch ahead of it
        File.WriteAllText(boundary, "two, edited");
        static ScannedFile Scan(string p) => new()
        {
            FullPath = p, SizeBytes = new FileInfo(p).Length, LastWriteUtc = File.GetLastWriteTimeUtc(p),
        };
        var diff14 = new BackupDiff
        {
            NewFiles = fillers.Select(Scan).ToList(),
            ChangedFiles = [Scan(boundary)],
            DeletedFiles = [],
        };
        bool boundaryHit = false;
        var result14b = await svc.ExecuteAsync(job, dest, tiers, null, CancellationToken.None,
            precomputedDiff: diff14,
            onCommitBoundary: async (_, _) =>
            {
                if (boundaryHit)
                    return;
                boundaryHit = true;
                await svc.ExecuteTargetedAsync(job, dest, [boundary], tiers, CancellationToken.None);
                File.WriteAllText(boundary, "three, edited again");
            });
        var (intact14b, detail14b) = await VersionsIntactAsync(repo, set.Id, dest, boundary, 3);
        Check(result14b.Success && boundaryHit && intact14b,
            $"written at a commit boundary: versions 1-3, each copy holding its own content ({detail14b})");

        // ------------------------------------------------------------ cancel and pause after the copy
        Console.WriteLine();
        Console.WriteLine("=== 15. Cancel and Pause hold after the copy too ===");
        var batch15 = MakeBatch(src, "fifteen", 40, 1024);
        using var cts15 = new CancellationTokenSource();
        var cancelAtVerify = new SyncProgress<BackupProgress>(p =>
        {
            if (p.StatusMessage == "Verifying backup")
                cts15.Cancel();
        });
        bool cancelled15 = false;
        try { await svc.ExecuteTargetedAsync(verifyJob, dest, batch15, tiers, cts15.Token, cancelAtVerify); }
        catch (OperationCanceledException) { cancelled15 = true; }
        Check(cancelled15 && CountInDest(dest, "fifteen") == 40,
            "cancelled while verifying, the run ends cancelled, not completed (its files are copied)");

        // Pause as retention starts: nothing may be pruned until Resume.
        string held = Path.Combine(histDir, "thirteen_held.txt");
        File.WriteAllText(held, "1");
        await svc.ExecuteTargetedAsync(job, dest, [held], keepOne, CancellationToken.None);
        File.WriteAllText(held, "2, edited");
        await svc.ExecuteTargetedAsync(job, dest, [held], keepOne, CancellationToken.None);
        File.WriteAllText(held, "3, edited again");
        using var pause15 = new ManualResetEventSlim(true);
        bool pausedOnce = false;   // the user clicks Pause once; later reports must not re-pause
        var pauseAtRetention = new SyncProgress<BackupProgress>(p =>
        {
            if (p.StatusMessage == "Applying retention rules" && !pausedOnce)
            {
                pausedOnce = true;
                pause15.Reset();
            }
        });
        var run15 = Task.Run(() => svc.ExecuteTargetedAsync(
            job, dest, [held], keepOne, CancellationToken.None, pauseAtRetention, pause15));
        await Task.Delay(1500);
        var heldV1 = (await repo.GetFileRecordsByPathAsync(set.Id, held)).FirstOrDefault(r => r.Version == 1);
        Check(!run15.IsCompleted && heldV1 is { IsDeleted: false } && File.Exists(Path.Combine(dest, heldV1.DiscPath)),
            "paused as retention starts, it prunes nothing and waits");
        pause15.Set();
        await run15;
        heldV1 = (await repo.GetFileRecordsByPathAsync(set.Id, held)).FirstOrDefault(r => r.Version == 1);
        Check(heldV1 is { IsDeleted: true }, "resumed, it prunes the expired version");

        // Cancelled during retention, after the copy: cancelled, and says the copy finished.
        string held2 = Path.Combine(histDir, "thirteen_held2.txt");
        File.WriteAllText(held2, "1");
        await svc.ExecuteTargetedAsync(job, dest, [held2], keepOne, CancellationToken.None);
        File.WriteAllText(held2, "2, edited");
        await svc.ExecuteTargetedAsync(job, dest, [held2], keepOne, CancellationToken.None);
        File.WriteAllText(held2, "3, edited again");
        using var cts15b = new CancellationTokenSource();
        var cancelAtRetention = new SyncProgress<BackupProgress>(p =>
        {
            if (p.StatusMessage == "Applying retention rules")
                cts15b.Cancel();
        });
        BackupCanceledException? atRetention = null;
        try { await svc.ExecuteTargetedAsync(job, dest, [held2], keepOne, cts15b.Token, cancelAtRetention); }
        catch (BackupCanceledException ex) { atRetention = ex; }
        Check(atRetention is { CopyFinished: true },
            "cancelled during retention, the run ends cancelled, and says its copy had finished");

        // ------------------------------------------------------------ the lock, and the failed list
        Console.WriteLine();
        Console.WriteLine("=== 16. a paused or cancelled copy ===");
        // Paused mid-copy, the run must not keep the set's write lock: held, it
        // stalled every other writer of the set - the Worker's whole poll loop.
        var batch16 = MakeBatch(src, "sixteen", 4, 64 * 1024);
        using var pause16 = new ManualResetEventSlim(true);
        bool paused16 = false;
        var pauseAtCopy = new SyncProgress<BackupProgress>(p =>
        {
            if (!paused16 && string.IsNullOrEmpty(p.StatusMessage) && !string.IsNullOrEmpty(p.CurrentFile))
            {
                paused16 = true;
                pause16.Reset();
            }
        });
        var run16 = Task.Run(() => svc.ExecuteTargetedAsync(
            job, dest, batch16, tiers, CancellationToken.None, pauseAtCopy, pause16));
        await WaitForAsync(() => paused16, 30_000);
        await Task.Delay(300);
        string otherDir = Path.Combine(src, "sixteen_other");
        Directory.CreateDirectory(otherDir);
        string other = Path.Combine(otherDir, "other.txt");
        File.WriteAllText(other, "written while the other run is paused");
        var otherRun = svc.ExecuteTargetedAsync(job, dest, [other], tiers, CancellationToken.None);
        bool otherGotIn = await Task.WhenAny(otherRun, Task.Delay(10_000)) == otherRun;
        Check(paused16 && otherGotIn && !run16.IsCompleted,
            "paused mid-copy, the run lets the set's write lock go: another backup of the set gets in");
        pause16.Set();
        var result16 = await run16;
        await otherRun;
        Check(result16.Success && CountInDest(dest, "sixteen") == 4, "resumed, the paused run finishes");

        // Cancelled mid-copy: the files that had failed are still reported.
        var batch16b = MakeBatch(src, "sixteenbee", 4, 4 * 1024);
        using var cts16 = new CancellationTokenSource();
        var cancelAtThird = new SyncProgress<BackupProgress>(p =>
        {
            if (p.CurrentFile == batch16b[2])
                cts16.Cancel();
        });
        BackupCanceledException? midCopy = null;
        using (new FileStream(batch16b[0], FileMode.Open, FileAccess.Read, FileShare.None))   // cannot be read
        {
            try { await svc.ExecuteTargetedAsync(job, dest, batch16b, tiers, cts16.Token, cancelAtThird); }
            catch (BackupCanceledException ex) { midCopy = ex; }
        }
        Check(midCopy is { CopyFinished: false } && midCopy.FailedFiles.Any(f => f.Path == batch16b[0]),
            $"cancelled mid-copy, it still reports the file that failed ({midCopy?.FailedFiles.Count} failed)");

        // Verifying after another writer has backed a file up again is no failure.
        var batch16c = MakeBatch(src, "sixteencee", 3, 4 * 1024);
        bool rewritten = false;
        var rewriteAtVerify = new SyncProgress<BackupProgress>(p =>
        {
            if (rewritten || p.StatusMessage != "Verifying backup")
                return;
            rewritten = true;
            File.WriteAllText(batch16c[0], "edited, and backed up by the Worker meanwhile");
            svc.ExecuteTargetedAsync(job, dest, [batch16c[0]], tiers, CancellationToken.None)
               .GetAwaiter().GetResult();
        });
        var result16c = await svc.ExecuteTargetedAsync(
            verifyJob, dest, batch16c, tiers, CancellationToken.None, rewriteAtVerify);
        Check(rewritten && result16c.Success && result16c.FailedFiles.Count == 0,
            $"a file another writer backed up again during verification is not reported as failing ({result16c.FailedFiles.Count} failed)");

        // ------------------------------------------------------------ version numbers
        Console.WriteLine();
        Console.WriteLine("=== 17. a version number is never given twice ===");
        // A pause mid-file lets the write lock go after the file's number was
        // read. Another writer may take that number meanwhile: the file restarts.
        string seventeen = Path.Combine(src, "seventeen");
        Directory.CreateDirectory(seventeen);
        string bigFile = Path.Combine(seventeen, "seventeen_big.bin");
        File.WriteAllBytes(bigFile, Enumerable.Repeat((byte)1, 1_000_000).ToArray());
        await svc.ExecuteTargetedAsync(job, dest, [bigFile], tiers, CancellationToken.None);
        File.WriteAllBytes(bigFile, Enumerable.Repeat((byte)2, 1_200_000).ToArray());
        using var pause17 = new ManualResetEventSlim(true);
        bool paused17 = false;
        var pauseAtBig = new SyncProgress<BackupProgress>(p =>
        {
            if (!paused17 && string.IsNullOrEmpty(p.StatusMessage) && p.CurrentFile == bigFile)
            {
                paused17 = true;
                pause17.Reset();
            }
        });
        var run17 = Task.Run(() => svc.ExecuteTargetedAsync(
            job, dest, [bigFile], tiers, CancellationToken.None, pauseAtBig, pause17));
        await WaitForAsync(() => paused17, 30_000);
        await Task.Delay(300);
        // Another writer records version 2 meanwhile. (A real one could not write
        // this file now - the paused copy holds its temp file exclusively - but it
        // can commit a version of it, which is what the run must notice.)
        var v1Big = (await repo.GetFileRecordsByPathAsync(set.Id, bigFile)).Single(r => r.Version == 1);
        byte[] takenBytes = Enumerable.Repeat((byte)2, 1_200_000).ToArray();
        string takenDiscPath = v1Big.DiscPath[..^".v1".Length] + ".v2";
        File.WriteAllBytes(Path.Combine(dest, takenDiscPath), takenBytes);
        await repo.CreateFileRecordAsync(new FileRecord
        {
            DiscId = v1Big.DiscId, SourcePath = bigFile, DiscPath = takenDiscPath,
            SizeBytes = takenBytes.Length, Version = 2,
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(takenBytes)).ToLowerInvariant(),
            SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        });
        File.WriteAllBytes(bigFile, Enumerable.Repeat((byte)3, 1_400_000).ToArray());
        pause17.Set();
        var result17 = await run17;
        var (intact17, detail17) = await VersionsIntactAsync(repo, set.Id, dest, bigFile, 3);
        Check(paused17 && result17.Success && intact17,
            $"paused mid-file while another writer took its number, the file starts again: versions 1-3 ({detail17}; paused {paused17}, failed: {string.Join(" | ", result17.FailedFiles.Select(f => f.Error))})");

        // A newer record that was tombstoned (its copy found missing) keeps its
        // number; the next version goes after it, not after the newest live one.
        string tomb = Path.Combine(seventeen, "seventeen_tomb.txt");
        File.WriteAllText(tomb, "one");
        await svc.ExecuteTargetedAsync(job, dest, [tomb], tiers, CancellationToken.None);
        File.WriteAllText(tomb, "two, edited");
        await svc.ExecuteTargetedAsync(job, dest, [tomb], tiers, CancellationToken.None);
        var v2 = (await repo.GetFileRecordsByPathAsync(set.Id, tomb)).Single(r => r.Version == 2);
        File.Delete(Path.Combine(dest, v2.DiscPath));
        v2.IsDeleted = true;
        await repo.UpdateFileRecordAsync(v2);
        File.WriteAllText(tomb, "three, edited again");
        await svc.ExecuteTargetedAsync(job, dest, [tomb], tiers, CancellationToken.None);
        var (intactTomb, detailTomb) = await VersionsIntactAsync(repo, set.Id, dest, tomb, 3);
        Check(intactTomb, $"after a tombstoned version 2 comes version 3, not a second 2 ({detailTomb})");

        await Task.Run(PanelTests);
    }

    /// <summary>
    /// Whether <paramref name="path"/> has exactly versions 1 to
    /// <paramref name="expected"/>, each once, and every live one's copy at the
    /// destination holds the content its record says (by SHA-256).
    /// </summary>
    static async Task<(bool Ok, string Detail)> VersionsIntactAsync(
        ICatalogRepository repo, int setId, string dest, string path, int expected)
    {
        var records = await repo.GetFileRecordsByPathAsync(setId, path);
        var versions = records.Select(r => r.Version).OrderBy(v => v).ToList();
        string numbers = "versions " + string.Join(", ", versions);
        if (!versions.SequenceEqual(Enumerable.Range(1, expected)))
            return (false, numbers + ": " + string.Join("; ", records.OrderBy(r => r.Version).Select(r =>
                $"v{r.Version}{(r.IsDeleted ? " deleted" : "")} {r.SizeBytes:N0} B at {r.DiscPath}")));
        foreach (var r in records.Where(r => !r.IsDeleted))
        {
            string copy = Path.Combine(dest, r.DiscPath);
            if (!File.Exists(copy))
                return (false, $"{numbers}; version {r.Version}'s copy is missing");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copy)));
            if (!hash.Equals(r.Hash, StringComparison.OrdinalIgnoreCase))
                return (false, $"{numbers}; version {r.Version}'s copy holds other content");
        }
        return (true, numbers);
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

        var svm = new LithicBackup.ViewModels.BurnProgressViewModel { IsDirectoryMode = true };
        svm.StartBurn();
        static BackupProgress Slow(long done) => new()
        {
            StatusMessage = "Verifying backup", PhaseFilesDone = done, PhaseFilesTotal = 100,
        };
        svm.OnBackupProgress(Slow(0));
        await Task.Delay(2200);
        svm.OnBackupProgress(Slow(1));
        Check(svm.StatsText.Contains(" s per file"),
            $"a phase slower than a file a second says how long one takes (\"{svm.StatsText}\")");

        await Task.Delay(400);                          // the phases took 2.6 s
        svm.OnBackupProgress(Data(@"D:\x\a.bin", 0));   // then the copy starts
        await Task.Delay(600);
        svm.OnBackupProgress(Data(@"D:\x\a.bin", 50));  // and is half done 0.6 s later
        Check(svm.StatsText.EndsWith("about 00:00 remaining") || svm.StatsText.EndsWith("about 00:01 remaining"),
            $"the copy's time left follows its own rate, not the phases' time before it (\"{svm.StatsText}\")");

        svm.CancelCommand.Execute(null);
        await Task.Delay(600);
        svm.OnBackupProgress(Data(@"D:\x\b.bin", 60));
        svm.OnBackupProgress(Slow(3));
        Check(svm.StatusText == "Cancelling...",
            $"reports still on their way after Cancel leave \"Cancelling...\" up (\"{svm.StatusText}\")");
        svm.CompleteBurn(false, "Cancelled by user.", cancelled: true);
    }
}

/// <summary>
/// A catalog that counts the paths each version lookup is asked about, and
/// otherwise passes every call through to the real one.
/// </summary>
public class CountingCatalog : DispatchProxy
{
    public ICatalogRepository Inner = null!;
    public long LivePaths, LastVersionReads, RecordReads;

    /// <summary>
    /// Run once, after the next live-version lookup returns and before its caller
    /// sees the answer: what another writer might do in the meantime.
    /// </summary>
    public Func<Task>? AfterLiveLookup;

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
        else if (method.Name == nameof(ICatalogRepository.GetLastVersionAsync))
            Interlocked.Increment(ref LastVersionReads);
        else if (method.Name == nameof(ICatalogRepository.GetFileRecordsByPathAsync))
            Interlocked.Increment(ref RecordReads);

        object? result;
        try { result = method.Invoke(Inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }

        if (method.Name == nameof(ICatalogRepository.GetLatestVersionInfoForPathsAsync)
            && Interlocked.Exchange(ref AfterLiveLookup, null) is { } then)
            return ThenAsync((Task<Dictionary<string, LithicBackup.Core.Models.FileVersionInfo>>)result!, then);
        return result;

        static async Task<T> ThenAsync<T>(Task<T> task, Func<Task> then)
        {
            var answer = await task;
            await then();
            return answer;
        }
    }
}
