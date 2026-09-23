// End-to-end tests for the backup-set editor's save model.
//
// The rule: nothing the editor does reaches the catalog until the user clicks
// Save, or answers Yes when closing; Cancel (or No) throws the edit away; and
// closing without changes writes nothing at all. The editor used to auto-save
// every checkbox change 300 ms after it was made and to save on every close, so
// Cancel and No discarded nothing but settings.
//
// This drives the REAL editor - MainViewModel's Modify command, its window, its
// SourceSelectionViewModel - against a throwaway SQLite catalog, counts every
// backup-set write it makes, and answers its message boxes from a watcher
// thread, the way a user would. Largest Files is exercised both from the editor
// and on its own.
//
// Mutation run: build with -p:OldCode=true against the pre-change editor. Only
// the end-to-end part compiles there (the unit part uses new APIs), and it must
// FAIL - otherwise it proves nothing.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LithicBackup;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Services;
using LithicBackup.ViewModels;
using LithicBackup.Views;

namespace EditorSaveTest;

internal static class Program
{
    static int _failures;
    static int _checks;
    static bool _finishing;

    static void Check(bool cond, string msg)
    {
        _checks++;
        Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
        if (!cond) _failures++;
    }

    // ------------------------------------------------------------------ fixture

    static string _root = "";
    static string _src = "", _a = "", _b = "", _c = "", _dst = "";
    static string _bigFile = "", _smallFile = "", _otherFile = "";
    static string _gone = "", _missingDrive = "";
    static SqliteCatalogRepository _observer = null!;   // uncounted: reads, and "someone else" writing
    static CountingCatalog _counter = null!;
    static ICatalogRepository _catalog = null!;          // counted: what the app is given
    static MainViewModel _vm = null!;
    static App _app = null!;
    static DialogWatcher _dialogs = null!;
    static int _setId;

    [STAThread]
    static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "lithic_editsave_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            // The app's resources (styles, converters, view templates) without
            // running the app itself. SkipStartupForTests matters: WPF's
            // Application constructor queues OnStartup, which runs at the first
            // dispatcher pump whether or not Run() is ever called - and the real
            // OnStartup starts the whole of Lithic Backup: the single-instance
            // lock, the real catalog, a main window, a tray icon, background
            // monitors, an update check. With the app already open it instead
            // pulls that window to the front and shuts this process down.
            // Relative pack URIs in the app's XAML (window icons) resolve against
            // the "application" assembly, which would otherwise be this test exe.
            // (Its public setter refuses once WPF has defaulted it, so set the field.)
            typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, typeof(App).Assembly);
            App.SkipStartupForTests = true;
            _app = new App();
            _app.InitializeComponent();

            // Keep the editor's directory-size cache out of the real app data.
            CacheMaintenance.DirectoryOverride = Path.Combine(_root, "appdata");
            Directory.CreateDirectory(CacheMaintenance.DirectoryOverride);

            using (_dialogs = new DialogWatcher())
            {
                var dispatcher = Dispatcher.CurrentDispatcher;

                // Nothing in a test run may shut the app down; if something does,
                // fail loudly - that is how the OnStartup problem above showed up.
                _app.Exit += (_, _) =>
                {
                    if (_finishing) return;
                    Console.WriteLine("  ✗ FAIL: the app shut down mid-run:\n" + Environment.StackTrace);
                    _failures++;
                };

                // An exception thrown inside the app's own async-void handlers lands
                // here. Report it as a failure instead of letting it take the run down.
                dispatcher.UnhandledException += (_, e) =>
                {
                    Console.WriteLine("  ✗ FAIL: unhandled in the app: " + e.Exception);
                    _failures++;
                    e.Handled = true;
                };
                dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await RunAllAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  ✗ FAIL: unhandled " + ex);
                        _failures++;
                    }
                    finally
                    {
                        foreach (Window w in Application.Current.Windows.OfType<Window>().ToList())
                        {
                            try { w.Close(); } catch { }
                        }
                        _finishing = true;
                        dispatcher.InvokeShutdown();
                    }
                });
                Dispatcher.Run();
            }
        }
        finally
        {
            SqliteClearPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL {_checks} CHECKS PASSED"
            : $"{_failures} OF {_checks} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static void SqliteClearPools()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
    }

    static SourceSelection Dir(string path, bool? selected, bool auto, bool expanded = false,
                               params SourceSelection[] kids)
        => new()
        {
            Path = path,
            IsDirectory = true,
            IsSelected = selected,
            AutoIncludeNewSubdirectories = auto,
            IsExpanded = expanded,
            Children = kids.ToList(),
        };

    static async Task RunAllAsync()
    {
        // A source folder with three subfolders; the saved selection picks "a".
        _src = Path.Combine(_root, "src");
        _a = Path.Combine(_src, "a");
        _b = Path.Combine(_src, "b");
        _c = Path.Combine(_src, "c");
        _dst = Path.Combine(_root, "dst");
        _gone = Path.Combine(_src, "gone");   // never created
        var present = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        _missingDrive = Enumerable.Range('Q', 10).Select(i => (char)i).First(c => !present.Contains(c)) + @":\";
        foreach (var d in new[] { _a, _b, _c, _dst })
            Directory.CreateDirectory(d);
        _smallFile = Path.Combine(_a, "small.txt");
        _bigFile = Path.Combine(_b, "big.bin");
        _otherFile = Path.Combine(_c, "other.bin");
        File.WriteAllBytes(_smallFile, new byte[100]);
        File.WriteAllBytes(_bigFile, new byte[5000]);
        File.WriteAllBytes(_otherFile, new byte[3000]);

        string dbPath = Path.Combine(_root, "catalog.db");
        _observer = new SqliteCatalogRepository(dbPath);
        _catalog = CountingCatalog.Wrap(new SqliteCatalogRepository(dbPath), out _counter);

        var created = await _observer.CreateBackupSetAsync(new BackupSet
        {
            Name = "editor-save-test",
            SourceRoots = [_src],
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            // A directory destination; with no DirectoryBackupService the
            // editor's live "to back up" plan is skipped, which keeps the test
            // off every real drive.
            JobOptions = new JobOptions { TargetDirectory = _dst },
            SourceSelections =
            [
                Dir(_src, null, auto: false, expanded: true,
                    Dir(_a, true, auto: false),
                    // A ticked folder that has since been deleted.
                    Dir(_gone, true, auto: false)),
                // A source drive that isn't plugged in. A save used to drop it.
                Dir(_missingDrive, true, auto: true),
            ],
        });
        _setId = created.Id;

        var scanner = new StubScanner(
        [
            new ScannedFile { FullPath = _smallFile, SizeBytes = 100, LastWriteUtc = DateTime.UtcNow },
            new ScannedFile { FullPath = _bigFile, SizeBytes = 5000, LastWriteUtc = DateTime.UtcNow },
            new ScannedFile { FullPath = _otherFile, SizeBytes = 3000, LastWriteUtc = DateTime.UtcNow },
        ]);

        // The real app's main window is Application.MainWindow, and the editor
        // makes it its Owner. Stand in with an invisible one; otherwise WPF makes
        // the first editor window the MainWindow, and every editor after it tries
        // to own itself or a closed window.
        var host = new Window
        {
            Width = 1, Height = 1, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        host.Show();
        Application.Current.MainWindow = host;

        _vm = new MainViewModel(
            _catalog,
            burner: null!,
            scanner: scanner,
            orchestrator: null!,
            restoreService: null!,
            catalogFreeRestoreService: null!,
            directoryBackupService: null!,
            settings: new UserSettings { ReconcileMode = ReconcileAfterEditMode.Never });

        Check(await WaitFor(() => _vm.BackupSets.Any(r => r.Id == _setId), 10000),
            "main window loaded the test set");

        await CancelDiscardsAsync();
        await CleanCloseWritesNothingAsync();
        await CloseAnswerNoDiscardsAsync();
        await CloseAnswerCancelThenYesSavesAsync();
        await SaveButtonSavesAsync();
        await LargestFilesInsideEditorAsync();
        await ForcedShutdownSavesNothingAsync();
        await ExitAnswerYesSavesBeforeReturningAsync();
        await StandaloneLargestFilesAsync();
        await NewSetAsync();
#if !OLD_CODE
        await ForgetMissingInEditorAsync();
#endif
#if !OLD_CODE
        await UnitTestsAsync();
#endif
        Check(_dialogs.Unexpected.Count == 0,
            "no unexpected message box appeared"
            + (_dialogs.Unexpected.Count > 0 ? ": " + string.Join(" | ", _dialogs.Unexpected) : ""));
    }

    // ------------------------------------------------------------------ helpers

    static async Task<bool> WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(40);
        }
        return cond();
    }

    static bool IsOpen(Window w) => Application.Current.Windows.OfType<Window>().Contains(w);

    static IEnumerable<SourceSelectionNodeViewModel> Walk(IEnumerable<SourceSelectionNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var d in Walk(n.Children))
                yield return d;
        }
    }

    static SourceSelectionNodeViewModel? FindNode(SourceSelectionViewModel ss, string path)
        => Walk(ss.Roots).FirstOrDefault(n =>
            string.Equals(n.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a saved selection backs up <paramref name="path"/> (a folder), by
    /// the scanner's rules: an explicit entry decides for itself; an unlisted
    /// folder follows its nearest listed ancestor - covered if that is fully
    /// selected, or partial with auto-include-new on. Null: nothing covers it.
    /// </summary>
    static bool? Covers(IEnumerable<SourceSelection>? list, string path)
    {
        path = path.TrimEnd('\\');
        foreach (var s in list ?? [])
        {
            string sp = s.Path.TrimEnd('\\');
            if (sp.Length == 0)
            {
                if (Covers(s.Children, path) is { } inRoot) return inRoot;
                continue;
            }
            if (string.Equals(sp, path, StringComparison.OrdinalIgnoreCase))
                return s.IsSelected != false;
            if (path.StartsWith(sp + "\\", StringComparison.OrdinalIgnoreCase))
            {
                if (Covers(s.Children, path) is { } deeper) return deeper;
                return s.IsSelected == true || (s.IsSelected is null && s.AutoIncludeNewSubdirectories);
            }
        }
        return null;
    }

    static bool IsCovered(IEnumerable<SourceSelection>? list, string path) => Covers(list, path) == true;

    /// <summary>The saved entry for exactly this path, if there is one.</summary>
    static SourceSelection? Find(IEnumerable<SourceSelection>? list, string path)
    {
        foreach (var s in list ?? [])
        {
            if (string.Equals(s.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return s;
            if (Find(s.Children, path) is { } hit)
                return hit;
        }
        return null;
    }

    /// <summary>
    /// Close a window a scenario expected to be gone (only happens against the old
    /// editor), answering No so it cannot linger into the next scenario.
    /// </summary>
    static async Task EnsureClosedAsync(Window w)
    {
        if (!IsOpen(w)) return;
        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDNO);
        try { w.Close(); } catch { }
        await WaitFor(() => !IsOpen(w), 4000);
        _dialogs.ClearExpectations();
    }

    static async Task<BackupSet> DbSetAsync() => (await _observer.GetBackupSetAsync(_setId))!;

    static string Json(object? o) => JsonSerializer.Serialize(o);

    static void MoveOffscreen(Window w)
    {
        try { w.Left = -20000; w.Top = -20000; } catch { }
    }

    sealed record Editor(BackupSetEditorWindow Window, SourceSelectionViewModel Vm);

    /// <summary>Open the editor the way the row's Modify button does, and wait until it has settled.</summary>
    static async Task<Editor?> OpenEditorAsync(string label)
    {
        var row = _vm.BackupSets.First(r => r.Id == _setId);
        var before = Application.Current.Windows.OfType<BackupSetEditorWindow>().ToHashSet();
        _vm.SetModifyCommand.Execute(row);

        BackupSetEditorWindow? win = null;
        if (!await WaitFor(() => (win = Application.Current.Windows.OfType<BackupSetEditorWindow>()
                                     .FirstOrDefault(w => !before.Contains(w)
                                                          && w.ContentArea?.Content is SourceSelectionViewModel)) is not null,
                           20000))
        {
            Check(false, $"{label}: the editor opened");
            return null;
        }
        MoveOffscreen(win!);
        var ss = (SourceSelectionViewModel)win!.ContentArea.Content;

        bool restored = await WaitFor(() => !ss.IsApplyingSelections && FindNode(ss, _src) is not null, 20000);
        var srcNode = FindNode(ss, _src);
        if (restored && srcNode is not null && FindNode(ss, _b) is null)
        {
            srcNode.IsExpanded = true;
            restored = await WaitFor(() => FindNode(ss, _b) is not null, 10000);
        }
        if (!restored)
        {
            Check(false, $"{label}: the saved selection was restored into the tree");
            return null;
        }

        // The editor arms change tracking on a ContextIdle pass after the restore;
        // let that and the post-show init finish before acting like a user.
        await Task.Delay(1500);
        return new Editor(win, ss);
    }

    /// <summary>Tick a folder the way a click does, and confirm the editor saw a user edit.</summary>
    static async Task<bool> ToggleAsync(Editor ed, string path, bool value)
    {
        var node = FindNode(ed.Vm, path);
        if (node is null) return false;
        int before = ed.Vm.ChangedSelectionPaths.Count;
        node.IsSelected = value;
        // The settle pass runs at Background priority; give it a moment.
        return await WaitFor(() => ed.Vm.ChangedSelectionPaths.Count > before, 3000);
    }

    /// <summary>Close after the Closed handler's async tail (save, reload) has run.</summary>
    static async Task<bool> WaitClosedAsync(Window w, int timeoutMs = 8000)
    {
        bool closed = await WaitFor(() => !IsOpen(w), timeoutMs);
        await Task.Delay(700);
        return closed;
    }

    // ------------------------------------------------------------------ scenarios

    static async Task CancelDiscardsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 1. edits are not saved while the editor is open, and Cancel discards them ===");
        var dbBefore = await DbSetAsync();
        var ed = await OpenEditorAsync("cancel");
        if (ed is null) return;

        int w0 = _counter.Writes;
        Check(await ToggleAsync(ed, _b, true), "ticking folder b registers as a user edit");
        await Task.Delay(1500);   // five times the old 300 ms auto-save delay
        Check(_counter.Writes == w0,
            $"nothing was written 1.5 s after the tick (writes: {_counter.Writes - w0}; the old editor auto-saved at 300 ms)");

        _dialogs.Expect("Discard Changes", DialogWatcher.IDYES);
        ed.Vm.CancelCommand.Execute(null);
        bool closed = await WaitClosedAsync(ed.Window);
        Check(_dialogs.WasAnswered("Discard Changes"), "Cancel asked \"Discard your changes?\"");
        Check(closed, "and, answered Yes, closed the editor");
        _dialogs.ClearExpectations();

        var dbAfter = await DbSetAsync();
        Check(_counter.Writes == w0, $"no catalog write at all (writes: {_counter.Writes - w0})");
        Check(Json(dbAfter.SourceSelections) == Json(dbBefore.SourceSelections),
            "the saved selection is unchanged - folder b is not in it");
        var shared = _vm.SelectedBackupSet;
        Check(!IsCovered(dbBefore.SourceSelections, _b) && !IsCovered(dbAfter.SourceSelections, _b),
            "folder b was not backed up before, and still is not");
        Check(shared is not null && !IsCovered(shared.SourceSelections, _b),
            "and the app's in-memory copy of the set does not carry the discarded tick either");
        await EnsureClosedAsync(ed.Window);
    }

    static async Task CleanCloseWritesNothingAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 2. closing without changes writes nothing ===");
        var ed = await OpenEditorAsync("clean close");
        if (ed is null) return;
        int w0 = _counter.Writes;
        ed.Window.Close();   // the window's X
        bool closed = await WaitClosedAsync(ed.Window);
        Check(closed, "the editor closed without asking anything");
        Check(_counter.Writes == w0,
            $"and wrote nothing (writes: {_counter.Writes - w0}; the old editor saved on every close)");
        await EnsureClosedAsync(ed.Window);
    }

    static async Task CloseAnswerNoDiscardsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 3. X with changes, answer No: nothing saved ===");
        var dbBefore = await DbSetAsync();
        var ed = await OpenEditorAsync("X + No");
        if (ed is null) return;
        int w0 = _counter.Writes;
        await ToggleAsync(ed, _b, true);

        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDNO);
        ed.Window.Close();
        bool closed = await WaitClosedAsync(ed.Window);
        Check(_dialogs.WasAnswered("Unsaved Changes"), "X asked \"Save before closing?\"");
        Check(closed, "and No closed the editor");
        _dialogs.ClearExpectations();

        Check(_counter.Writes == w0, $"no catalog write (writes: {_counter.Writes - w0})");
        Check(Json((await DbSetAsync()).SourceSelections) == Json(dbBefore.SourceSelections),
            "the saved selection is unchanged");
        await EnsureClosedAsync(ed.Window);
    }

    static async Task CloseAnswerCancelThenYesSavesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 4. X with changes: Cancel keeps it open; Yes saves ===");
        var ed = await OpenEditorAsync("X + Cancel/Yes");
        if (ed is null) return;
        int w0 = _counter.Writes;
        await ToggleAsync(ed, _b, true);

        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDCANCEL);
        ed.Window.Close();
        await Task.Delay(500);
        Check(_dialogs.WasAnswered("Unsaved Changes") && IsOpen(ed.Window),
            "Cancel at the prompt keeps the editor open");
        Check(_counter.Writes == w0, "and writes nothing");
        _dialogs.ClearExpectations();

        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDYES);
        ed.Window.Close();
        bool closed = await WaitClosedAsync(ed.Window);
        Check(closed && _dialogs.WasAnswered("Unsaved Changes"), "Yes closes it");
        _dialogs.ClearExpectations();

        var db = await DbSetAsync();
        Check(_counter.Writes - w0 == 1, $"with exactly one write (writes: {_counter.Writes - w0})");
        Check(IsCovered(db.SourceSelections, _b), "and folder b is now saved as backed up");
        Check(IsCovered(db.SourceSelections, _a), "folder a still is");
        Check(!IsCovered(db.SourceSelections, _c), "folder c still is not");
        await EnsureClosedAsync(ed.Window);
    }

    static async Task SaveButtonSavesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 5. the Save button saves and closes ===");
        var ed = await OpenEditorAsync("Save");
        if (ed is null) return;
        int w0 = _counter.Writes;
        await ToggleAsync(ed, _c, true);
        Check(_counter.Writes == w0, "ticking c alone wrote nothing");

        ed.Vm.SaveCommand.Execute(null);
        bool closed = await WaitClosedAsync(ed.Window);
        Check(closed, "Save closed the editor without a prompt");
        var db = await DbSetAsync();
        Check(_counter.Writes - w0 == 1, $"with exactly one write (writes: {_counter.Writes - w0})");
        Check(IsCovered(db.SourceSelections, _c), "and folder c is saved as backed up");
        Check(IsCovered(_vm.SelectedBackupSet?.SourceSelections, _c),
            "the app's in-memory copy of the set shows the save too");
        Check(Find(db.SourceSelections, _missingDrive) is not null,
            $"the unplugged drive {_missingDrive} is still a source after the save (a save used to drop it)");
        Check(Find(db.SourceSelections, _gone)?.IsSelected == true,
            "and the deleted-but-ticked folder is still selected");
        await EnsureClosedAsync(ed.Window);
    }

    static LargestFilesViewModel? FindLargestFilesVm(out Window? host)
    {
        host = null;
        foreach (var w in Application.Current.Windows.OfType<Window>())
        {
            if (w is BackupSetEditorWindow bw && bw.ContentArea?.Content is LargestFilesViewModel lf1)
            { host = w; return lf1; }
            if (w is TaskWindow tw && tw.Flow is LargestFilesViewModel lf2)
            { host = w; return lf2; }
        }
        return null;
    }

    static async Task<(LargestFilesViewModel Vm, Window Host)?> WaitLargestFilesAsync(string label)
    {
        LargestFilesViewModel? lf = null;
        Window? host = null;
        if (!await WaitFor(() => (lf = FindLargestFilesVm(out host)) is not null, 15000))
        {
            Check(false, $"{label}: Largest Files opened");
            return null;
        }
        MoveOffscreen(host!);
        if (!await WaitFor(() => !lf!.IsLoading && lf.Files.Count > 0, 15000))
        {
            Check(false, $"{label}: Largest Files finished its scan");
            return null;
        }
        return (lf!, host!);
    }

    static async Task LargestFilesInsideEditorAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 6. Largest Files opened from the editor is part of the edit ===");
        var excludedBefore = (await DbSetAsync()).JobOptions?.ExcludedExtensions.ToList() ?? [];

        var ed = await OpenEditorAsync("LF in editor");
        if (ed is null) return;
        int w0 = _counter.Writes;
        ed.Vm.LargestFilesCommand.Execute(null);
        var lf = await WaitLargestFilesAsync("LF in editor");
        if (lf is null) return;
        Check(_counter.Writes == w0, $"opening it wrote nothing (writes: {_counter.Writes - w0}; it used to save the whole set)");

        Untick(lf.Value.Vm, _bigFile);
        await Task.Delay(300);
        Check(_counter.Writes == w0, $"unticking a file wrote nothing (writes: {_counter.Writes - w0})");
        Check(BackupJobViewModel.ParseExclusionPatterns(ed.Vm.ExcludedExtensions)
                  .Contains(_bigFile, StringComparer.OrdinalIgnoreCase),
            "the file appears in the editor's own exclusion list");

        _dialogs.Expect("Discard Changes", DialogWatcher.IDYES);
        ed.Vm.CancelCommand.Execute(null);
        bool closed = await WaitClosedAsync(ed.Window);
        Check(closed && _dialogs.WasAnswered("Discard Changes"),
            "Cancel on the editor counts it as an unsaved change and asks before discarding");
        Check(!IsOpen(lf.Value.Host), "Largest Files closed with the editor");
        _dialogs.ClearExpectations();
        Check(_counter.Writes == w0, $"no catalog write (writes: {_counter.Writes - w0})");
        Check(Json((await DbSetAsync()).JobOptions?.ExcludedExtensions) == Json(excludedBefore),
            "the saved exclusions are unchanged");
        await EnsureClosedAsync(lf.Value.Host);
        await EnsureClosedAsync(ed.Window);

        // And kept when the editor is saved.
        var ed2 = await OpenEditorAsync("LF in editor, saved");
        if (ed2 is null) return;
        w0 = _counter.Writes;
        ed2.Vm.LargestFilesCommand.Execute(null);
        var lf2 = await WaitLargestFilesAsync("LF in editor, saved");
        if (lf2 is null) return;
        Untick(lf2.Value.Vm, _bigFile);
        await Task.Delay(300);
        lf2.Value.Vm.CloseCommand.Execute(null);
        await WaitFor(() => !IsOpen(lf2.Value.Host), 5000);
        ed2.Vm.SaveCommand.Execute(null);
        closed = await WaitClosedAsync(ed2.Window);
        var saved = (await DbSetAsync()).JobOptions?.ExcludedExtensions ?? [];
        Check(closed && saved.Contains(_bigFile, StringComparer.OrdinalIgnoreCase),
            "after the editor's Save the exclusion is in the catalog (it used to be lost to the editor's own list)");
        Check(_counter.Writes - w0 == 1, $"in exactly one write (writes: {_counter.Writes - w0})");
        await EnsureClosedAsync(lf2.Value.Host);
        await EnsureClosedAsync(ed2.Window);
    }

    /// <summary>Untick a file in Largest Files; a missing row is a failed check, not a crash.</summary>
    static void Untick(LargestFilesViewModel lf, string path)
    {
        var item = lf.Files.FirstOrDefault(f => f.FullPath == path);
        if (item is null)
            Check(false, $"Largest Files lists {Path.GetFileName(path)} (already excluded?)");
        else
            item.IsIncluded = false;
    }

    static void SetAppFlag(string name, bool value)
        => typeof(App).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                      .SetValue(_app, value);

    static async Task ForcedShutdownSavesNothingAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 7. a forced shutdown (installer / Windows) neither prompts nor saves ===");
        var dbBefore = await DbSetAsync();
        var ed = await OpenEditorAsync("forced shutdown");
        if (ed is null) return;
        int w0 = _counter.Writes;
        await ToggleAsync(ed, _a, false);

        SetAppFlag("IsForcedShutdown", true);
        try
        {
            ed.Window.Close();
            bool closed = await WaitClosedAsync(ed.Window);
            Check(closed, "the editor closed without a prompt");
        }
        finally
        {
            SetAppFlag("IsForcedShutdown", false);
        }
        Check(_counter.Writes == w0,
            $"and saved nothing nobody confirmed (writes: {_counter.Writes - w0}; it used to save \"best effort\")");
        Check(Json((await DbSetAsync()).SourceSelections) == Json(dbBefore.SourceSelections),
            "the saved selection is unchanged");
        await EnsureClosedAsync(ed.Window);
    }

    static async Task ExitAnswerYesSavesBeforeReturningAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 8. File > Exit, Yes: the save is on disk before the window is gone ===");
        var ed = await OpenEditorAsync("exit");
        if (ed is null) return;
        int w0 = _counter.Writes;
        await ToggleAsync(ed, _a, false);

        SetAppFlag("IsExiting", true);
        try
        {
            _dialogs.Expect("Unsaved Changes", DialogWatcher.IDYES);
            ed.Window.Close();
            // No awaiting here: an exiting process does not wait either.
            var db = await Task.Run(() => _observer.GetBackupSetAsync(_setId));
            Check(_dialogs.WasAnswered("Unsaved Changes"), "the exit asked whether to save");
            Check(!IsCovered(db!.SourceSelections, _a),
                "and by the time Close() returned, the unticked folder was already saved");
            await WaitClosedAsync(ed.Window);
            _dialogs.ClearExpectations();
        }
        finally
        {
            SetAppFlag("IsExiting", false);
        }
        Check(_counter.Writes - w0 == 1, $"with exactly one write (writes: {_counter.Writes - w0})");
        await EnsureClosedAsync(ed.Window);
    }

    static async Task StandaloneLargestFilesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 9. Largest Files on its own: nothing saved until Save ===");
        var row = _vm.BackupSets.First(r => r.Id == _setId);
        var excludedBefore = (await DbSetAsync()).JobOptions?.ExcludedExtensions.ToList() ?? [];

        int w0 = _counter.Writes;
        _vm.SetLargestFilesCommand.Execute(row);
        var lf = await WaitLargestFilesAsync("standalone LF");
        if (lf is null) return;
        Untick(lf.Value.Vm, _otherFile);
        await Task.Delay(300);
        Check(_counter.Writes == w0, $"unticking a file wrote nothing (writes: {_counter.Writes - w0})");

        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDNO);
        lf.Value.Host.Close();   // the window's X
        bool closed = await WaitClosedAsync(lf.Value.Host);
        Check(closed && _dialogs.WasAnswered("Unsaved Changes"), "X asked, and No closed it");
        _dialogs.ClearExpectations();
        Check(_counter.Writes == w0, $"no catalog write (writes: {_counter.Writes - w0})");
        Check(Json((await DbSetAsync()).JobOptions?.ExcludedExtensions) == Json(excludedBefore),
            "the saved exclusions are unchanged");
        await EnsureClosedAsync(lf.Value.Host);

        // Again, answering Yes - after someone else changed a setting meanwhile.
        _vm.SetLargestFilesCommand.Execute(row);
        lf = await WaitLargestFilesAsync("standalone LF, Yes");
        if (lf is null) return;
        w0 = _counter.Writes;
        Untick(lf.Value.Vm, _otherFile);
        await Task.Delay(300);

        var elsewhere = await DbSetAsync();
        elsewhere.JobOptions!.SubdirectoryName = "changed-elsewhere";
        await _observer.UpdateBackupSetMetadataAsync(elsewhere);

        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDYES);
        lf.Value.Vm.CloseCommand.Execute(null);   // the Close button
        closed = await WaitClosedAsync(lf.Value.Host);
        Check(closed && _dialogs.WasAnswered("Unsaved Changes"), "the Close button asked too, and Yes closed it");
        _dialogs.ClearExpectations();
        var db = await DbSetAsync();
        Check(db.JobOptions!.ExcludedExtensions.Contains(_otherFile, StringComparer.OrdinalIgnoreCase),
            "the exclusion was saved");
        Check(db.JobOptions.SubdirectoryName == "changed-elsewhere",
            "without putting back this window's stale copy of the other settings");
        Check(_counter.Writes - w0 == 1, $"in exactly one write (writes: {_counter.Writes - w0})");
        await EnsureClosedAsync(lf.Value.Host);
    }

    /// <summary>Open the editor for a brand-new set, as the New Backup Set button does.</summary>
    static async Task<Editor?> OpenNewSetEditorAsync(string label)
    {
        var before = Application.Current.Windows.OfType<BackupSetEditorWindow>().ToHashSet();
        _vm.NewBackupSetCommand.Execute(null);
        BackupSetEditorWindow? win = null;
        if (!await WaitFor(() => (win = Application.Current.Windows.OfType<BackupSetEditorWindow>()
                                     .FirstOrDefault(w => !before.Contains(w)
                                                          && w.ContentArea?.Content is SourceSelectionViewModel)) is not null,
                           20000))
        {
            Check(false, $"{label}: the editor opened");
            return null;
        }
        MoveOffscreen(win!);
        var ss = (SourceSelectionViewModel)win!.ContentArea.Content;
        await WaitFor(() => !ss.IsApplyingSelections, 20000);
        await Task.Delay(1500);
        return new Editor(win, ss);
    }

    static async Task<int> SetCountAsync() => (await _observer.GetAllBackupSetsAsync()).Count;

#if !OLD_CODE
    static async Task ForgetMissingInEditorAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 11. \"Forget them\" in the real editor: an edit like any other ===");
        var dbBefore = await DbSetAsync();
        var ed = await OpenEditorAsync("forget");
        if (ed is null) return;
        Check(await WaitFor(() => ed.Vm.HasMissingFolders, 5000) && ed.Vm.MissingFolderCount == 1,
            $"the bar reports the deleted folder - one, not the unplugged drive (count {ed.Vm.MissingFolderCount})");
        int w0 = _counter.Writes;

        ed.Vm.ForgetMissingCommand.Execute(null);
        Check(await WaitFor(() => !ed.Vm.HasMissingFolders, 5000), "Forget clears the bar");
        Check(_counter.Writes == w0, "and writes nothing by itself");

        _dialogs.Expect("Discard Changes", DialogWatcher.IDYES);
        ed.Vm.CancelCommand.Execute(null);
        bool closed = await WaitClosedAsync(ed.Window);
        Check(closed && _dialogs.WasAnswered("Discard Changes"),
            "Cancel treats it as an unsaved change and asks before discarding");
        _dialogs.ClearExpectations();
        Check(_counter.Writes == w0 && Json((await DbSetAsync()).SourceSelections) == Json(dbBefore.SourceSelections),
            "discarded: nothing written, the deleted folder still selected");
        await EnsureClosedAsync(ed.Window);

        var dbBeforeSave = await DbSetAsync();
        var ed2 = await OpenEditorAsync("forget, saved");
        if (ed2 is null) return;
        await WaitFor(() => ed2.Vm.HasMissingFolders, 5000);
        w0 = _counter.Writes;
        ed2.Vm.ForgetMissingCommand.Execute(null);
        await WaitFor(() => !ed2.Vm.HasMissingFolders, 5000);
        ed2.Vm.SaveCommand.Execute(null);
        closed = await WaitClosedAsync(ed2.Window);
        var db = await DbSetAsync();
        Check(closed && _counter.Writes - w0 == 1, $"Save writes it, once (writes: {_counter.Writes - w0})");
        Check(Find(db.SourceSelections, _gone) is null, "the deleted folder is gone from the selection");
        Check(Find(db.SourceSelections, _missingDrive) is not null
              && new[] { _a, _b, _c }.All(p => Covers(db.SourceSelections, p) == Covers(dbBeforeSave.SourceSelections, p)),
            "the unplugged drive, and what is and isn't backed up among the present folders, are untouched");
        await EnsureClosedAsync(ed2.Window);
    }
#endif

    static async Task NewSetAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 10. a new set: kept only if the user says so ===");
        int sets0 = await SetCountAsync();

        var ed = await OpenNewSetEditorAsync("new set, Cancel");
        if (ed is null) return;
        Check(await SetCountAsync() == sets0 + 1, "opening it creates the temporary record, as before");
        _dialogs.Expect("Discard Changes", DialogWatcher.IDYES);
        ed.Vm.CancelCommand.Execute(null);
        bool closed = await WaitClosedAsync(ed.Window);
        Check(closed && _dialogs.WasAnswered("Discard Changes"), "Cancel asked \"Discard this new backup set?\" and closed");
        _dialogs.ClearExpectations();
        Check(await SetCountAsync() == sets0, "and the temporary record is gone");
        await EnsureClosedAsync(ed.Window);

        ed = await OpenNewSetEditorAsync("new set, X + No");
        if (ed is null) return;
        _dialogs.Expect("Unsaved Backup Set", DialogWatcher.IDNO);
        ed.Window.Close();
        closed = await WaitClosedAsync(ed.Window);
        Check(closed && _dialogs.WasAnswered("Unsaved Backup Set"), "X asked whether to save it; No closed it");
        _dialogs.ClearExpectations();
        Check(await SetCountAsync() == sets0, "and the temporary record is gone");
        await EnsureClosedAsync(ed.Window);

        ed = await OpenNewSetEditorAsync("new set, X + Yes");
        if (ed is null) return;
        _dialogs.Expect("Unsaved Backup Set", DialogWatcher.IDYES);
        ed.Window.Close();
        closed = await WaitClosedAsync(ed.Window);
        Check(closed && _dialogs.WasAnswered("Unsaved Backup Set"), "X, Yes: closed");
        _dialogs.ClearExpectations();
        Check(await SetCountAsync() == sets0 + 1, "and the new set was kept");
        Check(await WaitFor(() => _vm.BackupSets.Count == sets0 + 1, 5000), "and appears in the main window's list");
        await EnsureClosedAsync(ed.Window);
    }

#if !OLD_CODE
    // ------------------------------------------------------------------ units

    static async Task UnitTestsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 12. the editor's private copy is complete and independent ===");
        var original = new BackupSet
        {
            Id = 3,
            Name = "orig",
            SourceRoots = [@"C:\x"],
            MaxIncrementalDiscs = 7,
            DefaultMediaType = MediaType.Directory,
            DefaultFilesystemType = (FilesystemType)1,
            CapacityOverrideBytes = 123,
            SourceSelections = [Dir(@"C:\x", null, true, false, Dir(@"C:\x\y", true, false))],
            JobOptions = new JobOptions { TargetDirectory = @"E:\t", ExcludedExtensions = ["*.log"] },
            CreatedUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            LastBackupUtc = new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        var clone = MainViewModel.CloneBackupSet(original);
        var props = typeof(BackupSet).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite).ToList();
        var differ = props.Where(p => Json(p.GetValue(original)) != Json(p.GetValue(clone))).Select(p => p.Name).ToList();
        Check(differ.Count == 0, $"all {props.Count} properties copied" + (differ.Count > 0 ? " - DIFFER: " + string.Join(", ", differ) : ""));
        var shared = props.Where(p => !p.PropertyType.IsValueType && p.PropertyType != typeof(string)
                                      && p.GetValue(original) is { } v && ReferenceEquals(v, p.GetValue(clone)))
                          .Select(p => p.Name).ToList();
        Check(shared.Count == 0, "no list or object is shared with the original" + (shared.Count > 0 ? " - SHARED: " + string.Join(", ", shared) : ""));
        clone.JobOptions!.ExcludedExtensions.Add("*.tmp");
        clone.SourceSelections![0].Children.Add(Dir(@"C:\x\z", true, false));
        clone.SourceRoots.Add(@"D:\");
        Check(original.JobOptions!.ExcludedExtensions.Count == 1
              && original.SourceSelections![0].Children.Count == 1
              && original.SourceRoots.Count == 1,
            "editing the copy's nested lists leaves the original alone");

        Console.WriteLine();
        Console.WriteLine("=== 13. a Largest Files toggle edits the exclusion list text correctly ===");
        Check(MainViewModel.ApplyExclusionToggle("", @"C:\a\x.bin", true) == @"C:\a\x.bin", "adds to an empty list");
        string text = "*.log\n" + @"C:\a\x.bin";
        Check(ReferenceEquals(MainViewModel.ApplyExclusionToggle(text, @"c:\A\X.BIN", true), text),
            "an already-present pattern (any case) changes nothing - the same text comes back");
        Check(MainViewModel.ApplyExclusionToggle(text, @"c:\A\X.BIN", false) == "*.log",
            "re-including removes it, case-insensitively, leaving the rest");
        Check(ReferenceEquals(MainViewModel.ApplyExclusionToggle("*.log", @"C:\zzz", false), "*.log"),
            "re-including something not listed changes nothing");
        Check(BackupJobViewModel.ParseExclusionPatterns(
                  MainViewModel.ApplyExclusionToggle("*.log\r\n*.tmp", @"C:\d\*", true))
              .SequenceEqual(["*.log", "*.tmp", @"C:\d\*"]),
            "appends to a CRLF list without losing entries");

        Console.WriteLine();
        Console.WriteLine("=== 14. Largest Files view model, both modes ===");
        var set2 = await _observer.CreateBackupSetAsync(new BackupSet
        {
            Name = "lf-unit",
            SourceRoots = [_src],
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = _dst },
            SourceSelections = [Dir(_src, true, false)],
        });
        var scanner = new StubScanner(
        [
            new ScannedFile { FullPath = _bigFile, SizeBytes = 5000, LastWriteUtc = DateTime.UtcNow },
            new ScannedFile { FullPath = _otherFile, SizeBytes = 3000, LastWriteUtc = DateTime.UtcNow },
        ]);

        // Deferred (inside the editor): toggles go to the host, never to the catalog.
        var deferred = new LargestFilesViewModel(scanner, _catalog, (await _observer.GetBackupSetAsync(set2.Id))!,
                                                 0, deferPersistence: true);
        var got = new List<(string, bool)>();
        deferred.ExclusionToggled += (p, e) => got.Add((p, e));
        await WaitFor(() => !deferred.IsLoading, 10000);
        int w0 = _counter.Writes;
        deferred.Files.First(f => f.FullPath == _bigFile).IsIncluded = false;
        var bDir = FindDir(deferred.Directories, _b);
        if (bDir is not null) bDir.IsIncluded = false;
        Check(got.Count == 2 && got[0] == (_bigFile, true) && got[1] == (_b + @"\*", true),
            "deferred: each toggle is handed to the editor as (pattern, excluded)");
        Check(_counter.Writes == w0 && !deferred.HasUnsavedChanges && !deferred.ShowSaveButton,
            "and nothing is written, nothing is pending, and there is no Save button");
        Check(deferred.ConfirmClose(canCancel: true), "closing it asks nothing");

        // Standalone.
        var alone = new LargestFilesViewModel(scanner, _catalog, (await _observer.GetBackupSetAsync(set2.Id))!, 0);
        await WaitFor(() => !alone.IsLoading, 10000);
        w0 = _counter.Writes;
        var item = alone.Files.First(f => f.FullPath == _bigFile);
        item.IsIncluded = false;
        Check(alone.HasUnsavedChanges && _counter.Writes == w0, "standalone: a toggle is pending, not written");
        item.IsIncluded = true;
        Check(!alone.HasUnsavedChanges, "toggling it back leaves nothing to save");
        Check(alone.ConfirmClose(canCancel: true), "so closing asks nothing");
        item.IsIncluded = false;
        Check(await alone.SaveAsync() && !alone.HasUnsavedChanges, "Save writes it and clears the pending state");
        var after = (await _observer.GetBackupSetAsync(set2.Id))!;
        Check(after.JobOptions!.ExcludedExtensions.Contains(_bigFile, StringComparer.OrdinalIgnoreCase)
              && _counter.Writes - w0 == 1,
            "in exactly one write");
        Check(Json(after.SourceSelections) == Json(set2.SourceSelections),
            "and the selection was not touched");
    }

    static DirectoryItem? FindDir(IEnumerable<DirectoryItem>? items, string path)
    {
        if (items is null) return null;
        foreach (var d in items)
        {
            if (string.Equals(d.FullPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return d;
            if (FindDir(d.Children, path) is { } hit)
                return hit;
        }
        return null;
    }
#endif
}

// ---------------------------------------------------------------------- doubles


/// <summary>A scanner that "finds" a fixed list of files, honouring the exclusion filter.</summary>
internal sealed class StubScanner(IReadOnlyList<ScannedFile> files) : IFileScanner
{
    public Task<IReadOnlyList<ScannedFile>> ScanAsync(
        IReadOnlyList<SourceSelection> sources, IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default, Func<string, bool>? isExcluded = null,
        Func<string, bool>? isDirectoryExcluded = null)
        => Task.FromResult<IReadOnlyList<ScannedFile>>(
            files.Where(f => isExcluded is null || !isExcluded(f.FullPath)).ToList());

    public Task<BackupDiff> ComputeDiffAsync(
        IReadOnlyList<ScannedFile> scannedFiles, int backupSetId, CancellationToken ct = default)
        => Task.FromResult(new BackupDiff { NewFiles = scannedFiles });
}

/// <summary>Forwards every catalog call to the real repository, counting backup-set writes.</summary>
public class CountingCatalog : DispatchProxy
{
    private ICatalogRepository _inner = null!;
    private int _writes;

    public int Writes => Volatile.Read(ref _writes);

    internal static ICatalogRepository Wrap(ICatalogRepository inner, out CountingCatalog counter)
    {
        var proxy = Create<ICatalogRepository, CountingCatalog>();
        counter = (CountingCatalog)(object)proxy;
        counter._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name is nameof(ICatalogRepository.UpdateBackupSetAsync)
                               or nameof(ICatalogRepository.UpdateBackupSetMetadataAsync)
                               or nameof(ICatalogRepository.CreateBackupSetAsync)
                               or nameof(ICatalogRepository.DeleteBackupSetAsync))
            Interlocked.Increment(ref _writes);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// Answers this process's message boxes from a background thread, as a user
/// would: expected ones (by title) with the given button, anything else with
/// Cancel/No - and records it as unexpected.
/// </summary>
internal sealed class DialogWatcher : IDisposable
{
    public const int IDOK = 1, IDCANCEL = 2, IDYES = 6, IDNO = 7;

    private readonly object _lock = new();
    private readonly List<(string Title, int Button)> _expected = [];
    private readonly HashSet<string> _answered = [];
    public readonly List<string> Unexpected = [];
    private readonly Thread _thread;
    private volatile bool _stop;

    public DialogWatcher()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "dialog watcher" };
        _thread.Start();
    }

    public void Expect(string title, int button)
    {
        lock (_lock) { _expected.Add((title, button)); _answered.Remove(title); }
    }

    public bool WasAnswered(string title) { lock (_lock) return _answered.Contains(title); }

    public void ClearExpectations() { lock (_lock) { _expected.Clear(); _answered.Clear(); } }

    private void Loop()
    {
        uint me = (uint)Environment.ProcessId;
        var seen = new Dictionary<IntPtr, long>();
        var sw = Stopwatch.StartNew();
        while (!_stop)
        {
            var present = new HashSet<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;
                present.Add(h);

                if (seen.TryGetValue(h, out long at))
                {
                    // Answered but still up after a second: force it shut.
                    if (sw.ElapsedMilliseconds - at > 1000)
                    {
                        PostMessage(h, 0x0111, IDCANCEL, IntPtr.Zero);
                        PostMessage(h, 0x0111, IDNO, IntPtr.Zero);
                        seen[h] = sw.ElapsedMilliseconds;
                    }
                    return true;
                }

                var title = new StringBuilder(256);
                GetWindowText(h, title, title.Capacity);
                var textBuf = new StringBuilder(1024);
                GetWindowText(GetDlgItem(h, 0xFFFF), textBuf, textBuf.Capacity);
                string t = title.ToString();

                int? button = null;
                lock (_lock)
                {
                    int i = _expected.FindIndex(e => e.Title == t);
                    if (i >= 0)
                    {
                        button = _expected[i].Button;
                        _expected.RemoveAt(i);
                        _answered.Add(t);
                    }
                    else
                    {
                        Unexpected.Add($"{t}: {textBuf.ToString().Replace("\n", " ")}");
                    }
                }
                seen[h] = sw.ElapsedMilliseconds;
                if (button is int b)
                {
                    PostMessage(h, 0x0111, b, IntPtr.Zero);
                }
                else
                {
                    PostMessage(h, 0x0111, IDCANCEL, IntPtr.Zero);
                    PostMessage(h, 0x0111, IDNO, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);

            foreach (var h in seen.Keys.Where(h => !present.Contains(h)).ToList())
                seen.Remove(h);
            Thread.Sleep(40);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hDlg, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, nint wParam, IntPtr lParam);
}
