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
