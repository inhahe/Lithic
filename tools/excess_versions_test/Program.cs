// Regression test: earlier versions of files whose tier set keeps none.
//
// Removing every tier from a set's Default tier set means "back up the files no
// other tier set matches, with no history". The retention pass applies no rules
// to a set with no tiers, so the versions those files already had are Cleanup's
// to remove: its Excess Versions category lists every earlier version of such a
// file, newest included.
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
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using LithicBackup.Core;
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
        Check(!Directory.Exists(root), "the harness leaves no temp folder behind");
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

        using var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));
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
        // Only a no-tier set that is NOT Default excludes: an empty Default backs
        // big.vmdk up, with no history.
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
        // No history: the new copy replaces the old one, and its record must not
        // stay live claiming the old content at a path now holding the new bytes:
        // the .fileref would restore the wrong content, and a later identical
        // file would become a reference to nothing.
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
        string? xAnchor = (await repo.GetActivePlainContentPathsByHashAsync(set.Id, xHash)).FirstOrDefault();
        Check(xAnchor is not null && File.ReadAllText(Path.Combine(target, xAnchor)) == "shared content X",
            "the .fileref's content still resolves to a copy that holds it");
        var oldRows = (await repo.GetFileRecordsByPathAsync(set.Id, xPlain)).Where(r => !r.IsDeleted).ToList();
        Check(oldRows.Count == 1 && oldRows[0].Hash != xHash,
            $"the changed file has one live record, for its new content (live: {oldRows.Count})");
        string x3 = Path.Combine(source, "x3.bin");
        await File.WriteAllTextAsync(x3, "shared content X");
        await Backup();
        string? x3Anchor = (await repo.GetActivePlainContentPathsByHashAsync(set.Id, xHash)).FirstOrDefault();
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
        await InPlaceOverwriteAsync(root);
        await KeptCopyAcrossCategoriesAsync(root);
        await GuardedRetireAsync(root);
        await PreviousVersionsIndexAsync(root);
        TierWalk();
        await CleanupInSubfolderAsync(root);
        await RecordsElsewhereAsync(root);
        await SharedBackupFolderAsync(root);
        await ReferencesResolveToAnIntactCopyAsync(root);
        await ComingBackAfterRetirementAsync(root);
        await SeedLeavesOutReferencesWithoutContentAsync(root);
        await RetiredCopiesComeBackAsync(root);
        await EmptyContentNeedsNoCopyAsync(root);
        await WrongAnchorIsReportedAsync(root);
    }

    /// <summary>A set backing <paramref name="source"/> up to <paramref name="target"/> with file-level dedup, in a catalog of its own.</summary>
    static async Task<(SqliteCatalogRepository Repo, BackupSet Set, Func<BackupJob> Job)> DedupSetAsync(
        string root, string name, string source, string target)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = name, SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true },
        });
        BackupJob Job() => new()
        {
            BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
            ZipMode = ZipMode.None,
        };
        return (repo, set, Job);
    }

    /// <summary>
    /// §28. Empty content needs no copy. A backup stores empty files plain, never
    /// as references. A reference to empty content that an older build wrote,
    /// with no plain copy of it anywhere, is kept by a seed, passed by Verify and
    /// restored as an empty file.
    /// </summary>
    static async Task EmptyContentNeedsNoCopyAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 28. Empty content needs no copy ===");
        string source = Path.Combine(root, "src28"), target = Path.Combine(root, "dst28");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        string e1 = Path.Combine(source, "e1.txt"), e2 = Path.Combine(source, "e2.txt");
        await File.WriteAllBytesAsync(e1, []);
        await File.WriteAllBytesAsync(e2, []);
        var (repo, set, job) = await DedupSetAsync(root, "cat28", source, target);
        using (repo)
        {
            var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
            if (!(await svc.ExecuteAsync(job(), target, VersionRetentionService.DefaultTiers, null, CancellationToken.None)).Success)
                throw new Exception("backup failed");
            var rows = (await repo.GetFileRecordsByPathAsync(set.Id, e1)).Concat(await repo.GetFileRecordsByPathAsync(set.Id, e2)).ToList();
            Check(rows.Count == 2 && rows.All(r => !r.IsFileRef && r.Hash == EmptyContent.Hash),
                "two empty files are both stored plain, not one as a reference to the other");
        }

        // A destination written by an older build: an empty file's reference, and no
        // plain copy of empty content anywhere.
        string oldTarget = Path.Combine(root, "dst28-old");
        string e3 = Path.Combine(source, "e3.txt");
        await File.WriteAllBytesAsync(e3, []);
        string manifestPath = Path.Combine(oldTarget, e3[..1], e3[3..]) + ".fileref";
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new FileRefManifest
        {
            OriginalName = "e3.txt", OriginalSize = 0, Hash = EmptyContent.Hash, SourcePath = e3, ContentPath = "",
        }));
        var (oldRepo, oldSet, oldJob) = await DedupSetAsync(root, "cat28-old", source, oldTarget);
        using (oldRepo)
        {
            var svc = new DirectoryBackupService(oldRepo, new FileScanner(oldRepo), new VersionRetentionService(oldRepo));
            var seeded = await svc.SeedFromExistingDirectoryAsync(oldSet.Id, oldTarget);
            var e3Rows = await oldRepo.GetFileRecordsByPathAsync(oldSet.Id, e3);
            Check(seeded.ReferencesWithoutContent == 0 && e3Rows.Count == 1 && e3Rows[0].IsFileRef && !e3Rows[0].IsDeleted,
                $"a seed keeps the reference to empty content ({seeded.ReferencesWithoutContent} left out)");
            int broken = (await new VerifyService(oldRepo, new FileScanner(oldRepo)).VerifyAsync(oldJob(), oldTarget))
                .Issues.Count(i => i.Kind == VerifyIssueKind.FileRefUnresolved);
            Check(broken == 0, $"Verify does not call it broken ({broken})");
            var restorer = new RestoreService(oldRepo);
            string restoreTo = Path.Combine(root, "restored28");
            var restored = await restorer.RestoreAsync(
                await restorer.MaterializeSelectionAsync(oldSet.Id, [], [e3]),
                new Dictionary<string, string> { [source[..1].ToUpperInvariant()] = restoreTo });
            var files = Directory.Exists(restoreTo) ? Directory.GetFiles(restoreTo, "e3.txt", SearchOption.AllDirectories) : [];
            Check(restored.Errors.Count == 0 && files.Length == 1 && new FileInfo(files[0]).Length == 0,
                $"and restore writes the empty file ({string.Join("; ", restored.Errors)})");
            string freeTo = Path.Combine(root, "restored28-free");
            var free = await new CatalogFreeRestoreService().RestoreAsync(oldTarget, freeTo);
            var freeFiles = Directory.Exists(freeTo) ? Directory.GetFiles(freeTo, "e3.txt", SearchOption.AllDirectories) : [];
            Check(free.Errors.Count == 0 && freeFiles.Length == 1 && new FileInfo(freeFiles[0]).Length == 0,
                $"as does the restore that reads no catalog ({string.Join("; ", free.Errors)})");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §29. With "Verify file contents", a plain copy whose bytes are wrong is
    /// reported as its own file, and so is every reference that resolves to it,
    /// whichever the catalog reaches first. Verify's repair then retires the copy
    /// too, rather than only the first reference, after which the next backup
    /// would have revived the others against the bad copy.
    /// </summary>
    static async Task WrongAnchorIsReportedAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 29. A wrong copy is reported for itself and for every reference to it ===");
        string source = Path.Combine(root, "src29"), target = Path.Combine(root, "dst29");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var paths = new[] { "a.txt", "b.txt", "c.txt" }.Select(n => Path.Combine(source, n)).ToList();
        foreach (var p in paths)
            await File.WriteAllTextAsync(p, "the same bytes, section twenty-nine");
        var (repo, set, job) = await DedupSetAsync(root, "cat29", source, target);
        using (repo)
        {
            var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
            if (!(await svc.ExecuteAsync(job(), target, VersionRetentionService.DefaultTiers, null, CancellationToken.None)).Success)
                throw new Exception("backup failed");
            var live = new List<FileRecord>();
            foreach (var p in paths)
                live.AddRange((await repo.GetFileRecordsByPathAsync(set.Id, p)).Where(r => !r.IsDeleted));
            var plain = live.Single(r => !r.IsFileRef);
            await File.WriteAllTextAsync(Path.Combine(target, plain.DiscPath), "THE SAME BYTES, SECTION TWENTY-NINE");
            var mismatched = (await new VerifyService(repo, new FileScanner(repo)).VerifyAsync(job(), target, verifyContents: true))
                .Issues.Where(i => i.Kind == VerifyIssueKind.ContentMismatch)
                .Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check(mismatched.SetEquals(paths),
                $"the copy and both references to it are reported ({mismatched.Count} of {paths.Count})");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §27. A reference whose copy was retired while it still needed it (as
    /// removals did before the guard) gets that copy back when the copy's file is
    /// still there with the content. The copy comes back from ContentAnchors
    /// directly, from Cleanup's purge instead of being deleted as deleted in the
    /// catalog, and from Verify's repair instead of the reference's file being
    /// flagged for re-backup. A copy whose bytes changed does not come back.
    /// </summary>
    static async Task RetiredCopiesComeBackAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 27. A retired copy a reference still needs comes back ===");
        string source = Path.Combine(root, "src27"), target = Path.Combine(root, "dst27");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        string dir = Path.Combine(root, "cat27");
        Directory.CreateDirectory(dir);
        using var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "retired copies", SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true },
        });
        BackupJob Job() => new()
        {
            BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
            ZipMode = ZipMode.None,
        };
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        async Task<FileRecord> Live(string path) =>
            (await repo.GetFileRecordsByPathAsync(set.Id, path)).Where(r => !r.IsDeleted).MaxBy(r => r.Version)!;
        async Task<bool> IsLive(FileRecord r) =>
            (await repo.GetFileRecordsByPathAsync(set.Id, r.SourcePath)).Any(x => x.Id == r.Id && !x.IsDeleted);
        async Task<List<string>> Broken() =>
            [.. (await new VerifyService(repo, new FileScanner(repo)).VerifyAsync(Job(), target))
                .Issues.Where(i => i.Kind == VerifyIssueKind.FileRefUnresolved).Select(i => i.Path)];

        string a = Path.Combine(source, "a.txt"), b = Path.Combine(source, "b.txt");
        string c = Path.Combine(source, "c.txt"), d = Path.Combine(source, "d.txt");
        foreach (var (path, text) in new[] { (a, "kept for a reference, section twenty-seven"),
                                             (b, "kept for a reference, section twenty-seven"),
                                             (c, "changed under its record, section twenty-seven"),
                                             (d, "changed under its record, section twenty-seven") })
            await File.WriteAllTextAsync(path, text);
        if (!(await svc.ExecuteAsync(Job(), target, VersionRetentionService.DefaultTiers, null, CancellationToken.None)).Success)
            throw new Exception("backup failed");
        var plainAB = (await Live(a)).IsFileRef ? await Live(b) : await Live(a);
        string refAB = plainAB.SourcePath == a ? b : a;
        var plainCD = (await Live(c)).IsFileRef ? await Live(d) : await Live(c);
        string refCD = plainCD.SourcePath == c ? d : c;
        // Retired without the guard, as removals were; one copy's bytes then changed.
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [plainAB.Id, plainCD.Id]);
        await File.WriteAllTextAsync(Path.Combine(target, plainCD.DiscPath), "other bytes now, section twenty-seven");
        var broken = await Broken();
        Check(broken.Count == 2, $"set-up: both references are broken ({broken.Count})");

        async Task<IReadOnlyList<FileRecord>> Revive(params string[] hashes)
        {
            var candidates = await repo.GetRetiredPlainCopiesAsync(set.Id, hashes);
            using var tx = await repo.BeginTransactionAsync(set.Id);
            var made = await ContentAnchors.ReviveCopiesAsync(repo, set.Id, target, candidates, CancellationToken.None);
            tx.Complete();
            return made;
        }
        FileRecord Fake(string name, string discPath, string hash, bool isRef, int version = 1) => new()
        {
            DiscId = plainAB.DiscId, SourcePath = Path.Combine(root, "elsewhere27", name), DiscPath = discPath,
            SizeBytes = 1, Hash = hash, IsFileRef = isRef, Version = version,
            SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        };

        bool refused;
        try
        {
            await ContentAnchors.ReviveCopiesAsync(repo, set.Id, target, [], CancellationToken.None);
            refused = false;
        }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "reviving copies outside the set's write transaction is refused");

        // A live record of the content whose file has gone is no copy of it.
        var goneCopy = await repo.CreateFileRecordAsync(Fake("gone.txt", @"!gone27\gone.txt", plainAB.Hash, isRef: false));
        var revived = await Revive(plainAB.Hash, plainCD.Hash);
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [goneCopy.Id]);
        broken = await Broken();
        Check(revived.Count == 1 && revived[0].Id == plainAB.Id && await IsLive(plainAB),
            $"the copy still holding its content is live again, past a live record whose file is gone ({revived.Count} revived)");
        Check(broken.SequenceEqual([refCD], StringComparer.OrdinalIgnoreCase),
            $"and only the reference whose copy changed is still broken ({string.Join("; ", broken)})");

        // A retired copy at a file's current location, though intact, is not
        // revived when a newer record of that file stores it under another name:
        // the next backup of that file could write over it unawares.
        byte[] eBytes = System.Text.Encoding.UTF8.GetBytes("a stale head's content, section twenty-seven");
        string eHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(eBytes)).ToLowerInvariant();
        string eStored = Path.Combine(target, "stale", "e.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(eStored)!);
        await File.WriteAllBytesAsync(eStored, eBytes);
        var oldHead = await repo.CreateFileRecordAsync(Fake("e.txt", @"stale\e.txt", eHash, isRef: false));
        var newHead = await repo.CreateFileRecordAsync(
            Fake("e.txt", @"stale\e.txt.fileref", plainAB.Hash, isRef: true, version: 2));
        var needsE = await repo.CreateFileRecordAsync(Fake("needs-e.txt", @"!needs\e.fileref", eHash, isRef: true));
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [oldHead.Id]);
        var stale = await Revive(eHash);
        Check(stale.Count == 0 && !await IsLive(oldHead),
            $"a retired copy at the current location under a newer head is left retired ({stale.Count} revived)");
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [newHead.Id, needsE.Id]);
        File.Delete(eStored);

        // Cleanup lists the retired copy as deleted in the catalog; its purge keeps it.
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [plainAB.Id]);
        var vm = new OrphanedDirectoriesViewModel(repo, set);
        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading, 60000);
        vm.ScanDestinationCommand.Execute(null);
        await WaitFor(() => vm.IsScanningDestination, 3000);
        await WaitFor(() => !vm.IsScanningDestination, 60000);
        bool listed = vm.Items.Any(i => i.Reason == OrphanedReason.CatalogDeleted
            && i.DiscFilePaths?.Any(p => p.Equals(plainAB.DiscPath, StringComparison.OrdinalIgnoreCase)) == true);
        Check(listed, "Cleanup lists the retired copy as deleted in the catalog");
        foreach (var cat in vm.Categories)
            foreach (var n in cat.RootNodes)
                n.IsChecked = cat.Reason == OrphanedReason.CatalogDeleted;
        vm.PurgeSelectedCommand.Execute(null);
        await WaitFor(() => vm.IsPurging, 3000);
        await WaitFor(() => !vm.IsPurging, 60000);
        Check(File.Exists(Path.Combine(target, plainAB.DiscPath)) && await IsLive(plainAB)
              && vm.SummaryText.Contains("references still need", StringComparison.Ordinal),
            $"the purge keeps it, live again, for the reference (\"{vm.SummaryText}\")");

        // Verify's repair gives the reference its copy back instead of flagging it.
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [plainAB.Id]);
        var verify = new VerifyIntegrityViewModel(repo, new FileScanner(repo), set);
        verify.VerifyCommand.Execute(null);
        await WaitFor(() => verify.HasRun && !verify.IsLoading, 60000);
        Check(verify.RepairableCount == 2,
            $"set-up: Verify offers both broken references for repair ({string.Join("; ", verify.Issues.Select(i => $"{i.Kind} {Path.GetFileName(i.Path)}"))})");
        verify.RepairCommand.Execute(null);
        await WaitFor(() => verify.Repaired && !verify.IsBusy, 60000);
        Check(await IsLive(plainAB) && (await repo.GetFileRecordsByPathAsync(set.Id, refAB)).Any(r => !r.IsDeleted),
            $"Verify's repair makes the copy live and leaves its reference alone (\"{verify.SummaryText}\")");
        Check((await repo.GetFileRecordsByPathAsync(set.Id, refCD)).All(r => r.IsDeleted),
            "and flags the file whose reference has no copy left, for the next backup");
        verify.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §26. A seed from a backup whose reference points at content no longer on
    /// the drive does not record that reference as backed up: it is retired and
    /// counted, and the next backup copies the file again.
    /// </summary>
    static async Task SeedLeavesOutReferencesWithoutContentAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 26. A seed leaves out references to content not on the drive ===");
        string source = Path.Combine(root, "src26"), target = Path.Combine(root, "dst26");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        BackupJob Job(int setId) => new()
        {
            BackupSetId = setId, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
            ZipMode = ZipMode.None,
        };
        async Task<(SqliteCatalogRepository Repo, BackupSet Set)> NewSet(string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
            var set = await repo.CreateBackupSetAsync(new BackupSet
            {
                Name = name, SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
                DefaultMediaType = MediaType.Directory,
                JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true },
            });
            return (repo, set);
        }
        static DirectoryBackupService Service(SqliteCatalogRepository repo) =>
            new(repo, new FileScanner(repo), new VersionRetentionService(repo));
        async Task Backup(SqliteCatalogRepository repo, int setId)
        {
            var r = await Service(repo).ExecuteAsync(Job(setId), target, VersionRetentionService.DefaultTiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }
        async Task<FileRecord?> Live(SqliteCatalogRepository repo, int setId, string path) =>
            (await repo.GetFileRecordsByPathAsync(setId, path)).Where(r => !r.IsDeleted).MaxBy(r => r.Version);

        // The backup a seed will find: two pairs of duplicates, then one pair's
        // plain copy deleted from the drive.
        string a = Path.Combine(source, "a.txt"), b = Path.Combine(source, "b.txt");
        string c = Path.Combine(source, "c.txt"), d = Path.Combine(source, "d.txt");
        await File.WriteAllTextAsync(a, "kept content, section twenty-six");
        await File.WriteAllTextAsync(b, "kept content, section twenty-six");
        await File.WriteAllTextAsync(c, "lost content, section twenty-six");
        await File.WriteAllTextAsync(d, "lost content, section twenty-six");
        var (old, oldSet) = await NewSet("cat26-old");
        string lostRef, keptRef;
        using (old)
        {
            await Backup(old, oldSet.Id);
            var lostPlain = (await Live(old, oldSet.Id, c))!.IsFileRef ? d : c;
            lostRef = lostPlain == c ? d : c;
            keptRef = (await Live(old, oldSet.Id, a))!.IsFileRef ? a : b;
            File.Delete(Path.Combine(target, (await Live(old, oldSet.Id, lostPlain))!.DiscPath));
            File.Delete(lostPlain);
        }

        var (repo, set) = await NewSet("cat26-new");
        using (repo)
        {
            var outcome = await Service(repo).SeedFromExistingDirectoryAsync(set.Id, target);
            Check(outcome.ReferencesWithoutContent == 1, $"the seed counts one reference without content ({outcome.ReferencesWithoutContent})");
            var lostRows = await repo.GetFileRecordsByPathAsync(set.Id, lostRef);
            Check(lostRows.Count == 1 && lostRows[0].IsDeleted && outcome.Files == 2,
                $"and leaves it retired, out of the files it imported ({outcome.Files})");
            Check((await Live(repo, set.Id, keptRef))?.IsFileRef == true, "while a reference whose content is there is recorded");
            var again = await Service(repo).SeedFromExistingDirectoryAsync(set.Id, target);
            Check(again.ReferencesWithoutContent == 1 && again.Files == 0
                  && (await repo.GetFileRecordsByPathAsync(set.Id, lostRef)).Count == 1,
                $"seeding again records nothing more, not even another retired row ({again.Files} file(s))");
            int broken = (await new VerifyService(repo, new FileScanner(repo)).VerifyAsync(Job(set.Id), target))
                .Issues.Count(i => i.Kind == VerifyIssueKind.FileRefUnresolved);
            Check(broken == 0, $"Verify finds no broken reference after the seed ({broken})");

            await Backup(repo, set.Id);
            var copied = await Live(repo, set.Id, lostRef);
            Check(copied is { IsFileRef: false } && File.ReadAllText(Path.Combine(target, copied.DiscPath)) == "lost content, section twenty-six",
                "the next backup copies that file again");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §25. A path whose records were all retired (moved out of the set, or caught
    /// between the rename and the write of a program's save) and that then comes
    /// back continues its chain. Its earlier versions are live again. A
    /// reference is revived only when its content still has a live copy, and
    /// otherwise the file is stored afresh. A copy whose bytes no longer match its
    /// record is not revived either.
    /// </summary>
    static async Task ComingBackAfterRetirementAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 25. A file that comes back after its records were retired ===");
        string source = Path.Combine(root, "src25"), target = Path.Combine(root, "dst25");
        Directory.CreateDirectory(Path.Combine(source, "one"));
        Directory.CreateDirectory(Path.Combine(source, "two"));
        Directory.CreateDirectory(target);
        string dir = Path.Combine(root, "cat25");
        Directory.CreateDirectory(dir);
        using var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "coming back", SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true },
        });
        BackupJob Job() => new()
        {
            BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
            ZipMode = ZipMode.None,
        };
        async Task Backup()
        {
            var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
            var r = await svc.ExecuteAsync(Job(), target, VersionRetentionService.DefaultTiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }
        async Task<List<FileRecord>> All(string path) => [.. await repo.GetFileRecordsByPathAsync(set.Id, path)];
        async Task<FileRecord?> Live(string path) => (await All(path)).Where(r => !r.IsDeleted).MaxBy(r => r.Version);
        // What the Worker does with a path that left the set.
        async Task Retire(string path) => await repo.RetireRecordsKeepingNeededCopiesAsync(
            set.Id, (await All(path)).Where(r => !r.IsDeleted).Select(r => r.Id).ToList());
        var touch = DateTime.UtcNow.AddMinutes(1);
        void Touch(string path) => File.SetLastWriteTimeUtc(path, touch = touch.AddMinutes(1));
        async Task<List<string>> Broken() =>
            [.. (await new VerifyService(repo, new FileScanner(repo)).VerifyAsync(Job(), target))
                .Issues.Where(i => i.Kind == VerifyIssueKind.FileRefUnresolved).Select(i => i.Path)];

        // The chain: A, then B, then A again - stored as a reference to the A that
        // is now an earlier version.
        string x = Path.Combine(source, "one", "x.txt");
        await File.WriteAllTextAsync(x, "content A, section twenty-five");
        await Backup();
        await File.WriteAllTextAsync(x, "content B, section twenty-five, longer");
        await Backup();
        await File.WriteAllTextAsync(x, "content A, section twenty-five");
        await Backup();
        var chain = await All(x);
        Check(chain.Count == 3 && chain.Single(r => r.Version == 3).IsFileRef,
            $"set-up: x.txt has three versions, the newest a reference to the first ({chain.Count})");
        await Retire(x);
        Check((await All(x)).All(r => r.IsDeleted), "set-up: moving x.txt out retires all three");
        Touch(x);
        await Backup();
        var back = await All(x);
        Check(back.Count == 3 && back.All(r => !r.IsDeleted),
            $"back unchanged, x.txt has all three versions live again ({back.Count(r => !r.IsDeleted)} of {back.Count})");

        // The guard keeps live an earlier version another file's reference needs,
        // so the path comes back with one version live and the rest retired: it
        // still continues its whole chain.
        string w = Path.Combine(source, "one", "w.txt"), wDup = Path.Combine(source, "two", "w-dup.txt");
        await File.WriteAllTextAsync(w, "w content A, section twenty-five");
        await Backup();
        await File.WriteAllTextAsync(w, "w content B, section twenty-five, longer");
        await Backup();
        await File.WriteAllTextAsync(w, "w content C, section twenty-five, longer still");
        await File.WriteAllTextAsync(wDup, "w content A, section twenty-five");
        await Backup();
        Check((await Live(wDup))!.IsFileRef, "set-up: w-dup.txt is a reference to w.txt's first version");
        await Retire(w);
        Check((await All(w)).Count(r => !r.IsDeleted) == 1,
            "set-up: moving w.txt out keeps live only the version w-dup.txt needs");
        Touch(w);
        await Backup();
        var wBack = await All(w);
        Check(wBack.Count == 3 && wBack.All(r => !r.IsDeleted),
            $"back unchanged, w.txt has all three versions live again ({wBack.Count(r => !r.IsDeleted)} of {wBack.Count})");

        // The newest copy, corrupt, cannot come back, while the history can: the
        // corrupt bytes are never taken for a newer copy and recorded live.
        string y = Path.Combine(source, "one", "y.txt");
        await File.WriteAllTextAsync(y, "y content A, section twenty-five");
        await Backup();
        await File.WriteAllTextAsync(y, "y content B, section twenty-five, longer");
        await Backup();
        await File.WriteAllTextAsync(y, "y content C, section twenty-five, longest");
        await Backup();
        var yHead = (await Live(y))!;
        await File.WriteAllTextAsync(Path.Combine(target, yHead.DiscPath), "Y CONTENT C, SECTION TWENTY-FIVE, LONGEST");
        await Retire(y);
        Touch(y);
        await Backup();
        var yLive = (await All(y)).Where(r => !r.IsDeleted).ToList();
        static string Sha(string path) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Check(yLive.Count == 3 && yLive.All(r => !r.IsFileRef && Sha(Path.Combine(target, r.DiscPath)) == r.Hash)
              && File.ReadAllText(Path.Combine(target, (await Live(y))!.DiscPath)) == "y content C, section twenty-five, longest",
            $"its history comes back, the file is copied afresh, and no live record holds the corrupt bytes ({yLive.Count} live)");

        // An earlier version that is a reference whose only copy is the newest
        // version comes back with it.
        // (Folder "one" is backed up before "two", so the anchor is the plain copy.)
        string anchor = Path.Combine(source, "one", "aa-anchor.txt"), z2 = Path.Combine(source, "two", "z2.txt");
        await File.WriteAllTextAsync(anchor, "z2 content A, section twenty-five");
        await File.WriteAllTextAsync(z2, "z2 content A, section twenty-five");
        await Backup();
        Check((await Live(z2))!.IsFileRef && !(await Live(anchor))!.IsFileRef,
            "set-up: z2.txt is a reference to aa-anchor.txt");
        await File.WriteAllTextAsync(z2, "z2 content B, section twenty-five, longer");
        await Backup();
        // The anchor goes (retired and deleted, as removals did before the guard),
        // so z2's content A comes back as plain bytes.
        var anchorLive = await Live(anchor);
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [anchorLive!.Id]);
        File.Delete(Path.Combine(target, anchorLive.DiscPath));
        File.Delete(anchor);
        await File.WriteAllTextAsync(z2, "z2 content A, section twenty-five");
        await Backup();
        var z2Rows = await All(z2);
        Check(z2Rows.Count == 3 && z2Rows.Single(r => r.Version == 1).IsFileRef && !z2Rows.Single(r => r.Version == 3).IsFileRef,
            "set-up: z2.txt's first version is a reference, its newest the content in plain bytes");
        await Retire(z2);
        Touch(z2);
        await Backup();
        z2Rows = await All(z2);
        Check(z2Rows.Count == 3 && z2Rows.All(r => !r.IsDeleted),
            $"back unchanged, z2.txt's reference version comes back with the newest copy it needs ({z2Rows.Count(r => !r.IsDeleted)} of {z2Rows.Count})");

        // Across paths: the reference comes back, the copy it resolved to does not.
        string p = Path.Combine(source, "one", "p.txt"), q = Path.Combine(source, "two", "q.txt");
        await File.WriteAllTextAsync(p, "shared content, section twenty-five");
        await File.WriteAllTextAsync(q, "shared content, section twenty-five");
        await Backup();
        var (refPath, plainPath) = (await Live(p))!.IsFileRef ? (p, q) : (q, p);
        Check((await Live(refPath))!.IsFileRef && !(await Live(plainPath))!.IsFileRef,
            "set-up: of two files of one content, one is a reference to the other");
        await Retire(refPath);
        await Retire(plainPath);
        File.Delete(plainPath);
        Touch(refPath);
        await Backup();
        var comeBack = await Live(refPath);
        Check(comeBack is { IsFileRef: false } && File.Exists(Path.Combine(target, comeBack.DiscPath)),
            "the reference whose copy stayed retired comes back with its own bytes");

        // The same, coming back changed: the old reference goes into the history,
        // still retired, as nothing holds its content.
        string m = Path.Combine(source, "one", "m.txt"), n = Path.Combine(source, "two", "n.txt");
        await File.WriteAllTextAsync(m, "more shared content, section twenty-five");
        await File.WriteAllTextAsync(n, "more shared content, section twenty-five");
        await Backup();
        var (mRef, mPlain) = (await Live(m))!.IsFileRef ? (m, n) : (n, m);
        await Retire(mRef);
        await Retire(mPlain);
        File.Delete(mPlain);
        await File.WriteAllTextAsync(mRef, "changed since, section twenty-five");
        await Backup();
        var mRows = await All(mRef);
        Check(mRows.Where(r => !r.IsDeleted).All(r => !r.IsFileRef) && mRows.Any(r => r.IsFileRef && r.IsDeleted),
            "coming back changed, its old reference stays retired");

        // A copy Verify found changed, retired for the next backup to replace.
        string z = Path.Combine(source, "two", "z.txt");
        await File.WriteAllTextAsync(z, "zed content, section twenty-five");
        await Backup();
        string zStored = Path.Combine(target, (await Live(z))!.DiscPath);
        await File.WriteAllTextAsync(zStored, "ZED CONTENT, SECTION TWENTY-FIVE");
        await Retire(z);
        Touch(z);
        await Backup();
        var zLive = await Live(z);
        Check(zLive is not null && File.ReadAllText(Path.Combine(target, zLive.DiscPath)) == "zed content, section twenty-five",
            "a copy whose bytes changed is replaced, not revived");

        var broken = await Broken();
        Check(broken.Count == 0, $"no reference is left without its content ({string.Join("; ", broken)})");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §24. A .fileref resolves to any recorded plain copy of its content whose
    /// file is still there, not just the first recorded: Verify does not call it
    /// broken, restore restores it, and the backup points a new duplicate at an
    /// intact copy. With every copy gone, a new duplicate gets real bytes, which
    /// the old references then resolve to.
    /// </summary>
    static async Task ReferencesResolveToAnIntactCopyAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 24. A reference resolves to any copy of its content that is still there ===");
        string source = Path.Combine(root, "src24"), target = Path.Combine(root, "dst24");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        string dir = Path.Combine(root, "cat24");
        Directory.CreateDirectory(dir);
        using var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "intact copies", SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true },
        });
        BackupJob Job() => new()
        {
            BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
            ZipMode = ZipMode.None,
        };
        async Task Backup()
        {
            var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
            var r = await svc.ExecuteAsync(Job(), target, VersionRetentionService.DefaultTiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }
        async Task<FileRecord> Live(string path) =>
            (await repo.GetFileRecordsByPathAsync(set.Id, path)).Where(r => !r.IsDeleted).MaxBy(r => r.Version)!;
        async Task<int> BrokenReferences() =>
            (await new VerifyService(repo, new FileScanner(repo)).VerifyAsync(Job(), target))
                .Issues.Count(i => i.Kind == VerifyIssueKind.FileRefUnresolved);

        const string content = "the same bytes, for section twenty-four";
        string a = Path.Combine(source, "a.txt"), b = Path.Combine(source, "b.txt");
        string c = Path.Combine(source, "c.txt"), d = Path.Combine(source, "d.txt");
        await File.WriteAllTextAsync(a, content);
        await File.WriteAllTextAsync(b, content);
        await Backup();
        var recA = await Live(a);
        var recB = await Live(b);
        var plain = recA.IsFileRef ? recB : recA;
        Check(recA.IsFileRef != recB.IsFileRef, "two files of one content: one stored plain, the other a reference to it");

        // Another plain copy of the content recorded, whose file has gone, and
        // first in path order: a resolver must look past it.
        var disc = await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = set.Id, Label = target, SequenceNumber = 9, MediaType = MediaType.Directory,
            Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow,
        });
        await repo.CreateFileRecordAsync(new FileRecord
        {
            DiscId = disc.Id, SourcePath = Path.Combine(root, "elsewhere", "gone.txt"), DiscPath = @"!gone\gone.txt",
            SizeBytes = content.Length, Hash = plain.Hash, SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        });
        Check((await repo.GetActivePlainContentPathsByHashAsync(set.Id, plain.Hash)).FirstOrDefault() == @"!gone\gone.txt"
              && await PlainContent.FirstExistingAsync(repo, set.Id, plain.Hash, target) == plain.DiscPath,
            "the intact copy is found, though a copy whose file has gone is recorded first");
        Check(await BrokenReferences() == 0, "Verify does not call the reference broken");

        var restorer = new RestoreService(repo);
        string restoreTo = Path.Combine(root, "restored24");
        var restored = await restorer.RestoreAsync(
            await restorer.MaterializeSelectionAsync(set.Id, [source], []),
            new Dictionary<string, string> { [source[..1].ToUpperInvariant()] = restoreTo });
        var restoredFiles = Directory.Exists(restoreTo)
            ? Directory.GetFiles(restoreTo, "*.txt", SearchOption.AllDirectories) : [];
        Check(restored.Errors.Count == 0 && restoredFiles.Length == 2
              && restoredFiles.All(f => File.ReadAllText(f) == content),
            $"restore restores the reference from the intact copy ({restoredFiles.Length} restored; {string.Join("; ", restored.Errors)})");

        await File.WriteAllTextAsync(c, content);
        await Backup();
        var recC = await Live(c);
        var manifest = JsonSerializer.Deserialize<FileRefManifest>(await File.ReadAllTextAsync(Path.Combine(target, recC.DiscPath)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check(recC.IsFileRef && manifest?.ContentPath == plain.DiscPath,
            $"a new duplicate is stored as a reference to the intact copy ({manifest?.ContentPath})");

        // Every recorded copy gone: a reference written now would be broken from
        // birth, so the new duplicate gets real bytes, and the references already
        // there resolve to them.
        File.Delete(Path.Combine(target, plain.DiscPath));
        await File.WriteAllTextAsync(d, content);
        await Backup();
        var recD = await Live(d);
        Check(!recD.IsFileRef && File.Exists(Path.Combine(target, recD.DiscPath)),
            "with every copy gone, a new duplicate is stored with its real bytes");
        Check(await BrokenReferences() == 0, "and the references already there resolve to them");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §23. Two sets may not share a backup folder: what counts as the same folder,
    /// finding the set that has one, Cleanup refusing to scan a folder two sets
    /// already share, and the Copy dialog refusing another set's folder.
    /// </summary>
    static async Task SharedBackupFolderAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 23. A backup folder belongs to one set ===");
        JobOptions At(string dest, string? sub = null, string? volume = null) => new()
        {
            TargetDirectory = dest, CreateSubdirectory = sub is not null, SubdirectoryName = sub,
            DestinationVolumeId = volume,
        };
        Check(At(@"E:\B").SharesBackupFolderWith(At(@"e:\b\")), "the same folder, whatever the case and trailing separator");
        Check(!At(@"E:\B").SharesBackupFolderWith(At(@"F:\B")), "two drives' folders of one name, identities unknown, are two folders");
        Check(At(@"E:\B", volume: "V1").SharesBackupFolderWith(At(@"F:\B", volume: "V1")),
            "one volume under two letters is one folder");
        Check(!At(@"E:\B", volume: "V1").SharesBackupFolderWith(At(@"E:\B", volume: "V2")),
            "two volumes are two folders, whatever the stored letters say");
        Check(!At(@"E:\B").SharesBackupFolderWith(At(@"E:\B", "Sub")), "a folder inside another's is not the same folder");
        Check(At(@"E:\B\Sub").SharesBackupFolderWith(At(@"E:\B", "Sub")), "a subfolder that makes them the same is");
        Check(!At(@"E:\B").SharesBackupFolderWith(new JobOptions()), "a set with no destination shares nothing");
        Check(At(@"E:\B ").SharesBackupFolderWith(At(@"E:\B")) && At(@"E:\B.").SharesBackupFolderWith(At(@"E:\B"))
              && At(@"E:\X\..\B").SharesBackupFolderWith(At(@"E:\B")) && At(@"E:\\B").SharesBackupFolderWith(At(@"E:\B")),
            "paths Windows resolves to one folder are one folder: trailing space or dot, '..', doubled separators");
        JobOptions OnVolume(string dest, string subpath) => new()
        {
            TargetDirectory = dest, DestinationVolumeId = "V9", DestinationSubpath = subpath,
        };
        Check(OnVolume(@"F:\B", "B").SharesBackupFolderWith(OnVolume(@"C:\Mounts\Disk\B", "B"))
              && !OnVolume(@"F:\B", "B").SharesBackupFolderWith(OnVolume(@"C:\Mounts\Disk\C", "C")),
            "one volume through a drive letter and through a mount folder is one volume's folder");

        string dir = Path.Combine(root, "cat23");
        Directory.CreateDirectory(dir);
        using var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
        string shared = Path.Combine(dir, "shared");
        Directory.CreateDirectory(Path.Combine(shared, "D", "src"));
        await File.WriteAllTextAsync(Path.Combine(shared, "D", "src", "theirs.txt"), "the other set's file");
        BackupSet NewSet(string name, string dest) => new()
        {
            Name = name, SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = dest },
        };
        var first = await repo.CreateBackupSetAsync(NewSet("first", shared));
        var second = await repo.CreateBackupSetAsync(NewSet("second", shared));
        Check(await BackupFolderOwners.FindOtherAsync(repo, first.Id, first.JobOptions!) == "second"
              && await BackupFolderOwners.FindOtherAsync(repo, 0, At(Path.Combine(dir, "free"))) is null,
            "the set that has a folder is found, and none for a free one");

        // Saved before such sets were refused: Cleanup bans the folder - no purge,
        // scan or reconcile - as for records elsewhere, and says why.
        var vm = new OrphanedDirectoriesViewModel(repo, first);
        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading, 60000);
        Check(vm.IsDestinationUnavailable && !vm.ScanDestinationCommand.CanExecute(null)
              && vm.DestinationUnavailableText.Contains("\"second\" backs up to the same folder", StringComparison.Ordinal),
            $"Cleanup does not work in a folder another set shares, and says why (\"{vm.DestinationUnavailableText}\")");

        // Copy suggests a folder of the copy's own, beside the original's.
        BackupSet Rooted(string dest, string? sub = null) => new() { Name = "Main", JobOptions = At(dest, sub) };
        Check(LithicBackup.ViewModels.MainViewModel.SuggestedCopyFolder(Rooted(@"E:\Backups\Main"), "Copy of Main") == @"E:\Backups\Copy of Main"
              && LithicBackup.ViewModels.MainViewModel.SuggestedCopyFolder(Rooted(@"E:\"), "Copy of Main") == @"E:\Copy of Main"
              && LithicBackup.ViewModels.MainViewModel.SuggestedCopyFolder(Rooted(@"E:\B", "Sub"), "a/b:c") == @"E:\B\a_b_c"
              && LithicBackup.ViewModels.MainViewModel.SuggestedCopyFolder(new BackupSet(), "x") == "",
            "Copy suggests a folder named for the copy beside the original's backup folder");

        // A check that fails says so, rather than blaming anything else.
        var failing = new OrphanedDirectoriesViewModel(
            FailsOn.Wrap(repo, nameof(ICatalogRepository.GetAllBackupSetOptionsAsync)), first);
        failing.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !failing.IsLoading, 60000);
        Check(failing.IsDestinationUnavailable
              && failing.DestinationUnavailableText.StartsWith("Could not check the backup folder", StringComparison.Ordinal),
            $"a folder check that fails leaves the folder unusable, and says so (\"{failing.DestinationUnavailableText}\")");

        // Copy's suggestion follows the copy's name until the destination is edited.
        var follow = new DuplicateBackupSetViewModel("X", name => Path.Combine(@"E:\B", name));
        string firstSuggestion = follow.TargetDirectory;
        follow.Name = "Renamed";
        string renamed = follow.TargetDirectory;
        follow.TargetDirectory = @"E:\Mine";
        follow.Name = "Again";
        Check(firstSuggestion == @"E:\B\Copy of X" && renamed == @"E:\B\Renamed" && follow.TargetDirectory == @"E:\Mine",
            "Copy's suggested folder follows the name, and stops once the destination is edited");

        // Copy: the dialog says so the moment the destination is another set's folder.
        var copy = new DuplicateBackupSetViewModel("first", _ => shared,
            dest => new JobOptions { TargetDirectory = dest }.SharesBackupFolderWith(first.JobOptions!) ? "first" : null);
        Check(copy.IsDestinationInUse && copy.DestinationInUseText.Contains("\"first\"", StringComparison.Ordinal),
            "the Copy dialog refuses the original's folder, which it starts from");
        copy.TargetDirectory = Path.Combine(dir, "copy");
        Check(!copy.IsDestinationInUse && copy.DestinationInUseText.Length == 0, "and accepts a folder of its own");
        copy.TargetDirectory = "";
        Check(!copy.IsDestinationInUse, "or none, to choose later");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §22. Records written to a folder other than the backup folder (a subfolder
    /// changed, a destination retyped, the old Worker beside the subfolder) are
    /// found - ignoring drive letters and retired records - and Cleanup will not
    /// purge such a set. And Cleanup's destination scan leaves another set's
    /// backup folder inside this one alone, instead of listing it all as untracked.
    /// </summary>
    static async Task RecordsElsewhereAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 22. Records written elsewhere stop a purge; another set's folder inside is not scanned ===");
        string dir = Path.Combine(root, "cat22");
        Directory.CreateDirectory(dir);
        using var repo = new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));

        // --- the catalog check ---
        var probe = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "probe", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        async Task<DiscRecord> Disc(int setId, string label) => await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = setId, Label = label, SequenceNumber = 1, MediaType = MediaType.Directory,
            Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow,
        });
        async Task Record(DiscRecord d, string discPath, bool deleted = false) => await repo.CreateFileRecordAsync(new FileRecord
        {
            DiscId = d.Id, SourcePath = @"D:\src\" + Path.GetFileName(discPath), DiscPath = discPath, SizeBytes = 1,
            Hash = "", IsDeleted = deleted, SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        });
        await Record(await Disc(probe.Id, @"H:\lithicbackup"), @"D\src\a.txt");
        await Record(await Disc(probe.Id, @"F:\lithicbackup\"), @"D\src\b.txt");       // same folder, old letter
        await Record(await Disc(probe.Id, "i:/lithicbackup"), @"D\src\c.txt");         // and other separators
        await Record(await Disc(probe.Id, @"H:\lithicbackup\Sub"), @"D\src\d.txt", deleted: true); // only retired
        Check(await repo.FindLiveRecordsOutsideAsync(probe.Id, @"H:\lithicbackup") is null,
            "a drive moved to another letter, other separators and retired records are not 'elsewhere'");
        // Case beyond ASCII, as JobOptions.SameFolderOnAnyDrive compares it (and
        // the editor's question with it): SQLite's NOCASE would not fold it.
        var umlaut = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "umlaut", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        await Record(await Disc(umlaut.Id, @"H:\Sicherung-Ärger"), @"D\src\u.txt");
        Check(await repo.FindLiveRecordsOutsideAsync(umlaut.Id, @"H:\sicherung-ärger") is null,
            "a folder whose name differs only in the case of a non-ASCII letter is the same folder");
        Check(await repo.FindLiveRecordsOutsideAsync(probe.Id, @"H:\lithicbackup\Sub") is { } other
              && other.StartsWith(@"H:\lithicbackup", StringComparison.OrdinalIgnoreCase),
            "with the subfolder as the backup folder, the records beside it are found");
        await Record(await Disc(probe.Id, @"H:\lithicbackup\Sub"), @"D\src\e.txt");
        Check(await repo.FindLiveRecordsOutsideAsync(probe.Id, @"G:\lithicbackup") is not null
              && await repo.FindLiveRecordsOutsideAsync(probe.Id, @"G:\elsewhere") is not null,
            "a live record in the subfolder is elsewhere from the folder above, and anything is from another folder");

        // --- Change Destination relabels what the check counts as the old folders ---
        // The backup folder H:\lithicbackup\Sub moves to J:\moved\Sub; the folder
        // above it (an older Worker's runs) goes along to J:\moved.
        var relabelled = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "relabelled", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        await Record(await Disc(relabelled.Id, @"H:\lithicbackup\Sub"), @"D\src\g.txt");
        await Record(await Disc(relabelled.Id, @"F:\lithicbackup\Sub\"), @"D\src\h.txt");   // before a letter change
        await Record(await Disc(relabelled.Id, @"F:\lithicbackup"), @"D\src\i.txt");        // an older Worker's run
        await Record(await Disc(relabelled.Id, @"Z:\unrelated"), @"D\src\j.txt");
        await LithicBackup.ViewModels.MainViewModel.RelabelDiscsAfterMoveAsync(repo, relabelled.Id,
            @"H:\lithicbackup\Sub", @"H:\lithicbackup", @"J:\moved\Sub", @"J:\moved");
        var labels = (await repo.GetDiscsForBackupSetAsync(relabelled.Id)).Select(d => d.Label).ToList();
        Check(labels.SequenceEqual([@"J:\moved\Sub", @"J:\moved\Sub", @"J:\moved", @"Z:\unrelated"]),
            $"Change Destination relabels the old folders on any drive letter, and nothing else ({string.Join(", ", labels)})");
        // A set like the author's: one folder, recorded under two drive letters.
        var twoLetters = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "two letters", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        await Record(await Disc(twoLetters.Id, @"H:\lithicbackup"), @"D\src\k.txt");
        await Record(await Disc(twoLetters.Id, @"F:\lithicbackup"), @"D\src\l.txt");
        await LithicBackup.ViewModels.MainViewModel.RelabelDiscsAfterMoveAsync(repo, twoLetters.Id,
            @"H:\lithicbackup", @"H:\lithicbackup", @"J:\moved", @"J:\moved");
        Check(await repo.FindLiveRecordsOutsideAsync(twoLetters.Id, @"J:\moved") is null,
            "after it, a set recorded under two drive letters of one folder has nothing elsewhere");

        // All or none: a relabel that fails part-way leaves every label as it was,
        // rather than the set half moved and refused for records elsewhere.
        var atomic = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "atomic", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        for (int i = 0; i < 3; i++)
            await Record(await Disc(atomic.Id, @"H:\a"), $@"D\src\atomic{i}.txt");
        bool relabelThrew = false;
        try
        {
            await LithicBackup.ViewModels.MainViewModel.RelabelDiscsAfterMoveAsync(SecondDiscUpdateFails.Wrap(repo),
                atomic.Id, @"H:\a", @"H:\a", @"J:\b", @"J:\b");
        }
        catch (IOException)
        {
            relabelThrew = true;
        }
        var atomicLabels = (await repo.GetDiscsForBackupSetAsync(atomic.Id)).Select(d => d.Label).ToList();
        Check(relabelThrew && atomicLabels.All(l => l == @"H:\a"),
            $"a relabel that fails part-way changes no label ({string.Join(", ", atomicLabels)})");

        // --- Cleanup refuses to purge such a set ---
        string dest = Path.Combine(dir, "dst");
        Directory.CreateDirectory(dest);
        var moved = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "moved", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = dest, CreateSubdirectory = true, SubdirectoryName = "Sub" },
        });
        Directory.CreateDirectory(Path.Combine(dest, "Sub"));
        await Record(await Disc(moved.Id, dest), @"D\src\f.txt");   // written beside the subfolder
        var vm = new OrphanedDirectoriesViewModel(repo, moved);
        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading, 60000);
        Check(vm.IsDestinationUnavailable && vm.DestinationUnavailableText.Contains("recorded in", StringComparison.Ordinal),
            $"Cleanup treats the backup folder as unusable, and says why (\"{vm.DestinationUnavailableText}\")");
        Check(!vm.ScanDestinationCommand.CanExecute(null), "and offers no destination scan");
        Check(vm.DestinationUnavailableText.Contains("turn it off again", StringComparison.Ordinal)
              && vm.DestinationUnavailableText.Contains("seed it from its backup folder", StringComparison.Ordinal),
            "records just above the subfolder get the way out that fits them: the setting, or a fresh start");
        Check(RecordsElsewhere.Advice(@"H:\old", @"H:\new").Contains("Change Destination", StringComparison.Ordinal)
              && !RecordsElsewhere.Advice(@"H:\old", @"H:\new").Contains("turn it off", StringComparison.Ordinal),
            "records in an unrelated folder get the general one");

        // A subfolder the first backup has not made yet: the drive is there, so
        // the banner must not say it is not connected.
        var unmade = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "unmade", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = dest, CreateSubdirectory = true, SubdirectoryName = "NotYet" },
        });
        var unmadeVm = new OrphanedDirectoriesViewModel(repo, unmade);
        unmadeVm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !unmadeVm.IsLoading, 60000);
        Check(unmadeVm.IsDestinationUnavailable
              && unmadeVm.DestinationUnavailableText.Contains("does not exist", StringComparison.Ordinal)
              && !unmadeVm.DestinationUnavailableText.Contains("not connected", StringComparison.Ordinal),
            $"a backup folder not made yet is said to be missing, not unconnected (\"{unmadeVm.DestinationUnavailableText}\")");

        // --- another set's backup folder inside this one is not scanned ---
        string shared = Path.Combine(dir, "shared");
        var outer = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "outer", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = shared },
        });
        await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "inner", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            // Stored under another drive letter: the drive changed letters, and the
            // outer set has run since, the inner one not yet.
            JobOptions = new JobOptions { TargetDirectory = "Q" + shared[1..], CreateSubdirectory = true, SubdirectoryName = "Inner" },
        });
        string innerFile = Path.Combine(shared, "Inner", "D", "src", "inner.txt");
        string strayFile = Path.Combine(shared, "D", "src", "stray.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(innerFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(strayFile)!);
        await File.WriteAllTextAsync(innerFile, "the inner set's copy");
        await File.WriteAllTextAsync(strayFile, "untracked in outer");
        var outerVm = new OrphanedDirectoriesViewModel(repo, outer);
        outerVm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !outerVm.IsLoading, 60000);
        outerVm.ScanDestinationCommand.Execute(null);
        await WaitFor(() => outerVm.IsScanningDestination, 3000);
        await WaitFor(() => !outerVm.IsScanningDestination, 60000);
        var untracked = outerVm.Items.Where(i => i.Reason == OrphanedReason.UntrackedFile)
            .SelectMany(i => i.DiscFilePaths ?? []).ToList();
        Check(untracked.Any(p => p.EndsWith("stray.txt", StringComparison.OrdinalIgnoreCase)),
            $"the outer set's own untracked file is found ({string.Join(", ", untracked)})");
        Check(!untracked.Any(p => p.Contains(@"Inner\", StringComparison.OrdinalIgnoreCase)),
            "the inner set's backup folder is not scanned, so none of its files is offered for deletion");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §21. With "create subdirectory", Cleanup works in the subfolder, where the
    /// set's backup lives. It used the destination itself: it looked for, and
    /// deleted, the set's stored files there - another set's copies, when one
    /// backs up to that folder - and missed the set's own.
    /// </summary>
    static async Task CleanupInSubfolderAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 21. Cleanup works in the backup's subfolder, and never beside it ===");
        string source = Path.Combine(root, "src21"), dest = Path.Combine(root, "dst21");
        string sub = Path.Combine(dest, "SetA");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(root, "cat21"));
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat21", "catalog.db"));
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var keepAll = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = null }] };
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "subfolder", SourceRoots = [source], SourceSelections = Sel(),
            CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions
            {
                TargetDirectory = dest, CreateSubdirectory = true, SubdirectoryName = "SetA",
                TierSets = [keepAll], RetentionTiers = keepAll.Tiers,
            },
        });
        string root21 = set.JobOptions!.BackupRoot()!;
        Check(string.Equals(root21, sub, StringComparison.OrdinalIgnoreCase), $"BackupRoot is the subfolder ({root21})");

        string file = Path.Combine(source, "x.txt");
        string rel = file[Path.GetPathRoot(file)!.Length..];
        string drive = file[..1];
        for (int v = 1; v <= 3; v++)
        {
            await File.WriteAllTextAsync(file, $"x, version {v}" + new string('.', v));
            var r = await svc.ExecuteAsync(new BackupJob
            {
                BackupSetId = set.Id, Sources = Sel(), TargetDirectory = sub, ZipMode = ZipMode.None,
                TierSets = [keepAll], RetentionTiers = keepAll.Tiers,
            }, sub, keepAll.Tiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed");
        }
        string v1 = Path.Combine(sub, drive + "_prev", rel + ".v1");
        // Another set's copy at the same relative path, directly in the destination.
        string decoy = Path.Combine(dest, drive + "_prev", rel + ".v1");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        await File.WriteAllTextAsync(decoy, "another set's version");
        Check(File.Exists(v1), "set-up: x.txt.v1 is in the subfolder");

        var s = (await repo.GetBackupSetAsync(set.Id))!;
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        s.JobOptions!.TierSets = [keepOne];
        s.JobOptions.RetentionTiers = keepOne.Tiers;
        await repo.UpdateBackupSetAsync(s);

        var vm = new OrphanedDirectoriesViewModel(repo, s);
        Check(!vm.IsDestinationUnavailable, "Cleanup finds the backup's folder");
        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading && vm.Categories.Count > 0, 60000);
        bool listed = vm.Items.Any(i => i.Reason == OrphanedReason.ExcessVersion);
        Check(listed, "x.txt's older earlier version is excess under keep-1");
        if (!listed) return;
        foreach (var c in vm.Categories)
            foreach (var n in c.RootNodes)
                n.IsChecked = c.Reason == OrphanedReason.ExcessVersion;
        vm.PurgeSelectedCommand.Execute(null);
        await WaitFor(() => vm.IsPurging, 3000);
        await WaitFor(() => !vm.IsPurging, 60000);
        Check(!File.Exists(v1), $"the purge deletes it in the subfolder (\"{vm.SummaryText}\")");
        Check(File.Exists(decoy) && File.ReadAllText(decoy) == "another set's version",
            "and never the file at the same path directly in the destination");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §19. GetActivePreviousVersionsAsync: exactly the live earlier versions -
    /// the first path segment ending in _prev, either separator - read through an
    /// index of their own that the first call builds, so a whole-set retention
    /// pass no longer reads every record to find them. GetLiveDiscPathsAsync:
    /// only live ids, with their stored paths.
    /// </summary>
    static async Task PreviousVersionsIndexAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 19. Earlier versions are read through an index of their own ===");
        string dbPath = Path.Combine(root, "prev-index", "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var repo = new SqliteCatalogRepository(dbPath);
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "prev-index", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
        });
        var disc = await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = set.Id, Label = @"X:\dst", SequenceNumber = 1, MediaType = MediaType.Directory,
            Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow,
        });
        async Task<FileRecord> Add(string source, string discPath, bool deleted = false) =>
            await repo.CreateFileRecordAsync(new FileRecord
            {
                DiscId = disc.Id, SourcePath = source, DiscPath = discPath, SizeBytes = 1, Hash = "",
                IsDeleted = deleted, SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
            });
        var prev = await Add(@"D:\src\a.txt", @"D_prev\src\a.txt.v1");
        var prevSlash = await Add(@"D:\src\b.txt", "D_prev/src/b.txt.v1");
        var current = await Add(@"D:\src\a.txt", @"D\src\a.txt");
        var deep = await Add(@"D:\src\old_prev\c.txt", @"D\src\old_prev\c.txt");
        var preview = await Add(@"D:\preview\d.txt", @"D\preview\d.txt");
        var retired = await Add(@"D:\src\e.txt", @"D_prev\src\e.txt.v1", deleted: true);

        string setDb = Path.Combine(Path.GetDirectoryName(dbPath)!, "sets", $"set-{set.Id}.db");
        bool IndexExists()
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={setDb};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'IX_Files_ActivePrevious')";
            return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
        }
        Check(!IndexExists(), "a set database does not build the index when it opens");

        int indexing = 0;
        var found = await repo.GetActivePreviousVersionsAsync(set.Id, onIndexing: () => indexing++);
        var ids = found.Select(f => f.Id).ToHashSet();
        Check(ids.SetEquals([prev.Id, prevSlash.Id]),
            $"exactly the live earlier versions, either separator (got {string.Join(", ", found.Select(f => f.DiscPath))})");
        Check(found.All(f => f.SizeBytes == 1 && f.SourcePath.Length > 0), "as whole records");
        Check(indexing == 1 && IndexExists(), "the first read builds the index, and says so once");
        await repo.GetActivePreviousVersionsAsync(set.Id, onIndexing: () => indexing++);
        Check(indexing == 1, "later reads use it");

        // The planner must be able to serve the read from the index: the
        // predicate has to be the index's own, word for word.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={setDb};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT * FROM Files
                WHERE IsDeleted = 0 AND instr(lower(DiscPath), '_prev') > 0
                  AND EXISTS (SELECT 1 FROM Discs d WHERE d.Id = Files.DiscId AND d.BackupSetId = 1)
                ORDER BY SourcePath
                """;
            var plan = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    plan.Add(r.GetString(3));
            Check(plan.Any(p => p.Contains("IX_Files_ActivePrevious", StringComparison.Ordinal)),
                $"the read is planned through the index ({string.Join(" | ", plan)})");
        }

        // Stored paths still in use, earlier versions found through the index: a
        // retired record's path is free, a live one's is not, whichever form.
        var inUse = await repo.FindDiscPathsInUseAsync(set.Id,
            [prev.DiscPath, retired.DiscPath, current.DiscPath, @"D\src\nothing.txt"]);
        Check(inUse.Count == 2 && inUse.Contains(prev.DiscPath) && inUse.Contains(current.DiscPath),
            $"FindDiscPathsInUseAsync: earlier versions and current copies in use, a retired one free ({string.Join(", ", inUse)})");

        // An index built inside a transaction that is then rolled back is gone;
        // the next read must build it again, not believe it is there.
        // A catalog of its own: set databases are named by set id, so a second
        // catalog beside the first would share its set-1.db, index and all.
        string rollbackDir = Path.Combine(root, "prev-index-rollback");
        Directory.CreateDirectory(rollbackDir);
        using (var repo2 = new SqliteCatalogRepository(Path.Combine(rollbackDir, "catalog.db")))
        {
            var set2 = await repo2.CreateBackupSetAsync(new BackupSet
            {
                Name = "rollback", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow,
                DefaultMediaType = MediaType.Directory,
            });
            int built = 0;
            var tx = await repo2.BeginTransactionAsync(set2.Id);
            await repo2.GetActivePreviousVersionsAsync(set2.Id, onIndexing: () => built++);
            // A second read in the same transaction sees the index it built, still
            // uncommitted, and must not take it for one that stays.
            await repo2.GetActivePreviousVersionsAsync(set2.Id, onIndexing: () => built++);
            tx.Dispose(); // no Complete: rolled back, index with it
            await repo2.GetActivePreviousVersionsAsync(set2.Id, onIndexing: () => built++);
            Check(built == 2, $"an index built inside a rolled-back transaction is built again by the next read, "
                              + $"however often it was read there ({built} builds)");

            // The in-use check, asked only about earlier versions' paths, reads
            // them through the index, building it first: a later read builds nothing.
            var set3 = await repo2.CreateBackupSetAsync(new BackupSet
            {
                Name = "in-use", SourceRoots = [@"D:\src"], CreatedUtc = DateTime.UtcNow,
                DefaultMediaType = MediaType.Directory,
            });
            var tx3 = await repo2.BeginTransactionAsync(set3.Id);
            await repo2.FindDiscPathsInUseAsync(set3.Id, [@"D_prev\src\x.txt.v1"]);
            tx3.Complete();
            tx3.Dispose();
            int built3 = 0;
            await repo2.GetActivePreviousVersionsAsync(set3.Id, onIndexing: () => built3++);
            Check(built3 == 0, $"the in-use check of earlier versions' paths builds their index ({built3} later builds)");
        }

        var livePaths = await repo.GetLiveDiscPathsAsync(set.Id, [prev.Id, current.Id, retired.Id, 999_999]);
        Check(livePaths.Count == 2 && livePaths[prev.Id] == prev.DiscPath && livePaths[current.Id] == current.DiscPath,
            "GetLiveDiscPathsAsync: live ids with their stored paths; retired and unknown ids left out");
        _ = deep; _ = preview;
    }

    /// <summary>
    /// §20. The one tier walk (VersionRetentionService.SelectExpired) the backup's
    /// retention pass, Cleanup and the editor's offer share; and FindVersionsNotKept,
    /// which adds an empty Default's versions and leaves an excluded file's.
    /// </summary>
    static void TierWalk()
    {
        Console.WriteLine();
        Console.WriteLine("=== 20. One tier walk for every caller ===");
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        long id = 0;
        FileRecord V(string path, double daysOld) => new()
        {
            Id = ++id, SourcePath = path, DiscPath = @"D_prev\" + path[3..] + ".v" + id,
            BackedUpUtc = now.AddDays(-daysOld),
        };
        // The stock tiers: all under 10 days, 3 under a year, 1 older.
        var tiers = VersionRetentionService.DefaultTiers;
        var a = new[] { V(@"D:\a", 1), V(@"D:\a", 2), V(@"D:\a", 20), V(@"D:\a", 30), V(@"D:\a", 40), V(@"D:\a", 50),
                        V(@"D:\a", 400), V(@"D:\a", 500) };
        var expired = VersionRetentionService.SelectExpired(a, tiers, now).Select(v => v.BackedUpUtc).ToHashSet();
        Check(expired.SetEquals([now.AddDays(-50), now.AddDays(-500)]),
            "under 10 days all kept; of 20-50 days the newest 3 kept; over a year the newest 1 kept");
        var lone = new[] { V(@"D:\b", 900) };
        Check(VersionRetentionService.SelectExpired(lone, [new() { MaxAge = null, MaxVersions = 0 }], now).Count == 0,
            "a file's newest earlier version is always kept, even by a tier that keeps none");
        Check(VersionRetentionService.SelectExpired(a, [], now).Count == 0, "no tiers expire nothing");

        // Two versions backed up at the same moment: the lower version expires,
        // whatever order they come in.
        var t1 = new FileRecord { Id = 50, SourcePath = @"D:\t", DiscPath = @"D_prev\t.v3", Version = 3, BackedUpUtc = now.AddDays(-1) };
        var t2 = new FileRecord { Id = 40, SourcePath = @"D:\t", DiscPath = @"D_prev\t.v2", Version = 2, BackedUpUtc = now.AddDays(-1) };
        var keepOne = new List<VersionRetentionTier> { new() { MaxVersions = 1 } };
        var e1 = VersionRetentionService.SelectExpired([t1, t2], keepOne, now);
        var e2 = VersionRetentionService.SelectExpired([t2, t1], keepOne, now);
        Check(e1.Count == 1 && e1[0].Version == 2 && e2.Count == 1 && e2[0].Version == 2,
            "a tie in backup time keeps the higher version, in any input order");

        var opts = new JobOptions
        {
            TierSets =
            [
                new VersionTierSet { Name = "Default", Tiers = [] },
                new VersionTierSet { Name = "Logs", Tiers = [new() { MaxVersions = 1 }], FilePatterns = ["*.log"] },
                new VersionTierSet { Name = "None", Tiers = [], FilePatterns = ["*.tmp"] },
            ],
        };
        var all = new[] { V(@"D:\x.bin", 1), V(@"D:\x.bin", 2), V(@"D:\y.log", 1), V(@"D:\y.log", 2), V(@"D:\z.tmp", 1) };
        var notKept = VersionRetentionService.FindVersionsNotKept(all, opts, now).Select(v => v.SourcePath + "@" + v.BackedUpUtc.Day).ToList();
        Check(notKept.Count == 3 && notKept.Count(p => p.StartsWith(@"D:\x.bin")) == 2 && notKept.Count(p => p.StartsWith(@"D:\y.log")) == 1,
            $"FindVersionsNotKept: every version of an empty Default's file, the tiers' excess of the rest, none of an excluded file's ({string.Join(", ", notKept)})");
        opts.TierSets.RemoveAt(0);
        Check(VersionRetentionService.FindVersionsNotKept(all, opts, now).All(v => !v.SourcePath.EndsWith(".bin")),
            "with no Default in the list, unmatched files' versions are not offered");
    }

    /// <summary>
    /// §18. RetireRecordsKeepingNeededCopiesAsync, which removing a folder's
    /// backups after an edit, Verify's repair and the Worker after a rename now
    /// retire through: none of them checked whether a record held the last copy
    /// another file's .fileref needed.
    /// </summary>
    static async Task GuardedRetireAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 18. Retiring records keeps the copy a live reference needs ===");
        Directory.CreateDirectory(Path.Combine(root, "cat18"));
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat18", "catalog.db"));
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "guarded-retire", SourceRoots = [@"C:\x"], CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory, JobOptions = new JobOptions { TargetDirectory = Path.Combine(root, "dst18") },
        });
        var disc = await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = set.Id, Label = "d", SequenceNumber = 1, MediaType = MediaType.Directory,
            FilesystemType = FilesystemType.UDF, Status = BurnSessionStatus.Completed,
        });
        async Task<FileRecord> Add(string name, string hash, bool isRef = false) => await repo.CreateFileRecordAsync(new FileRecord
        {
            DiscId = disc.Id, SourcePath = $@"C:\x\{name}", DiscPath = $@"C\x\{name}" + (isRef ? ".fileref" : ""),
            SizeBytes = 5, Hash = hash, IsFileRef = isRef, Version = 1,
            SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        });
        async Task<bool> Live(FileRecord r) => !(await repo.GetFileRecordsByPathAsync(set.Id, r.SourcePath)).Single(x => x.Id == r.Id).IsDeleted;

        var anchor = await Add("a.txt", "aaaa");
        var reference = await Add("f.txt", "aaaa", isRef: true);
        var other = await Add("o.txt", "bbbb");
        var kept = await repo.RetireRecordsKeepingNeededCopiesAsync(set.Id, [anchor.Id, other.Id]);
        Check(kept.SetEquals([anchor.Id]) && await Live(anchor) && !await Live(other),
            "the last copy of content a live .fileref needs stays live, and the rest are retired");

        var anchor2 = await Add("a2.txt", "cccc");
        var reference2 = await Add("f2.txt", "cccc", isRef: true);
        var none = await repo.RetireRecordsKeepingNeededCopiesAsync(set.Id, [anchor2.Id, reference2.Id]);
        Check(none.Count == 0 && !await Live(anchor2) && !await Live(reference2),
            "retired together with the only reference to it, the copy goes too");

        var broken = await Add("b.txt", "dddd");
        await Add("g.txt", "dddd", isRef: true);
        var notKeepable = await repo.RetireRecordsKeepingNeededCopiesAsync(set.Id, [broken.Id], keepableIds: []);
        Check(notKeepable.Count == 0 && !await Live(broken), "a copy the caller does not allow to be kept is retired");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §17. Every copy of content a .fileref needs is in files deleted from disk,
    /// and only that category is cleaned. The purge's guard considered excess
    /// versions only, so it checked nothing, and the last copy went; and a copy it
    /// did keep was then retired with the rest of its file's records by the
    /// category purged by path. The copy is kept now, live, with its file.
    /// </summary>
    static async Task KeptCopyAcrossCategoriesAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 17. A kept copy stays live when its own file is purged by path ===");
        string source = Path.Combine(root, "src17"), target = Path.Combine(root, "dst17");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "cat17"));
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat17", "catalog.db"));
        var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "kept-copy", SourceRoots = [source], SourceSelections = Sel(),
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
        await Backup(dedup: false);                       // h.bin.v1: plain, content H
        await File.WriteAllTextAsync(k, "content H");
        await Backup(dedup: false);                       // k.bin: a second plain copy of H
        await File.WriteAllTextAsync(m, "content H");
        await Backup(dedup: true);                        // m.bin: a .fileref to H
        string hHash = (await repo.GetLatestVersionInfoAsync(set.Id))[k].Hash;

        var s = (await repo.GetBackupSetAsync(set.Id))!;
        s.JobOptions!.TierSets = [new VersionTierSet { Name = "Default", Tiers = [] }];
        s.JobOptions.RetentionTiers = [];
        await repo.UpdateBackupSetAsync(s);
        File.Delete(k);
        File.Delete(h);                                   // h.bin's own records go by path too

        var vm = new OrphanedDirectoriesViewModel(repo, s);
        vm.ScanCatalogCommand.Execute(null);
        await WaitFor(() => !vm.IsLoading && vm.Categories.Count > 0, 60000);
        // h.bin's earlier version is listed with h.bin, under deleted from disk: no
        // excess version is involved, which is what left the purge unguarded.
        var deletedPaths = vm.Items.Where(i => i.Reason == OrphanedReason.DeletedFromDisk)
            .SelectMany(i => i.MatchingSourcePaths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool listed = deletedPaths.Contains(h) && deletedPaths.Contains(k);
        Check(listed, "h.bin and k.bin, both copies of H, are listed as deleted from disk");
        if (!listed) return;
        foreach (var c in vm.Categories)
            foreach (var n in c.RootNodes)
                n.IsChecked = c.Reason == OrphanedReason.DeletedFromDisk;
        vm.PurgeSelectedCommand.Execute(null);
        await WaitFor(() => vm.IsPurging, 3000);
        await WaitFor(() => !vm.IsPurging, 60000);
        string? anchor = (await repo.GetActivePlainContentPathsByHashAsync(set.Id, hHash)).FirstOrDefault();
        Check(vm.SummaryText.Contains("kept 1", StringComparison.Ordinal), $"the purge keeps one copy for m.bin (\"{vm.SummaryText}\")");
        Check(anchor is not null && File.Exists(Path.Combine(target, anchor))
              && File.ReadAllText(Path.Combine(target, anchor)) == "content H",
            $"and that copy is still live, so m.bin's content resolves ({anchor ?? "no live copy"})");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// §16. A file that keeps no history is overwritten in place, and the records
    /// of the copy it replaces retired. Only the current copy's: an earlier
    /// version that happens to be the newest live record is not being replaced.
    /// And all of them: a duplicate record of the copy (a seed run twice) is not
    /// another copy of its content, here or in Cleanup's guard.
    /// </summary>
    static async Task InPlaceOverwriteAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 16. Overwriting in place retires the current copy's records - all of them, and nothing else ===");
        string source = Path.Combine(root, "src16"), target = Path.Combine(root, "dst16");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "cat16"));
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat16", "catalog.db"));
        var keepOne = new VersionTierSet { Name = "Default", Tiers = [new() { MaxAge = null, MaxVersions = 1 }] };
        List<SourceSelection> Sel() =>
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true, AutoIncludeNewSubdirectories = true }];
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "in-place", SourceRoots = [source], SourceSelections = Sel(), CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = target, EnableFileDeduplication = true, TierSets = [keepOne], RetentionTiers = keepOne.Tiers },
        });
        async Task Backup()
        {
            var svc = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
            var opts = (await repo.GetBackupSetAsync(set.Id))!.JobOptions!;
            var job = new BackupJob
            {
                BackupSetId = set.Id, Sources = Sel(), EnableFileDeduplication = true, TargetDirectory = target,
                ZipMode = ZipMode.None, TierSets = opts.TierSets, RetentionTiers = opts.RetentionTiers,
            };
            var tiers = opts.RetentionTiers.Count > 0 ? opts.RetentionTiers : VersionRetentionService.DefaultTiers;
            var r = await svc.ExecuteAsync(job, target, tiers, null, CancellationToken.None);
            if (!r.Success) throw new Exception("backup failed: " + string.Join("; ", r.FailedFiles.Select(f => f.Error)));
        }
        async Task EmptyDefault()
        {
            var s = (await repo.GetBackupSetAsync(set.Id))!;
            s.JobOptions!.TierSets = [new VersionTierSet { Name = "Default", Tiers = [] }];
            s.JobOptions.RetentionTiers = [];
            await repo.UpdateBackupSetAsync(s);
        }

        // (a) The newest live record is an earlier version: the current copy went
        //     missing and its record was pruned.
        string p = Path.Combine(source, "p.txt");
        await File.WriteAllTextAsync(p, "p one");
        await Backup();
        await File.WriteAllTextAsync(p, "p two, longer");
        await Backup();
        var pRows = (await repo.GetFileRecordsByPathAsync(set.Id, p)).Where(r => !r.IsDeleted).OrderBy(r => r.Version).ToList();
        Check(pRows.Count == 2, $"p.txt has an earlier version and a current copy ({pRows.Count} live)");
        if (pRows.Count != 2) return;
        File.Delete(Path.Combine(target, pRows[1].DiscPath));
        await repo.MarkFileRecordsDeletedByIdsAsync(set.Id, [pRows[1].Id]);
        await EmptyDefault();
        await File.WriteAllTextAsync(p, "p three, with no history now");
        await Backup();
        var v1 = (await repo.GetFileRecordsByPathAsync(set.Id, p)).Single(r => r.Id == pRows[0].Id);
        Check(!v1.IsDeleted && File.Exists(Path.Combine(target, v1.DiscPath))
              && File.ReadAllText(Path.Combine(target, v1.DiscPath)) == "p one",
            "an earlier version that was the newest live record is left alone (it used to be deleted)");

        // (b) Two live records of one current copy, whose content a .fileref needs.
        string q = Path.Combine(source, "q.txt"), r = Path.Combine(source, "r.txt");
        await File.WriteAllTextAsync(q, "q and r share this");
        await File.WriteAllTextAsync(r, "q and r share this");
        await Backup();
        var heads = await repo.GetLatestVersionInfoAsync(set.Id);
        string plainOne = heads[q].IsFileRef ? r : q, refOne = heads[q].IsFileRef ? q : r;
        string shared = heads[plainOne].Hash;
        var original = (await repo.GetFileRecordsByPathAsync(set.Id, plainOne)).Single(x => !x.IsDeleted);
        var twin = await repo.CreateFileRecordAsync(new FileRecord
        {
            DiscId = original.DiscId, SourcePath = original.SourcePath, DiscPath = original.DiscPath,
            SizeBytes = original.SizeBytes, Hash = original.Hash, Version = original.Version,
            SourceLastWriteUtc = original.SourceLastWriteUtc, BackedUpUtc = original.BackedUpUtc,
        });
        await File.WriteAllTextAsync(plainOne, "the plain one changed");
        await Backup();
        var plainRows = await repo.GetFileRecordsByPathAsync(set.Id, plainOne);
        Check(plainRows.Where(x => x.Id == original.Id || x.Id == twin.Id).All(x => x.IsDeleted),
            "both records of the replaced copy are retired, the duplicate too");
        string? home = (await repo.GetActivePlainContentPathsByHashAsync(set.Id, shared)).FirstOrDefault();
        Check(home is not null && File.Exists(Path.Combine(target, home))
              && File.ReadAllText(Path.Combine(target, home)) == "q and r share this",
            $"and the .fileref that needed its content was given it, the duplicate not counting as a copy ({home ?? "none"})");

        // (c) Cleanup's guard, on its own: a duplicate at the candidate's path is
        //     not a surviving copy.
        var disc = await repo.CreateDiscAsync(new DiscRecord
        {
            BackupSetId = set.Id, Label = target, SequenceNumber = 9, MediaType = MediaType.Directory,
            FilesystemType = FilesystemType.UDF, Status = BurnSessionStatus.Completed,
        });
        FileRecord Row(string disc_, bool isRef) => new()
        {
            DiscId = disc.Id, SourcePath = Path.Combine(source, isRef ? "needs.txt" : "anchor.txt"),
            DiscPath = disc_, SizeBytes = 5, Hash = "cafe", IsFileRef = isRef, Version = 1,
            SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        };
        var anchorRow = await repo.CreateFileRecordAsync(Row(@"C_prev\x\anchor.txt.v1", false));
        await repo.CreateFileRecordAsync(Row(@"C_prev\x\anchor.txt.v1", false));       // its duplicate
        await repo.CreateFileRecordAsync(Row(@"C\x\needs.txt.fileref", true));
        var needed = await repo.GetNeededContentCopiesAsync(set.Id, [anchorRow.Id]);
        Check(needed.Contains(anchorRow.Id), "Cleanup's guard keeps a copy whose only 'other copy' is its own duplicate");

        // (d) ... nor a stale row: an older live row at another file's current
        //     path, whose bytes a newer row of that file has since replaced.
        FileRecord At(string sourceName, string discPath, string hash, int version, bool isRef = false) => new()
        {
            DiscId = disc.Id, SourcePath = Path.Combine(source, sourceName), DiscPath = discPath,
            SizeBytes = 5, Hash = hash, IsFileRef = isRef, Version = version,
            SourceLastWriteUtc = DateTime.UtcNow, BackedUpUtc = DateTime.UtcNow,
        };
        await repo.CreateFileRecordAsync(At("x1.txt", @"C\x\x1.txt", "beef", 1));           // stale
        await repo.CreateFileRecordAsync(At("x1.txt", @"C\x\x1.txt", "f00d", 2));           // what is there now
        await repo.CreateFileRecordAsync(At("y.txt", @"C\x\y.txt.fileref", "beef", 1, isRef: true));
        var lastCopy = await repo.CreateFileRecordAsync(At("z.txt", @"C_prev\x\z.txt.v1", "beef", 1));
        var neededToo = await repo.GetNeededContentCopiesAsync(set.Id, [lastCopy.Id]);
        Check(neededToo.Contains(lastCopy.Id), "nor a stale row whose bytes a newer row of its file replaced");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

        // Cleanup, with the anchor's hash gone again (as in a set seeded without hashing).
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
        string? home = (await repo.GetActivePlainContentPathsByHashAsync(set.Id, sharedHash)).FirstOrDefault();
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
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat13", "catalog.db"));
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
        using var repo = new SqliteCatalogRepository(Path.Combine(catDir, "catalog.db"));
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
        string restored = Path.Combine(source, "restored.txt");
        await File.WriteAllTextAsync(back, "comes back, first content");
        await File.WriteAllTextAsync(gone, "gone for good");
        await File.WriteAllTextAsync(keepMe, "deleted, but unticked in Cleanup");
        await File.WriteAllTextAsync(gone2, "purged in the second cleanup");
        await File.WriteAllTextAsync(restored, "deleted, then restored unchanged");
        var r0 = await svc.ExecuteAsync(Job(), target, keepOne.Tiers, null, CancellationToken.None);
        Check(r0.Success, "the set is backed up");
        var restoredWritten = File.GetLastWriteTimeUtc(restored);
        foreach (var f in new[] { back, gone, keepMe, gone2, restored })
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
        Check(listedDeleted.SetEquals([back, gone, keepMe, gone2, restored]),
            $"the scan lists the five deleted files (got {string.Join(", ", listedDeleted.Select(Path.GetFileName))})");
        var listedStrays = vm.Items.Where(i => i.Reason == OrphanedReason.UntrackedFile)
            .SelectMany(i => i.DiscFilePaths ?? []).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(listedStrays.SetEquals(["adopted-now.txt", "adopted-old.txt", "stray-keep.txt", "stray-gone.txt"]),
            $"and the four stray copies as untracked (got {string.Join(", ", listedStrays)})");

        // A rescan of the destination rebuilds every category's tree, the
        // catalog's too; a file row unticked before it stays unticked.
        Row(OrphanedReason.DeletedFromDisk, "keep-me.txt").IsChecked = false;
        vm.ScanDestinationCommand.Execute(null);
        await Task.Delay(100);
        await WaitFor(() => !vm.IsScanningDestination, 60000);
        Check(Row(OrphanedReason.DeletedFromDisk, "keep-me.txt").IsChecked == false,
            "a file row unticked before a rescan is still unticked after it");

        // restored.txt comes back unchanged (from the recycle bin, say): no
        // backup has anything to write for it.
        await File.WriteAllTextAsync(restored, "deleted, then restored unchanged");
        File.SetLastWriteTimeUtc(restored, restoredWritten);

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

        // A double click: the second must find the window busy, though the first
        // is still awaiting its check of where the set's records are.
        vm.PurgeSelectedCommand.Execute(null);
        bool busyAtOnce = vm.IsPurging;
        vm.PurgeSelectedCommand.Execute(null);
        await WaitFor(() => vm.IsPurging, 3000);
        await WaitFor(() => !vm.IsPurging, 60000);
        Check(!vm.IsPurging, $"the purge finished (\"{vm.SummaryText}\")");
        Check(busyAtOnce && calls.Count(c => c == "begin") == 1,
            $"a second click while the first starts does not purge again (busy at once: {busyAtOnce}, purges: {calls.Count(c => c == "begin")})");
        Check(vm.SummaryText.Contains("left 2 files alone", StringComparison.Ordinal),
            "and says it left two files alone: one backed up again since the scan, one back on disk");
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
        Check(await repo.GetActiveLatestRecordByPathAsync(set.Id, restored) is not null && File.Exists(Stored(restored)),
            "restored.txt, back on disk unchanged, keeps its record and its copy");
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
            // sub directory leaves it part-ticked, and a part-ticked directory is
            // not purged.
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
    /// §11. A file stored as a .fileref renamed over one already backed up, as
    /// git's atomic save can do: index.lock is caught by a backup while it holds a
    /// copy of another file's content (so stored as a .fileref), then renamed over
    /// index. Relocating it would put index.fileref beside the plain index: a
    /// stale pointer at the current location. (An empty index.lock cannot show
    /// this: empty files are stored plain.)
    /// </summary>
    static async Task RenameOverExistingAsync(string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 11. A rename over a file that is already backed up ===");
        string source = Path.Combine(root, "src11"), target = Path.Combine(root, "dst11");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(root, "cat11"));
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat11", "catalog.db"));
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

        string lockTwin = Path.Combine(source, "other.txt");
        string index = Path.Combine(source, "index");
        string lockFile = Path.Combine(source, "index.lock");
        await File.WriteAllTextAsync(lockTwin, "content index.lock is caught with");
        await File.WriteAllTextAsync(index, "index version one");
        await Backup();
        await File.WriteAllTextAsync(lockFile, "content index.lock is caught with");
        await Backup();                                       // a backup catches index.lock part-written
        var lockHead = (await repo.GetLatestVersionInfoAsync(set.Id))[lockFile];
        Check(lockHead.IsFileRef, "index.lock, caught holding other.txt's content, is stored as a .fileref");

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

        // A folder renamed onto the name of one that was deleted, whose records
        // are still there, falls back the same way.
        string gone = Path.Combine(source, "B"), live = Path.Combine(source, "A");
        Directory.CreateDirectory(gone);
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(gone, "x.txt"), "B's x");
        await File.WriteAllTextAsync(Path.Combine(live, "x.txt"), "A's x");
        await Backup();
        Directory.Delete(gone, recursive: true);
        await repo.MarkFilesDeletedBySourcePathsAsync(set.Id, [Path.Combine(gone, "x.txt")]);
        // Cleanup has removed its copies; its records stay, deleted.
        Directory.Delete(Path.Combine(target, gone[..1], gone[Path.GetPathRoot(gone)!.Length..]), recursive: true);
        Directory.Move(live, gone);
        var dirOutcome = await svc.MoveTargetedAsync(Job(), target, live, gone, isDirectory: true, CancellationToken.None);
        Check(dirOutcome == TargetedMoveOutcome.FellBack, $"a folder renamed onto a deleted folder's name falls back (got {dirOutcome})");
        string fresh = Path.Combine(source, "C");
        Directory.Move(gone, fresh);
        await svc.ExecuteTargetedAsync(Job(), target, [Path.Combine(fresh, "x.txt")], keepOne.Tiers, CancellationToken.None);
        string fresh2 = Path.Combine(source, "D");
        Directory.Move(fresh, fresh2);
        var dirMoved = await svc.MoveTargetedAsync(Job(), target, fresh, fresh2, isDirectory: true, CancellationToken.None);
        Check(dirMoved == TargetedMoveOutcome.Relocated, $"a folder renamed to a new name still relocates (got {dirMoved})");
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
        using var repo = new SqliteCatalogRepository(Path.Combine(root, "cat9", "catalog.db"));
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

        string? anchor = (await repo.GetActivePlainContentPathsByHashAsync(set.Id, hHash)).FirstOrDefault();
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

/// <summary>A catalog whose second <c>UpdateDiscAsync</c> fails; everything else passes through.</summary>
public class SecondDiscUpdateFails : DispatchProxy
{
    private ICatalogRepository _inner = null!;
    private int _updates;

    public static ICatalogRepository Wrap(ICatalogRepository inner)
    {
        var proxy = Create<ICatalogRepository, SecondDiscUpdateFails>();
        ((SecondDiscUpdateFails)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(ICatalogRepository.UpdateDiscAsync) && ++_updates == 2)
            return Task.FromException(new IOException("simulated failure on the second disc"));
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>A catalog whose one named method throws; everything else passes through.</summary>
public class FailsOn : DispatchProxy
{
    private ICatalogRepository _inner = null!;
    private string _method = "";

    public static ICatalogRepository Wrap(ICatalogRepository inner, string method)
    {
        var proxy = Create<ICatalogRepository, FailsOn>();
        ((FailsOn)(object)proxy)._inner = inner;
        ((FailsOn)(object)proxy)._method = method;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == _method)
            throw new IOException($"simulated failure in {_method}");
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
