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
            // Closed first: SQLite's open files keep the folder from going.
            try { _catalog?.Dispose(); } catch { }
            try { _observer?.Dispose(); } catch { }
            SqliteClearPools();
            // Retried briefly: a folder another process (a scanner) still has
            // open is only marked for deletion, and stays visible until it lets go.
            for (int attempt = 0; attempt < 20 && Directory.Exists(_root); attempt++)
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
                if (Directory.Exists(_root))
                    Thread.Sleep(100);
            }
        }
        Check(!Directory.Exists(_root), $"the harness leaves no temp folder behind ({_root})");

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
        await TierEditsEnableSaveAsync();
#if !OLD_CODE
        await ForgetMissingInEditorAsync();
#endif
#if !OLD_CODE
        await UnitTestsAsync();
        await LayoutAndSmallFixesAsync();
        await SeedHoldsTheRowAsync(dbPath);
        NoTierWarning();
        await ReconcileAfterEditAsync();
        await VerifyStartsAsync();
        await RetentionChangeOffersRemovalAsync();
        await RecordsElsewhereRefusedAsync();
        await SharedBackupFolderAsync();
#endif
        Check(_dialogs.Unexpected.Count == 0,
            "no unexpected message box appeared"
            + (_dialogs.Unexpected.Count > 0 ? ": " + string.Join(" | ", _dialogs.Unexpected) : ""));
    }

#if !OLD_CODE
    /// <summary>
    /// 22: a set of the user's own with no tiers excludes its files, which the
    /// editor showed only while Default was selected; the set says so itself now.
    /// </summary>
    static void NoTierWarning()
    {
        Console.WriteLine();
        Console.WriteLine("=== 22. a custom set with no tiers says it excludes its files ===");
        var photos = new TierSetViewModel("Photos", isBuiltIn: false);
        Check(photos.ExcludesForLackOfTiers, "a custom set with no tiers warns that it excludes");
        int raised = 0;
        photos.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TierSetViewModel.ExcludesForLackOfTiers)) raised++; };
        photos.Tiers.Add(RetentionTierViewModel.FromModel(new VersionRetentionTier { MaxVersions = 3 }));
        Check(!photos.ExcludesForLackOfTiers && raised == 1, "and stops, and says so, once it has a tier");
        Check(!new TierSetViewModel("Default", isBuiltIn: true).ExcludesForLackOfTiers,
            "an empty Default never does (its files are backed up without history)");
        Check(!new TierSetViewModel("None", isBuiltIn: true).ExcludesForLackOfTiers,
            "nor \"None\", which says it excludes in its own words");
    }

    /// <summary>
    /// 24: Verify Integrity verifies when its window opens, and says so as it
    /// goes: the run it starts on opening finishes, and Verify is available
    /// again after it.
    /// </summary>
    static async Task VerifyStartsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 24. Verify Integrity verifies when its window opens ===");
        var row = _vm.BackupSets.First(r => r.Id == _setId);
        _vm.SetVerifyIntegrityCommand.Execute(row);
        TaskWindow? win = null;
        Check(await WaitFor(() => (win = Application.Current.Windows.OfType<TaskWindow>()
                                      .FirstOrDefault(w => w.Flow is VerifyIntegrityViewModel)) is not null, 10000),
            "the Verify Integrity window opened");
        if (win is null) return;
        MoveOffscreen(win);
        // Its steps' reports are verify_progress_test's to check; this small set
        // can finish before anything here could watch them.
        var verify = (VerifyIntegrityViewModel)win.Flow!;
        bool finished = await WaitFor(() => !verify.IsLoading && verify.SummaryText.Length > 0, 30000);
        Check(finished, $"it verified, and finished (\"{verify.SummaryText}\"; showing \"{verify.ProgressText}\")");
        Check(verify.CanVerify, "and Verify is available again");
        win.Close();
        await WaitFor(() => !IsOpen(win), 5000);
    }

    /// <summary>
    /// 23: after an edit that drops one folder and adds another, the app looks for
    /// what changed. The dropped folder's backed-up copies are offered for removal,
    /// and Remove deletes them and retires their records, leaving the rest; the
    /// added folder's files are offered for backup. The scans and the purge do
    /// their catalog work inside progress windows, awaited there.
    /// </summary>
    static async Task ReconcileAfterEditAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 23. after an edit, a dropped folder's copies go and an added one's files are offered ===");

        // a and b backed up, for real, to a destination of its own (a new
        // folder, not the earlier scenarios' emptied: a folder deleted and made
        // again at once can be refused while a scanner still has it open).
        string dst = Path.Combine(_root, "dst-reconcile");
        Directory.CreateDirectory(dst);
        var set = await DbSetAsync();
        set.SourceSelections =
        [
            Dir(_src, null, auto: false, expanded: true,
                Dir(_a, true, auto: false),
                Dir(_b, true, auto: false)),
        ];
        set.JobOptions!.TargetDirectory = dst;
        await _observer.UpdateBackupSetAsync(set);
        await _observer.ClearBackupSetCatalogAsync(_setId);
        var backup = new DirectoryBackupService(
            _observer, new LithicBackup.Infrastructure.FileSystem.FileScanner(_observer),
            new VersionRetentionService(_observer));
        var ran = await backup.ExecuteAsync(new BackupJob
        {
            BackupSetId = _setId, Sources = set.SourceSelections, EnableFileDeduplication = true,
            TargetDirectory = dst, ZipMode = ZipMode.None,
        }, dst, null, null, CancellationToken.None);
        string CopyOf(string source) => Path.Combine(dst, source[..1], source[Path.GetPathRoot(source)!.Length..]);
        bool smallCopied = File.Exists(CopyOf(_smallFile)), bigCopied = File.Exists(CopyOf(_bigFile));
        if (!(ran.Success && smallCopied && bigCopied))
        {
            Check(false, $"set-up: a and b are backed up (success: {ran.Success}; "
                + $"{CopyOf(_smallFile)}: {smallCopied}; {CopyOf(_bigFile)}: {bigCopied}"
                + string.Concat(ran.FailedFiles.Select(f => $"; {f.Path}: {f.Error}")) + ")");
            return;
        }

        // A main window that reconciles after every edit, without asking first.
        _vm = new MainViewModel(
            _catalog,
            burner: null!,
            scanner: new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
            orchestrator: null!,
            restoreService: null!,
            catalogFreeRestoreService: null!,
            directoryBackupService: new DirectoryBackupService(
                _catalog,
                new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
                new VersionRetentionService(_catalog)),
            settings: new UserSettings { ReconcileMode = ReconcileAfterEditMode.Always });
        if (!await WaitFor(() => _vm.BackupSets.Any(r => r.Id == _setId), 10000))
        {
            Check(false, "the reconciling main window loaded the test set");
            return;
        }
        var row = _vm.BackupSets.First(r => r.Id == _setId);

        var ed = await OpenEditorAsync("reconcile");
        if (ed is null) return;
        await ToggleAsync(ed, _b, false);
        await ToggleAsync(ed, _c, true);
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closed the editor");

        // Dropped: b's copy is offered for removal. Remove.
        ReviewTreeDialog? removal = null;
        Check(await WaitFor(() => (removal = Application.Current.Windows.OfType<ReviewTreeDialog>()
                                      .FirstOrDefault()) is not null, 20000),
            "the scan found what the edit dropped, and asks about it");
        if (removal is null) return;
        string removalHeader = ((ReviewTreeViewModel)removal.DataContext).HeaderText.Split('\n')[0];
        // N0, as the header writes it: the separator is the locale's.
        Check(removalHeader.StartsWith($"1 file ({5000:N0} bytes)", StringComparison.Ordinal),
            $"just b's file (\"{removalHeader}\")");
        removal.DialogResult = true;

        // Added: c's file is offered for backup. Not now.
        ReviewTreeDialog? addition = null;
        Check(await WaitFor(() => (addition = Application.Current.Windows.OfType<ReviewTreeDialog>()
                                       .FirstOrDefault(w => w != removal)) is not null, 20000),
            "then the added folder's files are offered for backup");
        if (addition is null) return;
        string additionHeader = ((ReviewTreeViewModel)addition.DataContext).HeaderText.Split('\n')[0];
        Check(additionHeader.StartsWith($"1 file ({3000:N0} bytes)", StringComparison.Ordinal),
            $"just c's file (\"{additionHeader}\")");
        addition.DialogResult = false;
        await Task.Delay(500);

        Check(!File.Exists(CopyOf(_bigFile)) && File.Exists(CopyOf(_smallFile)),
            "Remove deleted b's backed-up copy, and only that");
        var live = (await _observer.GetAllFilesForBackupSetAsync(_setId))
            .Where(r => !r.IsDeleted).Select(r => r.SourcePath).ToList();
        Check(live.Count == 1 && string.Equals(live[0], _smallFile, StringComparison.OrdinalIgnoreCase),
            $"and retired its record, leaving a's ({string.Join(", ", live)})");
        Check(row.LastResultText.StartsWith("Removed sources: purged 1 catalog record, deleted 1 file", StringComparison.Ordinal),
            $"the row says what was removed (\"{row.LastResultText}\")");
    }

    /// <summary>
    /// 25: after a saved edit that tightens the retention settings, the earlier
    /// versions they no longer keep are offered for removal, and Remove deletes
    /// exactly those. Declining removes nothing, and a save that leaves the
    /// retention settings alone offers nothing - even with versions still there
    /// that the settings do not keep.
    /// </summary>
    static async Task RetentionChangeOffersRemovalAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 25. after a retention change, the versions no longer kept are offered for removal ===");

        // Real version history: small.txt backed up three times (two earlier
        // versions), big.bin twice (one), with a Default that keeps every version.
        string dst = Path.Combine(_root, "dst-retention");
        Directory.CreateDirectory(dst);
        var keepAll = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = null }] };
        var set = await DbSetAsync();
        set.SourceSelections =
        [
            Dir(_src, null, auto: false, expanded: true,
                Dir(_a, true, auto: false),
                Dir(_b, true, auto: false)),
        ];
        set.JobOptions!.TargetDirectory = dst;
        set.JobOptions.TierSets = [keepAll];
        set.JobOptions.RetentionTiers = keepAll.Tiers;
        await _observer.UpdateBackupSetAsync(set);
        await _observer.ClearBackupSetCatalogAsync(_setId);
        var backup = new DirectoryBackupService(
            _observer, new LithicBackup.Infrastructure.FileSystem.FileScanner(_observer),
            new VersionRetentionService(_observer));
        async Task<bool> BackUp()
        {
            var r = await backup.ExecuteAsync(new BackupJob
            {
                BackupSetId = _setId, Sources = set.SourceSelections, TargetDirectory = dst,
                ZipMode = ZipMode.None, TierSets = [keepAll], RetentionTiers = keepAll.Tiers,
            }, dst, keepAll.Tiers, null, CancellationToken.None);
            return r.Success;
        }
        File.WriteAllText(_smallFile, "small, version one");
        File.WriteAllText(_bigFile, "big, version one");
        bool built = await BackUp();
        File.WriteAllText(_smallFile, "small, version two, longer");
        built &= await BackUp();
        File.WriteAllText(_smallFile, "small, version three, longer still");
        File.WriteAllText(_bigFile, "big, version two, longer");
        built &= await BackUp();

        string Rel(string source) => source[Path.GetPathRoot(source)!.Length..];
        string Current(string source) => Path.Combine(dst, source[..1], Rel(source));
        string Prev(string source, int v) => Path.Combine(dst, source[..1] + "_prev", Rel(source) + $".v{v}");
        if (!(built && File.Exists(Prev(_smallFile, 1)) && File.Exists(Prev(_smallFile, 2)) && File.Exists(Prev(_bigFile, 1))))
        {
            Check(false, "set-up: small.txt has two earlier versions on disk and big.bin one");
            return;
        }
        long smallV1Bytes = new FileInfo(Prev(_smallFile, 1)).Length;

        // A main window that reconciles no source edit, so only the retention
        // offer can ask anything.
        _vm = new MainViewModel(
            _catalog,
            burner: null!,
            scanner: new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
            orchestrator: null!,
            restoreService: null!,
            catalogFreeRestoreService: null!,
            directoryBackupService: null!,
            settings: new UserSettings { ReconcileMode = ReconcileAfterEditMode.Never });
        if (!await WaitFor(() => _vm.BackupSets.Any(r => r.Id == _setId), 10000))
        {
            Check(false, "the main window loaded the test set");
            return;
        }
        var row = _vm.BackupSets.First(r => r.Id == _setId);
        static string Header(ReviewTreeDialog d) => ((ReviewTreeViewModel)d.DataContext).HeaderText.Split('\n')[0];
        async Task<HashSet<string>> LiveDiscPaths() => (await _observer.GetActivePreviousVersionsAsync(_setId))
            .Select(r => Path.Combine(dst, r.DiscPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An empty folder elsewhere in the backup, which a removal of one file's
        // version has no business sweeping away.
        string unrelatedEmpty = Path.Combine(dst, "unrelated-empty-folder");
        Directory.CreateDirectory(unrelatedEmpty);

        // --- keep every version -> keep 1: small.txt's older earlier version goes ---
        var ed = await OpenEditorAsync("retention: keep 1");
        if (ed is null) return;
        var def = DefaultOf(ed);
        ed.Vm.SelectedTierSet = def;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        def!.Tiers[0].MaxVersions = "1";
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");

        ReviewTreeDialog? review = null;
        Check(await WaitFor(() => (review = OpenReview()) is not null, 20000),
            "the versions keep-1 no longer keeps are offered for removal");
        if (review is null) return;
        // N0, as the header writes it: the separator is the locale's.
        Check(Header(review).StartsWith($"1 earlier version of 1 file ({smallV1Bytes:N0} bytes) is no longer kept",
                StringComparison.Ordinal),
            $"just small.txt's older earlier version (\"{Header(review)}\")");
        review.DialogResult = true;
        Check(await WaitFor(() => !row.IsRunning && row.LastResultText.Length > 0, 20000), "the removal finishes");
        Check(!File.Exists(Prev(_smallFile, 1)), "Remove deleted small.txt.v1");
        Check(File.Exists(Prev(_smallFile, 2)) && File.Exists(Current(_smallFile))
              && File.Exists(Prev(_bigFile, 1)) && File.Exists(Current(_bigFile)),
            "and nothing else: small.txt's newer earlier version, both current copies and big.bin's earlier version stay");
        var live = await LiveDiscPaths();
        Check(!live.Contains(Prev(_smallFile, 1)) && live.Contains(Prev(_smallFile, 2)) && live.Contains(Prev(_bigFile, 1)),
            "its record is retired, and only its");
        Check(row.LastResultText.StartsWith("Removed 1 earlier version: deleted 1 file", StringComparison.Ordinal),
            $"the row says what was removed (\"{row.LastResultText}\")");
        Check(Directory.Exists(unrelatedEmpty), "and sweeps no empty folder but the deleted file's own");
        await EnsureClosedAsync(ed.Window);

        // --- empty Default, then decline: nothing is removed ---
        ed = await OpenEditorAsync("retention: empty, decline");
        if (ed is null) return;
        def = DefaultOf(ed);
        ed.Vm.SelectedTierSet = def;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        foreach (var tier in def!.Tiers.ToList())
            tier.RemoveCommand.Execute(null);
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");
        review = null;
        Check(await WaitFor(() => (review = OpenReview()) is not null, 20000),
            "emptying Default offers every earlier version left");
        if (review is null) return;
        Check(Header(review).StartsWith("2 earlier versions of 2 files", StringComparison.Ordinal),
            $"small.txt's and big.bin's (\"{Header(review)}\")");
        review.DialogResult = false;
        await Task.Delay(1000);
        Check(File.Exists(Prev(_smallFile, 2)) && File.Exists(Prev(_bigFile, 1)), "declining deletes nothing");
        Check((await LiveDiscPaths()).Count == 2, "and retires nothing");
        Check(_vm.StatusText.Contains("Cleanup", StringComparison.Ordinal),
            $"and says where they can go later (\"{_vm.StatusText}\")");
        await EnsureClosedAsync(ed.Window);

        // --- a save that leaves retention alone offers nothing, though versions
        // the settings do not keep are still there. Stored without the built-in
        // "None" set, which the editor adds on loading: the save writes the tier
        // sets back in a different form with the same meaning, which must not
        // count as a change. ---
        set = await DbSetAsync();
        set.JobOptions!.TierSets = [new VersionTierSet { Name = "Default", Tiers = [] }];
        set.JobOptions.RetentionTiers = [];
        await _observer.UpdateBackupSetAsync(set);
        ed = await OpenEditorAsync("retention: untouched");
        if (ed is null) return;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        ed.Vm.SetName = "editor-save-test (renamed)";
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");
        Check(!await WaitFor(() => OpenReview() is not null, 3000),
            "a save that changed no retention setting offers no removal");
        if (OpenReview() is { } stray)
            stray.DialogResult = false;
        await EnsureClosedAsync(ed.Window);

        // Change Default's tiers in a fresh editor, and save.
        async Task<bool> SaveDefaultTiers(string label, string? maxVersions, Action? beforeSave = null)
        {
            var e = await OpenEditorAsync(label);
            if (e is null) return false;
            var d = DefaultOf(e)!;
            e.Vm.SelectedTierSet = d;
            await Task.Delay(300);
            e.Vm.MarkClean();
            foreach (var tier in d.Tiers.ToList())
                tier.RemoveCommand.Execute(null);
            if (maxVersions is not null)
            {
                e.Vm.AddRetentionTierCommand.Execute(null);
                d.Tiers[0].MaxVersions = maxVersions;
            }
            beforeSave?.Invoke();
            e.Vm.SaveCommand.Execute(null);
            bool closed = await WaitClosedAsync(e.Window);
            await EnsureClosedAsync(e.Window);
            return closed;
        }

        // --- a scan that fails says so, and the app carries on ---
        _counter.FailOn = nameof(ICatalogRepository.GetActivePreviousVersionsAsync);
        try
        {
            Check(await SaveDefaultTiers("retention: scan fails", "1"), "Save closes the editor");
            Check(await WaitFor(() => _vm.StatusText.StartsWith("Could not look for", StringComparison.Ordinal), 10000),
                $"a scan that fails says so on the status bar (\"{_vm.StatusText}\")");
            Check(OpenReview() is null, "and offers nothing");
        }
        finally
        {
            _counter.FailOn = null;
        }

        // --- nothing is offered while the set is busy ---
        Check(await SaveDefaultTiers("retention: busy", "2", beforeSave: () => row.IsRunning = true),
            "Save closes the editor");
        try
        {
            Check(await WaitFor(() => _vm.StatusText.Contains("right now", StringComparison.Ordinal), 10000),
                $"a set that is working is not scanned, and the status bar says why (\"{_vm.StatusText}\")");
            Check(!await WaitFor(() => OpenReview() is not null, 2000), "and nothing is offered");
        }
        finally
        {
            row.IsRunning = false;
        }

        // --- settings saved again between the review and Remove: nothing goes ---
        Check(await SaveDefaultTiers("retention: changed during review", null), "Save closes the editor");
        review = null;
        Check(await WaitFor(() => (review = OpenReview()) is not null, 20000), "emptying Default offers the versions");
        if (review is null) return;
        set = await DbSetAsync();
        set.JobOptions!.TierSets = [keepAll];
        set.JobOptions.RetentionTiers = keepAll.Tiers;
        await _observer.UpdateBackupSetAsync(set);
        review.DialogResult = true;
        Check(await WaitFor(() => !row.IsRunning && row.LastResultText.Contains("changed again", StringComparison.Ordinal), 20000),
            $"settings changed since the review remove nothing, and say why (\"{row.LastResultText}\")");
        Check(File.Exists(Prev(_smallFile, 2)) && File.Exists(Prev(_bigFile, 1)) && (await LiveDiscPaths()).Count == 2,
            "both earlier versions are still there, and recorded");
    }

    /// <summary>
    /// 26: while some of a set's live records belong to another folder than its
    /// backup folder (a subfolder setting changed, a destination retyped, an
    /// older Worker's runs), the app does nothing in the backup folder: the
    /// retention offer is not made, a backup is refused, and dropping a source
    /// removes nothing - each saying why. Working there would lose track of those
    /// records' files, or retire records whose files it never deletes. Runs on
    /// the set and backup that section 25 left.
    /// </summary>
    static async Task RecordsElsewhereRefusedAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 26. with files recorded in another folder, the app does not work in the backup folder ===");
        string dst = Path.Combine(_root, "dst-retention");

        // A main window that really backs up, and removes a dropped source's
        // copies without asking first, so that each refusal is all that stops it.
        // With the app's destination resolver, which the folder checks of this
        // section and the next judge and save volume identities by.
        _vm = new MainViewModel(
            _catalog,
            burner: null!,
            scanner: new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
            orchestrator: null!,
            restoreService: null!,
            catalogFreeRestoreService: null!,
            directoryBackupService: new DirectoryBackupService(
                _catalog,
                new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
                new VersionRetentionService(_catalog)),
            destinationResolver: new LithicBackup.Infrastructure.FileSystem.DestinationResolver(
                new LithicBackup.Infrastructure.FileSystem.Win32VolumeResolver()),
            settings: new UserSettings { ReconcileMode = ReconcileAfterEditMode.Always });
        if (!await WaitFor(() => _vm.BackupSets.Any(r => r.Id == _setId), 10000))
        {
            Check(false, "the main window loaded the test set");
            return;
        }
        var disc = await _observer.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = _setId, Label = Path.Combine(_root, "elsewhere"), SequenceNumber = 99,
            MediaType = MediaType.Directory, Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow,
        });
        await _observer.CreateFileRecordAsync(new FileRecord
        {
            DiscId = disc.Id, SourcePath = Path.Combine(_a, "elsewhere.txt"), DiscPath = Path.Combine("X", "elsewhere.txt"),
            SizeBytes = 1, Hash = "", SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        });
        int discsBefore = (await _observer.GetDiscsForBackupSetAsync(_setId)).Count;
        var filesBefore = Directory.GetFiles(dst, "*", SearchOption.AllDirectories)
            .Select(f => (f, File.GetLastWriteTimeUtc(f))).ToHashSet();
        bool BackupUntouched() => Directory.GetFiles(dst, "*", SearchOption.AllDirectories)
            .Select(f => (f, File.GetLastWriteTimeUtc(f))).ToHashSet().SetEquals(filesBefore);

        // --- a retention change offers nothing ---
        var ed = await OpenEditorAsync("elsewhere: retention");
        if (ed is null) return;
        var def = DefaultOf(ed)!;
        ed.Vm.SelectedTierSet = def;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        def.Tiers[0].MaxVersions = "1";
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");
        await EnsureClosedAsync(ed.Window);
        Check(await WaitFor(() => _vm.StatusText.Contains("recorded in another folder", StringComparison.Ordinal), 20000),
            $"a retention change offers no removal, and the status bar says why (\"{_vm.StatusText}\")");
        Check(!await WaitFor(() => OpenReview() is not null, 2000) && BackupUntouched(), "and nothing is offered or removed");

        // --- a backup is refused ---
        var row = _vm.BackupSets.First(r => r.Id == _setId);
        File.WriteAllText(_smallFile, "changed, so a backup would copy it");
        _dialogs.Expect("Backup Folder Changed", DialogWatcher.IDOK);
        _vm.SetBackupCommand.Execute(row);
        Check(await WaitFor(() => _dialogs.WasAnswered("Backup Folder Changed"), 20000),
            "backing the set up explains that its files are recorded in another folder");
        Check(await WaitFor(() => _vm.StatusText.Contains("was not backed up", StringComparison.Ordinal), 5000),
            $"and the status bar says it was not backed up (\"{_vm.StatusText}\")");
        await Task.Delay(1000);
        Check(BackupUntouched() && (await _observer.GetDiscsForBackupSetAsync(_setId)).Count == discsBefore,
            "nothing was written");
        _dialogs.ClearExpectations();

        // --- dropping a source removes nothing, and asks nothing first ---
        ed = await OpenEditorAsync("elsewhere: drop b");
        if (ed is null) return;
        _dialogs.Expect("Backup Folder Changed", DialogWatcher.IDOK);
        Check(await ToggleAsync(ed, _b, false), "unticking folder b registers as a user edit");
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");
        await EnsureClosedAsync(ed.Window);
        Check(await WaitFor(() => _dialogs.WasAnswered("Backup Folder Changed"), 20000) && OpenReview() is null,
            "dropping a source explains, without first reviewing what it would remove");
        Check(await WaitFor(() => _vm.StatusText.Contains("Left the removed-source files in place", StringComparison.Ordinal), 5000),
            $"and the status bar says the files were left (\"{_vm.StatusText}\")");
        Check(BackupUntouched(), "folder b's backed-up files are still there");
        _dialogs.ClearExpectations();

        // --- moving the backup folder in the editor asks first ---
        // (Every save above left the folder alone and asked nothing: the check for
        // unexpected message boxes at the end covers that.)
        ed = await OpenEditorAsync("elsewhere: move folder");
        if (ed is null) return;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        ed.Vm.SubdirectoryName = "Moved";
        ed.Vm.CreateSubdirectory = true;
        async Task<bool> Unmoved() => (await DbSetAsync()).JobOptions is { CreateSubdirectory: false };

        _dialogs.Expect("Change Backup Folder", DialogWatcher.IDNO);
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitFor(() => _dialogs.WasAnswered("Change Backup Folder"), 10000),
            "saving a new backup folder for a set with history asks first");
        await Task.Delay(500);
        Check(IsOpen(ed.Window) && await Unmoved(), "No keeps the editor open, and saves nothing");
        _dialogs.ClearExpectations();

        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDYES);
        _dialogs.Expect("Change Backup Folder", DialogWatcher.IDNO);
        ed.Window.Close();
        await WaitFor(() => _dialogs.WasAnswered("Change Backup Folder"), 10000);
        await Task.Delay(500);
        Check(_dialogs.WasAnswered("Unsaved Changes") && _dialogs.WasAnswered("Change Backup Folder"),
            "so does Yes at the close prompt");
        Check(IsOpen(ed.Window) && await Unmoved(), "and No there keeps the editor open too, and saves nothing");
        _dialogs.ClearExpectations();

        // A catalog that fails while the close prompt's Yes asks: the editor stays
        // open with the edit in it, and says so (it used to be waited for inside
        // the window's Closing handler, out of which the failure escaped).
        _counter.FailOn = nameof(ICatalogRepository.FindLiveRecordsOutsideAsync);
        try
        {
            _dialogs.Expect("Unsaved Changes", DialogWatcher.IDYES);
            ed.Window.Close();
            Check(await WaitFor(() => _vm.StatusText.StartsWith("Failed to save", StringComparison.Ordinal), 10000),
                $"a failure while asking says so (\"{_vm.StatusText}\")");
            Check(IsOpen(ed.Window) && ed.Vm.CreateSubdirectory && await Unmoved(),
                "and leaves the editor open with the edit in it, and nothing saved");
        }
        finally
        {
            _counter.FailOn = null;
            _dialogs.ClearExpectations();
        }

        _dialogs.Expect("Change Backup Folder", DialogWatcher.IDYES);
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Yes saves and closes the editor");
        await EnsureClosedAsync(ed.Window);
        var moved = (await DbSetAsync()).JobOptions!;
        Check(moved is { CreateSubdirectory: true, SubdirectoryName: "Moved" }, "and the new backup folder is saved");
        _dialogs.ClearExpectations();
    }

    /// <summary>
    /// 27: a save may not give a set another set's backup folder, though a set that
    /// already shares one can still be edited; and a destination changed in the
    /// editor gets a new volume identity, rather than being put back at the next
    /// backup by the old one. Runs on the set section 26 left.
    /// </summary>
    static async Task SharedBackupFolderAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 27. a set's backup folder may not be another set's ===");
        string theirs = Path.Combine(_root, "other-set-folder");
        Directory.CreateDirectory(theirs);
        var other = await _observer.CreateBackupSetAsync(new BackupSet
        {
            Name = "the other set", SourceRoots = [], CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory, JobOptions = new JobOptions { TargetDirectory = theirs },
        });
        Editor? ed;
        try
        {
            // --- the other set's folder is refused ---
            ed = await OpenEditorAsync("shared: onto the other set's folder");
            if (ed is null) return;
            await Task.Delay(300);
            ed.Vm.MarkClean();
            ed.Vm.CreateSubdirectory = false;
            ed.Vm.TargetDirectory = theirs + @"\";
            _dialogs.Expect("Backup Folder In Use", DialogWatcher.IDOK);
            ed.Vm.SaveCommand.Execute(null);
            Check(await WaitFor(() => _dialogs.WasAnswered("Backup Folder In Use"), 10000),
                "saving another set's backup folder is refused, and says why");
            await Task.Delay(500);
            Check(IsOpen(ed.Window) && (await DbSetAsync()).JobOptions is { CreateSubdirectory: true } stored
                  && !string.Equals(stored.TargetDirectory, theirs, StringComparison.OrdinalIgnoreCase),
                "the editor stays open, and nothing is saved");
            _dialogs.ClearExpectations();
            await EnsureClosedAsync(ed.Window);

            // --- two sets that already share a folder can still be edited ---
            var mine = (await DbSetAsync()).JobOptions!;
            other.JobOptions = new JobOptions
            {
                TargetDirectory = mine.TargetDirectory, CreateSubdirectory = mine.CreateSubdirectory,
                SubdirectoryName = mine.SubdirectoryName,
            };
            await _observer.UpdateBackupSetAsync(other);
            ed = await OpenEditorAsync("shared: already shared, renamed");
            if (ed is null) return;
            await Task.Delay(300);
            ed.Vm.MarkClean();
            ed.Vm.SetName = "editor-save-test (shared)";
            ed.Vm.SaveCommand.Execute(null);
            Check(await WaitClosedAsync(ed.Window) && (await DbSetAsync()).Name == "editor-save-test (shared)",
                "a save that leaves an already shared folder alone goes through - it is how they are separated");
            await EnsureClosedAsync(ed.Window);

            // --- and until they are separated, neither is backed up ---
            var row = _vm.BackupSets.First(r => r.Id == _setId);
            int discsBefore = (await _observer.GetDiscsForBackupSetAsync(_setId)).Count;
            _dialogs.Expect("Backup Folder Shared", DialogWatcher.IDOK);
            _vm.SetBackupCommand.Execute(row);
            Check(await WaitFor(() => _dialogs.WasAnswered("Backup Folder Shared"), 20000)
                  && await WaitFor(() => _vm.StatusText.Contains("\"the other set\" backs up to the same folder",
                      StringComparison.Ordinal), 5000),
                $"backing up a set whose folder another set shares is refused, and says why (\"{_vm.StatusText}\")");
            Check((await _observer.GetDiscsForBackupSetAsync(_setId)).Count == discsBefore, "and nothing is written");
            _dialogs.ClearExpectations();

            // --- nor seeded from it: the seed would take the other set's files ---
            Directory.CreateDirectory((await DbSetAsync()).JobOptions!.BackupRoot()!);
            ed = await OpenEditorAsync("shared: seed");
            if (ed is null) return;
            await Task.Delay(300);
            ed.Vm.MarkClean();
            int filesBefore = (await _observer.GetAllFilesForBackupSetAsync(_setId)).Count;
            _dialogs.Expect("Backup Folder Shared", DialogWatcher.IDOK);
            ed.Vm.SeedFromExistingCommand.Execute(null);
            Check(await WaitFor(() => _dialogs.WasAnswered("Backup Folder Shared"), 20000)
                  && await WaitFor(() => ed.Vm.SeedResult.StartsWith("Not seeded", StringComparison.Ordinal), 5000)
                  && (await _observer.GetAllFilesForBackupSetAsync(_setId)).Count == filesBefore,
                $"seeding from a folder another set shares is refused, and imports nothing (\"{ed.Vm.SeedResult}\")");
            _dialogs.ClearExpectations();
            await EnsureClosedAsync(ed.Window);
        }
        finally
        {
            // Left behind, it would share the test set's folder for every later section.
            await _observer.DeleteBackupSetAsync(other.Id);
        }

        // --- a changed destination gets a new volume identity ---
        var set = await DbSetAsync();
        var volumes = new LithicBackup.Infrastructure.FileSystem.Win32VolumeResolver();
        string oldDest = set.JobOptions!.TargetDirectory!;
        string? volumeId = volumes.GetVolumeId(oldDest);
        set.JobOptions.DestinationVolumeId = volumeId;
        set.JobOptions.DestinationSubpath = oldDest[Path.GetPathRoot(oldDest)!.Length..];
        await _observer.UpdateBackupSetAsync(set);
        string retyped = Path.Combine(_root, "retyped-destination");
        Directory.CreateDirectory(retyped);
        ed = await OpenEditorAsync("shared: retyped destination");
        if (ed is null) return;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        ed.Vm.CreateSubdirectory = false;
        ed.Vm.TargetDirectory = retyped;
        _dialogs.Expect("Change Backup Folder", DialogWatcher.IDYES);   // its files are recorded in the old one
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window, 15000), "the retyped destination is saved");
        Check((await DbSetAsync()).JobOptions!.DestinationVolumeId is { } savedVolume
              && savedVolume == volumes.GetVolumeId(retyped),
            "with the new volume identity, captured at the save from the drive it was judged by");
        await EnsureClosedAsync(ed.Window);
        _dialogs.ClearExpectations();
        var saved = (await DbSetAsync()).JobOptions!;
        new LithicBackup.Infrastructure.FileSystem.DestinationResolver(volumes).Resolve(saved);
        Check(volumeId is not null && string.Equals(saved.TargetDirectory, retyped, StringComparison.OrdinalIgnoreCase),
            $"and the next backup's resolve keeps it, rather than the old volume's path ({saved.TargetDirectory})");
    }
#endif

    static ReviewTreeDialog? OpenReview() => Application.Current.Windows.OfType<ReviewTreeDialog>().FirstOrDefault();
    static TierSetViewModel? DefaultOf(Editor e) => e.Vm.TierSets.FirstOrDefault(t => t.Name == "Default");

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

    /// <summary>
    /// Removing, adding or editing a retention tier is an edit like any other.
    /// Those edits live inside the tier sets (their tier rows), one level below
    /// what the dirty flag sees: missed, removing Default's last tier would leave
    /// Save disabled and the close prompt silent, and the edit lost on closing.
    /// </summary>
    static async Task TierEditsEnableSaveAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 16. tier edits enable Save, are saved, and are asked about on X ===");

        static VersionTierSet? DbDefault(BackupSet s) => s.JobOptions?.TierSets.FirstOrDefault(t => t.Name == "Default");

        // --- remove every tier from Default, then Save ---
        var ed = await OpenEditorAsync("tiers: remove");
        if (ed is null) return;
        var def = DefaultOf(ed);
        Check(def is { Tiers.Count: > 0 }, $"Default starts with tiers ({def?.Tiers.Count})");
        Check(!ed.Vm.SaveCommand.CanExecute(null), "Save starts disabled");
        ed.Vm.SelectedTierSet = ed.Vm.TierSets.First(t => t.Name == "None");
        ed.Vm.SelectedTierSet = def;
        await Task.Delay(300);
        Check(!ed.Vm.SaveCommand.CanExecute(null) && !ed.Vm.HasUnsavedChanges,
            "selecting tier sets to look at them is not an edit (it enabled Save)");
        foreach (var tier in def!.Tiers.ToList())
            tier.RemoveCommand.Execute(null);
        Check(def.Tiers.Count == 0, "every tier is removed from Default");
        Check(ed.Vm.SaveCommand.CanExecute(null), "removing them enables Save");

        int w0 = _counter.Writes;
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");
        var db = await DbSetAsync();
        Check(_counter.Writes - w0 == 1, $"with one write (writes: {_counter.Writes - w0})");
        Check(DbDefault(db) is { Tiers.Count: 0 }, "the saved Default has no tiers");
        Check(db.JobOptions?.RetentionTiers.Count == 0, "nor do the saved flat retention tiers");
        await EnsureClosedAsync(ed.Window);

        // --- reopen: still empty; add a tier and give it a limit ---
        ed = await OpenEditorAsync("tiers: add");
        if (ed is null) return;
        def = DefaultOf(ed);
        Check(def is { Tiers.Count: 0 }, "reopened, Default still has no tiers");
        ed.Vm.SelectedTierSet = def;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        ed.Vm.AddRetentionTierCommand.Execute(null);
        Check(ed.Vm.SaveCommand.CanExecute(null), "adding a tier enables Save");
        def!.Tiers[0].MaxVersions = "2";
        ed.Vm.SaveCommand.Execute(null);
        Check(await WaitClosedAsync(ed.Window), "Save closes the editor");
        db = await DbSetAsync();
        Check(DbDefault(db) is { Tiers: [{ MaxVersions: 2, MaxAge: null }] }, "the saved Default keeps 2 versions");
        await EnsureClosedAsync(ed.Window);

        // --- change only the number, close with X, answer No ---
        ed = await OpenEditorAsync("tiers: edit");
        if (ed is null) return;
        def = DefaultOf(ed);
        ed.Vm.SelectedTierSet = def;
        await Task.Delay(300);
        ed.Vm.MarkClean();
        def!.Tiers[0].MaxVersions = "5";
        Check(ed.Vm.SaveCommand.CanExecute(null), "changing a tier's versions enables Save");
        w0 = _counter.Writes;
        _dialogs.Expect("Unsaved Changes", DialogWatcher.IDNO);
        ed.Window.Close();
        bool closed = await WaitClosedAsync(ed.Window);
        Check(_dialogs.WasAnswered("Unsaved Changes"), "X asks \"Save before closing?\" about it");
        Check(closed && _counter.Writes == w0, "and No closes without writing");
        _dialogs.ClearExpectations();
        Check(DbDefault(await DbSetAsync()) is { Tiers: [{ MaxVersions: 2 }] }, "the saved Default still keeps 2");
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

#if !OLD_CODE
    /// <summary>
    /// A seed holds the set's row like a backup: progress on the row, Backup /
    /// Modify / Delete unavailable, and it carries on, visibly, after Save closes
    /// the editor; the row's Abort stops it, and no backup can start meanwhile,
    /// which would copy every file the seed had not reached yet.
    ///
    /// The seed is frozen deterministically by holding the set's cross-process
    /// write lock (the seed's first write - its disc record - waits for it), so
    /// nothing here depends on how fast the disk is.
    /// </summary>
    static async Task SeedHoldsTheRowAsync(string dbPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 15. a seed holds the set's row, survives Save, and Abort stops it ===");

        // A Lithic-style destination: the current copy of small.txt and one
        // earlier version of it.
        string rel = _smallFile[Path.GetPathRoot(_smallFile)!.Length..];
        string current = Path.Combine(_dst, _smallFile[..1], rel);
        string prev = Path.Combine(_dst, _smallFile[..1] + "_prev", rel + ".v1");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        Directory.CreateDirectory(Path.GetDirectoryName(prev)!);
        File.WriteAllText(current, "current copy");
        File.WriteAllText(prev, "an earlier version");

        // This scenario needs a real backup service (the others run without one).
        _vm = new MainViewModel(
            _catalog,
            burner: null!,
            scanner: new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
            orchestrator: null!,
            restoreService: null!,
            catalogFreeRestoreService: null!,
            directoryBackupService: new DirectoryBackupService(
                _catalog,
                new LithicBackup.Infrastructure.FileSystem.FileScanner(_catalog),
                new VersionRetentionService(_catalog)),
            settings: new UserSettings { ReconcileMode = ReconcileAfterEditMode.Never });
        if (!await WaitFor(() => _vm.BackupSets.Any(r => r.Id == _setId), 10000))
        {
            Check(false, "the second main window loaded the test set");
            return;
        }
        var row = _vm.BackupSets.First(r => r.Id == _setId);
        string lockPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "sets", $"set-{_setId}.db.writelock");

        // --- Save mid-seed ---
        var ed = await OpenEditorAsync("seed");
        if (ed is null) return;
        var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            ed.Vm.SeedFromExistingCommand.Execute(null);
            Check(await WaitFor(() => row.IsRunning && row.BusyActivity == "seeding", 10000),
                "the seed holds the set's row as soon as it starts");
            Check(!_vm.SetBackupCommand.CanExecute(row), "Backup is unavailable while it seeds");
            Check(!_vm.SetModifyCommand.CanExecute(row), "so is Modify");
            Check(!_vm.SetDeleteCommand.CanExecute(row), "and Delete");
            Check(row.RunningStatusText.Contains("Seeding", StringComparison.Ordinal),
                $"the row says it is seeding (\"{row.RunningStatusText}\")");

            await ToggleAsync(ed, _b, true);
            ed.Vm.SaveCommand.Execute(null);
            Check(await WaitClosedAsync(ed.Window), "Save closes the editor mid-seed");
            Check(row.IsRunning && row.BusyActivity == "seeding", "and the seed carries on, on the row");
            Check(_vm.StatusText.Contains("still seeding", StringComparison.Ordinal),
                $"the status bar says where it went (\"{_vm.StatusText}\")");
        }
        finally
        {
            held.Dispose();
        }

        Check(await WaitFor(() => !row.IsRunning, 20000), "once it can write, the seed finishes and releases the row");
        Check(row.LastResultText.StartsWith("Seeded:", StringComparison.Ordinal),
            $"its result is left on the row (\"{row.LastResultText}\")");
        var heads = await _observer.GetLatestVersionInfoAsync(_setId);
        Check(heads.TryGetValue(_smallFile, out var head) && head.MaxVersion == 2,
            "it recorded small.txt after its earlier version (v2)");
        Check(_vm.SetBackupCommand.CanExecute(row), "and Backup is available again");

        // --- Abort mid-seed ---
        await _observer.ClearBackupSetCatalogAsync(_setId);
        var ed2 = await OpenEditorAsync("seed abort");
        if (ed2 is null) return;
        held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            ed2.Vm.SeedFromExistingCommand.Execute(null);
            Check(await WaitFor(() => row.IsRunning && row.BusyActivity == "seeding", 10000),
                "a second seed holds the row");
            Check(_vm.AbortBackupCommand.CanExecute(row), "the row's Abort is available during a seed");
            _vm.AbortBackupCommand.Execute(row);
            Check(await WaitFor(() => !row.IsRunning, 10000), "Abort stops the seed and releases the row");
            Check(row.LastResultText.StartsWith("Seed stopped", StringComparison.Ordinal),
                $"and says so (\"{row.LastResultText}\")");
            Check(await WaitFor(() => !ed2.Vm.IsSeeding, 5000), "the open editor sees it stop too");
        }
        finally
        {
            held.Dispose();
        }
        await EnsureClosedAsync(ed2.Window);

        // --- the background service is backing the set up ---
        // Holding the row tells it to stop; the seed waits until it has, rather
        // than recording files beside the service's last batch.
        await _observer.ClearBackupSetCatalogAsync(_setId);
        var ed3 = await OpenEditorAsync("seed after the service");
        if (ed3 is null) return;
        var serviceRun = _catalog.HoldSetForBackgroundBackup(_setId);
        try
        {
            ed3.Vm.SeedFromExistingCommand.Execute(null);
            Check(await WaitFor(() => row.RunningStatusText.Contains("waiting for the background service", StringComparison.Ordinal), 10000),
                $"a seed waits while the service is still backing the set up (\"{row.RunningStatusText}\")");
            await Task.Delay(1000);
            Check((await _observer.GetLatestVersionInfoAsync(_setId)).Count == 0, "and records nothing meanwhile");
        }
        finally
        {
            serviceRun.Dispose();
        }
        Check(await WaitFor(() => !row.IsRunning, 20000) && row.LastResultText.StartsWith("Seeded:", StringComparison.Ordinal),
            $"once the service has stopped, the seed goes ahead and finishes (\"{row.LastResultText}\")");
        await EnsureClosedAsync(ed3.Window);
    }
#endif

#if !OLD_CODE
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    static Rect BoundsIn(FrameworkElement e, System.Windows.Media.Visual ancestor) =>
        e.TransformToAncestor(ancestor).TransformBounds(new Rect(e.RenderSize));

    /// <summary>
    /// 17-21: Cleanup's action row with a long path, the seed result line, the
    /// tray balloon, drive-row counts, and opening a .lithic export.
    /// </summary>
    static async Task LayoutAndSmallFixesAsync()
    {
        string longPath = @"D:\" + string.Join(@"\", Enumerable.Repeat("a-rather-long-folder-name-without-spaces", 8)) + @"\file.bin";

        // --- 17. Cleanup: the progress line wraps before the buttons ---
        Console.WriteLine();
        Console.WriteLine("=== 17. Cleanup's progress line wraps before its buttons ===");
        var set = await DbSetAsync();
        var cleanupVm = new OrphanedDirectoriesViewModel(_catalog, set) { PurgeStatusText = "Deleting files: " + longPath };
        var host = new Window
        {
            Width = 900, Height = 700, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Content = new OrphanedDirectoriesView { DataContext = cleanupVm },
        };
        host.Show();
        await Task.Delay(300);
        host.UpdateLayout();
        var content = (FrameworkElement)host.Content;
        var buttons = Descendants<System.Windows.Controls.Button>(content)
            .Where(b => b.Content is "Close" or "Clean Selected").ToList();
        var status = Descendants<System.Windows.Controls.TextBlock>(content)
            .FirstOrDefault(t => t.Text == cleanupVm.PurgeStatusText);
        Check(buttons.Count == 2 && status is not null, "found the action row's buttons and progress line");
        if (buttons.Count == 2 && status is not null)
        {
            var sb = BoundsIn(status, content);
            foreach (var b in buttons)
            {
                var bb = BoundsIn(b, content);
                Check(b.ActualWidth > 20 && bb.Right <= content.ActualWidth + 0.5,
                    $"'{b.Content}' is fully on screen ({bb.Left:F0}-{bb.Right:F0} of {content.ActualWidth:F0})");
                Check(sb.Right <= bb.Left + 0.5, $"and the progress line ends before it ({sb.Right:F0} <= {bb.Left:F0})");
            }
            Check(status.ActualHeight > status.FontSize * 2, "the long path wraps onto more lines");
        }
        host.Close();

        // --- 18. the seed result line wraps instead of being cut off ---
        Console.WriteLine();
        Console.WriteLine("=== 18. the seed result line wraps ===");
        var ed = await OpenEditorAsync("seed line");
        if (ed is not null)
        {
            var tabs = Descendants<System.Windows.Controls.TabControl>(ed.Window).FirstOrDefault();
            var general = tabs?.Items.OfType<System.Windows.Controls.TabItem>().FirstOrDefault(t => (t.Header as string) == "General");
            if (general is not null) tabs!.SelectedItem = general;
            ed.Vm.SeedResult = "Imported 1,223,361 files (210,521 earlier versions; 33 duplicate stored copies were left alone). "
                               + "Run a backup next: it copies only what changed, and resumes automated backups.";
            await Task.Delay(400);
            ed.Window.UpdateLayout();
            var line = Descendants<System.Windows.Controls.TextBlock>(ed.Window).FirstOrDefault(t => t.Text == ed.Vm.SeedResult);
            Check(line is not null, "found the seed result line on the General tab");
            if (line is not null)
            {
                var panel = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(line);
                Check(line.ActualWidth <= panel.ActualWidth + 0.5,
                    $"it fits its panel ({line.ActualWidth:F0} <= {panel.ActualWidth:F0})");
                Check(line.ActualHeight > line.FontSize * 2, "and wraps onto more lines rather than running off");
            }
            ed.Vm.SeedResult = "";
            await EnsureClosedAsync(ed.Window);
        }

        // --- 19. the tray balloon says whether automated backups run ---
        Console.WriteLine();
        Console.WriteLine("=== 19. the tray balloon follows the service's state ===");
        Check(App.TrayBalloonText(LithicBackup.Services.ServiceState.Running, true).Text.Contains("keeps running"),
            "running: automated backups carry on");
        Check(App.TrayBalloonText(LithicBackup.Services.ServiceState.Running, false).Text.Contains("No backup set runs automatically"),
            "running, but every set unscheduled or held: says none runs");
        Check(App.TrayBalloonText(LithicBackup.Services.ServiceState.Stopped, true).Text.Contains("is stopped, so no automated backups"),
            "stopped: says no automated backups will run");
        Check(App.TrayBalloonText(LithicBackup.Services.ServiceState.NotInstalled, true).Text.Contains("not installed"),
            "not installed: says so");
        Check(!App.TrayBalloonText(LithicBackup.Services.ServiceState.Stopped, true).Text.Contains("monitoring continues"),
            "and none claims monitoring continues");
        var heldSet = new BackupSet { JobOptions = new JobOptions
            { TargetDirectory = @"X:\b", Schedule = new BackupSchedule { Enabled = true }, HoldAutomatedBackups = true } };
        Check(!heldSet.RunsAutomatically(), "a held set does not run automatically");
        heldSet.JobOptions.HoldAutomatedBackups = false;
        Check(heldSet.RunsAutomatically(), "released, it does");

        // --- 20. a drive row takes its totals from its children ---
        Console.WriteLine();
        Console.WriteLine("=== 20. a drive row's size and count come from its children ===");
        string fakeDrive = Path.Combine(_root, "fake-drive");
        Directory.CreateDirectory(Path.Combine(fakeDrive, "a"));
        Directory.CreateDirectory(Path.Combine(fakeDrive, "b"));
        File.WriteAllBytes(Path.Combine(fakeDrive, "f.bin"), new byte[3]);
        var drive = new SourceSelectionNodeViewModel(fakeDrive, true, null);
        drive.SetDriveUsedSpace(123_456_789);
        Check(drive.FormattedFileCount == "" && !drive.NeedsUnfilteredTotals,
            $"collapsed: no count, and not queued for a walk (\"{drive.FormattedFileCount}\")");
        await drive.EnsureChildrenLoadedAsync();
        Check(drive.FormattedFileCount == "Working..." && drive.Size == 123_456_789,
            $"listed, folders unsized: Working..., used space kept (\"{drive.FormattedFileCount}\", {drive.Size})");
        var childA = drive.Children.First(c => c.Name == "a");
        var childB = drive.Children.First(c => c.Name == "b");
        childA.Size = 10;
        childA.FileCount = 2;
        Check(drive.Size == 123_456_789 && drive.FileCount < 0, "one folder sized: still the used space");
        childB.FileCount = 1;   // count first, then size: either order completes it
        childB.Size = 5;
        Check(drive.Size == 18 && drive.FileCount == 4 && drive.FormattedFileCount == "4" && !drive.SizeIsDriveUsage,
            $"all sized: their sums (size {drive.Size}, count {drive.FileCount})");
        var folder = new SourceSelectionNodeViewModel(@"Q:\x", true, null);
        Check(folder.FormattedFileCount == "Working..." && folder.NeedsUnfilteredTotals, "an uncounted folder: Working..., as before");

        // Under a filter, the filtered totals are the children's too.
        using (var sched = new SizeComputeScheduler())
        {
            sched.GlobalExcludeFilter = p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            string fd2 = Path.Combine(_root, "fake-drive-2");
            Directory.CreateDirectory(Path.Combine(fd2, "a"));
            File.WriteAllBytes(Path.Combine(fd2, "keep.bin"), new byte[3]);
            File.WriteAllBytes(Path.Combine(fd2, "skip.tmp"), new byte[7]);
            var drive2 = new SourceSelectionNodeViewModel(fd2, true, null, scheduler: sched);
            drive2.SetDriveUsedSpace(999);
            await drive2.EnsureChildrenLoadedAsync();
            var a2 = drive2.Children.First(c => c.Name == "a");
            a2.Size = 4;
            a2.FileCount = 1;
            Check(drive2.Size == 14 && drive2.FileCount == 3 && drive2.FilteredSize < 0,
                $"unfiltered sums first ({drive2.Size}, {drive2.FileCount}), filtered still to come");
            a2.FilteredSize = 4;
            a2.FilteredFileCount = 1;
            Check(drive2.FilteredSize == 7 && drive2.FilteredFileCount == 2,
                $"filtered: the excluded file left out ({drive2.FilteredSize}, {drive2.FilteredFileCount})");

            // Opening the tree walks no drive, for either total: a walk of every
            // drive would hold the scheduler's one worker for minutes.
            string fd3 = Path.Combine(_root, "fake-drive-3");
            Directory.CreateDirectory(Path.Combine(fd3, "a"));
            File.WriteAllBytes(Path.Combine(fd3, "a", "x.bin"), new byte[5]);
            var root3 = new SourceSelectionNodeViewModel("", true, null, scheduler: sched) { IsLoaded = true };
            root3.Children.Clear();
            var drive3 = new SourceSelectionNodeViewModel(fd3, true, root3);
            drive3.SetDriveUsedSpace(999);
            root3.Children.Add(drive3);
            await root3.ComputeUnknownSizesAsync();
            await Task.Delay(1500);
            Check(drive3.Size == 999 && drive3.FileCount < 0 && drive3.FilteredSize < 0,
                $"no drive walk on open (size {drive3.Size}, count {drive3.FileCount}, filtered {drive3.FilteredSize})");
        }

        // Calculate Size: a fully selected drive that is not expanded is walked;
        // one that is expanded is summed from its children, which are sized.
        var unexpanded = new SourceSelectionNodeViewModel(Path.Combine(_root, "fake-drive-3"), true, null);
        unexpanded.SetDriveUsedSpace(999);
        unexpanded.IsSelected = true;
        var expanded = new SourceSelectionNodeViewModel(Path.Combine(_root, "fake-drive-3"), true, null);
        expanded.SetDriveUsedSpace(999);
        await expanded.EnsureChildrenLoadedAsync();
        expanded.IsSelected = true;
        var collected = new List<SourceSelectionNodeViewModel>();
        SourceSelectionViewModel.CollectUncalculatedSelectedDirs([unexpanded, expanded], collected);
        Check(collected.Contains(unexpanded) && !collected.Contains(expanded)
              && collected.Any(n => n.Parent == expanded && n.Name == "a"),
            $"collected: {string.Join(", ", collected.Select(n => n.Path))}");
        unexpanded.WalkInsteadOfSummingChildren();
        Check(unexpanded.NeedsUnfilteredTotals && unexpanded.FormattedSize == "Working...",
            "the walked drive is queued, and says Working... meanwhile");
        Check(SourceSelectionNodeViewModel.IsVolumeRoot(new DirectoryInfo(Path.GetPathRoot(_root)!))
              && !SourceSelectionNodeViewModel.IsVolumeRoot(new DirectoryInfo(_root)),
            "a volume root is told from a folder (its walk leaves out the recycle bin and page file)");

        // A walk cancelled part-way stores nothing as a total, and does not
        // return one. The canceller fires once the first folder's own files are
        // in the cache, while the walk still has the rest ahead of it.
        Console.WriteLine();
        Console.WriteLine("=== 20b. a cancelled size walk is not a total ===");
        foreach (bool filtered in new[] { false, true })
        {
            string kind = filtered ? "filtered" : "unfiltered";
            const string sig = "editor-save-test";
            bool sawCancel = false, sawPartial = false;
            for (int attempt = 0; attempt < 5 && !sawCancel && !sawPartial; attempt++)
            {
                string tree = Path.Combine(_root, $"walk-{kind}-{attempt}");
                for (int i = 0; i < 300; i++)
                {
                    string d = Path.Combine(tree, $"d{i:D3}");
                    Directory.CreateDirectory(d);
                    File.WriteAllBytes(Path.Combine(d, "x.bin"), new byte[1]);
                }
                string first = new DirectoryInfo(tree).EnumerateDirectories().First().FullName;
                using var cache = new DirectorySizeCache();
                using var cts = new CancellationTokenSource();
                bool Landed(string path) => filtered
                    ? cache.TryGetFilteredRecursive(path, sig, DateTime.MinValue) is not null
                    : cache.TryGet(path) is not null;
                var canceller = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (!Landed(first) && sw.ElapsedMilliseconds < 10_000) { }
                    cts.Cancel();
                });
                try
                {
                    var (sz, n) = await Task.Run(() => filtered
                        ? SourceSelectionNodeViewModel.ComputeDirectorySizeFilteredCached(
                            new DirectoryInfo(tree), _ => false, cache, sig, cts.Token)
                        : SourceSelectionNodeViewModel.ComputeDirectorySizeCached(new DirectoryInfo(tree), cache, cts.Token));
                    if (n != 300) sawPartial = true;
                }
                catch (OperationCanceledException) { sawCancel = true; }
                await canceller;
                if (sawCancel)
                    Check(filtered ? !Landed(tree) : cache.TryGetRecursive(tree) is null,
                        $"{kind}: the cancelled walk cached no total for the folder");
            }
            Check(!sawPartial, $"{kind}: a cancelled walk never returns a partial sum as the total");
            Check(sawCancel, $"{kind}: and the cancellation reached the caller");
        }

        // --- 20c. size walks over links that loop ---
        Console.WriteLine();
        Console.WriteLine("=== 20c. size walks follow links, but not into themselves ===");
        {
            // a\b\f.txt and a\c.txt; a\b\loop -> a, a\ext -> outside\g.txt. (Loops above
            // the start, and through two links, are link_loop_test's.)
            string lt = Path.Combine(_root, "linkloop");
            string la = Path.Combine(lt, "a"), lb = Path.Combine(la, "b"), lo = Path.Combine(lt, "outside");
            Directory.CreateDirectory(lb);
            Directory.CreateDirectory(lo);
            File.WriteAllBytes(Path.Combine(lb, "f.txt"), new byte[2]);
            File.WriteAllBytes(Path.Combine(la, "c.txt"), new byte[3]);
            File.WriteAllBytes(Path.Combine(lo, "g.txt"), new byte[5]);
            void Junction(string link, string target)
            {
                var pj = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                pj.WaitForExit();
            }
            Junction(Path.Combine(lb, "loop"), la);
            Junction(Path.Combine(la, "ext"), lo);

            // Each walk gets a deadline, and a thread with room for a walk that
            // follows the loop until the path is too long, which then ends.
            async Task<T?> Within<T>(Func<T> walk) where T : struct
            {
                var done = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
                new Thread(() =>
                {
                    try { done.TrySetResult(walk()); }
                    catch { done.TrySetResult(null); }
                }, 256 * 1024 * 1024) { IsBackground = true }.Start();
                return await Task.WhenAny(done.Task, Task.Delay(20_000)) == done.Task ? done.Task.Result : null;
            }
            var dirA = new DirectoryInfo(la);
            using (var lcache = new DirectorySizeCache())
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var cached = await Within(() => SourceSelectionNodeViewModel.ComputeDirectorySizeCached(dirA, lcache, deadline.Token));
                Check(cached is (10, 3), $"the cached walk: 3 files, 10 bytes (got {cached})");
                var filteredCached = await Within(() => SourceSelectionNodeViewModel.ComputeDirectorySizeFilteredCached(
                    dirA, p => p.EndsWith("c.txt", StringComparison.OrdinalIgnoreCase), lcache, "linkloop-sig", deadline.Token));
                Check(filteredCached is (7, 2), $"the filtered cached walk: 2 files, 7 bytes (got {filteredCached})");
            }
            var filtered = await Within(() => SourceSelectionNodeViewModel.ComputeDirectorySizeFiltered(dirA, _ => false));
            Check(filtered is (10, 3), $"the filtered walk: 3 files, 10 bytes (got {filtered})");
            var added = await Within(() => MainViewModel.EnumerateFilesUnderRoots([la]).Take(1000).Select(f => f.Path).ToList().Count);
            Check(added == 3, $"the walk of an added folder: 3 files (got {added})");

            foreach (var j in new[] { Path.Combine(lb, "loop"), Path.Combine(la, "ext") })
                try { Directory.Delete(j); } catch { }
        }

        // --- 21. opening a .lithic export ---
        Console.WriteLine();
        Console.WriteLine("=== 21. opening a .lithic export offers to add it ===");
        string q = Path.Combine(_root, "open-requests");
        LithicBackup.Services.OpenRequestQueue.DirectoryOverride = q;
        try
        {
            // Exported with a destination of its own: imported beside the set it
            // came from, into that set's folder, it is refused (checked below).
            string export = Path.Combine(_root, "shared.lithic");
            var ownFolder = new BackupSet
            {
                Name = set.Name, SourceRoots = set.SourceRoots, SourceSelections = set.SourceSelections,
                DefaultMediaType = set.DefaultMediaType, CreatedUtc = set.CreatedUtc,
                JobOptions = JsonSerializer.Deserialize<JobOptions>(JsonSerializer.Serialize(set.JobOptions))!,
            };
            ownFolder.JobOptions.TargetDirectory = Path.Combine(_root, "imported-elsewhere");
            ownFolder.JobOptions.DestinationVolumeId = null;
            ownFolder.JobOptions.DestinationSubpath = null;
            await BackupSetTransfer.ExportAsync(_catalog, ownFolder, export, includeHistory: false);
            var fromArgs = LithicBackup.Services.OpenRequestQueue.FilesFromArguments(
                ["--test-mode", export, Path.Combine(_root, "missing.lithic"), Path.Combine(_root, "notes.txt")]);
            Check(fromArgs.Count == 1 && fromArgs[0] == export, "the command line yields just the existing export");
            string second = Path.Combine(_root, "second.lithic");
            File.Copy(export, second);
            LithicBackup.Services.OpenRequestQueue.Enqueue([export, second]);
            var taken = LithicBackup.Services.OpenRequestQueue.TakeAll();
            Check(taken.SequenceEqual([export, second]), "queued requests come back once, in order");
            Check(LithicBackup.Services.OpenRequestQueue.TakeAll().Count == 0, "and are gone after");
            LithicBackup.Services.OpenRequestQueue.Enqueue([export]);
            foreach (var f in Directory.GetFiles(q, "*.txt"))
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));
            Check(LithicBackup.Services.OpenRequestQueue.TakeAll().Count == 0, "a request left behind an hour ago is dropped");
            string unfinished = Path.Combine(q, "00000000000000000001-x.partial");
            string writing = Path.Combine(q, "00000000000000000002-y.partial");
            File.WriteAllText(unfinished, export);
            File.SetLastWriteTimeUtc(unfinished, DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(writing, export);
            LithicBackup.Services.OpenRequestQueue.TakeAll();
            Check(!File.Exists(unfinished) && File.Exists(writing),
                "a request a crashed launch never finished writing is removed; one being written now is not");
            File.Delete(writing);

            int before = await SetCountAsync();
            _dialogs.Expect("Open Backup Set", DialogWatcher.IDNO);
            await _vm.OfferOpenedFilesAsync([export]);
            Check(_dialogs.WasAnswered("Open Backup Set"), "it asks whether to add the set");
            Check(await SetCountAsync() == before, "No adds nothing");
            _dialogs.ClearExpectations();

            _dialogs.Expect("Open Backup Set", DialogWatcher.IDYES);
            _dialogs.Expect("Import Backup Set", DialogWatcher.IDOK);
            await _vm.OfferOpenedFilesAsync([export]);
            var sets = await _observer.GetAllBackupSetsAsync();
            Check(sets.Count == before + 1, "Yes adds it");
            var added = sets.Where(s => s.Id != _setId && s.Name == set.Name).OrderByDescending(s => s.Id).FirstOrDefault();
            Check(added is { JobOptions.HoldAutomatedBackups: true } || added is { JobOptions: null },
                "as an imported set, automated backups held");
            _dialogs.ClearExpectations();
            if (added is not null)
                await _observer.DeleteBackupSetAsync(added.Id);

            // The set exported as it is: imported, it would share its backup folder.
            string sameFolder = Path.Combine(_root, "same-folder.lithic");
            await BackupSetTransfer.ExportAsync(_catalog, set, sameFolder, includeHistory: false);
            _dialogs.Expect("Open Backup Set", DialogWatcher.IDYES);
            _dialogs.Expect("Import Backup Set", DialogWatcher.IDOK);
            await _vm.OfferOpenedFilesAsync([sameFolder]);
            Check(set.JobOptions?.BackupRoot() is not null && await SetCountAsync() == before
                  && _vm.StatusText.StartsWith("Did not import", StringComparison.Ordinal),
                $"a set whose backup folder another set already has is not imported (\"{_vm.StatusText}\")");
            _dialogs.ClearExpectations();

            string junk = Path.Combine(_root, "junk.lithic");
            File.WriteAllText(junk, "not an export");
            _dialogs.Expect("Open Backup Set", DialogWatcher.IDOK);
            await _vm.OfferOpenedFilesAsync([junk]);
            Check(_dialogs.WasAnswered("Open Backup Set") && await SetCountAsync() == before,
                "a file that is not an export says so and adds nothing");
            _dialogs.ClearExpectations();

            string otherJson = Path.Combine(_root, "settings.lithic");
            File.WriteAllText(otherJson, "{ \"Theme\": \"dark\", \"Recent\": [] }");
            _dialogs.Expect("Open Backup Set", DialogWatcher.IDYES);   // were it offered, Yes would add it
            await _vm.OfferOpenedFilesAsync([otherJson]);
            Check(_dialogs.WasAnswered("Open Backup Set") && await SetCountAsync() == before,
                "nor does another program's JSON");
            _dialogs.ClearExpectations();

            // An offer that arrives while a dialog is open belongs to that dialog: a
            // box owned by the main window it disabled would enable it on closing.
            var dlg = new Window
            {
                Width = 120, Height = 80, ShowInTaskbar = false, Content = new System.Windows.Controls.TextBlock(),
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = -3000,
            };
            Window? ownerWhileModal = null;
            dlg.Loaded += (_, _) => dlg.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                ownerWhileModal = MainViewModel.PromptOwner();
                dlg.Close();
            });
            dlg.ShowDialog();
            Check(ownerWhileModal == dlg, "a prompt that arrives under an open dialog is owned by the dialog");
            Check(MainViewModel.PromptOwner() == Application.Current.MainWindow, "and by the main window otherwise");
        }
        finally
        {
            _dialogs.ClearExpectations();
            LithicBackup.Services.OpenRequestQueue.DirectoryOverride = null;
        }
    }
#endif

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
        Check(MainViewModel.ParseExclusionPatterns(ed.Vm.ExcludedExtensions)
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

    /// <summary>
    /// 8: File > Exit closes an open editor first, while it can still refuse. Yes
    /// saves as the Save button does and the exit carries on once the editor has
    /// closed; Cancel keeps the app running. (An exit used to save synchronously
    /// inside the editor's Closed handler, and to drop an edit it could not save.)
    /// </summary>
    static async Task ExitAnswerYesSavesBeforeReturningAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 8. File > Exit: the editor asks first; Yes saves and the exit carries on ===");
        int ended = 0;
        _app.EndApplicationOverride = () => ended++;
        try
        {
            var ed = await OpenEditorAsync("exit");
            if (ed is null) return;
            int w0 = _counter.Writes;
            await ToggleAsync(ed, _a, false);

            _dialogs.Expect("Unsaved Changes", DialogWatcher.IDYES);
            _app.ExitApplication();
            Check(_dialogs.WasAnswered("Unsaved Changes"), "the exit asked whether to save");
            Check(await WaitFor(() => !IsOpen(ed.Window), 15000) && await WaitFor(() => ended == 1, 5000),
                $"Yes saved, closed the editor, and the exit carried on (exits: {ended})");
            var db = await DbSetAsync();
            Check(!IsCovered(db.SourceSelections, _a) && _counter.Writes - w0 == 1,
                $"with the unticked folder saved, in exactly one write (writes: {_counter.Writes - w0})");
            _dialogs.ClearExpectations();
            await EnsureClosedAsync(ed.Window);

            ed = await OpenEditorAsync("exit, cancelled");
            if (ed is null) return;
            await ToggleAsync(ed, _a, true);
            _dialogs.Expect("Unsaved Changes", DialogWatcher.IDCANCEL);
            _app.ExitApplication();
            await Task.Delay(500);
            Check(_dialogs.WasAnswered("Unsaved Changes") && IsOpen(ed.Window) && ended == 1,
                "Cancel at the prompt keeps the editor open, and the app running");
            _dialogs.ClearExpectations();
            await EnsureClosedAsync(ed.Window);
        }
        finally
        {
            _app.EndApplicationOverride = null;
        }
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
        Check(MainViewModel.ParseExclusionPatterns(
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
            // A folder of its own: in the test set's, the two would share one,
            // which the app refuses to back up, seed or clean.
            JobOptions = new JobOptions { TargetDirectory = Path.Combine(_root, "dst-lf-unit") },
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

    /// <summary>A method that throws, once named here, as a failing disk would.</summary>
    public volatile string? FailOn;

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
        if (FailOn == targetMethod.Name)
            throw new IOException($"simulated failure in {FailOn}");
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
