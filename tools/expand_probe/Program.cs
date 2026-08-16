// Time a real source-tree expansion with no window attached.
//
// Expanding a drive in the source-selection tree takes seconds, while the
// filesystem work behind it is ~10 ms and (measured by tools\treeview_bench)
// the WPF layout of 249 rows is ~75 ms. Neither accounts for what the user
// sees, so this probe drives the *actual* SourceSelectionNodeViewModel -- same
// enumeration, same node construction, same sort, same scheduler submission --
// with no visual tree at all. Whatever time shows up here is view-model cost;
// whatever doesn't is UI/dispatcher cost, and the two need separating before
// anything is "fixed".
//
// Run:  dotnet run --project tools\expand_probe -c Release -- D:\ [--sizes]

using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LithicBackup.ViewModels;

namespace ExpandProbe;

public static class Program
{
    /// <summary>Run the dispatcher until nothing above <paramref name="priority"/>
    /// is left queued, or until <paramref name="budget"/> is exhausted.</summary>
    private static void Pump(Func<bool> until, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        while (!until() && sw.Elapsed < budget)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string path = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : @"D:\";
        bool showSizes = Array.IndexOf(args, "--sizes") >= 0;

        // The size cache reads its whole SQLite table into a Dictionary on a
        // background task at construction, and every lookup takes the same lock,
        // so an expansion that happens before the load finishes waits for all of
        // it. Time that load on its own so it can be subtracted from -- or
        // recognised as -- the expansion time.
        if (Array.IndexOf(args, "--cache-only") >= 0)
        {
            var swc = Stopwatch.StartNew();
            var cache = new DirectorySizeCache();
            cache.TryGet(@"X:\definitely\not\present");   // blocks until loaded
            swc.Stop();
            Console.WriteLine($"  size-cache load: {swc.Elapsed.TotalMilliseconds,8:N1} ms");

            // The file-hash cache has the same shape and is worse placed: it is
            // constructed synchronously on the UI thread during App startup.
            var swh = Stopwatch.StartNew();
            var hashes = new FileHashCache();
            hashes.TryGetHash(@"X:\definitely\not\present", 0, DateTime.MinValue);
            swh.Stop();
            Console.WriteLine($"  hash-cache load: {swh.Elapsed.TotalMilliseconds,8:N1} ms");
            return 0;
        }

        // FlushBatchAsync marshals through Application.Current.Dispatcher, so an
        // Application must exist even though nothing is ever shown.
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var scheduler = new SizeComputeScheduler();

        var node = new SourceSelectionNodeViewModel(
            path, isDirectory: true, parent: null,
            getShowSizes: () => showSizes,
            getSortMode: () => (SortColumn.Name, true),
            scheduler: scheduler,
            getShowSelectedOnly: () => false);

        Console.WriteLine($"expanding {path}   showSizes={showSizes}");

        // The children arrive in one shot (Children.ReplaceAll fires a single
        // Reset), so the Reset is exactly the moment the tree has something to
        // display -- the instant the user is waiting for.
        double childrenReadyMs = -1;
        var sw = Stopwatch.StartNew();
        node.Children.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset && childrenReadyMs < 0)
                childrenReadyMs = sw.Elapsed.TotalMilliseconds;
        };

        node.IsExpanded = true;
        Pump(() => childrenReadyMs >= 0, TimeSpan.FromSeconds(60));

        // Everything queued behind the load (deferred restore, backup-status
        // stamping, the scheduler submission) settles after the Reset; measure
        // that separately, because it is not what blocks the first paint.
        double settledMs = -1;
        Pump(() =>
        {
            settledMs = sw.Elapsed.TotalMilliseconds;
            return true;
        }, TimeSpan.FromSeconds(60));

        // A fast expansion that returns no cached sizes would be a regression
        // dressed up as a fix, so count the directories whose recursive total
        // came straight out of the cache (Size >= 0 means "known already";
        // anything still -1 has to be scanned by the background scheduler).
        int dirs = 0, files = 0, cached = 0;
        foreach (var c in node.Children)
        {
            if (c.IsDirectory)
            {
                dirs++;
                if (c.Size >= 0) cached++;
            }
            else files++;
        }

        Console.WriteLine($"  children ready : {childrenReadyMs,8:N1} ms   " +
                          $"({dirs} dirs, {files} files; {cached} dir sizes served from cache)");
        Console.WriteLine($"  dispatcher idle: {settledMs,8:N1} ms");

        Application.Current.Shutdown();
        return 0;
    }
}
