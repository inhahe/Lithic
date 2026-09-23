// Regression tests for the source-selection WIDENING bugs.
//
// A backup set's C:\ was saved as "partial: these five folders". Twice it came
// back as "fully selected: every top-level entry on the drive", and the second
// time the Worker copied 1.2 TB of C: overnight. Then, minutes after the
// selection was corrected by hand, a GUI session holding the old widened copy
// wrote it straight back. Three bugs:
//
//   A. The editor's tristate recompute promoted a partial folder to FULLY
//      selected from children that were only drawn checked by auto-include
//      inference. Two open/close cycles, zero clicks, and the drive was widened.
//   B. The editor edited the in-memory copy of the set, never re-reading the
//      catalog, so a stale copy was displayed and then saved back over.
//   C. Writers that only meant to record a timestamp or a destination rewrote
//      the whole row - selection included - from whatever copy they held.
//
// Each is tested at the level it lives: A against a real directory tree and the
// real node view-model; B by checking the refresh copies every persisted field;
// C against a real SQLite catalog.

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
