// Verify Integrity says what it is doing, how far it is, and for how long.
//
// The window sat on "Preparing verification..." with nothing to say whether it
// was doing anything: the view model refused its own first run (its loading
// flag started true), and the service, once running, said "Loading catalog..."
// and "Scanning source files..." with no count, through a read sorted before
// its first row and a scan with no progress at all.
//
// This drives the real VerifyService against a throwaway catalog of 200,000
// records and checks that every step reports, at the ProgressCadence, its step
// number, a count (and a percentage where the total is known) and its elapsed
// time - also through a stretch where the step has nothing to count: the
// set's catalog held by another transaction.
// (The view model's start is checked end to end in editor_save_test.)

using System.Diagnostics;
using System.Text.RegularExpressions;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using Microsoft.Data.Sqlite;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "lithic_verifyprogress_" + Guid.NewGuid().ToString("N"));
string src = Path.Combine(root, "src");
string dst = Path.Combine(root, "dst");
Directory.CreateDirectory(src);
Directory.CreateDirectory(dst);
const int SourceFiles = 10;
for (int i = 0; i < SourceFiles; i++)
    File.WriteAllText(Path.Combine(src, $"source_{i}.txt"), $"source file {i}");
File.WriteAllText(Path.Combine(dst, "stored.bin"), "the one stored copy every record points at");

var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));
var set = await repo.CreateBackupSetAsync(new BackupSet
{
    Name = "verify progress", SourceRoots = [src], CreatedUtc = DateTime.UtcNow,
    DefaultMediaType = MediaType.Directory,
    JobOptions = new JobOptions { TargetDirectory = dst },
});
var disc = await repo.CreateDiscAsync(new DiscRecord
{
    BackupSetId = set.Id, Label = dst, SequenceNumber = 1, MediaType = MediaType.Directory,
    Status = BurnSessionStatus.Completed, CreatedUtc = DateTime.UtcNow, LastWrittenUtc = DateTime.UtcNow,
});

// The records: elsewhere than the sources, all stored at one file that exists,
// so the check of each passes and the time goes on reading and checking them.
const int Records = 200_000;
using (var raw = new SqliteConnection(
           $"Data Source={Path.Combine(repo.DataDirectory, "sets", $"set-{set.Id}.db")};Pooling=False"))
{
    raw.Open();
    using var tx = raw.BeginTransaction();
    using var ins = raw.CreateCommand();
    ins.CommandText = """
        INSERT INTO Files (DiscId, SourcePath, DiscPath, SizeBytes, Hash, Version, IsDeleted,
                           SourceLastWriteUtc, BackedUpUtc)
        VALUES ($disc, $path, 'stored.bin', 100, 'h', 1, 0, $when, $when)
        """;
    ins.Parameters.AddWithValue("$disc", disc.Id);
    var pPath = ins.Parameters.Add("$path", SqliteType.Text);
    ins.Parameters.AddWithValue("$when", DateTime.UtcNow.ToString("O"));
    for (int i = 0; i < Records; i++)
    {
        pPath.Value = $@"C:\elsewhere\folder_{i / 1000:D4}\file_{i:D7}.dat";
        ins.ExecuteNonQuery();
    }
    tx.Commit();
}

var job = new BackupJob
{
    BackupSetId = set.Id,
    Sources = [new SourceSelection { Path = src, IsDirectory = true, IsSelected = true }],
    TargetDirectory = dst,
};
var service = new VerifyService(repo, new FileScanner(repo));

// Every report, in order, and when it came.
List<(long Ms, VerifyProgress Report)> Recording(out IProgress<VerifyProgress> progress)
{
    var list = new List<(long, VerifyProgress)>();
    var clock = Stopwatch.StartNew();
    progress = new SyncProgress<VerifyProgress>(r => { lock (list) list.Add((clock.ElapsedMilliseconds, r)); });
    return list;
}
var elapsedAtEnd = new Regex(@" - \d+:\d{2}(:\d{2})?$");
string Text(VerifyProgress r) => r.StatusMessage ?? "";

Console.WriteLine("=== 1. Each step reports its number, its count and its time ===");
var reports = Recording(out var progress);
var result = await Task.Run(() => service.VerifyAsync(job, dst, verifyContents: false, progress));
int First(string step) => reports.FindIndex(r => Text(r.Report).StartsWith(step, StringComparison.Ordinal));
int Last(string step) => reports.FindLastIndex(r => Text(r.Report).StartsWith(step, StringComparison.Ordinal));
int s1 = First("Step 1 of 3"), s2 = First("Step 2 of 3"), s3 = First("Step 3 of 3");
int done = reports.FindIndex(r => Text(r.Report) == "Verification complete.");
Check(s1 >= 0 && s2 > Last("Step 1 of 3") && s3 > Last("Step 2 of 3") && done > Last("Step 3 of 3"),
    $"the steps report in order: loading, scanning, checking, then complete ({reports.Count} reports)");
var steps = reports.Where(r => Text(r.Report).StartsWith("Step ", StringComparison.Ordinal)).ToList();
Check(steps.Count > 0 && steps.All(r => elapsedAtEnd.IsMatch(Text(r.Report))),
    $"every step's report ends with its elapsed time (\"{(steps.Count > 0 ? Text(steps[^1].Report) : "")}\")");
var loading = reports.Where(r => Text(r.Report).StartsWith("Step 1 of 3", StringComparison.Ordinal)).ToList();
Check(loading.Count > 0 && Text(loading[^1].Report).Contains($"of {Records:N0} records", StringComparison.Ordinal),
    $"loading counts against the catalog's {Records:N0} records (\"{(loading.Count > 0 ? Text(loading[^1].Report) : "")}\")");
var checking = reports.Where(r => Text(r.Report).StartsWith("Step 3 of 3", StringComparison.Ordinal)).ToList();
var checkPercents = checking.Select(r => r.Report.Percentage).ToList();
Check(checking.Count > 0 && checking.All(r => r.Report.TotalItems == Records)
      && checkPercents.Zip(checkPercents.Skip(1)).All(p => p.Second >= p.First),
    $"checking counts the records with a rising percentage ({string.Join(", ", checkPercents.Select(p => $"{p:F0}%"))})");
Check(done >= 0 && reports[done].Report.Percentage == 100, "and the run ends at 100%");
var gaps = reports.Zip(reports.Skip(1)).Select(p => p.Second.Ms - p.First.Ms).ToList();
Check(gaps.Count == 0 || gaps.Max() < 3 * ProgressCadence.IntervalMs,
    $"with no silence longer than three cadences (longest {gaps.DefaultIfEmpty().Max()} ms)");
Check(result.RecordsChecked == Records && result.SourceFilesChecked == SourceFiles
      && result.Issues.Count == SourceFiles,
    $"and it verifies what it did before ({result.RecordsChecked:N0} records, {result.SourceFilesChecked} source files, "
    + $"{result.Issues.Count} issues: the source files not backed up)");

Console.WriteLine();
Console.WriteLine("=== 2. A step with nothing to count still shows it is going ===");
// The set's catalog held by a transaction for two seconds: the load waits for
// it. Taken on a flow of its own, as the transaction's owner flows to whatever
// the flow that took it starts.
const int HoldMs = 2_000;
var held = await Task.Run(() => repo.BeginTransactionAsync(set.Id));
var heldReports = Recording(out var heldProgress);
var clockHeld = Stopwatch.StartNew();
var verifying = Task.Run(() => service.VerifyAsync(job, dst, verifyContents: false, heldProgress));
await Task.Delay(HoldMs);
long releasedAtMs = clockHeld.ElapsedMilliseconds;
held.Dispose();
await verifying;
var whileHeld = heldReports.Where(r => r.Ms < releasedAtMs - 50).ToList();
var clocks = whileHeld.Select(r => elapsedAtEnd.Match(Text(r.Report)).Value).Distinct().ToList();
Check(whileHeld.Count >= HoldMs / ProgressCadence.IntervalMs - 1
      && whileHeld.All(r => Text(r.Report).StartsWith("Step 1 of 3", StringComparison.Ordinal)),
    $"while the catalog is held, the load keeps reporting ({whileHeld.Count} reports in {HoldMs:N0} ms)");
Check(clocks.Count >= 2,
    $"and its time moves on meanwhile ({string.Join(",", clocks.Select(c => c.Trim(' ', '-')))})");

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
