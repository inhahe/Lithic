// Saved entries for things that are not on disk.
//
// A backup set keeps its selection by path. A selected folder that disappears
// keeps its entry, and is backed up again if a folder appears at that path. The
// editor used to keep such entries invisibly - and a whole missing SOURCE (a drive
// that isn't plugged in, an offline share) not at all: the next save dropped it.
// Now every saved entry whose path is gone is a visible "not found" row that can
// be unticked, a missing source is kept as "not connected", and "Forget them"
// removes the gone entries in one go.
//
// Everything here runs against the real scanner and the editor's real selection
// tree (SourceSelectionViewModel / SourceSelectionNodeViewModel), on temp folders.

using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.ViewModels;

namespace MissingSourceTest;

internal static class Program
{
    static int _failures, _checks;

    static void Check(bool cond, string msg)
    {
        _checks++;
        Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
        if (!cond) _failures++;
    }

    [STAThread]
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "lithic_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CacheMaintenance.DirectoryOverride = Path.Combine(root, "appdata");
        Directory.CreateDirectory(CacheMaintenance.DirectoryOverride);
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await RunAsync(root); }
                catch (Exception ex) { Console.WriteLine("  ✗ FAIL: unhandled " + ex); _failures++; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
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

    // ------------------------------------------------------------------ helpers

    static SourceSelection Dir(string path, bool? selected, bool auto, bool expanded = false,
                               params SourceSelection[] kids)
        => new()
        {
            Path = path, IsDirectory = true, IsSelected = selected,
            AutoIncludeNewSubdirectories = auto, IsExpanded = expanded, Children = kids.ToList(),
        };

    static SourceSelection? Find(IEnumerable<SourceSelection> list, string path)
    {
        foreach (var s in list)
        {
            if (string.Equals(s.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return s;
            if (Find(s.Children, path) is { } hit)
                return hit;
        }
        return null;
    }

    static IEnumerable<SourceSelectionNodeViewModel> Walk(IEnumerable<SourceSelectionNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var d in Walk(n.Children))
                yield return d;
        }
    }

    static SourceSelectionNodeViewModel? Node(SourceSelectionViewModel vm, string path)
        => Walk(vm.Roots).FirstOrDefault(n =>
            string.Equals(n.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Order-insensitive, comparable form of a selection tree, by meaning: an
    /// unticked entry's auto-include flag is left out, because nothing reads it
    /// (IncludesUnlistedDescendants is false for any unticked entry) and the
    /// editor always writes it at its default.
    /// </summary>
    static string Canon(IEnumerable<SourceSelection> list)
        => JsonSerializer.Serialize(list
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .Select(s => new
            {
                P = s.Path.TrimEnd('\\').ToLowerInvariant(), s.IsDirectory, s.IsSelected,
                A = s.IsSelected == false ? (bool?)null : s.AutoIncludeNewSubdirectories,
                C = Canon(s.Children),
            }));

    static async Task<SourceSelectionViewModel> OpenAsync(List<SourceSelection> saved)
    {
        var vm = new SourceSelectionViewModel(catalogInfo: null, SourceSelectionViewModel.EnumerateDrives());
        await vm.ApplySelectionsAsync(saved);
#if !OLD_CODE
        // Let the background recount the restore schedules land.
        for (int i = 0; i < 100 && vm.MissingFolderCount != CountNow(vm); i++)
            await Task.Delay(30);
#endif
        return vm;
    }

#if !OLD_CODE
    static int CountNow(SourceSelectionViewModel vm)
    {
        var (folders, files) = SourceSelectionViewModel.CountMissing(vm.GetSelections());
        return folders + files;
    }
#endif

    static void MakeDirs(params string[] dirs)
    {
        foreach (var d in dirs)
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, Path.GetFileName(d) + ".txt"), d);
        }
    }

    // ------------------------------------------------------------------ tests

    static async Task RunAsync(string root)
    {
        string src = Path.Combine(root, "src");
        string a = Path.Combine(src, "a"), b = Path.Combine(src, "b"), x = Path.Combine(src, "x");
        MakeDirs(a, b, x);

        // A drive letter this machine does not have: an external drive that isn't
        // plugged in. And a custom-path source that is absent.
        var present = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char letter = Enumerable.Range('Q', 10).Select(i => (char)i).First(c => !present.Contains(c));
        string missingDrive = letter + @":\";
        string missingCustom = Path.Combine(root, "not-mounted");

        // ---------------------------------------------------------------- scanner
        Console.WriteLine("=== 1. the scanner: a selection is kept by path ===");
        var scanSel = new List<SourceSelection>
        {
            Dir(src, null, auto: false, expanded: true, Dir(a, true, false), Dir(x, true, false)),
        };
        var scanner = new FileScanner(new SqliteCatalogRepository(Path.Combine(root, "catalog.db")));
        async Task<List<string>> Names() =>
            (await scanner.ScanAsync(scanSel)).Select(f => Path.GetFileName(f.FullPath)).OrderBy(n => n).ToList();

        Check((await Names()).SequenceEqual(["a.txt", "x.txt"]), "a and x backed up, b not (auto-include off)");
        Directory.Delete(x, true);
        Check((await Names()).SequenceEqual(["a.txt"]), "x deleted: skipped silently");
        MakeDirs(x);
        Check((await Names()).Contains("x.txt"), "x re-created: backed up again - its entry is still there");
        string y = Path.Combine(src, "y");
        MakeDirs(y);
        Check(!(await Names()).Contains("y.txt"), "a genuinely new folder y is not (auto-include off)");
        Directory.Delete(y, true);
        Directory.Delete(x, true);

        // ---------------------------------------------------------------- restore + round trip
        Console.WriteLine();
        Console.WriteLine("=== 2. opening the editor: gone entries are kept, and shown ===");
        string m = Path.Combine(src, "m");          // missing, saved as "all of it except tmp"
        string mTmp = Path.Combine(m, "tmp");
        string p = Path.Combine(root, "p");         // present, partial, auto off; ONLY child selected is missing
        string pGone = Path.Combine(p, "gone");
        string pOther = Path.Combine(p, "other");
        string q = Path.Combine(root, "q");         // present, partial, auto ON; has a missing child
        string qGone = Path.Combine(q, "gone");
        string d = Path.Combine(root, "d");         // present, COLLAPSED; a missing grandchild deep inside
        string dSub = Path.Combine(d, "sub"), dSubGone = Path.Combine(dSub, "gone"), dSubKeep = Path.Combine(dSub, "keep");
        MakeDirs(pOther, q, dSubKeep);
        Directory.CreateDirectory(Path.Combine(q, "live"));

        var saved = new List<SourceSelection>
        {
            Dir(src, null, auto: false, expanded: true,
                Dir(a, true, false),
                Dir(x, true, false),                                  // gone
                Dir(m, true, true, false, Dir(mTmp, false, false))),  // gone, with a saved exception
            Dir(p, null, auto: false, expanded: true, Dir(pGone, true, false)),
            Dir(q, null, auto: true, expanded: true, Dir(qGone, true, false)),
            Dir(d, null, auto: false, expanded: false,
                Dir(dSub, null, false, false, Dir(dSubGone, true, false), Dir(dSubKeep, true, false))),
            Dir(missingDrive, null, auto: true, expanded: false, Dir(missingDrive + "photos", true, false)),
            Dir(missingCustom, true, auto: true),
        };

        var vm = await OpenAsync(saved);
        var after = vm.GetSelections();
        bool roundTrip = Canon(after) == Canon(saved);
        Check(roundTrip,
            "opening and saving without a change writes back exactly what was saved - every gone entry included");
        if (!roundTrip)
        {
            Console.WriteLine("     saved: " + Canon(saved));
            Console.WriteLine("     after: " + Canon(after));
        }
        Check(Find(after, missingDrive) is not null && Find(after, missingDrive + "photos") is not null,
            $"the unplugged drive {missingDrive} and its selections survive a save (they used to be dropped)");
        Check(Find(after, missingCustom) is not null, "so does the absent custom-path source");

#if !OLD_CODE
        var xNode = Node(vm, x);
        Check(xNode is { IsMissing: true, IsSelected: true }, "x has a row, marked missing, still ticked");
        Check(xNode?.AnnotationDisplay == " (not found)", $"labelled \"(not found)\" (got \"{xNode?.AnnotationDisplay}\")");
        Check(xNode?.MissingToolTip?.Contains("backed up") == true, "with a tooltip saying what that means");
        var driveNode = Node(vm, missingDrive);
        Check(driveNode is { IsMissing: true } && driveNode.AnnotationDisplay == " (not connected)",
            $"the drive is a top-level row labelled \"(not connected)\" (got \"{driveNode?.AnnotationDisplay}\")");
        Check(Node(vm, missingCustom)?.IsMissing == true, "the custom path has a row too");
        Check(xNode is not null && xNode.FormattedSize == "", "a missing row shows no size");
#endif

        var mNode = Node(vm, m);
        Check(mNode is { IsSelected: true } && Find(after, mTmp)?.IsSelected == false,
            "a missing \"all of it except tmp\" folder keeps its saved state (re-deriving it from its one unticked child would make it \"none of it\")");
        var pNode = Node(vm, p);
        Check(pNode?.IsSelected is null && Find(after, pGone)?.IsSelected == true,
            "a folder whose only ticked child is gone stays partial, and keeps that child (it used to be recomputed to unticked, losing the child)");

#if !OLD_CODE
        Check(vm.MissingFolderCount == 5,
            $"5 gone entries counted - x, m, p\\gone, q\\gone, and d\\sub\\gone inside a collapsed folder; not the unplugged drive (got {vm.MissingFolderCount})");
        Check(vm.HasMissingFolders && vm.MissingFoldersText.StartsWith("5 folders"),
            $"the bar says so: \"{vm.MissingFoldersText}\"");

        // ---------------------------------------------------------------- untick
        Console.WriteLine();
        Console.WriteLine("=== 3. unticking a \"not found\" row ===");
        int edits0 = vm.MissingFolderEditCount;
        xNode!.IsSelected = false;
        var afterUntick = vm.GetSelections();
        Check(Find(afterUntick, x) is null, "x (under a folder that doesn't auto-include) leaves the selection");
        Check(vm.MissingFolderEditCount == edits0 + 1 && !vm.ChangedSelectionPaths.Contains(x),
            "counted as an unsaved change, but NOT handed to the post-save reconcile (which could offer to purge a deleted folder's only backups)");
        Check(vm.HasUnsavedChanges, "the editor's dirty flag is set");
        var qGoneNode = Node(vm, qGone);
        qGoneNode!.IsSelected = false;
        Check(Find(vm.GetSelections(), qGone)?.IsSelected == false,
            "under an auto-include folder, unticking keeps an exclusion - a re-created folder stays out, as for any unticked row");

        // ---------------------------------------------------------------- forget
        Console.WriteLine();
        Console.WriteLine("=== 4. Forget them ===");
        string z = Path.Combine(src, "z");          // gone at open, back before Forget
        var saved2 = new List<SourceSelection>(saved)
        {
            [0] = Dir(src, null, auto: false, expanded: true,
                Dir(a, true, false), Dir(x, true, false), Dir(z, true, false),
                Dir(m, true, true, false, Dir(mTmp, false, false))),
        };
        var vm2 = await OpenAsync(saved2);
        Check(vm2.MissingFolderCount == 6, $"6 gone entries including z (got {vm2.MissingFolderCount})");
        MakeDirs(z);   // z comes back while the editor is open
        edits0 = vm2.MissingFolderEditCount;
        await vm2.ForgetMissingAsync();
        var forgot = vm2.GetSelections();
        Check(Find(forgot, x) is null && Find(forgot, m) is null && Find(forgot, pGone) is null
              && Find(forgot, qGone) is null,
            "the visible gone entries are removed");
        Check(Find(forgot, dSubGone) is null && Find(forgot, dSubKeep)?.IsSelected == true,
            "the one inside the never-expanded folder too - and its present sibling is kept");
        Check(Find(forgot, z)?.IsSelected == true, "z, which came back in the meantime, is kept");
        Check(Find(forgot, missingDrive + "photos") is not null && Find(forgot, missingCustom) is not null,
            "the unplugged drive and the absent source are left alone");
        Check(Find(forgot, a)?.IsSelected == true, "present selections are untouched");
        Check(vm2.MissingFolderEditCount == edits0 + 1 && vm2.ChangedSelectionPaths.Count == 0,
            "an unsaved change, kept out of the reconcile");
        for (int i = 0; i < 100 && vm2.MissingFolderCount != 0; i++) await Task.Delay(30);
        Check(vm2.MissingFolderCount == 0 && !vm2.HasMissingFolders, "the bar goes away");
        var pAfter = Find(forgot, p);
        Check(pAfter is null || pAfter.IsSelected != true,
            "a folder left with no ticked child is not promoted to \"all of it\"");
        Check(SourceSelectionViewModel.CountMissing(forgot) == (0, 0), "nothing gone is left in what would be saved");
        vm2.Dispose();
#endif

        // ---------------------------------------------------------------- deleted while open
        Console.WriteLine();
        Console.WriteLine("=== 5. a ticked folder deleted while the editor is open ===");
        string w = Path.Combine(src, "w");
        MakeDirs(w);
        var saved3 = new List<SourceSelection>
        {
            Dir(src, null, auto: false, expanded: true, Dir(a, true, false), Dir(w, true, false)),
        };
        var vm3 = await OpenAsync(saved3);
        var srcNode = Node(vm3, src)!;
        Directory.Delete(w, true);
        srcNode.IsExpanded = false;
        srcNode.IsExpanded = true;               // re-reads the folder
        await srcNode.EnsureChildrenLoadedAsync();
        await Task.Delay(100);
#if !OLD_CODE
        Check(Node(vm3, w)?.IsMissing == true, "re-expanding its parent turns it into a \"not found\" row");
#endif
        Check(Find(vm3.GetSelections(), w)?.IsSelected == true,
            "and it stays in the selection (it used to vanish from the tree, and so from the next save)");

        MakeDirs(w);
        srcNode.IsExpanded = false;
        srcNode.IsExpanded = true;
        await srcNode.EnsureChildrenLoadedAsync();
        await Task.Delay(100);
#if !OLD_CODE
        var wBack = Node(vm3, w);
        Check(wBack is { IsMissing: false, IsSelected: true }, "re-created and re-expanded: a normal row again, still ticked");
#endif

        string v = Path.Combine(src, "v");       // appears and vanishes during the session, never chosen
        MakeDirs(v);
        srcNode.IsExpanded = false; srcNode.IsExpanded = true;
        await srcNode.EnsureChildrenLoadedAsync(); await Task.Delay(100);
        Directory.Delete(v, true);
        srcNode.IsExpanded = false; srcNode.IsExpanded = true;
        await srcNode.EnsureChildrenLoadedAsync(); await Task.Delay(100);
        Check(Node(vm3, v) is null, "a folder that came and went without ever being chosen leaves no row behind");

#if !OLD_CODE
        // ---------------------------------------------------------------- never-user-data entries
        Console.WriteLine();
        Console.WriteLine("=== 6. old saved entries for the recycle bin are left alone ===");
        // A fully-selected drive that was expanded lists every child, the recycle
        // bin included, so real selections carry entries like these. The recycle bin
        // is never backed up or shown now; its entry must not turn into a "not found"
        // row, a count on the bar, or something Forget prunes. Read-only: this only
        // asks whether C:'s recycle bin folder exists.
        string bin = @"C:\$Recycle.Bin";
        string fakeR = bin + @"\S-1-5-21-0-0-0-0\$RNOTREAL";
        // The shape the J: set really has: the whole drive ticked and expanded, so
        // its saved entry lists children - the recycle bin among them.
        var saved4 = new List<SourceSelection>
        {
            Dir(@"C:\", true, auto: true, expanded: true,
                Dir(bin, true, true, false, Dir(fakeR, true, false))),
        };
        Check(SourceSelectionViewModel.CountMissing(saved4) == (0, 0),
            "a gone entry inside the recycle bin is not counted");
        Check(SourceSelectionViewModel.PruneMissing(saved4[0]) is null, "nor pruned by Forget");
        var vm4 = await OpenAsync(saved4);
        Check(Node(vm4, bin) is null, "the recycle bin has no row in the tree");
        Check(Find(vm4.GetSelections(), fakeR) is not null, "and the old entry is written back as it was");
        Check(vm4.MissingFolderCount == 0, "the bar stays away");
        vm4.Dispose();
#endif

        vm.Dispose(); vm3.Dispose();
    }
}
