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

using System.Collections.Concurrent;
using System.IO;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
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
        Check(rec1.Statuses.FirstOrDefault()?.StartsWith("Checking 12 file(s)") == true,
            $"it starts by saying what it is doing (\"{rec1.Statuses.FirstOrDefault()}\")");
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
    }
}
