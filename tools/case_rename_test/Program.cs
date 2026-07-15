// Regression test for the case-only path-change bug (known-issues.md:
// "Case-only path changes corrupt version history + orphan _prev files").
//
// The Windows filesystem is case-INSENSITIVE, but SQLite's default BINARY
// collation is case-SENSITIVE.  When a file's on-disk path casing changed
// between runs (e.g. "sub\file.txt" -> "SUB\file.txt"), the versioning code
// physically moved the old current copy to "{drive}_prev\...", but the catalog
// repoint — GetFileRecordByPathAndVersionAsync(path=NEW casing, oldVersion) —
// used a case-SENSITIVE "SourcePath = $path" and found NOTHING.  Two failures
// followed:
//   1. the old version-1 row was NOT repointed, so it still claimed to live at
//      the current path (now gone) => an ORPHANED _prev file on disk and a
//      dangling catalog reference; and
//   2. the new version-2 row was inserted under the new casing, so the
//      PARTITION BY SourcePath in GetLatestVersionInfoAsync (also case-
//      sensitive) FORKED the chain into two "current" heads.
//
// The fix makes every SourcePath comparison/partition COLLATE NOCASE.  This
// test reproduces the exact scenario end-to-end (two real directory backups
// against a real per-set SQLite catalog, with a case-only rename in between)
// and pins the post-fix outcome:
//   * exactly ONE logical file in the catalog (no forked chain),
//   * its head version is 2,
//   * the version-1 row was repointed into "_prev" (repoint succeeded), and
//   * the physical "_prev" file exists (no orphan without a catalog owner).

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

// Change the case of an existing directory on Windows.  A direct
// Directory.Move to a case-only-different name is a no-op / throws on a
// case-insensitive filesystem, so bounce through a temporary name.
static void RecaseDirectory(string path, string newLeafName)
{
    string parent = Path.GetDirectoryName(path)!;
    string tmp = Path.Combine(parent, "__recase_tmp__");
    string dest = Path.Combine(parent, newLeafName);
    Directory.Move(path, tmp);
    Directory.Move(tmp, dest);
}

string root = Path.Combine(Path.GetTempPath(), "lithic_caserename_" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(root, "src");
string target = Path.Combine(root, "dst");
string catalog = Path.Combine(root, "catalog.db");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);

var repo = new SqliteCatalogRepository(catalog);
var scanner = new FileScanner(repo);
var retention = new VersionRetentionService(repo);
var svc = new DirectoryBackupService(repo, scanner, retention);

// A real backup set so the catalog-repoint path (guarded by
// job.BackupSetId.HasValue) actually runs.
var set = await repo.CreateBackupSetAsync(new BackupSet
{
    Name = "case-rename-test",
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

// --- Run 1: source has  src\sub\file.txt  (content v1) ---
string subDir = Path.Combine(source, "sub");
Directory.CreateDirectory(subDir);
string filePath = Path.Combine(subDir, "file.txt");
await File.WriteAllTextAsync(filePath, "version one contents");

var r1 = await svc.ExecuteAsync(MakeJob(), target, null, null, CancellationToken.None);
if (!r1.Success)
    throw new Exception("run 1 failed: " + string.Join("; ", r1.FailedFiles.Select(f => f.Error)));

// --- Case-only rename of the parent directory + a genuine content change ---
// After this the scanner reports  src\SUB\file.txt  while the catalog still
// holds  src\sub\file.txt  — the exact casing divergence that broke the repoint.
RecaseDirectory(subDir, "SUB");
string recasedFile = Path.Combine(source, "SUB", "file.txt");
await File.WriteAllTextAsync(recasedFile, "version two contents are different and longer");

// --- Run 2 ---
var r2 = await svc.ExecuteAsync(MakeJob(), target, null, null, CancellationToken.None);
if (!r2.Success)
    throw new Exception("run 2 failed: " + string.Join("; ", r2.FailedFiles.Select(f => f.Error)));

Console.WriteLine("=== case-only rename: version history integrity ===");

// 1. The catalog must see ONE logical file, not a forked pair.
int distinct = await repo.GetFileCountForBackupSetAsync(set.Id, CancellationToken.None);
Check(distinct == 1, $"catalog holds exactly one logical file (COUNT DISTINCT NOCASE) (got {distinct})");

// 2. GetLatestVersionInfoAsync must collapse to a single head at version 2.
var info = await repo.GetLatestVersionInfoAsync(set.Id, CancellationToken.None);
Check(info.Count == 1, $"exactly one version head (got {info.Count})");
Check(info.Values.All(v => v.MaxVersion == 2), "head version is 2 (the chain advanced, not forked)");

// 3. The version-1 row must have been repointed into "_prev".  Look it up under
//    the NEW casing — the case-insensitive lookup is exactly what the fix adds.
var v1 = await repo.GetFileRecordByPathAndVersionAsync(set.Id, recasedFile, 1, CancellationToken.None);
Check(v1 is not null, "version-1 row is findable under the new-cased path (case-insensitive lookup)");
Check(v1 is not null && v1.DiscPath.Contains("_prev", StringComparison.OrdinalIgnoreCase),
    "version-1 row was repointed into a _prev location (repoint succeeded, not orphaned)");

// 4. The physical _prev file must exist AND be owned by that catalog row.
var prevFilesOnDisk = Directory
    .EnumerateFiles(target, "*", SearchOption.AllDirectories)
    .Where(p => p.Replace('/', '\\').Contains("_prev\\", StringComparison.OrdinalIgnoreCase))
    .ToList();
Check(prevFilesOnDisk.Count == 1, $"exactly one _prev file on disk (got {prevFilesOnDisk.Count})");
if (v1 is not null && prevFilesOnDisk.Count == 1)
{
    string ownedAbs = Path.GetFullPath(Path.Combine(target, v1.DiscPath));
    string onDisk = Path.GetFullPath(prevFilesOnDisk[0]);
    Check(string.Equals(ownedAbs, onDisk, StringComparison.OrdinalIgnoreCase),
        "the _prev file on disk is the one the catalog row points at (no orphan)");
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL PASS");
    return 0;
}
Console.WriteLine($"{failures} CHECK(S) FAILED");
return 1;
