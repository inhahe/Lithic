// Regression test: earlier versions of files whose tier set keeps none.
//
// Removing every tier from a set's Default tier set means "back up the files no
// other tier set matches, with no history". The engine honoured that for new
// writes, but the versions those files already had were skipped by both the
// retention pass and Cleanup's Excess Versions category ("0 tiers = no rules"),
// so they stayed on the destination for good: 88 GB of them on one real set.
//
// Cleanup now lists every earlier version of such a file, newest included.
// Its purge has no .fileref promotion (the engine's retention pass does), so a
// plain copy that a surviving .fileref still resolves to by hash is kept and
// not listed; that guard covers tiered excess versions too.
//
// Drives the REAL Cleanup view model's catalog scan over versions made by REAL
// backups, against a throwaway catalog and destination:
//   1. with Default keeping 1, nothing is excess (the newest version is kept);
//   2. with Default empty, big.vmdk's earlier version is listed;
//   3. a versioned .txt (its own tier set, keep all) is not;
//   4. the earlier copy a .fileref depends on is not, and still exists;
//   5. a file excluded through a no-tier "None" set is the exclusion
//      category's, not listed as excess;
//   6. GetNeededContentCopiesAsync: deleting the reference with its anchor
//      needs nothing kept.

using System.IO;
using System.Windows.Threading;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using LithicBackup.ViewModels;

namespace ExcessVersionsTest;

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
        string root = Path.Combine(Path.GetTempPath(), "lithic_excess_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(root); }
            catch (Exception ex) { Check(false, "unhandled: " + ex); }
            finally { dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();

        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(root, recursive: true); } catch { }
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static async Task RunAsync(string root)
    {
        string source = Path.Combine(root, "src");
        string target = Path.Combine(root, "dst");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);

        var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));

        List<SourceSelection> Selection() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];

        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        var none = new VersionTierSet { Name = "None", Tiers = [] };
        var text = new VersionTierSet
        {
            Name = "Custom 1",
            Tiers = [new() { MaxAge = null, MaxVersions = null }],   // keep every version
            FilePatterns = ["*.txt"],
        };

        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "excess-versions-test",
            SourceRoots = [source],
            SourceSelections = Selection(),
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions
            {
                TargetDirectory = target,
                EnableFileDeduplication = true,
                TierSets = [keepOne, none, text],
                RetentionTiers = keepOne.Tiers,
            },
        });

        async Task Backup()
        {
            var opts = (await repo.GetBackupSetAsync(set.Id))!.JobOptions!;
            var job = new BackupJob
            {
                BackupSetId = set.Id,
                Sources = Selection(),
                EnableFileDeduplication = true,
                TargetDirectory = target,
                ZipMode = ZipMode.None,
                TierSets = opts.TierSets,
                RetentionTiers = opts.RetentionTiers,
                ExcludedExtensions = opts.ExcludedExtensions,
            };
            var tiers = opts.RetentionTiers.Count > 0 ? opts.RetentionTiers : VersionRetentionService.DefaultTiers;
            var r = await svc.ExecuteAsync(job, target, tiers, null, CancellationToken.None);
            if (!r.Success)
                throw new Exception("backup failed: " + string.Join("; ", r.FailedFiles.Select(f => f.Error)));
        }

        async Task SetTierSets(params VersionTierSet[] tierSets)
        {
            var s = (await repo.GetBackupSetAsync(set.Id))!;
            s.JobOptions!.TierSets = [.. tierSets];
            s.JobOptions.RetentionTiers = tierSets.First(t => t.Name == "Default").Tiers;
            await repo.UpdateBackupSetAsync(s);
        }

        string vmdk = Path.Combine(source, "vm", "big.vmdk");
        string note = Path.Combine(source, "note.txt");
        string dupA = Path.Combine(source, "dupA.bin");
        string dupB = Path.Combine(source, "dupB.bin");
        string log = Path.Combine(source, "old.log");
        Directory.CreateDirectory(Path.GetDirectoryName(vmdk)!);

        Console.WriteLine("=== Build up versions with real backups ===");
        await File.WriteAllTextAsync(vmdk, "disk image one");
        await File.WriteAllTextAsync(note, "note one");
        await File.WriteAllTextAsync(dupA, "identical content");
        await File.WriteAllTextAsync(dupB, "identical content");
        await File.WriteAllTextAsync(log, "log one");
        await Backup();

        await File.WriteAllTextAsync(vmdk, "disk image two, longer");
        await File.WriteAllTextAsync(note, "note two, longer");
        await File.WriteAllTextAsync(log, "log two, longer");
        await Backup();
        await File.WriteAllTextAsync(vmdk, "disk image three, longer still");
        await File.WriteAllTextAsync(note, "note three, longer still");
        await Backup();

        // Whichever duplicate was stored plain, change it, so its old copy - the
        // only plain copy of that content - moves to _prev while the other file's
        // .fileref still resolves to it.
        var heads = await repo.GetLatestVersionInfoAsync(set.Id);
        bool aIsRef = heads[dupA].IsFileRef, bIsRef = heads[dupB].IsFileRef;
        Check(aIsRef != bIsRef, "one duplicate is stored plain and the other as a .fileref");
        string plainDup = aIsRef ? dupB : dupA, refDup = aIsRef ? dupA : dupB;
        string sharedHash = heads[plainDup].Hash;
        await File.WriteAllTextAsync(plainDup, "different content now");
        await Backup();

        var all = await repo.GetAllFilesForBackupSetAsync(set.Id);
        var live = all.Where(f => !f.IsDeleted).ToList();
        static bool IsPrev(FileRecord f) => f.DiscPath.Replace('/', '\\').Split('\\')[0].EndsWith("_prev", StringComparison.OrdinalIgnoreCase);
        var vmdkPrev = live.Where(f => f.SourcePath == vmdk && IsPrev(f)).ToList();
        var notePrev = live.Where(f => f.SourcePath == note && IsPrev(f)).ToList();
        var anchor = live.SingleOrDefault(f => f.SourcePath == plainDup && IsPrev(f));
        var reference = live.Single(f => f.SourcePath == refDup && !IsPrev(f));
        Check(vmdkPrev.Count == 1, $"big.vmdk has one earlier version, Default keeping 1 (got {vmdkPrev.Count})");
        Check(notePrev.Count == 2, $"note.txt has both earlier versions, its tier set keeping all (got {notePrev.Count})");
        Check(anchor is { IsFileRef: false } && anchor.Hash == sharedHash,
            "the changed duplicate's old copy is in _prev, plain, holding the shared content");
        Check(reference.IsFileRef && reference.Hash == sharedHash, "the other duplicate is still a .fileref to it");
        Check(!live.Any(f => f.Hash == sharedHash && !f.IsFileRef && !IsPrev(f)),
            "and that earlier copy is the only plain copy of the content left");

        Console.WriteLine();
        Console.WriteLine("=== 1. Default keeps 1: nothing is excess ===");
        var excess1 = await ScanExcessAsync(repo, set.Id);
        Check(excess1.Count == 0, $"no excess versions (got {excess1.Count})");

        Console.WriteLine();
        Console.WriteLine("=== 2-5. Default emptied; old.log moved to an exclusion set ===");
        var emptyDefault = new VersionTierSet { Name = "Default", Tiers = [] };
        var noneWithLog = new VersionTierSet { Name = "None", Tiers = [], FilePatterns = ["*.log"] };
        await SetTierSets(emptyDefault, noneWithLog, text);
        var excess = await ScanExcessAsync(repo, set.Id);
        var excessIds = excess.Select(r => r.Id).ToHashSet();
        Check(vmdkPrev.All(v => excessIds.Contains(v.Id)),
            "big.vmdk's earlier version - its newest - is listed as excess");
        Check(!notePrev.Any(v => excessIds.Contains(v.Id)), "note.txt's versions (keep all) are not");
        Check(anchor is not null && !excessIds.Contains(anchor.Id),
            "the earlier copy the .fileref depends on is not listed");
        Check(!excess.Any(r => r.SourcePath == log), "old.log, now excluded from backup, is not listed as excess");
        Check(excess.Count == vmdkPrev.Count, $"nothing else is listed (got {excess.Count})");
        Check(anchor is not null && File.Exists(Path.Combine(target, anchor.DiscPath)), "and the anchor copy is still on disk");

        Console.WriteLine();
        Console.WriteLine("=== 6. The guard on its own ===");
        if (anchor is not null)
        {
            var needed = await repo.GetNeededContentCopiesAsync(set.Id, [anchor.Id]);
            Check(needed.SetEquals([anchor.Id]), "deleting the anchor alone: it is needed");
            var both = await repo.GetNeededContentCopiesAsync(set.Id, [anchor.Id, reference.Id]);
            Check(both.Count == 0, "deleting it together with its only reference: nothing is needed");
        }
        var nothing = await repo.GetNeededContentCopiesAsync(set.Id, vmdkPrev.Select(v => v.Id).ToList());
        Check(nothing.Count == 0, "a copy nothing refers to is not needed");
        var empty = await repo.GetNeededContentCopiesAsync(set.Id, []);
        Check(empty.Count == 0, "no candidates: nothing needed");

        Console.WriteLine();
        Console.WriteLine("=== 7. A backup with Default emptied and an exclusion set present ===");
        // The exclusion filter used to treat ANY no-tier set as an exclusion,
        // Default included, so this backup would have skipped big.vmdk entirely.
        var filter = DirectoryBackupService.BuildExclusionFilter([], [emptyDefault, noneWithLog, text]);
        Check(filter is not null && !filter(vmdk), "the exclusion filter does not exclude a file that falls to an empty Default");
        Check(filter is not null && filter(log), "it still excludes old.log, matched by the None set");
        int prevBefore = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Count(p => p.Contains("_prev" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(vmdk, "disk image four, after Default was emptied");
        await File.WriteAllTextAsync(log, "log three, excluded now");
        await Backup();
        var after = await repo.GetLatestVersionInfoAsync(set.Id);
        var current = await repo.GetActiveLatestRecordByPathAsync(set.Id, vmdk);
        Check(current is not null && File.ReadAllText(Path.Combine(target, current.DiscPath)) == "disk image four, after Default was emptied",
            "big.vmdk was backed up: its current copy holds the new content");
        int prevAfter = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Count(p => p.Contains("_prev" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Check(prevAfter == prevBefore, "and overwritten in place: no new earlier version was kept");
        Check(after[log].SizeBytes != new FileInfo(log).Length,
            "old.log, excluded, was not backed up again");

        Console.WriteLine();
        Console.WriteLine("=== 8. Overwritten in place while a .fileref needs the old content ===");
        // No history: the new copy replaces the old one. Its record used to stay
        // live, claiming the old content at a path now holding the new bytes, so
        // the .fileref restored the wrong content and a later identical file
        // became a reference to nothing.
        string x1 = Path.Combine(source, "x1.bin"), x2 = Path.Combine(source, "x2.bin");
        await File.WriteAllTextAsync(x1, "shared content X");
        await File.WriteAllTextAsync(x2, "shared content X");
        await Backup();
        var xh = await repo.GetLatestVersionInfoAsync(set.Id);
        string xPlain = xh[x1].IsFileRef ? x2 : x1, xRef = xh[x1].IsFileRef ? x1 : x2;
        string xHash = xh[xPlain].Hash;
        Check(xh[xRef].IsFileRef && !xh[xPlain].IsFileRef, "one is plain, the other a .fileref to it");
        await File.WriteAllTextAsync(xPlain, "the plain one changed");
        await Backup();
        string? xAnchor = await repo.GetActivePlainContentPathByHashAsync(set.Id, xHash);
        Check(xAnchor is not null && File.ReadAllText(Path.Combine(target, xAnchor)) == "shared content X",
            "the .fileref's content still resolves to a copy that holds it");
        var oldRows = (await repo.GetFileRecordsByPathAsync(set.Id, xPlain)).Where(r => !r.IsDeleted).ToList();
        Check(oldRows.Count == 1 && oldRows[0].Hash != xHash,
            $"the changed file has one live record, for its new content (live: {oldRows.Count})");
        string x3 = Path.Combine(source, "x3.bin");
        await File.WriteAllTextAsync(x3, "shared content X");
        await Backup();
        string? x3Anchor = await repo.GetActivePlainContentPathByHashAsync(set.Id, xHash);
        Check(x3Anchor is not null && File.ReadAllText(Path.Combine(target, x3Anchor)) == "shared content X",
            "a new file with the old content is stored with bytes that exist");

        await PurgeAcrossCategoriesAsync(root);

        Console.WriteLine();
        Console.WriteLine("=== 10. Directory pruning respects first-match order ===");
        var docs = new VersionTierSet { Name = "Docs", Tiers = [new() { MaxVersions = 3 }], FilePatterns = ["*.docx"] };
        var junk = new VersionTierSet { Name = "Junk", Tiers = [], FilePatterns = [@"*\tmp\*"] };
        string tmpDir = Path.Combine(source, "x", "tmp");
        var docsFirst = DirectoryBackupService.BuildDirectoryPruneFilter([], [keepOne, none, docs, junk]);
        Check(docsFirst is not null && !docsFirst(tmpDir),
            "a tiered set ahead of the excluding one (it can claim x\\tmp\\a.docx): x\\tmp is not skipped");
        var junkFirst = DirectoryBackupService.BuildDirectoryPruneFilter([], [keepOne, none, junk, docs]);
        Check(junkFirst is not null && junkFirst(tmpDir), "the excluding set first: x\\tmp is skipped, as before");
        var fileFilter = DirectoryBackupService.BuildExclusionFilter([], [keepOne, none, docs, junk]);
        Check(fileFilter is not null && !fileFilter(Path.Combine(tmpDir, "a.docx")) && fileFilter(Path.Combine(tmpDir, "a.bin")),
            "and per file, x\\tmp\\a.docx is backed up while x\\tmp\\a.bin is excluded");

        await RenameOverExistingAsync(root);
        await CleanupWhileInUseAsync(root);
        await ReconcileAfterChangesAsync(root);
        await TransactionOwnerAsync(root);
        await UnhashedAnchorsAsync(root);
    }

    /// <summary>
    /// §15. A seed with Skip hashing (the editor's default) recorded plain copies
    /// with no hash, and the guards that keep a .fileref's last plain copy -
    /// retention's and Cleanup's - match by hash, so they could delete it. The seed
    /// now hashes every plain copy of a live reference's size, and Cleanup and
    /// retention do the same for any still without a hash before deleting.
    /// </summary>
    static async Task UnhashedAnchorsAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 15. Copies a .fileref needs get a hash, however they were seeded ===");
        string source = Path.Combine(root, "src15"), target = Path.Combine(root, "dst15");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var keepAll = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = null }] };
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        BackupSet NewSet(string name) => new()
        {
            Name = name, SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true, TierSets = [keepAll], RetentionTiers = keepAll.Tiers },
        };
        async Task Backup(ICatalogRepository repo, int setId)
        {
            var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
            var opts = (await repo.GetBackupSetAsync(setId))!.JobOptions!;
            var job = new BackupJob
            {
                BackupSetId = setId, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
                ZipMode = ZipMode.None, TierSets = opts.TierSets, RetentionTiers = opts.RetentionTiers,
            };
            var tiers = opts.RetentionTiers.Count > 0 ? opts.RetentionTiers : VersionRetentionService.DefaultTiers;
            var r = await svc.ExecuteAsync(job, target, tiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }

        // Set A makes the layout: dupA and dupB share content, one is stored plain
        // and the other as a .fileref to it; changing the plain one moves its old
        // copy - the only plain copy of the shared content - into the history.
        Directory.CreateDirectory(Path.Combine(root, "cat15a"));
        string dupA = Path.Combine(source, "dupA.bin"), dupB = Path.Combine(source, "dupB.bin");
        string lonely = Path.Combine(source, "lonely.bin");
        string shared = "shared content for section fifteen";
        string sharedHash, plainDup;
        using (var repoA = new SqliteCatalogRepository(Path.Combine(root, "cat15a", "catalog.db")))
        {
            var setA = await repoA.CreateBackupSetAsync(NewSet("anchors-a"));
            await File.WriteAllTextAsync(dupA, shared);
            await File.WriteAllTextAsync(dupB, shared);
            await File.WriteAllTextAsync(lonely, "a lonely file, first");
            await Backup(repoA, setA.Id);
            var heads = await repoA.GetLatestVersionInfoAsync(setA.Id);
            plainDup = heads[dupA].IsFileRef ? dupB : dupA;
            sharedHash = heads[plainDup].Hash;
            await File.WriteAllTextAsync(plainDup, "the plain one changed, and is longer now");
            await File.WriteAllTextAsync(lonely, "a lonely file, second and longer");
            await Backup(repoA, setA.Id);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Set B adopts that destination by seeding it, without hashing.
        string catB = Path.Combine(root, "cat15b");
        Directory.CreateDirectory(catB);
        using var repo = new SqliteCatalogRepository(Path.Combine(catB, "catalog.db"));
        var set = await repo.CreateBackupSetAsync(NewSet("anchors-b"));
        var seeder = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        await seeder.SeedFromExistingDirectoryAsync(set.Id, target, null, CancellationToken.None, skipHashing: true);

        static bool IsPrev(FileRecord f) => f.DiscPath.Replace('/', '\\').Split('\\')[0].EndsWith("_prev", StringComparison.OrdinalIgnoreCase);
        var all = (await repo.GetAllFilesForBackupSetAsync(set.Id)).Where(f => !f.IsDeleted).ToList();
        var anchor = all.SingleOrDefault(f => IsPrev(f) && !f.IsFileRef && f.SizeBytes == shared.Length);
        var lonelyPrev = all.SingleOrDefault(f => f.SourcePath == lonely && IsPrev(f));
        Check(all.Any(f => f.IsFileRef && f.Hash == sharedHash), "the seed records the .fileref to the shared content");
        Check(anchor is not null && anchor.Hash == sharedHash,
            $"and hashes the plain copy it resolves to, though hashing was skipped (\"{anchor?.Hash}\")");
        Check(lonelyPrev is not null && lonelyPrev.Hash == "",
            "while a plain copy no reference could need keeps no hash (it is not read)");
        if (anchor is null) return;

        string setDb = Directory.EnumerateFiles(Path.Combine(catB, "sets"), $"set-{set.Id}.db").Single();
        void ForgetAnchorHash()
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={setDb};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Files SET Hash = '' WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", anchor.Id);
            cmd.ExecuteNonQuery();
        }

        // Default keeps nothing now, so every earlier version is excess.
        var s = (await repo.GetBackupSetAsync(set.Id))!;
        var emptyDefault = new VersionTierSet { Name = "Default", Tiers = [] };
        s.JobOptions!.TierSets = [emptyDefault];
        s.JobOptions.RetentionTiers = [];
        await repo.UpdateBackupSetAsync(s);

        // Cleanup, with the anchor's hash gone again (a set seeded before this).
        ForgetAnchorHash();
        var vm = new OrphanedDirectoriesViewModel(repo, s);
        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading && vm.Categories.Count > 0, 60000);
        foreach (var c in vm.Categories)
            foreach (var n in c.RootNodes)
                n.IsChecked = c.Reason == OrphanedReason.ExcessVersion;
        bool listed = vm.Items.Any(i => i.Reason == OrphanedReason.ExcessVersion
                                        && i.ExcessVersionRecords!.Any(r => r.Id == anchor.Id));
        vm.PurgeSelectedCommand.Execute(null);
        await WaitFor(() => vm.IsPurging, 3000);
        await WaitFor(() => !vm.IsPurging, 60000);
        string anchorPath = Path.Combine(target, anchor.DiscPath);
        Check(listed && File.Exists(anchorPath) && File.ReadAllText(anchorPath) == shared,
            $"Cleanup, listing the hash-less copy as excess, still keeps it (\"{vm.SummaryText}\")");

        // Retention, with the hash gone once more: keep one earlier version, and
        // change the file whose old copy is the anchor again, so the anchor is now
        // the excess one and retention deletes it.
        ForgetAnchorHash();
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        s = (await repo.GetBackupSetAsync(set.Id))!;
        s.JobOptions!.TierSets = [keepOne];
        s.JobOptions.RetentionTiers = keepOne.Tiers;
        await repo.UpdateBackupSetAsync(s);
        await File.WriteAllTextAsync(plainDup, "the plain one changed a second time, longer again");
        await Backup(repo, set.Id);
        var anchorNow = (await repo.GetAllFilesForBackupSetAsync(set.Id)).Single(f => f.Id == anchor.Id);
        Check(anchorNow.IsDeleted, "retention does delete the hash-less copy, now an excess version");
        string? home = await repo.GetActivePlainContentPathByHashAsync(set.Id, sharedHash);
        Check(home is not null && File.Exists(Path.Combine(target, home)) && File.ReadAllText(Path.Combine(target, home)) == shared,
            $"and, having made the .fileref a plain copy first, leaves its content a home ({home ?? "none"})");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §14. A set's open transaction lets only the flow that began it skip the
    /// set's gate. A process-wide flag let every flow skip it, so a read from
    /// another flow - the editor's plan check while a seed committed, Backup
    /// Coverage or a Cleanup scan while a backup ran - ran on the shared
    /// connection, inside that transaction, from another thread.
    /// </summary>
    static async Task TransactionOwnerAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 14. Only the flow that owns a transaction skips the set's gate ===");
        Directory.CreateDirectory(Path.Combine(root, "cat14"));
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat14", "catalog.db"));
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "tx-owner", SourceRoots = [@"C:\nowhere"], CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory, JobOptions = new JobOptions { TargetDirectory = Path.Combine(root, "dst14") },
        });

        // Another flow: started before the transaction, so it carries no part of it.
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            await go.Task;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await repo.GetLatestVersionInfoAsync(set.Id);
            return sw.Elapsed;
        });

        var tx = await repo.BeginTransactionAsync(set.Id);
        go.SetResult();
        await Task.Delay(1500);
        Check(!reader.IsCompleted, "a read from another flow waits while the transaction is open");
        var own = repo.GetLatestVersionInfoAsync(set.Id);
        Check(await Task.WhenAny(own, Task.Delay(5000)) == own, "a read by the flow that owns it does not");
        tx.Dispose();
        var waited = await reader;
        Check(waited >= TimeSpan.FromSeconds(1.2), $"and the other flow's read runs once it ends (waited {waited.TotalSeconds:F1} s)");

        // A helper that re-takes the transaction (as a backup does after each
        // commit) gives it to the same flow, which goes on using it.
        async Task<ICatalogTransaction> Retake() => await repo.BeginTransactionAsync(set.Id);
        var first = await repo.BeginTransactionAsync(set.Id);
        first.Complete();
        first.Dispose();
        var second = await Retake();
        var afterRetake = repo.GetLatestVersionInfoAsync(set.Id);
        Check(await Task.WhenAny(afterRetake, Task.Delay(5000)) == afterRetake,
            "a transaction re-taken in a helper still belongs to the flow that called it");
        second.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §13. Reconcile's Apply acts on records read at Analyze, which can be long
    /// before. It wrote them back whole, undoing whatever had happened since: a
    /// row Cleanup retired came back to life, and a row a backup moved into the
    /// history was pointed back at the current copy. Now it changes only rows
    /// still as Analyze read them.
    /// </summary>
    static async Task ReconcileAfterChangesAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 13. Reconcile Apply changes only rows still as Analyze read them ===");
        string source = Path.Combine(root, "src13"), target = Path.Combine(root, "dst13");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "cat13"));
        var repo = new SqliteCatalogRepository(Path.Combine(root, "cat13", "catalog.db"));
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "reconcile-stale", SourceRoots = [source], CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory, JobOptions = new JobOptions { TargetDirectory = target },
        });
        var disc = await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = set.Id, Label = target, SequenceNumber = 1, MediaType = MediaType.Directory,
            FilesystemType = FilesystemType.UDF, Status = BurnSessionStatus.Completed,
        });
        string Rel(string name) { string p = Path.Combine(source, name); return Path.Combine(p[..1], p[Path.GetPathRoot(p)!.Length..]); }
        async Task<FileRecord> Add(string name, bool isRef, string? content)
        {
            string rel = Rel(name);
            string hash = "0";
            if (content is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, rel))!);
                await File.WriteAllTextAsync(Path.Combine(target, rel), content);   // the materialised plain copy
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            }
            return await repo.CreateFileRecordAsync(new FileRecord
            {
                DiscId = disc.Id, SourcePath = Path.Combine(source, name), DiscPath = isRef ? rel + ".fileref" : rel,
                SizeBytes = content?.Length ?? 5, Hash = hash, IsFileRef = isRef, Version = 1,
                SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
            });
        }
        var flipKept = await Add("a.txt", isRef: true, "content a");
        var flipRetired = await Add("b.txt", isRef: true, "content b");
        await File.WriteAllTextAsync(Path.Combine(target, Rel("b.txt") + ".fileref"), "manifest");
        var pruneKept = await Add("c.txt", isRef: false, content: null);
        var pruneMoved = await Add("d.txt", isRef: false, content: null);

        var reconcile = new CatalogReconcileService(repo);
        var report = await reconcile.AnalyzeAsync(set.Id, target);
        Check(report.Flips.Count == 2 && report.Prunes.Count == 2,
            $"Analyze: two stale references to flip and two missing rows to prune (got {report.Flips.Count}, {report.Prunes.Count})");

        // Since Analyze: Cleanup retires b.txt, and a backup moves d.txt's row
        // into the history (a full-record update, as the engine makes one).
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [flipRetired.Id]);
        var d = (await repo.GetFileRecordsByPathAsync(set.Id, pruneMoved.SourcePath)).Single();
        d.DiscPath = Path.Combine("C_prev", Rel("d.txt")[2..]) + ".v1";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, d.DiscPath))!);
        await File.WriteAllTextAsync(Path.Combine(target, d.DiscPath), "d, now in the history");
        await repo.UpdateFileRecordAsync(d);

        var result = await reconcile.ApplyAsync(set.Id, report, target);
        Check(result.Flipped == 1 && result.Pruned == 1 && result.Skipped == 2,
            $"Apply: one flip and one prune, two skipped as changed since (got {result.Flipped}/{result.Pruned}/{result.Skipped})");
        var rows = (await repo.GetAllFilesForBackupSetAsync(set.Id)).ToDictionary(r => r.Id);
        Check(rows[flipKept.Id] is { IsFileRef: false, IsDeleted: false } a && a.DiscPath == Rel("a.txt"),
            "a.txt, unchanged, was flipped to its plain copy");
        Check(rows[flipRetired.Id].IsDeleted && rows[flipRetired.Id].IsFileRef,
            "b.txt, retired by Cleanup since, stays retired (it used to come back to life)");
        Check(File.Exists(Path.Combine(target, Rel("b.txt") + ".fileref")), "and its manifest was not deleted");
        Check(rows[pruneKept.Id].IsDeleted, "c.txt, still missing, was pruned");
        Check(!rows[pruneMoved.Id].IsDeleted && rows[pruneMoved.Id].DiscPath == d.DiscPath,
            "d.txt, moved into the history since, keeps its row and its new path");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    static async Task<bool> WaitFor(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms)
            await Task.Delay(25);
        return cond();
    }

    /// <summary>
    /// §12. A purge works from a scan that can be hours old, while the set's row
    /// and the background service could back it up. Files backed up again since
    /// the scan, and untracked files a seed has adopted since, are left alone;
    /// the purge holds the row (hooks) and the background service (markers), and
    /// waits for a background run that is going.
    /// </summary>
    static async Task CleanupWhileInUseAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 12. Cleanup while the set is in use ===");
        string source = Path.Combine(root, "src12"), target = Path.Combine(root, "dst12");
        string catDir = Path.Combine(root, "cat12");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(catDir);
        var repo = new SqliteCatalogRepository(Path.Combine(catDir, "catalog.db"));
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "cleanup-in-use", SourceRoots = [source], SourceSelections = Sel(),
            CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, TierSets = [keepOne], RetentionTiers = keepOne.Tiers },
        });
        BackupJob Job() => new()
        {
            BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = false,
            TargetDirectory = target, ZipMode = ZipMode.None, TierSets = [keepOne], RetentionTiers = keepOne.Tiers,
        };
        string Rel(string sourcePath) => Path.Combine(sourcePath[..1], sourcePath[Path.GetPathRoot(sourcePath)!.Length..]);
        string Stored(string sourcePath) => Path.Combine(target, Rel(sourcePath));

        string back = Path.Combine(source, "comes-back.txt");
        string gone = Path.Combine(source, "gone.txt");
        string keepMe = Path.Combine(source, "keep-me.txt");
        string gone2 = Path.Combine(source, "sub", "gone-later.txt");
        await File.WriteAllTextAsync(back, "comes back, first content");
        await File.WriteAllTextAsync(gone, "gone for good");
        await File.WriteAllTextAsync(keepMe, "deleted, but unticked in Cleanup");
        await File.WriteAllTextAsync(gone2, "purged in the second cleanup");
        var r0 = await svc.ExecuteAsync(Job(), target, keepOne.Tiers, null, CancellationToken.None);
        Check(r0.Success, "the set is backed up");
        foreach (var f in new[] { back, gone, keepMe, gone2 })
            File.Delete(f);

        // Stray copies where files would be stored, with no record: untracked.
        string adoptedNow = Path.Combine(source, "adopted-now.txt");
        string adoptedOld = Path.Combine(source, "adopted-old.txt");
        string strayKeep = Path.Combine(source, "stray-keep.txt");
        string strayGone = Path.Combine(source, "stray-gone.txt");
        foreach (var f in new[] { adoptedNow, adoptedOld, strayKeep, strayGone })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Stored(f))!);
            await File.WriteAllTextAsync(Stored(f), "stray copy of " + Path.GetFileName(f));
        }

        var calls = new List<string>();
        bool markerHeldDuringPurge = false;
        var hooks = new RowWorkHooks
        {
            DescribeBusy = () => null,
            Begin = _ => calls.Add("begin"),
            Progress = t =>
            {
                calls.Add("progress");
                markerHeldDuringPurge |= repo.IsSetBeingCleaned(set.Id);
            },
            End = (t, err) => calls.Add("end:" + (err ? "error:" : "") + t),
        };
        var s = (await repo.GetBackupSetAsync(set.Id))!;
        var vm = new OrphanedDirectoriesViewModel(repo, s) { RowWork = hooks };
        var bothLines = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrphanedDirectoriesViewModel.PurgeStatusText)
                && vm.PurgeStatusText.Length > 0 && vm.PurgeStatusText == vm.SummaryText)
                bothLines.Add(vm.PurgeStatusText);
        };

        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading && vm.Categories.Count > 0, 60000);
        vm.ScanDestinationCommand.Execute(null);
        Check(vm.IsScanningDestination && !vm.ScanCatalogCommand.CanExecute(null)
              && !vm.ReconcileAnalyzeCommand.CanExecute(null) && !vm.ScanExcludedCommand.CanExecute(null),
            "while the destination scan runs, the other actions wait (a catalog scan used not to)");
        await Task.Delay(100);
        await WaitFor(() => !vm.IsScanningDestination, 60000);
        var listedDeleted = vm.Items.Where(i => i.Reason == OrphanedReason.DeletedFromDisk)
            .SelectMany(i => i.MatchingSourcePaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(listedDeleted.SetEquals([back, gone, keepMe, gone2]),
            $"the scan lists the four deleted files (got {string.Join(", ", listedDeleted.Select(Path.GetFileName))})");
        var listedStrays = vm.Items.Where(i => i.Reason == OrphanedReason.UntrackedFile)
            .SelectMany(i => i.DiscFilePaths ?? []).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(listedStrays.SetEquals(["adopted-now.txt", "adopted-old.txt", "stray-keep.txt", "stray-gone.txt"]),
            $"and the four stray copies as untracked (got {string.Join(", ", listedStrays)})");

        // Since the scan: comes-back.txt returns and is backed up again (as the
        // background service would, one path), and two strays are adopted - one
        // recorded now, as a seed records a current copy, one under its old
        // time, as a seed records an earlier version.
        await File.WriteAllTextAsync(back, "comes back, second content");
        var rt = await svc.ExecuteTargetedAsync(Job(), target, [back], keepOne.Tiers, CancellationToken.None);
        Check(rt.Success, "comes-back.txt is backed up again after the scan");
        var disc = await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = set.Id, Label = target, SequenceNumber = 9, MediaType = MediaType.Directory,
            FilesystemType = FilesystemType.UDF, Status = BurnSessionStatus.Completed,
        });
        foreach (var (f, when) in new[] { (adoptedNow, DateTime.UtcNow), (adoptedOld, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)) })
            await repo.CreateFileRecordAsync(new FileRecord
            {
                DiscId = disc.Id, SourcePath = f, DiscPath = Rel(f), SizeBytes = new FileInfo(Stored(f)).Length,
                Hash = "adopted", Version = 1, SourceLastWriteUtc = when, BackedUpUtc = when,
            });

        // Tick both categories, then untick: the sub directory (for later), one
        // file row of a deleted directory, one stray's row.
        static IEnumerable<OrphanedNodeViewModel> All(OrphanedNodeViewModel n) => n.Children.SelectMany(All).Prepend(n);
        OrphanedCategoryViewModel Cat(OrphanedReason r) => vm.Categories.Single(c => c.Reason == r);
        OrphanedNodeViewModel Row(OrphanedReason r, string name) => Cat(r).RootNodes.SelectMany(All)
            .Single(n => n.File is not null && n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var c in vm.Categories)
            foreach (var n in c.RootNodes)
                n.IsChecked = c.Reason is OrphanedReason.DeletedFromDisk or OrphanedReason.UntrackedFile;
        Cat(OrphanedReason.DeletedFromDisk).RootNodes.SelectMany(All)
            .Single(n => n.Item is not null && n.Item.DirectoryPath.EndsWith("sub", StringComparison.OrdinalIgnoreCase))
            .IsChecked = false;
        Row(OrphanedReason.DeletedFromDisk, "keep-me.txt").IsChecked = false;
        Row(OrphanedReason.UntrackedFile, "stray-keep.txt").IsChecked = false;

        vm.PurgeSelectedCommand.Execute(null);
        await WaitFor(() => vm.IsPurging, 3000);
        await WaitFor(() => !vm.IsPurging, 60000);
        Check(!vm.IsPurging, $"the purge finished (\"{vm.SummaryText}\")");
        Check(vm.SummaryText.Contains("left 1 file alone", StringComparison.Ordinal),
            "and says it left one file alone that was backed up again since the scan");
        Check(vm.SummaryText.Contains("kept 1 stored file", StringComparison.Ordinal),
            "and that it kept one stored file the catalog uses again");
        Check(vm.SummaryText.Contains("deleted 2 files", StringComparison.Ordinal),
            "and deleted two files: gone.txt's copy and the ticked stray");

        var backNow = await repo.GetActiveLatestRecordByPathAsync(set.Id, back);
        Check(backNow is not null && File.Exists(Path.Combine(target, backNow.DiscPath))
              && File.ReadAllText(Path.Combine(target, backNow.DiscPath)) == "comes back, second content",
            "comes-back.txt keeps its live record and its new stored copy");
        Check((await repo.GetFileRecordsByPathAsync(set.Id, gone)).All(r => r.IsDeleted) && !File.Exists(Stored(gone)),
            "gone.txt was purged: records retired, stored copy deleted");
        Check(await repo.GetActiveLatestRecordByPathAsync(set.Id, keepMe) is not null && File.Exists(Stored(keepMe)),
            "keep-me.txt, its row unticked, keeps its record and its copy");
        Check(await repo.GetActiveLatestRecordByPathAsync(set.Id, gone2) is not null && File.Exists(Stored(gone2)),
            "the unticked sub directory was not touched");
        Check(File.Exists(Stored(adoptedNow)), "the stray adopted since the scan (recorded now) is kept");
        Check(File.Exists(Stored(adoptedOld)), "the stray adopted under an old time is kept too");
        Check(File.Exists(Stored(strayKeep)), "the stray whose row was unticked is kept");
        Check(!File.Exists(Stored(strayGone)), "the ticked stray was deleted");

        var leftDeleted = vm.Items.Where(i => i.Reason == OrphanedReason.DeletedFromDisk).ToList();
        Check(leftDeleted.SelectMany(i => i.MatchingSourcePaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase)
                  .SetEquals([keepMe, gone2]),
            "keep-me.txt stays listed beside the sub directory");
        Check(vm.Items.Where(i => i.Reason == OrphanedReason.UntrackedFile)
                  .SelectMany(i => i.DiscFilePaths ?? []).Select(Path.GetFileName).SequenceEqual(["stray-keep.txt"]),
            "and the unticked stray stays listed");
        Check(vm.Categories.All(c => c.RootNodes.SelectMany(All).All(n => n.IsChecked == false)),
            "what is left shows unticked, as it was");

        Check(calls.FirstOrDefault() == "begin", "the row was held before anything else");
        Check(calls.Count(c => c == "progress") >= 3, $"each stage went to the row ({calls.Count(c => c == "progress")} lines)");
        Check(calls.LastOrDefault() is { } last && last.StartsWith("end:Cleanup: Purged", StringComparison.Ordinal),
            $"and it was released with the result (\"{calls.LastOrDefault()}\")");
        Check(markerHeldDuringPurge, "the set was marked as being cleaned while the purge ran");
        Check(!repo.IsSetBeingCleaned(set.Id), "and is not any more");
        Check(bothLines.Any(t => t.StartsWith("Cleaning up:", StringComparison.Ordinal))
              && bothLines.Any(t => t.StartsWith("Updating catalog", StringComparison.Ordinal)),
            $"the stages showed in both status lines ({bothLines.Count} updates)");
        Check(vm.PurgeStatusText.Length == 0, "the line by Close is cleared at the end");

        Console.WriteLine();
        Console.WriteLine("=== 12b. Only a subdirectory ticked; the purge waits for a background run ===");
        using (repo.HoldSetForBackgroundBackup(set.Id))
        {
            Check(repo.IsSetBeingBackedUpInBackground(set.Id), "a background run's marker is seen");
            // The parent directory (keep-me.txt's) stays unticked; ticking the
            // sub directory leaves it part-ticked, which used to purge it too.
            Cat(OrphanedReason.DeletedFromDisk).RootNodes.SelectMany(All)
                .Single(n => n.Item is not null && n.Item.DirectoryPath.EndsWith("sub", StringComparison.OrdinalIgnoreCase))
                .IsChecked = true;
            calls.Clear();
            vm.PurgeSelectedCommand.Execute(null);
            bool waiting = await WaitFor(() => vm.SummaryText.Contains("waiting for the background service", StringComparison.Ordinal), 5000);
            Check(waiting, $"the purge says it is waiting for the background service (\"{vm.SummaryText}\")");
            await Task.Delay(1000);
            Check(vm.IsPurging && await repo.GetActiveLatestRecordByPathAsync(set.Id, gone2) is not null,
                "and changes nothing while the run goes on");
            Check(!vm.ScanCatalogCommand.CanExecute(null) && !vm.ScanDestinationCommand.CanExecute(null)
                  && !vm.ReconcileAnalyzeCommand.CanExecute(null) && !vm.PurgeSelectedCommand.CanExecute(null),
                "and nothing else in the window can start meanwhile");
        }
        Check(!repo.IsSetBeingBackedUpInBackground(set.Id), "the run's marker goes when it ends");
        await WaitFor(() => !vm.IsPurging, 60000);
        Check(!vm.IsPurging && (await repo.GetFileRecordsByPathAsync(set.Id, gone2)).All(r => r.IsDeleted)
              && !File.Exists(Stored(gone2)),
            $"then the purge carries on and purges the sub directory (\"{vm.SummaryText}\")");
        Check(await repo.GetActiveLatestRecordByPathAsync(set.Id, keepMe) is not null && File.Exists(Stored(keepMe)),
            "and not its unticked parent: keep-me.txt is still backed up");

        Console.WriteLine();
        Console.WriteLine("=== 12c. Markers and the in-use query ===");
        string? markerPath;
        using (repo.HoldSetForCleanup(set.Id))
        {
            markerPath = Directory.EnumerateFiles(catDir, "*.cleaning", SearchOption.AllDirectories).SingleOrDefault();
            Check(markerPath is not null && repo.IsSetBeingCleaned(set.Id), "a held cleanup marker exists and counts");
        }
        Check(markerPath is not null && !File.Exists(markerPath), "it is deleted when released");
        if (markerPath is not null)
        {
            File.WriteAllText(markerPath, "12345");
            File.SetLastWriteTimeUtc(markerPath, DateTime.UtcNow.AddMinutes(-2));
            Check(!repo.IsSetBeingCleaned(set.Id), "one left behind by a process that died does not count");
            File.Delete(markerPath);
        }
        string oldRel = Rel(adoptedOld);
        var inUse = await repo.FindDiscPathsInUseAsync(set.Id,
            [oldRel.Replace('\\', '/').ToUpperInvariant(), Rel(gone), Rel(Path.Combine(source, "never.txt"))]);
        Check(inUse.Count == 1 && inUse[0] == oldRel.Replace('\\', '/').ToUpperInvariant(),
            $"FindDiscPathsInUseAsync: only the live record's path, as given, whatever its case and slashes (got {inUse.Count})");
        Check(ConfirmCloseWhenIdle(vm), "Close is not questioned when no purge is running");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    static bool ConfirmCloseWhenIdle(IConfirmClose flow) => flow.ConfirmClose(canCancel: true);

    /// <summary>
    /// §11. git's atomic save: index.lock is created empty, caught by a backup
    /// while empty (so stored as a .fileref to empty content), then renamed over
    /// index. Relocating it put index.fileref beside the plain index - the
    /// 23,190 stale pointers found on a real destination.
    /// </summary>
    static async Task RenameOverExistingAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 11. A rename over a file that is already backed up ===");
        string source = Path.Combine(root, "src11"), target = Path.Combine(root, "dst11");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "cat11"));
        var repo = new SqliteCatalogRepository(Path.Combine(root, "cat11", "catalog.db"));
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "rename-over", SourceRoots = [source], CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, TierSets = [keepOne], RetentionTiers = keepOne.Tiers },
        });
        BackupJob Job() => new()
        {
            BackupSetId = set.Id,
            Sources = [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }],
            EnableFileDeduplication = true, TargetDirectory = target, ZipMode = ZipMode.None,
            TierSets = [keepOne], RetentionTiers = keepOne.Tiers,
        };
        async Task Backup()
        {
            var r = await svc.ExecuteAsync(Job(), target, keepOne.Tiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }

        string empty = Path.Combine(source, "empty.txt");
        string index = Path.Combine(source, "index");
        string lockFile = Path.Combine(source, "index.lock");
        await File.WriteAllTextAsync(empty, "");
        await File.WriteAllTextAsync(index, "index version one");
        await Backup();
        await File.WriteAllTextAsync(lockFile, "");          // git creates index.lock
        await Backup();                                       // ...and a backup catches it empty
        var lockHead = (await repo.GetLatestVersionInfoAsync(set.Id))[lockFile];
        Check(lockHead.IsFileRef, "index.lock, caught empty, is stored as a .fileref");

        await File.WriteAllTextAsync(lockFile, "index version two");
        File.Move(lockFile, index, overwrite: true);          // git renames it over index
        var outcome = await svc.MoveTargetedAsync(Job(), target, lockFile, index, isDirectory: false, CancellationToken.None);
        Check(outcome == TargetedMoveOutcome.FellBack, $"the rename over a backed-up file falls back (got {outcome})");
        string indexCurrent = Path.Combine(target, index[..1], index[Path.GetPathRoot(index)!.Length..]);
        Check(!File.Exists(indexCurrent + ".fileref"), "no index.fileref is put beside index");

        // What the Worker does on a fallback: back the target up as its next
        // version, and retire the vacated name.
        var r2 = await svc.ExecuteTargetedAsync(Job(), target, [index], keepOne.Tiers, CancellationToken.None);
        Check(r2.Success, "backing up index afterwards succeeds");
        await repo.MarkFilesDeletedBySourcePathsAsync(set.Id, [lockFile]);
        var indexRows = (await repo.GetFileRecordsByPathAsync(set.Id, index)).Where(r => !r.IsDeleted).OrderBy(r => r.Version).ToList();
        Check(indexRows.Select(r => (r.Version, r.IsFileRef)).SequenceEqual([(1, false), (2, false)]),
            $"index has one chain: v1 in the history, v2 current (got {string.Join(",", indexRows.Select(r => r.Version + (r.IsFileRef ? "r" : "p")))})");
        Check(File.ReadAllText(indexCurrent) == "index version two", "and its current copy holds the new content");

        // A rename to a name with no backup still just relocates.
        string other = Path.Combine(source, "other.txt"), renamed = Path.Combine(source, "renamed.txt");
        await File.WriteAllTextAsync(other, "something else entirely");
        await Backup();
        File.Move(other, renamed);
        var moved = await svc.MoveTargetedAsync(Job(), target, other, renamed, isDirectory: false, CancellationToken.None);
        Check(moved == TargetedMoveOutcome.Relocated, $"a rename to a new name still relocates (got {moved})");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §9. An excess version listed because another plain copy of its content
    /// survived, deleted together with that copy (ticked in another category):
    /// the purge must keep it, since a .fileref still needs the content.
    /// </summary>
    static async Task PurgeAcrossCategoriesAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 9. Purge re-checks against every ticked category ===");
        string source = Path.Combine(root, "src9"), target = Path.Combine(root, "dst9");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "cat9"));
        var repo = new SqliteCatalogRepository(Path.Combine(root, "cat9", "catalog.db"));
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "purge-recheck", SourceRoots = [source], SourceSelections = Sel(),
            CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, TierSets = [keepOne], RetentionTiers = keepOne.Tiers },
        });
        async Task Backup(bool dedup)
        {
            var opts = (await repo.GetBackupSetAsync(set.Id))!.JobOptions!;
            var job = new BackupJob
            {
                BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = dedup,
                TargetDirectory = target, ZipMode = ZipMode.None,
                TierSets = opts.TierSets, RetentionTiers = opts.RetentionTiers,
            };
            var tiers = opts.RetentionTiers.Count > 0 ? opts.RetentionTiers : VersionRetentionService.DefaultTiers;
            var r = await svc.ExecuteAsync(job, target, tiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }

        string h = Path.Combine(source, "h.bin"), k = Path.Combine(source, "k.bin"), m = Path.Combine(source, "m.bin");
        await File.WriteAllTextAsync(h, "content H");
        await Backup(dedup: false);
        await File.WriteAllTextAsync(h, "h changed");
        await Backup(dedup: false);                       // p: h.bin.v1, plain, content H
        await File.WriteAllTextAsync(k, "content H");
        await Backup(dedup: false);                       // q: k.bin, a second plain copy of H
        await File.WriteAllTextAsync(m, "content H");
        await Backup(dedup: true);                        // r: m.bin, a .fileref to H
        var heads = await repo.GetLatestVersionInfoAsync(set.Id);
        string hHash = heads[k].Hash;
        Check(heads[m].IsFileRef && heads[m].Hash == hHash, "m.bin is a .fileref to content H");

        var s = (await repo.GetBackupSetAsync(set.Id))!;
        s.JobOptions!.TierSets = [new VersionTierSet { Name = "Default", Tiers = [] }];
        s.JobOptions.RetentionTiers = [];
        await repo.UpdateBackupSetAsync(s);
        File.Delete(k);                                   // q becomes "deleted from disk"

        var vm = new OrphanedDirectoriesViewModel(repo, s);
        vm.ScanCatalogCommand.Execute(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((vm.IsLoading || vm.Categories.Count == 0) && sw.ElapsedMilliseconds < 60000)
            await Task.Delay(50);
        var excessCat = vm.Categories.FirstOrDefault(c => c.Reason == OrphanedReason.ExcessVersion);
        var deletedCat = vm.Categories.FirstOrDefault(c => c.Reason == OrphanedReason.DeletedFromDisk);
        Check(excessCat is not null, "Cleanup lists h.bin's earlier version (another copy of H survives)");
        Check(deletedCat is not null, "and k.bin under deleted from disk");
        if (excessCat is null || deletedCat is null) return;

        foreach (var c in vm.Categories)
            foreach (var n in c.RootNodes)
                n.IsChecked = c == excessCat || c == deletedCat;
        vm.PurgeSelectedCommand.Execute(null);
        sw.Restart();
        while (!vm.IsPurging && sw.ElapsedMilliseconds < 3000) await Task.Delay(20);
        while (vm.IsPurging && sw.ElapsedMilliseconds < 60000) await Task.Delay(50);
        Check(!vm.IsPurging, $"the purge finished (\"{vm.SummaryText}\")");
        Check(vm.SummaryText.Contains("kept 1", StringComparison.Ordinal), "and says it kept one copy for a reference");

        string? anchor = await repo.GetActivePlainContentPathByHashAsync(set.Id, hHash);
        Check(anchor is not null && File.Exists(Path.Combine(target, anchor))
              && File.ReadAllText(Path.Combine(target, anchor)) == "content H",
            "m.bin's content still resolves to a copy on disk");
        Check((await repo.GetFileRecordsByPathAsync(set.Id, k)).All(r => r.IsDeleted),
            "k.bin, deleted from disk, was purged");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>Run Cleanup's catalog scan and return every record it lists as an excess version.</summary>
    static async Task<List<FileRecord>> ScanExcessAsync(ICatalogRepository repo, int setId)
    {
        var set = (await repo.GetBackupSetAsync(setId))!;
        var vm = new OrphanedDirectoriesViewModel(repo, set);
        vm.ScanCatalogCommand.Execute(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((vm.IsLoading || vm.Items.Count == 0 && sw.ElapsedMilliseconds < 500) && sw.ElapsedMilliseconds < 60000)
            await Task.Delay(50);
        Check(!vm.IsLoading, "Cleanup's catalog scan finished");
        Check(!vm.SummaryText.StartsWith("Error", StringComparison.Ordinal), $"without an error (\"{vm.SummaryText}\")");
        return vm.Items
            .Where(i => i.Reason == OrphanedReason.ExcessVersion && i.ExcessVersionRecords is not null)
            .SelectMany(i => i.ExcessVersionRecords!)
            .ToList();
    }
}
