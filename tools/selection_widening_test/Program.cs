// Regression tests for the source-selection WIDENING bugs.
//
// A backup set's C:\ was saved as "partial: these five folders". Twice it came
// back as "fully selected: every top-level entry on the drive", and the second
// time the Worker copied 1.2 TB of C: overnight. Then, minutes after the
// selection was corrected by hand, a GUI session holding the old widened copy
// wrote it straight back. Then the same happened to another set's D:\ and E:\
// by a different route. Four bugs:
//
//   A. The editor's tristate recompute promoted a partial folder to FULLY
//      selected from children that were only drawn checked by auto-include
//      inference. Two open/close cycles, zero clicks, and the drive was widened.
//   B. The editor edited the in-memory copy of the set, never re-reading the
//      catalog, so a stale copy was displayed and then saved back over.
//   C. Writers that only meant to record a timestamp or a destination rewrote
//      the whole row - selection included - from whatever copy they held.
//   D. A folder saved as "ticked, auto-include off, these children" backs up
//      only those children, but the editor drew every other child ticked and
//      wrote them all out on save: J:'s D:\ went from 95 entries to 309 (the
//      recycle bin among them) and E:\ to every item on the drive, pagefile
//      included. Coverage diffs read the same shape as the whole folder.
//
// And the opposite fault, which narrowed instead:
//
//   E. Ticking a folder pushed the tick one level down only, and a collapsed
//      sub-folder was saved as its old entry. A folder shown ticked could back up
//      only part of itself.
//
// Each is tested at the level it lives: A against a real directory tree and the
// real node view-model; B by checking the refresh copies every persisted field;
// C against a real SQLite catalog; D against a real tree, the node view-model
// and the coverage functions the scanner and the Worker share; E against a real
// tree and the node view-model, with the editor's deferred push-down as well as
// the inline one.

using System.IO;
using System.Reflection;
using System.Windows.Threading;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.ViewModels;

namespace SelectionWideningTest;

internal static class Program
{
    static int _failures;

    static void Check(bool cond, string msg)
    {
        Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
        if (!cond) _failures++;
    }

    [STAThread]
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "lithic_widen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The node view-model awaits Dispatcher.Yield while loading children,
            // so the tree tests must run inside a pumping dispatcher.
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await TristateTests(root);
                    await ListedOnlyTests(root);
                    await TickTests(root);
                    CopyPersistedFieldsTest();
                    await MetadataWriteTests(root);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  ✗ FAIL: unhandled " + ex);
                    _failures++;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // A. tristate promotion
    // ------------------------------------------------------------------

    static SourceSelection Dir(string path, bool? selected, bool auto, params SourceSelection[] kids)
        => new()
        {
            Path = path,
            IsDirectory = true,
            IsSelected = selected,
            AutoIncludeNewSubdirectories = auto,
            IsExpanded = false,
            Children = kids.ToList(),
        };

    static async Task<SourceSelectionNodeViewModel> Open(string path, SourceSelection model)
    {
        var node = new SourceSelectionNodeViewModel(path, isDirectory: true, parent: null);
        await node.ApplySelectionAsync(model);
        return node;
    }

    static string Describe(SourceSelection? m)
        => m is null ? "(null)"
           : $"{(m.IsSelected is null ? "partial" : m.IsSelected == true ? "FULL" : "off")}, "
             + $"{m.Children.Count} listed [{string.Join(", ", m.Children.Select(c => Path.GetFileName(c.Path)))}]";

    static async Task TristateTests(string tmp)
    {
        Console.WriteLine("=== A. a partial folder must not be promoted by inferred checks ===");

        // A drive-like folder with five subfolders; the user picked ONE.
        string drive = Path.Combine(tmp, "drive");
        foreach (var name in new[] { "keep", "other1", "other2", "other3", "other4" })
            Directory.CreateDirectory(Path.Combine(drive, name));
        string keep = Path.Combine(drive, "keep");

        // --- the incident: partial, auto-include ON, one child picked ---
        var saved = Dir(drive, null, auto: true, Dir(keep, true, auto: false));

        var node = await Open(drive, saved);
        Check(node.Children.Count == 5, $"all 5 subfolders loaded from disk ({node.Children.Count})");
        Check(node.Children.Count(c => c.IsSelected == true) == 5,
            "the 4 unpicked folders are DRAWN checked (auto-include inference) — correct display");

        node.RecomputeLoadedTristate();
        Check(node.IsSelected is null,
            $"RecomputeLoadedTristate leaves the folder PARTIAL (got {Describe(node.ToModel())})");

        node.UpdateFromChildren();
        Check(node.IsSelected is null,
            "UpdateFromChildren (a click anywhere below) leaves it partial too");

        var afterFirst = node.ToModel();
        Check(afterFirst is not null && afterFirst.IsSelected is null && afterFirst.Children.Count == 1,
            $"cycle 1 saves exactly what was loaded: {Describe(afterFirst)}");

        // --- the zero-click, two-cycle reproduction ---
        var node2 = await Open(drive, afterFirst!);
        node2.RecomputeLoadedTristate();
        var afterSecond = node2.ToModel();
        Check(afterSecond is not null && afterSecond.IsSelected is null && afterSecond.Children.Count == 1,
            $"cycle 2 still saves 1 folder, not all 5: {Describe(afterSecond)}");
        Console.WriteLine("     (before the fix: cycle 1 saved FULL, cycle 2 listed all 5 — the incident)");

        // --- control: the user genuinely ticks every child -> promotion is right ---
        var allPicked = Dir(drive, null, auto: true,
            Dir(Path.Combine(drive, "keep"), true, false),
            Dir(Path.Combine(drive, "other1"), true, false),
            Dir(Path.Combine(drive, "other2"), true, false),
            Dir(Path.Combine(drive, "other3"), true, false),
            Dir(Path.Combine(drive, "other4"), true, false));
        var node3 = await Open(drive, allPicked);
        node3.RecomputeLoadedTristate();
        Check(node3.IsSelected == true,
            "control: when every child is GENUINELY selected, the folder still becomes full");

        // --- control: auto-include off -> unpicked children stay unchecked ---
        var autoOff = Dir(drive, null, auto: false, Dir(keep, true, auto: false));
        var node4 = await Open(drive, autoOff);
        node4.RecomputeLoadedTristate();
        Check(node4.Children.Count(c => c.IsSelected == true) == 1,
            "control: with auto-include off only the picked folder is checked");
        Check(node4.IsSelected is null, "control: with auto-include off the folder stays partial");
    }

    // ------------------------------------------------------------------
    // D. a ticked folder listing its children, auto-include off
    // ------------------------------------------------------------------

    static SourceSelection FileSel(string path)
        => new() { Path = path, IsDirectory = false, IsSelected = true };

    /// <summary>
    /// Restore under a real parent, as every drive and folder has in the editor
    /// (the rule under test is deliberately not applied to the parentless
    /// "All Drives" root).
    /// </summary>
    static async Task<SourceSelectionNodeViewModel> OpenUnder(string path, SourceSelection model,
        Action<SourceSelectionNodeViewModel>? settle = null)
    {
        // With a settle delegate a click queues its push-down, as in the real
        // editor, instead of running it inline.
        var parent = new SourceSelectionNodeViewModel("", isDirectory: true, parent: null,
            requestSelectionSettle: settle);
        var node = new SourceSelectionNodeViewModel(path, isDirectory: true, parent: parent);
        await node.ApplySelectionAsync(model);
        return node;
    }

    static async Task Reexpand(SourceSelectionNodeViewModel node)
    {
        node.IsExpanded = false;
        node.IsExpanded = true;             // re-expanding re-reads the folder
        await node.EnsureChildrenLoadedAsync();
    }

    /// <summary>Whether two selections back up exactly the same files under
    /// <paramref name="dir"/> - every file on disk, plus one in a folder that
    /// does not exist yet (the auto-include question).</summary>
    static bool SameCoverage(SourceSelection a, SourceSelection b, string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Append(Path.Combine(dir, "not-yet-created", "x.txt"))
            .All(f => SourceSelection.IsPathIncluded([a], f) == SourceSelection.IsPathIncluded([b], f));

    static string St(bool? v) => v is null ? "partial" : v == true ? "ticked" : "unticked";

    static string Ticked(SourceSelectionNodeViewModel node)
        => string.Join(", ", node.Children.Where(c => c.IsSelected == true).Select(c => Path.GetFileName(c.Path)));

    static async Task ListedOnlyTests(string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("=== D. \"ticked, auto-include off, these children\" covers only those children ===");

        string drive = Path.Combine(tmp, "listed");
        foreach (var name in new[] { "keep", "other1", "other2", "other3" })
            Directory.CreateDirectory(Path.Combine(drive, name));
        string keep = Path.Combine(drive, "keep");
        string other1 = Path.Combine(drive, "other1");
        File.WriteAllText(Path.Combine(drive, "loose.txt"), "x");
        File.WriteAllText(Path.Combine(keep, "a.txt"), "x");
        File.WriteAllText(Path.Combine(other1, "b.txt"), "x");

        // --- the incident: J:'s D:\, saved collapsed ---
        var saved = Dir(drive, true, auto: false, Dir(keep, true, auto: false));
        Check(SourceSelection.IsPathIncluded([saved], Path.Combine(keep, "a.txt"))
              && !SourceSelection.IsPathIncluded([saved], Path.Combine(other1, "b.txt")),
            "precondition: the backup takes keep\\a.txt and NOT other1\\b.txt");

        var node = await OpenUnder(drive, saved);
        Check(node.Children.Count == 5,
            $"its children are read at open although it is collapsed: 4 folders + 1 file ({node.Children.Count})");
        Check(Ticked(node) == "keep", $"only the listed child is drawn ticked (drawn ticked: {Ticked(node)})");
        Check(node.IsSelected is null, $"the folder shows PARTIAL - the backup takes part of it (got {St(node.IsSelected)})");
        Check(node.IsSelectionRestored, "and its checkbox is shown");

        var save1 = node.ToModel();
        Check(save1 is not null && save1.Children.Count == 1, $"save 1 writes only what it had: {Describe(save1)}");
        Check(save1 is not null && SameCoverage(saved, save1, drive), "save 1 backs up exactly the same files");
        var save2 = (await OpenUnder(drive, save1!)).ToModel();
        Check(save2 is not null && save2.Children.Count == 1, $"save 2 still has 1 entry: {Describe(save2)}");
        Console.WriteLine("     (before the fix: save 1 listed all 5 - on the real D:\\ that was 309, recycle bin included)");

        // --- a folder that restores collapsed INSIDE a collapsed parent ---
        string outer = Path.Combine(tmp, "outer");
        string inner = Path.Combine(outer, "inner");
        foreach (var name in new[] { "x", "y", "z" })
            Directory.CreateDirectory(Path.Combine(inner, name));
        var nested = Dir(outer, null, auto: false, Dir(inner, true, auto: false, Dir(Path.Combine(inner, "x"), true, false)));
        var outerNode = await OpenUnder(outer, nested);
        outerNode.IsExpanded = true;       // first expand: the deferred restore runs now
        await outerNode.EnsureChildrenLoadedAsync();
        var innerNode = outerNode.Children.Single(c => Path.GetFileName(c.Path) == "inner");
        Check(innerNode.IsSelected is null && Ticked(innerNode) == "x",
            $"restored later, on its parent's first expand: partial, only x ticked ({St(innerNode.IsSelected)}; {Ticked(innerNode)})");
        var outerSaved = outerNode.ToModel();
        Check(outerSaved is not null && SameCoverage(nested, outerSaved, outer), $"and saving it changes nothing backed up");

        // --- every entry listed and ticked: stays ticked, no change on expand ---
        var all = Dir(drive, true, auto: false,
            Dir(keep, true, false), Dir(other1, true, false),
            Dir(Path.Combine(drive, "other2"), true, false), Dir(Path.Combine(drive, "other3"), true, false),
            FileSel(Path.Combine(drive, "loose.txt")));
        var full = await OpenUnder(drive, all);
        Check(full.IsSelected == true, "every entry listed and ticked: it shows TICKED from the start");
        full.IsExpanded = true;
        await full.EnsureChildrenLoadedAsync();
        Check(full.IsSelected == true, "and expanding it changes nothing");

        // --- a folder created later is not covered by it: drawn unticked, not saved ---
        Directory.CreateDirectory(Path.Combine(drive, "late"));
        await Reexpand(full);
        var late = full.Children.FirstOrDefault(c => Path.GetFileName(c.Path) == "late");
        Check(late is not null && late.IsSelected == false,
            $"a folder created since the save is drawn unticked (auto-include is off) ({St(late?.IsSelected)})");
        Check(full.IsSelected is null, "and the folder turns partial, as it is");
        var fullSaved = full.ToModel();
        Check(fullSaved is not null && SameCoverage(all, fullSaved, drive),
            $"saving adds nothing to the backup: {Describe(fullSaved)}");

        // --- the user ticks the folder: now it IS the whole folder ---
        var clicked = await OpenUnder(drive, saved);
        clicked.IsSelected = true;          // a click (tests have no settle delegate: runs inline)
        Check(clicked.Children.All(c => c.IsSelected == true), "clicking the folder ticks every child");
        Directory.CreateDirectory(Path.Combine(drive, "late2"));
        await Reexpand(clicked);
        var late2 = clicked.Children.FirstOrDefault(c => Path.GetFileName(c.Path) == "late2");
        Check(late2?.IsSelected == true, "and a folder appearing after that click follows it, ticked");
        var clickedSaved = clicked.ToModel();
        Check(clickedSaved is not null && SourceSelection.IsPathIncluded([clickedSaved], Path.Combine(other1, "b.txt")),
            "saving that click backs up the rest of the folder");

        // --- controls: the shapes that DO cover unlisted children ---
        var whole = Dir(drive, true, auto: false);
        var wholeNode = await OpenUnder(drive, whole);
        await wholeNode.EnsureChildrenLoadedAsync();
        Check(wholeNode.Children.Count > 0 && wholeNode.Children.All(c => c.IsSelected == true),
            "control: ticked with nothing listed is the whole folder - every child drawn ticked");
        var autoOn = Dir(drive, true, auto: true, Dir(keep, true, false));
        var autoOnNode = await OpenUnder(drive, autoOn);
        await autoOnNode.EnsureChildrenLoadedAsync();
        Check(autoOnNode.Children.Count > 1 && autoOnNode.Children.All(c => c.IsSelected == true),
            "control: with auto-include ON unlisted children are covered - drawn ticked");

        // --- the coverage diff behind "back up what you just added" agrees ---
        var before = SourceSelection.CollectCoveredEntries([saved]);
        Check(before.SequenceEqual([keep]), $"CollectCoveredEntries: just the listed child ({string.Join(", ", before)})");
        var after = SourceSelection.CollectCoveredEntries([Dir(drive, null, false, Dir(keep, true, false), Dir(other1, true, false))]);
        var added = after.Where(r => !before.Any(o => r.Equals(o, StringComparison.OrdinalIgnoreCase)
                                                    || r.StartsWith(o.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                         .ToList();
        Check(added.SequenceEqual([other1]),
            $"ticking other1 under it counts as ADDED, so the offer to back it up appears ({string.Join(", ", added)})");
        Check(SourceSelection.CollectSelectedRoots([saved]).SequenceEqual([keep]),
            "CollectSelectedRoots (the Worker's watch list): just the listed child");
        Check(SourceSelection.CollectCoveredEntries([whole]).SequenceEqual([drive])
              && SourceSelection.CollectCoveredEntries([autoOn]).SequenceEqual([drive]),
            "control: whole-folder and auto-include-on shapes are still collected whole");
    }

    // ------------------------------------------------------------------
    // E. ticking a folder covers all of it, however deep it was opened
    // ------------------------------------------------------------------

    static SourceSelection Exp(SourceSelection s)
    {
        s.IsExpanded = true;
        return s;
    }

    /// <summary>The files under <paramref name="dir"/> a saved selection leaves out.</summary>
    static List<string> LeftOut(SourceSelection? m, string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => m is null || !SourceSelection.IsPathIncluded([m], f))
            .Select(f => Path.GetRelativePath(dir, f))
            .ToList();

    static async Task TickTests(string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("=== E. ticking a folder backs up ALL of it, however deep it was opened ===");

        string top = Path.Combine(tmp, "tick");
        string a = Path.Combine(top, "a"), b = Path.Combine(top, "b"), c = Path.Combine(top, "c");
        string ax = Path.Combine(a, "x"), ay = Path.Combine(a, "y");
        string cp = Path.Combine(c, "p"), cq = Path.Combine(c, "q");
        foreach (var (dir, file) in new[] { (ax, "1.txt"), (ay, "2.txt"), (b, "3.txt"), (cp, "4.txt"), (cq, "5.txt") })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), "x");
        }
        File.WriteAllText(Path.Combine(top, "loose.txt"), "x");

        // --- 1. an OPENED sub-folder's own choices, two levels down ---
        var opened = Exp(Dir(top, null, auto: false, Exp(Dir(a, null, auto: false, Dir(ax, true, false)))));
        var n1 = await OpenUnder(top, opened);
        var y1 = n1.Children.Single(ch => ch.Path == a).Children.Single(ch => ch.Path == ay);
        Check(y1.IsSelected == false, "precondition: a\\y is unticked inside the opened, partly ticked a");
        n1.IsSelected = true;               // the user ticks the folder
        Check(y1.IsSelected == true, $"ticking the folder ticks a\\y, two levels down (got {St(y1.IsSelected)})");
        var left1 = LeftOut(n1.ToModel(), top);
        Check(left1.Count == 0, $"and saving backs up every file in it (left out: {string.Join(", ", left1)})");
        Console.WriteLine("     (before the fix: a\\y stayed unticked under a ticked a, and a\\y\\2.txt was left out)");

        // --- 2. COLLAPSED sub-folders holding saved partial choices ---
        var collapsed = Exp(Dir(top, null, auto: false,
            Dir(a, null, auto: false, Dir(ax, true, false)),        // a: only x
            Dir(c, null, auto: true, Dir(cq, false, false))));      // c: all but q
        Check(LeftOut(collapsed, top).Contains(Path.Combine("a", "y", "2.txt"))
              && LeftOut(collapsed, top).Contains(Path.Combine("c", "q", "5.txt")),
            "precondition: a\\y and c\\q are not backed up");
        var n2 = await OpenUnder(top, collapsed);
        n2.IsSelected = true;
        var left2 = LeftOut(n2.ToModel(), top);
        Check(left2.Count == 0,
            $"ticking the folder covers its collapsed sub-folders' contents too (left out: {string.Join(", ", left2)})");
        var a2 = n2.Children.Single(ch => ch.Path == a);
        var c2 = n2.Children.Single(ch => ch.Path == c);
        a2.IsExpanded = true;
        await a2.EnsureChildrenLoadedAsync();
        c2.IsExpanded = true;
        await c2.EnsureChildrenLoadedAsync();
        Check(a2.Children.All(ch => ch.IsSelected == true) && c2.Children.All(ch => ch.IsSelected == true),
            $"expanding them afterwards shows all of it ticked (a: {Ticked(a2)}; c: {Ticked(c2)})");
        Check(a2.IsSelected == true && c2.IsSelected == true,
            "and no old exclusion comes back to turn them partial");
        Console.WriteLine("     (before the fix: both were saved as their old entries, leaving out a\\y and c\\q)");

        // --- 3. ticking a COLLAPSED folder itself ---
        var n3 = await OpenUnder(a, Dir(a, null, auto: false, Dir(ax, true, false)));
        n3.IsSelected = true;
        var left3 = LeftOut(n3.ToModel(), a);
        Check(left3.Count == 0,
            $"ticking a collapsed, partly ticked folder saves all of it (left out: {string.Join(", ", left3)})");

        // --- 4. ... expanded before the queued push has run, as the editor does it ---
        var queued = new List<SourceSelectionNodeViewModel>();
        var n4 = await OpenUnder(c, Dir(c, null, auto: true, Dir(cq, false, false)), settle: queued.Add);
        n4.IsSelected = true;               // the click; its push-down is queued
        n4.IsExpanded = true;               // the user expands before the settle pass
        await n4.EnsureChildrenLoadedAsync();
        foreach (var node in queued)
            node.PropagateSelection();       // the settle pass
        var q4 = n4.Children.Single(ch => ch.Path == cq);
        Check(n4.IsSelected == true && q4.IsSelected == true,
            $"the old exclusion of q is not re-applied by the expand (c {St(n4.IsSelected)}, q {St(q4.IsSelected)})");
        var left4 = LeftOut(n4.ToModel(), c);
        Check(left4.Count == 0, $"and saving backs up all of c (left out: {string.Join(", ", left4)})");

        // --- 5. a "not found" entry two levels down ---
        string gone = Path.Combine(a, "gone");
        var withMissing = Exp(Dir(top, null, auto: false, Exp(Dir(a, null, auto: true, Dir(gone, false, false)))));
        var n5 = await OpenUnder(top, withMissing);
        var g5 = n5.Children.Single(ch => ch.Path == a).Children.SingleOrDefault(ch => ch.Path == gone);
        Check(g5 is not null && g5.IsMissing && g5.IsSelected == false,
            "precondition: a\\gone is a 'not found' row, excluded");
        n5.IsSelected = true;
        Check(g5?.IsSelected == true, "ticking the folder ticks that row as well");
        Check(n5.ToModel() is { } m5 && SourceSelection.IsPathIncluded([m5], Path.Combine(gone, "back.txt")),
            "so if it comes back it is backed up with the rest of the folder");

        // --- 6. an entry saved under it that the tree could not list ---
        // On disk but not enumerated (a transient access error, say), and kept
        // invisibly so a save cannot drop it. Planted directly: there is no
        // portable way to make the enumeration skip a real entry.
        string unlisted = Path.Combine(top, "unlisted");
        var n6 = await OpenUnder(top, Exp(Dir(top, null, auto: false, Dir(b, true, false))));
        typeof(SourceSelectionNodeViewModel)
            .GetField("_orphanedChildModels", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(n6, new List<SourceSelection>
            {
                Dir(unlisted, null, false, Dir(Path.Combine(unlisted, "k"), true, false)),
            });
        Check(n6.ToModel() is { } before6
              && !SourceSelection.IsPathIncluded([before6], Path.Combine(unlisted, "z.txt")),
            "precondition: that entry's saved partial choice leaves unlisted\\z.txt out");
        n6.IsSelected = true;
        var m6 = n6.ToModel();
        Check(m6 is not null && SourceSelection.IsPathIncluded([m6], Path.Combine(unlisted, "z.txt"))
              && m6.Children.Any(ch => ch.Path == unlisted),
            "ticking the folder makes it all of that entry, still listed so auto-include-off keeps it");

        // --- controls ---
        var n7 = await OpenUnder(top, opened);
        n7.IsSelected = false;
        Check(LeftOut(n7.ToModel(), top).Count == Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Count(),
            "control: unticking the folder still takes out everything in it");
        var n8 = await OpenUnder(top, opened);
        var a8 = n8.Children.Single(ch => ch.Path == a);
        n8.Children.Single(ch => ch.Path == b).IsSelected = true;
        Check(a8.IsSelected is null && a8.Children.Single(ch => ch.Path == ay).IsSelected == false,
            "control: ticking one sub-folder leaves its siblings' choices alone");
    }

    // ------------------------------------------------------------------
    // B. the editor's refresh copies every persisted field
    // ------------------------------------------------------------------

    static void CopyPersistedFieldsTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== B. refreshing before edit copies EVERY persisted field ===");

        var method = typeof(MainViewModel).GetMethod(
            "CopyPersistedFields", BindingFlags.NonPublic | BindingFlags.Static);
        Check(method is not null, "MainViewModel.CopyPersistedFields exists");
        if (method is null) return;

        // Every public read/write property of BackupSet is catalog-persisted.
        // If someone adds one and forgets the copy, the editor would silently keep
        // editing a stale value of it - this makes that a test failure instead.
        var props = typeof(BackupSet).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(BackupSet.Id))
            .ToList();

        var source = new BackupSet
        {
            Id = 7,
            Name = "fresh",
            SourceRoots = ["X:\\"],
            MaxIncrementalDiscs = 99,
            DefaultMediaType = (MediaType)1,
            DefaultFilesystemType = (FilesystemType)1,
            CapacityOverrideBytes = 12345,
            SourceSelections = [Dir("X:\\", null, false)],
            JobOptions = new JobOptions(),
            CreatedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastBackupUtc = new DateTime(2021, 2, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var target = new BackupSet { Id = 7, Name = "stale" };

        method.Invoke(null, [source, target]);

        var missed = props.Where(p => !Equals(p.GetValue(source), p.GetValue(target)))
                          .Select(p => p.Name).ToList();
        Check(missed.Count == 0,
            $"all {props.Count} persisted properties copied"
            + (missed.Count > 0 ? " — MISSED: " + string.Join(", ", missed) : ""));
    }

    // ------------------------------------------------------------------
    // C. metadata writes leave the selection alone
    // ------------------------------------------------------------------

    static async Task MetadataWriteTests(string tmp)
    {
        Console.WriteLine();
        Console.WriteLine("=== C. a stale copy cannot write its selection back ===");

        var repo = new SqliteCatalogRepository(Path.Combine(tmp, "catalog.db"));

        var wide = new List<SourceSelection> { Dir("C:\\", true, false) };
        var narrow = new List<SourceSelection>
        {
            Dir("C:\\", null, false,
                Dir("C:\\mIRC", true, false), Dir("C:\\py", true, false)),
        };

        var created = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "widening-test",
            SourceRoots = ["C:\\"],
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            SourceSelections = wide,
            JobOptions = new JobOptions(),
        });

        // A long-lived window reads the set while it is still WIDE ...
        var stale = await repo.GetBackupSetAsync(created.Id);

        // ... the selection is corrected elsewhere ...
        var fixer = await repo.GetBackupSetAsync(created.Id);
        fixer!.SourceSelections = narrow;
        await repo.UpdateBackupSetAsync(fixer);

        // ... and the stale window records a backup time.
        var when = new DateTime(2026, 9, 22, 20, 19, 52, DateTimeKind.Utc);
        stale!.LastBackupUtc = when;
        await repo.UpdateBackupSetMetadataAsync(stale);

        var after = await repo.GetBackupSetAsync(created.Id);
        var c = after!.SourceSelections!.Single();
        Check(c.IsSelected is null && c.Children.Count == 2,
            $"the corrected selection survived the stale metadata write ({Describe(c)})");
        Check(after.LastBackupUtc == when,
            "and the metadata write still recorded its timestamp");

        // The full write still writes the selection - it has to, for the editor.
        await repo.UpdateBackupSetAsync(stale);
        var afterFull = await repo.GetBackupSetAsync(created.Id);
        Check(afterFull!.SourceSelections!.Single().IsSelected == true,
            "control: the FULL write does still overwrite the selection (why only "
            + "selection-changing callers may use it, and why the editor must refresh first)");
    }
}
