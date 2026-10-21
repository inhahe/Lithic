// The catalog's "everything under this folder" reads, held to one meaning.
//
// Six reads take a folder and answer for the paths under it: the records under
// it (the post-edit scan, folder renames), how many there are, the latest of
// each, their count and size, the restore browser's children, and the remap of
// the prefix. Each is a range on the case-insensitive SourcePath index. This
// checks that every one of them takes exactly the folder's paths:
//
//   - in any letter case (Windows paths ignore it);
//   - and the path itself, when it names a file;
//   - but no sibling that merely starts with the same name ("a bar", "a!", "a]");
//   - including names whose first character lies beyond U+FFFF (an emoji):
//     a range's upper bound of "prefix + U+FFFF" sorts BELOW them in UTF-8,
//     so such a folder vanished from all of these reads;
//
// and that reading or counting a small folder costs a sliver of the whole set:
// the planner reads every file of the set for a query shaped wrong (an OR, a
// join on Discs, an ORDER BY the BINARY index can serve), though the NOCASE
// index is there.

using System.Diagnostics;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using Microsoft.Data.Sqlite;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "lithic_pathrange_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));

var set = await repo.CreateBackupSetAsync(new BackupSet
{
    Name = "path ranges", SourceRoots = [@"C:\pr"], CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions { TargetDirectory = Path.Combine(root, "dst") },
});
var disc = await repo.CreateDiscAsync(new DiscRecord
{
    BackupSetId = set.Id, Label = "dst", SequenceNumber = 1, MediaType = MediaType.Directory,
    Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow, LastWrittenUtc = DateTime.UtcNow,
});

const string Emoji = "\U0001F600";   // beyond U+FFFF: a surrogate pair, four bytes in UTF-8
string[] under =
[
    @"C:\pr\a\one.txt",
    @"C:\pr\a\sub\two.txt",
    @"C:\PR\A\three.txt",                     // another letter case of the same folder
    $@"C:\pr\a\{Emoji} emoji\four.txt",
    $@"C:\pr\a\{Emoji}.txt",
];
string[] beside =
[
    @"C:\pr\a bar\x.txt",                     // ' ' sorts before '\'
    @"C:\pr\a!\y.txt",
    @"C:\pr\a]\z.txt",                        // ']' sorts right after '\'
    @"C:\pr\b\other.txt",
    @"C:\pr\file.txt",
];
const int Bulk = 300_000;                     // elsewhere in the set, for the cost check

using (var raw = new SqliteConnection(
           $"Data Source={Path.Combine(repo.DataDirectory, "sets", $"set-{set.Id}.db")};Pooling=False"))
{
    raw.Open();
    using var tx = raw.BeginTransaction();
    using var ins = raw.CreateCommand();
    ins.CommandText = """
        INSERT INTO Files (DiscId, SourcePath, DiscPath, SizeBytes, Hash, Version, IsDeleted,
                           SourceLastWriteUtc, BackedUpUtc)
        VALUES ($disc, $path, $path, 100, 'h', 1, 0, $when, $when)
        """;
    ins.Parameters.AddWithValue("$disc", disc.Id);
    var pPath = ins.Parameters.Add("$path", SqliteType.Text);
    ins.Parameters.AddWithValue("$when", DateTime.UtcNow.ToString("O"));
    foreach (string p in under.Concat(beside))
    {
        pPath.Value = p;
        ins.ExecuteNonQuery();
    }
    for (int i = 0; i < Bulk; i++)
    {
        pPath.Value = $@"C:\pr\bulk\folder_{i / 1000:D4}\file_{i:D7}.dat";
        ins.ExecuteNonQuery();
    }
    tx.Commit();
}

static string Show(IEnumerable<string> paths) => string.Join(", ", paths.Order(StringComparer.OrdinalIgnoreCase));
bool Same(IEnumerable<string> got, IEnumerable<string> want) =>
    got.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(want.Order(StringComparer.OrdinalIgnoreCase),
        StringComparer.OrdinalIgnoreCase);

Console.WriteLine("=== everything under C:\\pr\\a, and nothing beside it ===");
var records = await repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"C:\pr\a");
Check(Same(records.Select(r => r.SourcePath), under),
    $"the records under a folder: {Show(records.Select(r => r.SourcePath))}");
var fileItself = await repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"c:\PR\file.txt");
Check(fileItself.Count == 1 && fileItself[0].SourcePath == @"C:\pr\file.txt",
    $"and a path naming a file, in another case, finds the file ({fileItself.Count})");
Check(await repo.CountFilesUnderSourcePrefixAsync(set.Id, @"C:\pr\a") == under.Length,
    $"the count under it ({await repo.CountFilesUnderSourcePrefixAsync(set.Id, @"C:\pr\a")})");
var latest = await repo.GetActiveLatestRecordsUnderPrefixAsync(set.Id, @"C:\pr\a");
Check(Same(latest.Select(r => r.SourcePath), under),
    $"the latest record of each path under it: {Show(latest.Select(r => r.SourcePath))}");
var (count, bytes) = await repo.GetActiveSubtreeStatsAsync(set.Id, @"C:\pr\a");
Check(count == under.Length && bytes == 100L * under.Length,
    $"its file count and size ({count}, {bytes:N0} bytes)");
var children = await repo.GetRestoreChildrenAsync(set.Id, @"C:\pr\a");
string[] wantChildren = ["sub", $"{Emoji} emoji", "one.txt", "three.txt", $"{Emoji}.txt"];
Check(Same(children.Select(c => c.Name), wantChildren) && children.Count == wantChildren.Length,
    $"the restore browser's children of it: {Show(children.Select(c => c.Name))}");

Console.WriteLine();
Console.WriteLine("=== a small folder costs a sliver of the whole set ===");
// Warm both, then take the best of five: the cost of reading 5 records of
// 300,010 must not be the cost of reading all of them.
await repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"C:\pr\a");
await repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"C:\pr");
long Best(Func<Task> read)
{
    long best = long.MaxValue;
    for (int i = 0; i < 5; i++)
    {
        var sw = Stopwatch.StartNew();
        read().GetAwaiter().GetResult();
        best = Math.Min(best, sw.ElapsedTicks);
    }
    return best;
}
long small = Best(() => repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"C:\pr\a"));
long all = Best(() => repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"C:\pr"));
double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
Check(small * 50 < all,
    $"reading the 5 records under C:\\pr\\a takes {ms(small):F2} ms, against {ms(all):F0} ms for all {Bulk + under.Length + beside.Length:N0}");
long countSmall = Best(() => repo.CountFilesUnderSourcePrefixAsync(set.Id, @"C:\pr\a"));
long countAll = Best(() => repo.CountFilesUnderSourcePrefixAsync(set.Id, @"C:\pr"));
Check(countSmall * 50 < countAll,
    $"so does counting them: {ms(countSmall):F2} ms, against {ms(countAll):F0} ms for all");

Console.WriteLine();
Console.WriteLine("=== renaming the folder moves exactly its paths ===");
int moved = await repo.RemapSourcePathPrefixAsync(set.Id, @"C:\pr\a", @"C:\pr\renamed");
var after = await repo.GetFileRecordsUnderDirectoryAsync(set.Id, @"C:\pr\renamed");
var leftBeside = (await repo.GetAllFilesForBackupSetAsync(set.Id))
    .Where(r => !r.SourcePath.StartsWith(@"C:\pr\bulk\", StringComparison.Ordinal))
    .Select(r => r.SourcePath).ToList();
Check(moved == under.Length && after.Count == under.Length,
    $"the remap moved {moved} paths, and {after.Count} are now under the new name");
Check(beside.All(leftBeside.Contains),
    $"and left the siblings where they were: {Show(leftBeside.Where(p => !p.Contains(@"\renamed\")))}");

Console.WriteLine();
repo.Dispose();
SqliteConnection.ClearAllPools();
for (int attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
{
    try { Directory.Delete(root, recursive: true); } catch { }
    if (Directory.Exists(root))
        Thread.Sleep(100);
}
Check(!Directory.Exists(root), "the harness leaves no temp folder behind");
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
