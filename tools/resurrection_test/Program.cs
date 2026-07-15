// Regression test for the version-chain RESURRECTION fix (known-issues.md:
// "Atomically-saved files (e.g. KeyNote .knt) never accumulate versions").
//
// An application that saves atomically (write a temp file, then replace/rename
// the original — KeyNote NF .knt, and many editors) briefly REMOVES the original
// on every save. Continuous backup sees the disappearance and soft-deletes the
// catalog record (IsDeleted = 1), leaving the destination copy on disk, then
// sees the replacement as a brand-new file. Because GetLatestVersionInfoAsync
// filters WHERE IsDeleted = 0, the next backup found NO prior version, so the
// _prev move never ran and the version reset to 1 — history discarded on every
// save.
//
// The fix adds GetOrphanedVersionInfoAsync (paths whose entire history is
// tombstoned) and, in DirectoryBackupService, RESURRECTS such a chain when the
// path reappears: continue numbering, move the old copy into _prev, and
// un-delete that row as a retained version.
//
// This test reproduces the churn end-to-end against a real per-set SQLite
// catalog and pins the post-fix outcome for two scenarios:
//   A. reappeared with CHANGED content  -> head v2, v1 un-deleted in _prev.
//   B. reappeared with IDENTICAL content -> revived in place at v1 (no _prev).

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  \u2713 " : "  \u2717 FAIL: ") + msg);
    if (!cond) failures++;
}

static List<string> PrevFiles(string target) => Directory
    .EnumerateFiles(target, "*", SearchOption.AllDirectories)
    .Where(p => p.Replace('/', '\\').Contains("_prev\\", StringComparison.OrdinalIgnoreCase))
    .ToList();

string root = Path.Combine(Path.GetTempPath(), "lithic_resurrect_" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(root, "src");
string target = Path.Combine(root, "dst");
string catalog = Path.Combine(root, "catalog.db");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);

var repo = new SqliteCatalogRepository(catalog);
var scanner = new FileScanner(repo);
var retention = new VersionRetentionService(repo);
var svc = new DirectoryBackupService(repo, scanner, retention);

var set = await repo.CreateBackupSetAsync(new BackupSet
{
    Name = "resurrection-test",
    SourceRoots = new List<string> { source },
    CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
});

BackupJob MakeJob() => new()
{
    BackupSetId = set.Id,
    Sources = { new SourceSelection { Path = source, IsDirectory = true, IsSelected = true } },
    EnableFileDeduplication = true,
    EnableDeduplication = false,
    TargetDirectory = target,
    ZipMode = ZipMode.None,
    // No TierSets => tierResolver null => keepVersions = true for every file.
};

async Task Backup()
{
    var r = await svc.ExecuteAsync(MakeJob(), target, null, null, CancellationToken.None);
    if (!r.Success)
        throw new Exception("backup failed: " + string.Join("; ", r.FailedFiles.Select(f => f.Error)));
}

// Simulate the atomic-save tombstone: the continuous worker soft-deletes the
// record for the vanished original but never touches the destination copy.
async Task Tombstone(string sourcePath) =>
    await ((ICatalogRepository)repo).MarkFilesDeletedBySourcePathsAsync(
        set.Id, new[] { sourcePath }, CancellationToken.None);

string notePath = Path.Combine(source, "note.knt");

// ===================================================================
// Scenario A: reappears with CHANGED content -> resurrect to version 2
// ===================================================================
Console.WriteLine("=== Scenario A: tombstone + changed content -> resurrect (v2, old in _prev) ===");

await File.WriteAllTextAsync(notePath, "note contents version one");
await Backup();                       // v1 backed up, live

await Tombstone(notePath);            // atomic-save deletes the original
await File.WriteAllTextAsync(notePath, "note contents version two — longer and different");
await Backup();                       // reappears changed -> must RESURRECT

int distinctA = await repo.GetFileCountForBackupSetAsync(set.Id, CancellationToken.None);
Check(distinctA == 1, $"catalog holds exactly one logical file (got {distinctA})");

var infoA = await repo.GetLatestVersionInfoAsync(set.Id, CancellationToken.None);
Check(infoA.Count == 1, $"exactly one live version head (got {infoA.Count})");
Check(infoA.Values.All(v => v.MaxVersion == 2),
    "head version is 2 — the chain continued instead of restarting at 1");

var v1 = await repo.GetFileRecordByPathAndVersionAsync(set.Id, notePath, 1, CancellationToken.None);
Check(v1 is not null, "version-1 row still exists");
Check(v1 is not null && !v1.IsDeleted,
    "version-1 row was UN-DELETED (a retained version, not a ghost)");
Check(v1 is not null && v1.DiscPath.Contains("_prev", StringComparison.OrdinalIgnoreCase),
    "version-1 row was repointed into _prev");

var prevA = PrevFiles(target);
Check(prevA.Count == 1, $"exactly one _prev file on disk (got {prevA.Count})");
if (v1 is not null && prevA.Count == 1)
{
    string owned = Path.GetFullPath(Path.Combine(target, v1.DiscPath));
    Check(string.Equals(owned, Path.GetFullPath(prevA[0]), StringComparison.OrdinalIgnoreCase),
        "the _prev file on disk is the one the v1 row points at (no orphan)");
}

// ===================================================================
// Scenario B: reappears with IDENTICAL content -> revive in place (v2)
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== Scenario B: tombstone + identical content -> revive in place (no new version) ===");

// The file currently holds "version two" at head v2. Tombstone it, then let it
// reappear byte-for-byte identical (no edit). It must be revived in place, NOT
// cut a redundant v3.
await Tombstone(notePath);
// (source file unchanged on disk — identical bytes)
int prevCountBefore = PrevFiles(target).Count;
await Backup();

var infoB = await repo.GetLatestVersionInfoAsync(set.Id, CancellationToken.None);
Check(infoB.Count == 1, $"still exactly one live version head (got {infoB.Count})");
Check(infoB.Values.All(v => v.MaxVersion == 2),
    "head stayed at version 2 — identical reappearance revived in place, no churn");
Check(PrevFiles(target).Count == prevCountBefore,
    "no new _prev file cut for an identical-content revival");

var headB = await repo.GetFileRecordByPathAndVersionAsync(set.Id, notePath, 2, CancellationToken.None);
Check(headB is not null && !headB.IsDeleted,
    "the head version is live again (revived from its tombstone)");

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL PASS");
    return 0;
}
Console.WriteLine($"{failures} CHECK(S) FAILED");
return 1;
