// Regression test for "Cannot update file record: disc 0 has no owning set."
//
// Cleanup's classifier loads FIVE columns per row (Id, SourcePath, DiscPath,
// SizeBytes, BackedUpUtc) to keep a multi-million-row scan in memory. The purge
// then took those same partially-populated FileRecords, set IsDeleted, and passed
// them to UpdateFileRecordAsync — which rewrites EVERY column. Had it gone
// through, it would have written back defaults for Hash, Version, DiscId,
// SourceLastWriteUtc and the IsZipped/IsSplit/IsDeduped/IsFileRef flags — the
// flags that tell restore how to read a file's bytes.
//
// It never did go through, purely by luck: DiscId defaults to 0, and the
// repository routes writes to a set database by looking up the disc's owner, so
// disc 0 threw before any SQL ran. The visible symptom was a cleanup that always
// failed; the invisible one would have been a silently corrupted catalog.
//
// This pins all three properties:
//   1. classifier records really are partial (DiscId = 0, Hash empty);
//   2. handing one to UpdateFileRecordAsync still throws rather than corrupting;
//   3. MarkFileRecordsDeletedByIdsAsync tombstones the row and leaves every
//      other column exactly as it was.

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "lithic_purgeint_" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(root, "src");
string target = Path.Combine(root, "dst");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);

var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));
var scanner = new FileScanner(repo);
var retention = new VersionRetentionService(repo);
var svc = new DirectoryBackupService(repo, scanner, retention);

var set = await repo.CreateBackupSetAsync(new BackupSet
{
    Name = "purge-integrity-test",
    SourceRoots = new List<string> { source },
    CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
});

BackupJob MakeJob() => new()
{
    BackupSetId = set.Id,
    Sources = { new SourceSelection { Path = source, IsDirectory = true, IsSelected = true } },
    EnableFileDeduplication = false,
    EnableDeduplication = false,
    TargetDirectory = target,
    ZipMode = ZipMode.None,
    // No TierSets => every version retained, so v1 survives as an excess version.
};

async Task Backup()
{
    var r = await svc.ExecuteAsync(MakeJob(), target, null, null, CancellationToken.None);
    if (!r.Success)
        throw new Exception("backup failed: " + string.Join("; ", r.FailedFiles.Select(f => f.Error)));
}

// Two versions, so there is a genuine "excess version" row to purge — the exact
// category whose records the purge used to push through the whole-row update.
string filePath = Path.Combine(source, "doc.txt");
await File.WriteAllTextAsync(filePath, "version one contents");
await Backup();
await File.WriteAllTextAsync(filePath, "version two contents, longer than the first");
await Backup();

var v1 = await repo.GetFileRecordByPathAndVersionAsync(set.Id, filePath, 1, CancellationToken.None);
Check(v1 is not null, "a version-1 row exists to work with");
if (v1 is null) { Console.WriteLine("cannot continue"); return 1; }

Console.WriteLine();
Console.WriteLine("=== 1. the classifier's records are partial ===");

var classified = await repo.GetActiveFilesForClassificationAsync(set.Id, CancellationToken.None);
var partial = classified.FirstOrDefault(f => f.Id == v1.Id);
Check(partial is not null, "the v1 row comes back from the classification query");
if (partial is null) { Console.WriteLine("cannot continue"); return 1; }

Check(partial.DiscId == 0,
    $"DiscId is 0 on the partial record, while the real row has DiscId {v1.DiscId}");
Check(string.IsNullOrEmpty(partial.Hash) && !string.IsNullOrEmpty(v1.Hash),
    "Hash is empty on the partial record but set on the real row");

// Version is the nastiest of the lot. FileRecord.Version defaults to *1*, not 0,
// so a partial record does not look obviously empty — it looks like version 1.
// A whole-row update driven from one would have quietly rewritten a version-2 row
// as version 1, collapsing the chain with nothing to flag it.
var v2 = await repo.GetFileRecordByPathAndVersionAsync(set.Id, filePath, 2, CancellationToken.None);
Check(v2 is not null, "a version-2 row exists");
var partialV2 = classified.FirstOrDefault(f => v2 is not null && f.Id == v2.Id);
Check(partialV2 is not null, "the v2 row also comes back from the classification query");
Check(partialV2 is not null && v2 is not null && partialV2.Version == 1 && v2.Version == 2,
    $"Version reads as the default 1 on the partial v2 record, not its real {v2?.Version} "
    + "— it looks plausible rather than empty, which is what makes it dangerous");
Console.WriteLine("     (so a whole-row UPDATE from this object would blank or falsify all of the above)");

Console.WriteLine();
Console.WriteLine("=== 2. the whole-row update still refuses a partial record ===");

partial.IsDeleted = true;
string message = "";
try
{
    await repo.UpdateFileRecordAsync(partial, CancellationToken.None);
    Check(false, "UpdateFileRecordAsync threw for a partial record");
}
catch (InvalidOperationException ex)
{
    message = ex.Message;
    Check(true, "UpdateFileRecordAsync threw for a partial record");
}
Check(message.Contains("not read in full", StringComparison.OrdinalIgnoreCase),
    "the error explains the real cause rather than only naming disc 0");
Console.WriteLine("     message: " + message.Split(" DiscId is 0")[0].Trim());

// and it threw BEFORE writing: the row must be untouched.
var afterThrow = await repo.GetFileRecordByPathAndVersionAsync(set.Id, filePath, 1, CancellationToken.None);
Check(afterThrow is not null && !afterThrow.IsDeleted && afterThrow.Hash == v1.Hash,
    "the row is untouched after the throw (it failed before any SQL ran)");

Console.WriteLine();
Console.WriteLine("=== 3. the id-based tombstone changes IsDeleted and nothing else ===");

int changed = await repo.MarkFileRecordsDeletedByIdsAsync(
    set.Id, new[] { v1.Id }, CancellationToken.None);
Check(changed == 1, $"one row reported changed (got {changed})");

var after = await repo.GetFileRecordByPathAndVersionAsync(set.Id, filePath, 1, CancellationToken.None);
Check(after is not null, "the row still exists (tombstoned, not removed)");
if (after is null) { Console.WriteLine("cannot continue"); return 1; }

Check(after.IsDeleted, "IsDeleted is now true");
Check(after.DiscId == v1.DiscId, $"DiscId preserved ({after.DiscId})");
Check(after.Hash == v1.Hash, "Hash preserved — content identity intact for dedup/filerefs");
Check(after.Version == v1.Version, $"Version preserved ({after.Version}) — version chain intact");
Check(after.SourcePath == v1.SourcePath, "SourcePath preserved");
Check(after.DiscPath == v1.DiscPath, "DiscPath preserved");
Check(after.SizeBytes == v1.SizeBytes, "SizeBytes preserved");
Check(after.IsZipped == v1.IsZipped && after.IsSplit == v1.IsSplit
      && after.IsDeduped == v1.IsDeduped && after.IsFileRef == v1.IsFileRef,
    "storage flags preserved — restore still knows how to read the bytes");
Check(after.SourceLastWriteUtc == v1.SourceLastWriteUtc,
    "SourceLastWriteUtc preserved — change detection still works");

// Re-running must be a no-op, not a second "change".
int again = await repo.MarkFileRecordsDeletedByIdsAsync(
    set.Id, new[] { v1.Id }, CancellationToken.None);
Check(again == 0, $"re-marking an already-deleted row reports 0 changed (got {again})");

// The live head must be untouched by any of this.
var head = await repo.GetFileRecordByPathAndVersionAsync(set.Id, filePath, 2, CancellationToken.None);
Check(head is not null && !head.IsDeleted, "the current version is still live");

Console.WriteLine();
try { Directory.Delete(root, recursive: true); } catch { }
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
