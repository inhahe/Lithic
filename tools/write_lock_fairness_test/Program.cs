// Cross-process fairness of a set's write lock (GUI vs Worker).
//
// The incident: the GUI's "back up the folders you just added" run sat on
// "Checking 23,780 file(s) against the catalog..." for as long as the Worker's
// continuous backup of the same set kept going (90,415 changed files, hours).
// Every write to a set takes a cross-process lock file. The Worker commits every
// 50 files or 30 s and re-takes the lock within microseconds of letting it go;
// the GUI polls every 25 ms, so it effectively never got in.
//
// The fix: a waiting writer leaves a marker beside the lock file, and the
// Worker (SqliteCatalogRepository.YieldWritesToWaitingProcesses) steps aside at
// its next commit while a fresh marker from another process is there.
//
// The lock is between PROCESSES, so this harness runs a second copy of itself as
// the "Worker": it holds the set's lock in 150 ms batches, re-taking it at once
// each time, exactly as a continuous backup's commit loop does.

using System.Diagnostics;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;

namespace WriteLockFairnessTest;

internal static class Program
{
    static int _failures;

    static void Check(bool cond, string msg)
    {
        Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
        if (!cond) _failures++;
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "holder")
            return await HolderAsync(args[1], int.Parse(args[2]), double.Parse(args[3]), args[4] == "yield");

        string root = Path.Combine(Path.GetTempPath(), "lithic_lockfair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string catalog = Path.Combine(root, "catalog.db");
            var repo = new SqliteCatalogRepository(catalog);
            var set = await repo.CreateBackupSetAsync(new BackupSet
            {
                Name = "lock-fairness",
                SourceRoots = [root],
                CreatedUtc = DateTime.UtcNow,
                DefaultMediaType = MediaType.Directory,
                SourceSelections = [new SourceSelection { Path = root, IsDirectory = true, IsSelected = true }],
                JobOptions = new JobOptions(),
            });
            string setsDir = Path.Combine(root, "sets");

            // The GUI has the set open long before it backs it up (its catalog check
            // reads it), so open it here too.
            await repo.GetLatestVersionInfoForPathsAsync(set.Id, [Path.Combine(root, "x")]);

            Console.WriteLine("=== 0. opening a set while the other process is writing it ===");
            {
                using var holder = StartHolder(catalog, set.Id, seconds: 8, yield: true);
                await holder.Holding;
                var watch = Stopwatch.StartNew();
                var fresh = new SqliteCatalogRepository(catalog);     // a process opening the set anew
                await fresh.GetLatestVersionInfoForPathsAsync(set.Id, [Path.Combine(root, "x")]);
                Check(watch.Elapsed < TimeSpan.FromSeconds(1),
                    $"it opens at once, without waiting for the writer's run: {watch.Elapsed.TotalMilliseconds:N0} ms of an 8 s run");
                Console.WriteLine("     (before: its schema script's INSERT waited for SQLite's write lock - here the whole run, "
                                  + "on a run over 30 s a \"database is locked\")");
                await holder.ExitAsync();
            }

            Console.WriteLine();
            Console.WriteLine("=== 1. a background writer that steps aside (the Worker, fixed) ===");
            {
                using var holder = StartHolder(catalog, set.Id, seconds: 12, yield: true);
                await holder.Holding;
                int waitingCalls = 0;
                bool markerSeen = false;
                var watch = Stopwatch.StartNew();
                var acquire = repo.BeginTransactionAsync(set.Id, default, () => Interlocked.Increment(ref waitingCalls));
                while (!acquire.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    markerSeen |= Markers(setsDir).Any(m => m.EndsWith("-" + Environment.ProcessId));
                    await Task.Delay(10);
                }
                var tx = await acquire;
                var waited = watch.Elapsed;
                Check(waited < TimeSpan.FromSeconds(2),
                    $"the waiting writer got in within about one batch: {waited.TotalMilliseconds:N0} ms (the holder runs 12 s)");
                Check(waitingCalls == 1, $"it was told once that it was waiting ({waitingCalls})");
                Check(markerSeen, "it left a marker while it waited");
                Check(!Markers(setsDir).Any(), "and removed it once it was in");

                // Hold it for a while: the Worker must wait, then carry on after us.
                await Task.Delay(1000);
                tx.Complete();
                tx.Dispose();
                var done = await holder.ExitAsync();
                Check(done.ExitCode == 0 && done.BatchesAfterUs > 0,
                    $"the background writer carried on afterwards ({done.BatchesAfterUs} batches after ours)");
            }

            Console.WriteLine();
            Console.WriteLine("=== 2. control: a background writer that does NOT step aside (before the fix) ===");
            {
                // Starvation here is statistical: the waiter can land in the
                // microsecond gap between two batches, and with 150 ms batches
                // there are many gaps (in the Worker a batch lasts seconds). So
                // take the median of three runs.
                var waits = new List<double>();
                for (int round = 0; round < 3; round++)
                {
                    using var holder = StartHolder(catalog, set.Id, seconds: 5, yield: false);
                    await holder.Holding;
                    var watch = Stopwatch.StartNew();
                    var tx = await repo.BeginTransactionAsync(set.Id);
                    waits.Add(watch.Elapsed.TotalSeconds);
                    tx.Dispose();
                    await holder.ExitAsync();
                }
                waits.Sort();
                Check(waits[1] > 1.0,
                    $"without it the waiter is shut out far longer than one batch: median {waits[1]:N1} s "
                    + $"(runs: {string.Join(", ", waits.Select(w => w.ToString("N1")))} s of 5 s) - the incident");
            }

            Console.WriteLine();
            Console.WriteLine("=== 3. a marker left behind cannot hold the Worker up ===");
            {
                SqliteCatalogRepository.YieldWritesToWaitingProcesses = true;
                try
                {
                    string lockFile = Path.Combine(setsDir, $"set-{set.Id}.db.writelock");
                    string stale = lockFile + ".wait-999999";
                    File.WriteAllText(stale, "");
                    File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-1));
                    var watch = Stopwatch.StartNew();
                    (await repo.BeginTransactionAsync(set.Id)).Dispose();
                    Check(watch.Elapsed < TimeSpan.FromMilliseconds(500),
                        $"a stale marker (a waiter that died) is ignored: {watch.Elapsed.TotalMilliseconds:N0} ms");

                    File.WriteAllText(stale, "");   // fresh, but nobody will ever take the lock
                    watch.Restart();
                    (await repo.BeginTransactionAsync(set.Id)).Dispose();
                    Check(watch.Elapsed > TimeSpan.FromSeconds(3) && watch.Elapsed < TimeSpan.FromSeconds(10),
                        $"a fresh marker whose owner never refreshes it stops counting within seconds: {watch.Elapsed.TotalSeconds:N1} s");
                    File.Delete(stale);

                    File.WriteAllText(lockFile + ".wait-" + Environment.ProcessId, "");
                    watch.Restart();
                    (await repo.BeginTransactionAsync(set.Id)).Dispose();
                    Check(watch.Elapsed < TimeSpan.FromMilliseconds(500),
                        "a process never steps aside for its own marker");
                    File.Delete(lockFile + ".wait-" + Environment.ProcessId);
                }
                finally
                {
                    SqliteCatalogRepository.YieldWritesToWaitingProcesses = false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ FAIL: unhandled " + ex);
            _failures++;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static IEnumerable<string> Markers(string setsDir)
        => Directory.Exists(setsDir)
            ? Directory.EnumerateFiles(setsDir, "*.writelock.wait-*")
            : [];

    // ------------------------------------------------------------------
    // The "Worker": holds the set's lock in 150 ms batches for N seconds.
    // ------------------------------------------------------------------

    static async Task<int> HolderAsync(string catalog, int setId, double seconds, bool yield)
    {
        SqliteCatalogRepository.YieldWritesToWaitingProcesses = yield;
        var repo = new SqliteCatalogRepository(catalog);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        int batches = 0;
        int afterOthers = -1;           // batches since the last take that had to wait; -1 = never waited
        bool announced = false;
        while (DateTime.UtcNow < until)
        {
            var sw = Stopwatch.StartNew();
            var tx = await repo.BeginTransactionAsync(setId);
            // A take that waited means someone else had the lock in between.
            if (announced && sw.ElapsedMilliseconds > 100)
                afterOthers = 0;
            if (!announced)
            {
                Console.WriteLine("HOLDING");
                Console.Out.Flush();
                announced = true;
            }
            // A batch of work, under the lock. Spun, not awaited: a Task.Delay ends
            // on the same Windows timer tick as the waiter's 25 ms poll, lining
            // the poll up with the release far more often than chance - the real
            // Worker's batches end on disk I/O, at no particular tick.
            var batch = Stopwatch.StartNew();
            while (batch.ElapsedMilliseconds < 150)
                Thread.SpinWait(200);
            tx.Complete();
            tx.Dispose();               // commit; the next iteration re-takes at once
            batches++;
            if (afterOthers >= 0)
                afterOthers++;
        }
        Console.WriteLine($"DONE batches={batches} after={afterOthers}");
        return 0;
    }

    sealed class Holder(Process process) : IDisposable
    {
        readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? _done;
        public Task Holding => _holding.Task;

        public void Attach()
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == "HOLDING") _holding.TrySetResult();
                else if (e.Data?.StartsWith("DONE") == true) _done = e.Data;
            };
            process.BeginOutputReadLine();
        }

        public async Task<(int ExitCode, int BatchesAfterUs)> ExitAsync()
        {
            await process.WaitForExitAsync();
            process.WaitForExit();      // flush the redirected output
            int after = 0;
            if (_done is not null && _done.Contains("after="))
                int.TryParse(_done[(_done.IndexOf("after=") + 6)..], out after);
            return (process.ExitCode, after);
        }

        public void Dispose()
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose();
        }
    }

    static Holder StartHolder(string catalog, int setId, double seconds, bool yield)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "holder", catalog, setId.ToString(), seconds.ToString(), yield ? "yield" : "noyield" })
            psi.ArgumentList.Add(a);
        var holder = new Holder(Process.Start(psi)!);
        holder.Attach();
        return holder;
    }
}
