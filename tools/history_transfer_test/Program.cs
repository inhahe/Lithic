// Regression test for moving a backup set to another computer (known-issues.md:
// "An imported set said 'Never backed up' and began re-copying its whole backup").
//
// Exporting a set wrote its settings only. Imported on a new computer with the
// same backup drive attached, it had no history: it said "Never backed up", the
// Worker started a full re-copy a minute later, and as files changed their old
// versions in {drive}_prev were overwritten one by one, because version numbers
// restarted at 1 and the engine moved the current copy onto ".v1" with
// overwrite. Seeding from the destination had the same flaw: it skipped _prev
// and recorded everything as version 1.
//
// Against real per-set SQLite catalogs and a real destination tree, this pins:
//   1. export WITH history -> import into another catalog whose disc ids are
//      already taken: same records, disc ids remapped, LastBackupUtc kept,
//      automated backups held, and the next plan finds nothing to copy;
//   2. export WITHOUT history is plain JSON and imports with no history;
//      a legacy (pre-1.10) JSON export still imports;
//   3. FindUntrackedBackupAsync flags a history-less set whose destination holds
//      its files (with history), and not one whose sources are elsewhere;
//   4. seeding imports _prev versions, numbers the current copy after them,
//      leaves the next plan empty, is idempotent, and a later change does not
//      touch the old version files;
//   5. the engine never overwrites an untracked _prev file: a history-less
//      backup over an old destination files the old copy under a fresh number;
//   6. a seed that finds both a plain copy and a .fileref of one file records one.
//  10. export and import of a large history report rising progress through each
//      step, and a cancel stops them part-way leaving nothing behind.

using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core;
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

string root = Path.Combine(Path.GetTempPath(), "lithic_history_transfer_" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(root, "src");
string target = Path.Combine(root, "dst");
Directory.CreateDirectory(source);
Directory.CreateDirectory(target);

static SqliteCatalogRepository NewCatalog(string dir)
{
    Directory.CreateDirectory(dir);
    return new SqliteCatalogRepository(Path.Combine(dir, "catalog.db"));
}

static DirectoryBackupService NewService(ICatalogRepository repo) =>
    new(repo, new FileScanner(repo), new VersionRetentionService(repo));

// "from": the folder a set backs up, the harness's main source when null.
List<SourceSelection> Selection(string? from = null) =>
    [new SourceSelection { Path = from ?? source, IsDirectory = true, IsSelected = true }];

BackupJob Job(int setId, string dest, string? from = null) => new()
{
    BackupSetId = setId,
    Sources = Selection(from),
    EnableFileDeduplication = true,
    EnableDeduplication = false,
    TargetDirectory = dest,
    ZipMode = ZipMode.None,
};

async Task Backup(DirectoryBackupService svc, int setId, string dest, string? from = null)
{
    var r = await svc.ExecuteAsync(Job(setId, dest, from), dest, null, null, CancellationToken.None);
    if (!r.Success)
        throw new Exception("backup failed: " + string.Join("; ", r.FailedFiles.Select(f => $"{f.Path}: {f.Error}")));
}

static Dictionary<string, string> HashTree(string dir) => Directory
    .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
    .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),
        StringComparer.OrdinalIgnoreCase);

static void CopyTree(string from, string to)
{
    foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        string dest = Path.Combine(to, Path.GetRelativePath(from, f));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(f, dest);
    }
}

// Where a source file's copies live in a destination.
string Rel(string sourcePath) => sourcePath[Path.GetPathRoot(sourcePath)!.Length..];
string CurrentOf(string dest, string sourcePath) => Path.Combine(dest, sourcePath[..1], Rel(sourcePath));
string PrevOf(string dest, string sourcePath, int v) =>
    Path.Combine(dest, sourcePath[..1] + "_prev", Rel(sourcePath) + $".v{v}");

string aPath = Path.Combine(source, "a.txt");
string bPath = Path.Combine(source, "b.txt");
string cPath = Path.Combine(source, "sub", "c.txt");
string dup1 = Path.Combine(source, "dup1.bin");
string dup2 = Path.Combine(source, "dup2.bin");
Directory.CreateDirectory(Path.GetDirectoryName(cPath)!);

// ===================================================================
// The old computer: three backups, so there is real version history.
// ===================================================================
Console.WriteLine("=== Old computer: build a set with version history ===");
var repoA = NewCatalog(Path.Combine(root, "A"));
var svcA = NewService(repoA);
var setA = await repoA.CreateBackupSetAsync(new BackupSet
{
    Name = "laptop docs",
    SourceRoots = [source],
    SourceSelections = Selection(),
    CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions
    {
        TargetDirectory = target,
        EnableFileDeduplication = true,
        Schedule = new BackupSchedule { Enabled = true, Mode = ScheduleMode.Continuous },
    },
});

await File.WriteAllTextAsync(aPath, "a version one");
await File.WriteAllTextAsync(bPath, "b only version");
await File.WriteAllTextAsync(cPath, "c version one");
await File.WriteAllTextAsync(dup1, "identical duplicate content");
await File.WriteAllTextAsync(dup2, "identical duplicate content");
await Backup(svcA, setA.Id, target);
await File.WriteAllTextAsync(aPath, "a version two, longer");
await File.WriteAllTextAsync(cPath, "c version two, longer");
await Backup(svcA, setA.Id, target);
await File.WriteAllTextAsync(aPath, "a version three, longer still");
await Backup(svcA, setA.Id, target);

var infoA = await repoA.GetLatestVersionInfoAsync(setA.Id);
var allA = await repoA.GetAllFilesForBackupSetAsync(setA.Id);
var discsA = await repoA.GetDiscsForBackupSetAsync(setA.Id);
var setAStored = (await repoA.GetBackupSetAsync(setA.Id))!;
Check(infoA[aPath].MaxVersion == 3, $"a.txt is at version 3 on the old computer (got {infoA[aPath].MaxVersion})");
Check(File.Exists(PrevOf(target, aPath, 1)) && File.Exists(PrevOf(target, aPath, 2)),
    "a.txt.v1 and a.txt.v2 are in the destination's history");
Check(infoA[dup2].IsFileRef || infoA[dup1].IsFileRef, "one duplicate is stored as a .fileref");
Check(setAStored.LastBackupUtc is not null, "the old set has a last-backup time");

// The destination as the old computer left it, for tests 5 and 6.
string snapshot = Path.Combine(root, "dst_snapshot");
CopyTree(target, snapshot);
var snapshotHashes = HashTree(snapshot);

// ===================================================================
// 1. Export with history, import on the new computer.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 1. Export with history -> import on a new computer ===");
string exportFile = Path.Combine(root, "laptop docs.lithic");
// Anything either direction stages in the system's temp folder, however briefly.
var stagedInTemp = new System.Collections.Concurrent.ConcurrentBag<string>();
using var tempWatch = new FileSystemWatcher(Path.GetTempPath(), "lithic-*") { EnableRaisingEvents = true };
tempWatch.Created += (_, e) => stagedInTemp.Add(e.Name ?? "");
await BackupSetTransfer.ExportAsync(repoA, setAStored, exportFile, includeHistory: true);
byte[] head = File.ReadAllBytes(exportFile)[..4];
Check(head[0] == 0x50 && head[1] == 0x4B, "an export with history is a zip");
Check(!File.Exists(exportFile + ".partial"), "no partial file is left behind");
Check(!Directory.EnumerateFiles(root, "*.history.partial").Any(), "nor the history it staged beside the export");

var repoB = NewCatalog(Path.Combine(root, "B"));
var svcB = NewService(repoB);

// The new computer already has a set with discs, so the low disc ids are taken.
var other = await repoB.CreateBackupSetAsync(new BackupSet
{
    Name = "already here", SourceRoots = [Path.Combine(root, "elsewhere")],
    CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
});
for (int i = 1; i <= discsA.Count + 2; i++)
{
    await repoB.CreateDiscAsync(new DiscRecord
    {
        BackupSetId = other.Id, Label = "other", SequenceNumber = i,
        MediaType = MediaType.Directory, FilesystemType = FilesystemType.UDF,
        Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow,
    });
}
var otherDiscIds = (await repoB.GetDiscsForBackupSetAsync(other.Id)).Select(d => d.Id).ToHashSet();

var imported = await BackupSetTransfer.ImportAsync(repoB, exportFile);
Check(!Directory.EnumerateFiles(repoB.DataDirectory, "lithic-import-*").Any(),
    "the import's staged history, kept beside the catalog, is gone afterwards");
await Task.Delay(500);   // let the watcher's last events arrive
tempWatch.EnableRaisingEvents = false;
Check(stagedInTemp.IsEmpty, $"and neither direction staged anything in the system's temp folder ({string.Join(", ", stagedInTemp)})");
var setB = (await repoB.GetBackupSetAsync(imported.Set.Id))!;
Check(imported.IncludedHistory, "the import found the history");
Check(imported.FileRecords == allA.Count, $"every file record came across ({imported.FileRecords} of {allA.Count})");
Check(setB.JobOptions?.HoldAutomatedBackups == true, "automated backups arrive held");
Check(setB.JobOptions?.Schedule is { Enabled: true, Mode: ScheduleMode.Continuous }, "the schedule itself is kept");
Check(setB.LastBackupUtc == setAStored.LastBackupUtc, "the last-backup time came across (not 'Never backed up')");

var infoB = await repoB.GetLatestVersionInfoAsync(setB.Id);
bool sameHeads = infoB.Count == infoA.Count && infoA.All(kv =>
    infoB.TryGetValue(kv.Key, out var b) && b.MaxVersion == kv.Value.MaxVersion
    && b.Hash == kv.Value.Hash && b.IsFileRef == kv.Value.IsFileRef);
Check(sameHeads, "every file's latest version, hash and storage form match the old computer's");

var discsB = await repoB.GetDiscsForBackupSetAsync(setB.Id);
Check(discsB.Count == discsA.Count, $"same number of disc records ({discsB.Count})");
Check(!discsB.Any(d => otherDiscIds.Contains(d.Id)), "disc ids were re-allocated, not reused");
Check((await repoB.GetDiscsForBackupSetAsync(other.Id)).Count == otherDiscIds.Count,
    "the set already on the new computer kept its discs");
var firstDisc = discsB.OrderBy(d => d.Id).First();
Check((await repoB.GetFilesOnDiscAsync(firstDisc.Id)).Count > 0, "files route through the new disc ids");

var (_, _, toCopy) = await svcB.PlanAsync(Job(setB.Id, target), CancellationToken.None);
Check(toCopy == 0, $"the imported set picks up where it left off: nothing to copy (got {toCopy})");
Check(await svcB.FindUntrackedBackupAsync(setB.Id, [source], target) is null,
    "a set with history is never flagged as backing up over an untracked backup");

// ===================================================================
// 2. Export without history, and a legacy export.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 2. Settings-only and legacy exports ===");
string settingsFile = Path.Combine(root, "settings only.lithic");
await BackupSetTransfer.ExportAsync(repoA, setAStored, settingsFile, includeHistory: false);
Check(File.ReadAllText(settingsFile).TrimStart().StartsWith('{'), "a settings-only export is plain JSON, as before");
var settingsOnly = await BackupSetTransfer.ImportAsync(repoB, settingsFile);
var setC = (await repoB.GetBackupSetAsync(settingsOnly.Set.Id))!;
Check(!settingsOnly.IncludedHistory, "it imports with no history");
Check(setC.LastBackupUtc is null, "and honestly says it has never backed up");
Check(setC.JobOptions?.HoldAutomatedBackups == true, "automated backups arrive held here too");
Check(!await repoB.HasFileRecordsAsync(setC.Id), "its catalog is empty");

string legacyFile = Path.Combine(root, "legacy.lithic");
await File.WriteAllTextAsync(legacyFile, JsonSerializer.Serialize(new
{
    Name = "from 1.8",
    SourceRoots = new[] { source },
    MaxIncrementalDiscs = 5,
    JobOptions = new { TargetDirectory = target },
}));
var legacy = await BackupSetTransfer.ImportAsync(repoB, legacyFile);
Check(legacy.Set.Name == "from 1.8" && !legacy.IncludedHistory, "a legacy settings export still imports");

string junkFile = Path.Combine(root, "junk.lithic");
await File.WriteAllTextAsync(junkFile, "not json at all");
int setsBefore = (await repoB.GetAllBackupSetsAsync()).Count;
bool rejected = false;
try { await BackupSetTransfer.ImportAsync(repoB, junkFile); }
catch (InvalidDataException) { rejected = true; }
Check(rejected && (await repoB.GetAllBackupSetsAsync()).Count == setsBefore,
    "a file that is not an export is rejected and creates no set");

// The first open of a catalog drops its legacy master tables; a set must still
// be deletable in that same session (an import's clean-up after a cancel or
// failure deletes the set it created).
var throwaway = await repoB.CreateBackupSetAsync(new BackupSet
{
    Name = "throwaway", SourceRoots = [source], CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
});
string? deleteError = null;
try { await repoB.DeleteBackupSetAsync(throwaway.Id); }
catch (Exception ex) { deleteError = ex.Message; }
Check(deleteError is null && await repoB.GetBackupSetAsync(throwaway.Id) is null,
    $"a set can be deleted in the session that created its catalog{(deleteError is null ? "" : $" ({deleteError})")}");

// ===================================================================
// 3. The untracked-backup check.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 3. Untracked backup at the destination ===");
var found = await svcB.FindUntrackedBackupAsync(setC.Id, [source], target);
Check(found is not null, "a history-less set over its own old backup is flagged");
Check(found?.HasVersionHistory == true, "and the version history there is noticed");
Check(await svcB.FindUntrackedBackupAsync(setC.Id, [Path.Combine(root, "elsewhere")], target) is null,
    "a set whose sources are elsewhere, sharing the destination folder, is not flagged");
Check(await svcB.FindUntrackedBackupAsync(setC.Id, [source], Path.Combine(root, "empty_dest")) is null,
    "nor is one whose destination does not exist yet");

// ===================================================================
// 4. Seeding the settings-only set from the destination.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 4. Seed from the destination, history included ===");
var seeded = await svcB.SeedFromExistingDirectoryAsync(setC.Id, target);
Check(seeded.Files == 5, $"one record per current copy (got {seeded.Files})");
Check(seeded.PreviousVersions == 3, $"the three _prev versions were imported (got {seeded.PreviousVersions})");
var infoC = await repoB.GetLatestVersionInfoAsync(setC.Id);
Check(infoC[aPath].MaxVersion == 3, $"a.txt's current copy is numbered after its history: v3 (got {infoC[aPath].MaxVersion})");
Check(infoC[cPath].MaxVersion == 2, $"c.txt is v2 (got {infoC[cPath].MaxVersion})");
Check(infoC[bPath].MaxVersion == 1, "b.txt, with no history, is v1");
Check(infoC[dup1].IsFileRef || infoC[dup2].IsFileRef, "the .fileref was recorded as one");
var aRecords = await repoB.GetFileRecordsByPathAsync(setC.Id, aPath);
Check(aRecords.Count == 3 && aRecords.All(r => File.Exists(Path.Combine(target, r.DiscPath))),
    "a.txt has three records, each pointing at a file that exists");
var (_, _, afterSeed) = await svcB.PlanAsync(Job(setC.Id, target), CancellationToken.None);
Check(afterSeed == 0, $"after seeding, nothing needs copying (got {afterSeed})");

var again = await svcB.SeedFromExistingDirectoryAsync(setC.Id, target);
Check(again.Files == 0 && again.PreviousVersions == 0 && again.SkippedExisting == 5,
    "seeding again records nothing new");

var prevBefore = HashTree(Path.Combine(target, aPath[..1] + "_prev"));
await File.WriteAllTextAsync(aPath, "a version four, after the move");
await Backup(svcB, setC.Id, target);
var prevAfter = HashTree(Path.Combine(target, aPath[..1] + "_prev"));
Check(prevBefore.All(kv => prevAfter.TryGetValue(kv.Key, out var h) && h == kv.Value),
    "a change after seeding leaves every old version file untouched");
Check(File.Exists(PrevOf(target, aPath, 3))
      && File.ReadAllText(PrevOf(target, aPath, 3)) == "a version three, longer still",
    "the superseded copy became a.txt.v3");
Check((await repoB.GetLatestVersionInfoAsync(setC.Id))[aPath].MaxVersion == 4, "a.txt is now v4");

// ===================================================================
// 5. Engine guard: a history-less backup over an old destination.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 5. Backing up anyway never overwrites old versions ===");
string target2 = Path.Combine(root, "dst2");
CopyTree(snapshot, target2);
var setD = await repoB.CreateBackupSetAsync(new BackupSet
{
    Name = "re-created by hand", SourceRoots = [source], SourceSelections = Selection(),
    CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions { TargetDirectory = target2 },
});
Check(await svcB.FindUntrackedBackupAsync(setD.Id, [source], target2) is { HasVersionHistory: true },
    "the GUI and Worker would be warned before this backup");

await Backup(svcB, setD.Id, target2);                       // a.txt (v4 content) recorded as version 1
await File.WriteAllTextAsync(aPath, "a version five");
await Backup(svcB, setD.Id, target2);                       // moving version 1 into _prev
string a1 = PrevOf(target2, aPath, 1), a2 = PrevOf(target2, aPath, 2), a3 = PrevOf(target2, aPath, 3);
Check(snapshotHashes.TryGetValue(Path.Combine(snapshot, Path.GetRelativePath(target2, a1)), out var h1)
      && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a1))) == h1,
    "the old a.txt.v1 is untouched");
Check(snapshotHashes.TryGetValue(Path.Combine(snapshot, Path.GetRelativePath(target2, a2)), out var h2)
      && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a2))) == h2,
    "the old a.txt.v2 is untouched");
Check(File.Exists(a3) && File.ReadAllText(a3) == "a version four, after the move",
    "the superseded copy was filed under the first free number, a.txt.v3");
var dHeads = await repoB.GetLatestVersionInfoAsync(setD.Id);
Check(dHeads[aPath].MaxVersion == 4, $"the new copy is numbered after it: v4 (got {dHeads[aPath].MaxVersion})");
var dA = await repoB.GetFileRecordByPathAndVersionAsync(setD.Id, aPath, 3);
Check(dA is not null && dA.DiscPath.EndsWith("a.txt.v3", StringComparison.OrdinalIgnoreCase),
    "the catalog's record of the old copy says v3 and points at a.txt.v3");
// Versions 1 and 2 are the earlier catalog's, on the drive in no record: they are
// recorded as themselves now, where they were left untracked (Restore could not
// see them, Cleanup listed them for deletion).
var dA1 = await repoB.GetFileRecordByPathAndVersionAsync(setD.Id, aPath, 1);
var dA2 = await repoB.GetFileRecordByPathAndVersionAsync(setD.Id, aPath, 2);
Check(dA1 is not null && dA1.DiscPath.EndsWith("a.txt.v1", StringComparison.OrdinalIgnoreCase)
      && string.Equals(dA1.Hash, Sha(a1), StringComparison.OrdinalIgnoreCase),
    "version 1 is the old a.txt.v1 itself, recorded under its own content, not the renumbered copy");
Check(dA2 is not null && dA2.DiscPath.EndsWith("a.txt.v2", StringComparison.OrdinalIgnoreCase)
      && string.Equals(dA2.Hash, Sha(a2), StringComparison.OrdinalIgnoreCase),
    "and version 2 the old a.txt.v2");

// ===================================================================
// 6. Two stored forms of one file.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 6. Two stored forms of one file at its current location ===");
// A stale .fileref to EMPTY content (the file was briefly zero bytes when an
// earlier backup read it) beside the newer plain copy. And the reverse: a
// newer .fileref beside an older plain copy, which other references may need
// as their content.
string target3 = Path.Combine(root, "dst3");
CopyTree(snapshot, target3);
const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
string bCurrent = CurrentOf(target3, bPath);
await File.WriteAllTextAsync(bCurrent + ".fileref", JsonSerializer.Serialize(new FileRefManifest
{
    OriginalName = "b.txt", OriginalSize = 0, Hash = EmptyHash, SourcePath = bPath, ContentPath = "",
}));
File.SetLastWriteTimeUtc(bCurrent + ".fileref", DateTime.UtcNow.AddDays(-30));   // older than the plain copy

string cCurrent = CurrentOf(target3, cPath);
string bContentHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bCurrent))).ToLowerInvariant();
await File.WriteAllTextAsync(cCurrent + ".fileref", JsonSerializer.Serialize(new FileRefManifest
{
    OriginalName = "c.txt", OriginalSize = new FileInfo(bCurrent).Length, Hash = bContentHash,
    SourcePath = cPath, ContentPath = Path.GetRelativePath(target3, bCurrent),
}));
File.SetLastWriteTimeUtc(cCurrent, DateTime.UtcNow.AddDays(-30));                // the plain copy is the older one
string cOldContent = File.ReadAllText(cCurrent);

var setE = await repoB.CreateBackupSetAsync(new BackupSet
{
    Name = "two forms", SourceRoots = [source], SourceSelections = Selection(),
    CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions { TargetDirectory = target3 },
});
var seededE = await svcB.SeedFromExistingDirectoryAsync(setE.Id, target3);
Check(seededE.MovedToHistory == 2 && seededE.DuplicateCopies == 0,
    $"both older copies were moved into the history (moved {seededE.MovedToHistory}, left {seededE.DuplicateCopies})");

var bRecords = (await repoB.GetFileRecordsByPathAsync(setE.Id, bPath)).OrderBy(r => r.Version).ToList();
Check(bRecords.Select(r => (r.Version, r.IsFileRef)).SequenceEqual([(1, true), (2, false)]),
    "b.txt: the stale pointer is version 1, the newer plain copy version 2 and current");
Check(!File.Exists(bCurrent + ".fileref") && File.Exists(Path.Combine(target3, bRecords[0].DiscPath)),
    "and the pointer now sits in the history where its record says");

var cRecords = (await repoB.GetFileRecordsByPathAsync(setE.Id, cPath)).OrderBy(r => r.Version).ToList();
Check(cRecords.Select(r => (r.Version, r.IsFileRef)).SequenceEqual([(1, false), (2, false), (3, true)]),
    $"c.txt: its old history v1, the older plain copy v2, the newer pointer v3 and current " +
    $"(got {string.Join(",", cRecords.Select(r => $"{r.Version}{(r.IsFileRef ? "r" : "p")}"))})");
var cMoved = cRecords.FirstOrDefault(r => r.Version == 2);
Check(cMoved is not null && File.ReadAllText(Path.Combine(target3, cMoved.DiscPath)) == cOldContent,
    "the older plain copy kept its content in the history");

var recordedPaths = (await repoB.GetAllFilesForBackupSetAsync(setE.Id))
    .Where(r => !r.IsDeleted)
    .Select(r => r.DiscPath.Replace('/', '\\'))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var untracked = Directory.EnumerateFiles(target3, "*", SearchOption.AllDirectories)
    .Select(p => Path.GetRelativePath(target3, p))
    .Where(p => !recordedPaths.Contains(p))
    .ToList();
Check(untracked.Count == 0,
    $"nothing at the destination is left untracked{(untracked.Count > 0 ? ": " + string.Join(", ", untracked) : "")}");

// ===================================================================
// 7. The catalog is behind the drive: the old computer backed up again
//    after its history was exported.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 7. History exported, then the old computer backed up once more ===");
string source7 = Path.Combine(root, "src7");
string target7 = Path.Combine(root, "dst7");
Directory.CreateDirectory(source7);
Directory.CreateDirectory(target7);
string fPath = Path.Combine(source7, "f.txt");
static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));

var repoA7 = NewCatalog(Path.Combine(root, "A7"));
var svcA7 = NewService(repoA7);
var setA7 = await repoA7.CreateBackupSetAsync(new BackupSet
{
    Name = "behind", SourceRoots = [source7], SourceSelections = Selection(source7),
    CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions { TargetDirectory = target7 },
});
await File.WriteAllTextAsync(fPath, "f one");
await Backup(svcA7, setA7.Id, target7, source7);
await File.WriteAllTextAsync(fPath, "f two, longer");
await Backup(svcA7, setA7.Id, target7, source7);
string export7 = Path.Combine(root, "behind.lithic");
await BackupSetTransfer.ExportAsync(repoA7, (await repoA7.GetBackupSetAsync(setA7.Id))!, export7, includeHistory: true);
await File.WriteAllTextAsync(fPath, "f three, longer still");     // after the export
await Backup(svcA7, setA7.Id, target7, source7);

var repoB7 = NewCatalog(Path.Combine(root, "B7"));
var svcB7 = NewService(repoB7);
var setB7 = (await BackupSetTransfer.ImportAsync(repoB7, export7)).Set;
Check((await repoB7.GetLatestVersionInfoAsync(setB7.Id))[fPath].MaxVersion == 2,
    "the imported history ends at v2, while the drive has moved on to v3");

await File.WriteAllTextAsync(fPath, "f four, on the new computer");
await Backup(svcB7, setB7.Id, target7, source7);
var fRecords = (await repoB7.GetFileRecordsByPathAsync(setB7.Id, fPath)).OrderBy(r => r.Version).ToList();
Check(fRecords.Select(r => r.Version).SequenceEqual([1, 2, 3, 4]),
    $"versions 1 to 4 (got {string.Join(",", fRecords.Select(r => r.Version))})");
Check(fRecords.All(r =>
    {
        string p = Path.Combine(target7, r.DiscPath);
        return File.Exists(p) && string.Equals(Sha(p), r.Hash, StringComparison.OrdinalIgnoreCase);
    }),
    "every record's file holds exactly the content its hash says (no version relabelled)");
var f2 = fRecords.FirstOrDefault(r => r.Version == 2);
Check(f2 is not null && File.ReadAllText(Path.Combine(target7, f2.DiscPath)) == "f two, longer",
    "v2 still names the old computer's v2, now found in _prev where it already was");
var f3 = fRecords.FirstOrDefault(r => r.Version == 3);
Check(f3 is not null && File.ReadAllText(Path.Combine(target7, f3.DiscPath)) == "f three, longer still",
    "the version the old computer backed up after the export is recorded as v3");

// ===================================================================
// 7b. The old computer backed up twice more after the export.
// ===================================================================
// The free-number search steps over the old computer's intermediate version;
// it is on the drive and was in no record - invisible to Restore, and listed
// by Cleanup as untracked. Each such version is now recorded under its number.
Console.WriteLine();
Console.WriteLine("=== 7b. ... and twice more: the version in between is recorded too ===");
string source7b = Path.Combine(root, "src7b"), target7b = Path.Combine(root, "dst7b");
Directory.CreateDirectory(source7b);
Directory.CreateDirectory(target7b);
string gPath = Path.Combine(source7b, "g.txt");
var repoA7b = NewCatalog(Path.Combine(root, "A7b"));
var svcA7b = NewService(repoA7b);
var setA7b = await repoA7b.CreateBackupSetAsync(new BackupSet
{
    Name = "behind twice", SourceRoots = [source7b], CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory, JobOptions = new JobOptions { TargetDirectory = target7b },
});
string[] gContents = ["g one", "g two, longer", "g three, longer still", "g four, the old computer's last", "g five, on the new computer"];
await File.WriteAllTextAsync(gPath, gContents[0]);
await Backup(svcA7b, setA7b.Id, target7b, source7b);
await File.WriteAllTextAsync(gPath, gContents[1]);
await Backup(svcA7b, setA7b.Id, target7b, source7b);
string export7b = Path.Combine(root, "behind twice.lithic");
await BackupSetTransfer.ExportAsync(repoA7b, (await repoA7b.GetBackupSetAsync(setA7b.Id))!, export7b, includeHistory: true);
await File.WriteAllTextAsync(gPath, gContents[2]);     // after the export, twice
await Backup(svcA7b, setA7b.Id, target7b, source7b);
await File.WriteAllTextAsync(gPath, gContents[3]);
await Backup(svcA7b, setA7b.Id, target7b, source7b);

var repoB7b = NewCatalog(Path.Combine(root, "B7b"));
var svcB7b = NewService(repoB7b);
var setB7b = (await BackupSetTransfer.ImportAsync(repoB7b, export7b)).Set;
await File.WriteAllTextAsync(gPath, gContents[4]);
await Backup(svcB7b, setB7b.Id, target7b, source7b);
var gRecords = (await repoB7b.GetFileRecordsByPathAsync(setB7b.Id, gPath)).Where(r => !r.IsDeleted).OrderBy(r => r.Version).ToList();
Check(gRecords.Select(r => r.Version).SequenceEqual([1, 2, 3, 4, 5]),
    $"versions 1 to 5, the old computer's v3 among them (got {string.Join(",", gRecords.Select(r => r.Version))})");
Check(gRecords.Count == 5 && gRecords.All(r =>
        File.Exists(Path.Combine(target7b, r.DiscPath))
        && File.ReadAllText(Path.Combine(target7b, r.DiscPath)) == gContents[r.Version - 1]
        && string.Equals(Sha(Path.Combine(target7b, r.DiscPath)), r.Hash, StringComparison.OrdinalIgnoreCase)),
    "each holds its own content, under the hash that content has");

// ===================================================================
// 8. Restore after the destination moved (a new drive letter).
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 8. Restore after the backup folder moved ===");
string moved7 = Path.Combine(root, "dst7_moved");
Directory.Move(target7, moved7);
var stored7 = (await repoB7.GetBackupSetAsync(setB7.Id))!;
stored7.JobOptions!.TargetDirectory = moved7;     // what following the drive does
await repoB7.UpdateBackupSetMetadataAsync(stored7);
Check((await repoB7.GetDiscsForBackupSetAsync(setB7.Id)).All(d => !Directory.Exists(d.Label)),
    "every disc record still names the old folder");

var restorer = new RestoreService(repoB7);
var toRestore = await restorer.MaterializeSelectionAsync(setB7.Id, [source7], []);
string restoreTo = Path.Combine(root, "restored");
var restored = await restorer.RestoreAsync(toRestore,
    new Dictionary<string, string> { [source7[..1].ToUpperInvariant()] = restoreTo });
Check(restored.FilesRestored == 1 && restored.Errors.Count == 0,
    $"the restore found the files at the set's current destination ({restored.FilesRestored} restored, "
    + $"{restored.Errors.Count} error(s){(restored.Errors.Count > 0 ? ": " + restored.Errors[0] : "")})");
string restoredF = Path.Combine(restoreTo, source7[Path.GetPathRoot(source7)!.Length..], "f.txt");
Check(File.Exists(restoredF) && File.ReadAllText(restoredF) == "f four, on the new computer",
    "and restored the latest content");

// ===================================================================
// 9. Restore before any backup has followed the drive to its new letter.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 9. Restore follows the destination's volume, not its stored path ===");
// An imported set keeps the old computer's path until a backup follows the
// drive; restore must find the folder through the volume identity instead.
string moved9 = Path.Combine(root, "dst9_moved");
Directory.Move(moved7, moved9);
var volumes = new Win32VolumeResolver();
string? volumeId = volumes.GetVolumeId(moved9);
string? mount = volumeId is null ? null : volumes.GetCurrentMountPoint(volumeId);
var stored9 = (await repoB7.GetBackupSetAsync(setB7.Id))!;
char unused = Enumerable.Range('M', 14).Select(c => (char)c).First(c => !Directory.Exists($"{c}:\\"));
stored9.JobOptions!.TargetDirectory = $"{unused}:\\{Path.GetFileName(moved9)}";   // the old computer's letter
stored9.JobOptions.DestinationVolumeId = volumeId;
stored9.JobOptions.DestinationSubpath = mount is null ? null : Path.GetRelativePath(mount, moved9);
await repoB7.UpdateBackupSetMetadataAsync(stored9);
if (volumeId is null || mount is null)
{
    Check(false, "the temp folder's volume identity can be read");
}
else
{
    var following = new RestoreService(repoB7, new DestinationResolver(volumes));
    var picked9 = await following.MaterializeSelectionAsync(setB7.Id, [source7], []);
    string restoreTo9 = Path.Combine(root, "restored9");
    var restored9 = await following.RestoreAsync(picked9,
        new Dictionary<string, string> { [source7[..1].ToUpperInvariant()] = restoreTo9 });
    Check(restored9.FilesRestored == 1 && restored9.Errors.Count == 0,
        $"with the stored path on {unused}:, the restore finds the files through the volume ({restored9.FilesRestored} restored, "
        + $"{restored9.Errors.Count} error(s){(restored9.Errors.Count > 0 ? ": " + restored9.Errors[0] : "")})");
    Check((await repoB7.GetBackupSetAsync(setB7.Id))!.JobOptions!.TargetDirectory!.StartsWith($"{unused}:"),
        "and saves nothing: the stored path is left for a backup to update");
}

// ===================================================================
// 10. Export and import say how far they have got, and stop when told.
// ===================================================================
Console.WriteLine();
Console.WriteLine("=== 10. Progress and Cancel on a large history ===");
// A history big enough to take a while: 600,000 records, written straight into
// the set's database (backing up that many files would take far longer).
var repoP = NewCatalog(Path.Combine(root, "P"));
var setP = await repoP.CreateBackupSetAsync(new BackupSet
{
    Name = "large", SourceRoots = [source], CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions { TargetDirectory = target },
});
var discP = await repoP.CreateDiscAsync(new DiscRecord
{
    BackupSetId = setP.Id, Label = target, SequenceNumber = 1, MediaType = MediaType.Directory,
    Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow, LastWrittenUtc = DateTime.UtcNow,
});
const int LargeRecords = 600_000;
using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
           $"Data Source={Path.Combine(repoP.DataDirectory, "sets", $"set-{setP.Id}.db")};Pooling=False"))
{
    raw.Open();
    using var tx = raw.BeginTransaction();
    using var ins = raw.CreateCommand();
    ins.CommandText = """
        INSERT INTO Files (DiscId, SourcePath, DiscPath, SizeBytes, Hash, Version, IsDeleted,
                           SourceLastWriteUtc, BackedUpUtc)
        VALUES ($disc, $path, $discPath, 1000, $hash, 1, 0, $when, $when)
        """;
    ins.Parameters.AddWithValue("$disc", discP.Id);
    var pPath = ins.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
    var pDisc = ins.Parameters.Add("$discPath", Microsoft.Data.Sqlite.SqliteType.Text);
    var pHash = ins.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Text);
    ins.Parameters.AddWithValue("$when", DateTime.UtcNow.ToString("O"));
    for (int i = 0; i < LargeRecords; i++)
    {
        string rel = $@"music\modarchive\leftovers\folder_{i / 1000:D4}\module_{i:D7}_with_a_longish_name.mod";
        pPath.Value = @"C:\" + rel;
        pDisc.Value = @"C\" + rel;
        pHash.Value = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i))).ToLowerInvariant();
        ins.ExecuteNonQuery();
    }
    tx.Commit();

    // And 200 MB of free pages, as a purge leaves: the copy leaves them out, so
    // the size its progress counts toward must too.
    using var scratch = raw.CreateCommand();
    scratch.CommandText = """
        CREATE TABLE Scratch (b BLOB);
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 50000)
        INSERT INTO Scratch SELECT zeroblob(4000) FROM n;
        DROP TABLE Scratch;
        """;
    scratch.ExecuteNonQuery();
}
var setPStored = (await repoP.GetBackupSetAsync(setP.Id))!;
string setPFile = Path.Combine(repoP.DataDirectory, "sets", $"set-{setP.Id}.db");
Console.WriteLine($"  (history: {repoP.GetSetHistorySizeBytes(setP.Id):N0} bytes in use, "
    + $"{FileLength.OrZero(setPFile) + FileLength.OrZero(setPFile + "-wal"):N0} on disk)");

// Every report, in order, and when it came.
List<(long Ms, ProgressReport Report)> Recording(out IProgress<ProgressReport> progress, Action<ProgressReport>? also = null)
{
    var list = new List<(long, ProgressReport)>();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    progress = new SyncProgress<ProgressReport>(r => { lock (list) list.Add((clock.ElapsedMilliseconds, r)); also?.Invoke(r); });
    return list;
}
static bool Rising(IEnumerable<double?> percents)
{
    var p = percents.Select(x => x ?? -1).ToList();
    return p.Count > 0 && p.Zip(p.Skip(1)).All(x => x.Second >= x.First);
}

string bigExport = Path.Combine(root, "large.lithic");
var exportReports = Recording(out var exportProgress);
var exportClock = System.Diagnostics.Stopwatch.StartNew();
await BackupSetTransfer.ExportAsync(repoP, setPStored, bigExport, includeHistory: true, exportProgress);
long fullExportMs = exportClock.ElapsedMilliseconds;
var copySteps = exportReports.Where(r => r.Report.Text.StartsWith("Step 1 of 2: copying the backup history")).ToList();
var zipSteps = exportReports.Where(r => r.Report.Text.StartsWith("Step 2 of 2: compressing")).ToList();
Check(File.Exists(bigExport), $"the large export is written ({fullExportMs:N0} ms)");
Check(copySteps.Count > 0 && copySteps[0].Report.Percent == 0 && Rising(copySteps.Select(r => r.Report.Percent)),
    $"copying the history reports from 0%, rising ({string.Join(", ", copySteps.Select(r => $"{r.Report.Percent:F0}%"))})");
Check(copySteps.Count > 1 && copySteps.Skip(1).All(r => r.Report.Text.Contains(" of about ")),
    $"counting bytes as the copy grows (\"{copySteps.LastOrDefault().Report.Text}\")");
// The last report is the finished copy against the estimate it was measured
// by: near 1 when the estimate counts what the copy writes, and far below it
// when it counts free pages too.
var copied = copySteps.Count == 0 ? null : System.Text.RegularExpressions.Regex.Match(
    copySteps[^1].Report.Text, @"\(([\d,]+) of about ([\d,]+) bytes\)");
double copiedShare = copied is { Success: true }
    ? long.Parse(copied.Groups[1].Value.Replace(",", "")) / (double)long.Parse(copied.Groups[2].Value.Replace(",", ""))
    : 0;
Check(copySteps.Count > 0 && copySteps[^1].Report.Percent == 100 && copiedShare is > 0.85 and < 1.05,
    $"and ends at 100%, having written {copiedShare:P0} of the estimate (\"{copySteps.LastOrDefault().Report.Text}\")");
Check(zipSteps.Count > 0 && Rising(zipSteps.Select(r => r.Report.Percent)) && zipSteps[^1].Report.Percent == 100,
    $"then compressing reports, rising to 100% ({string.Join(", ", zipSteps.Select(r => $"{r.Report.Percent:F0}%"))})");
var exportGaps = exportReports.Zip(exportReports.Skip(1)).Select(p => p.Second.Ms - p.First.Ms).ToList();
Check(exportGaps.Count == 0 || exportGaps.Max() < 3 * ProgressCadence.IntervalMs,
    $"with no long silence between reports (longest {exportGaps.DefaultIfEmpty().Max()} ms)");

// Cancelled while the history is being copied: the copy - one SQLite statement
// - is interrupted, not waited out. The cancel comes as soon as the copy is
// seen writing, not from a report, so it lands early in the copy however fast
// this machine copies, and the stop is measured against the whole copy's time.
long copyMs = zipSteps.Count > 0 && copySteps.Count > 0 ? zipSteps[0].Ms - copySteps[0].Ms : 0;
Check(copyMs >= 200, $"the history takes long enough to copy to time a cancel against ({copyMs:N0} ms)");
string cancelledExport = Path.Combine(root, "cancelled.lithic");
using (var cts = new CancellationTokenSource())
using (var stopWatching = new CancellationTokenSource())
{
    var cancelClock = System.Diagnostics.Stopwatch.StartNew();
    long cancelledAtMs = -1, writtenAtCancel = 0;
    var watcher = Task.Run(() =>
    {
        while (!stopWatching.IsCancellationRequested)
        {
            try
            {
                var copy = Directory.EnumerateFiles(root, "cancelled.lithic.*.history.partial").FirstOrDefault();
                long length = copy is null ? 0 : new FileInfo(copy).Length;
                if (length > 0)
                {
                    cancelledAtMs = cancelClock.ElapsedMilliseconds;
                    writtenAtCancel = length;
                    cts.Cancel();
                    return;
                }
            }
            catch (IOException) { /* the file went between listing and measuring */ }
            Thread.Sleep(1);
        }
    });
    bool cancelled = false;
    try { await BackupSetTransfer.ExportAsync(repoP, setPStored, cancelledExport, true, null, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    long stopMs = cancelClock.ElapsedMilliseconds - cancelledAtMs;
    stopWatching.Cancel();
    await watcher;
    Check(cancelled && cancelledAtMs >= 0 && stopMs < copyMs / 4,
        $"cancelled mid-copy, the export stops at once ({stopMs:N0} ms after the cancel, which came with "
        + $"{writtenAtCancel:N0} bytes copied, against {copyMs:N0} ms for the whole copy)");
    Check(!File.Exists(cancelledExport) && !Directory.EnumerateFiles(root, "cancelled.lithic*").Any(),
        "and leaves nothing behind");
}

// Cancelled while compressing: the counted copy stops between blocks.
string cancelledZip = Path.Combine(root, "cancelled-zip.lithic");
using (var cts = new CancellationTokenSource())
{
    Recording(out var cancelOnZip, r => { if (r.Text.StartsWith("Step 2 of 2: compressing")) cts.Cancel(); });
    bool cancelled = false;
    try { await BackupSetTransfer.ExportAsync(repoP, setPStored, cancelledZip, true, cancelOnZip, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && !Directory.EnumerateFiles(root, "cancelled-zip.lithic*").Any(),
        $"cancelled while compressing, the export stops and leaves nothing behind (cancelled: {cancelled})");
}

// A cancel reaches a catalog read part-way, not only before it starts: the
// read of a whole history (Cleanup, the post-edit scan) can take many seconds.
using (var cts = new CancellationTokenSource())
{
    int readWhenCancelled = 0;
    bool cancelled = false;
    try
    {
        await repoP.GetAllFilesForBackupSetAsync(setP.Id, cts.Token, new SyncProgress<int>(read =>
        {
            if (readWhenCancelled == 0)
            {
                readWhenCancelled = read;
                cts.Cancel();
            }
        }));
    }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && readWhenCancelled < LargeRecords,
        $"a catalog read cancelled part-way stops there (cancelled after {readWhenCancelled:N0} of {LargeRecords:N0} records; stopped: {cancelled})");
}

// Import: extracting counts bytes, importing counts records, to the total.
var repoQ = NewCatalog(Path.Combine(root, "Q"));
var importReports = Recording(out var importProgress);
var importedP = await BackupSetTransfer.ImportAsync(repoQ, bigExport, importProgress);
var extractSteps = importReports.Where(r => r.Report.Text.StartsWith("Step 1 of 2: extracting")).ToList();
var recordSteps = importReports.Where(r => r.Report.Text.StartsWith("Step 2 of 2: importing")).ToList();
Check(importedP.FileRecords == LargeRecords, $"the large history imports ({importedP.FileRecords:N0} records)");
Check(extractSteps.Count > 0 && Rising(extractSteps.Select(r => r.Report.Percent)),
    $"extracting reports, rising ({extractSteps.Count} report(s))");
Check(recordSteps.Count > 0 && Rising(recordSteps.Select(r => r.Report.Percent))
      && recordSteps[^1].Report.Percent == 100
      && recordSteps[^1].Report.Text.Contains($"{LargeRecords:N0} of {LargeRecords:N0} records"),
    $"importing counts the records up to all of them (\"{recordSteps.LastOrDefault().Report.Text}\")");

// A cancel while extracting, or while the records go in (when the set exists
// already, and must go again), adds no set and stages nothing that stays.
int setsInQ = (await repoQ.GetAllBackupSetsAsync()).Count;
using (var cts = new CancellationTokenSource())
{
    Recording(out var cancelOnExtract, r => { if (r.Text.StartsWith("Step 1 of 2: extracting")) cts.Cancel(); });
    bool cancelled = false;
    try { await BackupSetTransfer.ImportAsync(repoQ, bigExport, cancelOnExtract, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    int setsAfter = (await repoQ.GetAllBackupSetsAsync()).Count;
    Check(cancelled && setsAfter == setsInQ
          && !Directory.EnumerateFiles(repoQ.DataDirectory, "lithic-import-*").Any(),
        $"cancelled while extracting, no backup set is added and nothing is left (cancelled: {cancelled}, sets {setsInQ} -> {setsAfter})");
}
using (var cts = new CancellationTokenSource())
{
    Recording(out var cancelOnImport, r =>
    {
        if (r.Text.StartsWith("Step 2 of 2") && r.Percent is not null)
            cts.Cancel();
    });
    bool cancelled = false;
    try { await BackupSetTransfer.ImportAsync(repoQ, bigExport, cancelOnImport, cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    int setsAfter = (await repoQ.GetAllBackupSetsAsync()).Count;
    Check(cancelled && setsAfter == setsInQ,
        $"cancelled while importing, no backup set is added (cancelled: {cancelled}, sets {setsInQ} -> {setsAfter})");
    Check(!Directory.EnumerateFiles(repoQ.DataDirectory, "lithic-import-*").Any(),
        "and the extracted history is cleaned up");
}

Console.WriteLine();
// Closed before the folder goes, or SQLite's open files keep it from going.
foreach (var repo in new[] { repoA, repoB, repoA7, repoB7, repoA7b, repoB7b, repoP, repoQ })
    repo.Dispose();
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
try { Directory.Delete(root, recursive: true); } catch { /* temp; best effort */ }
Check(!Directory.Exists(root), "the harness leaves no temp folder behind");
if (failures == 0)
{
    Console.WriteLine("ALL PASS");
    return 0;
}
Console.WriteLine($"{failures} CHECK(S) FAILED");
return 1;
