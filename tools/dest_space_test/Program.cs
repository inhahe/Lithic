// Regression test for DestinationSpaceMonitor's "destination drive is full"
// alert (known-issues.md: "'Destination drive full' warning fires on a transient
// dip and then sits on screen after the drive recovers").
//
// What went wrong in the field
// ----------------------------
// The user got a modal saying backup set "test backup" couldn't back up because
// drive D: was full, and found D: with ~38 GB free. The alert was not wrong when
// it was raised -- Windows' own System log recorded Volsnap 24 ("insufficient
// disk space on volume D: to grow the shadow copy storage") at 19:00:48 followed
// by Volsnap 35 ("shadow copies of volume D: were aborted") at 19:03:20, i.e. D:
// genuinely hit zero free for ~2.5 minutes while VSS grew a diff area, then
// recovered tens of GB when Windows discarded the shadow copies. Two separate
// defects turned a true observation into something indistinguishable from a bug:
//
//   1. A SINGLE low sample armed the alert, so a dip Windows resolves by itself
//      is enough to interrupt the user.
//   2. The alert is displayed asynchronously and is owner-less, so the box can
//      be read long after the fact -- stating "is full" in the present tense
//      about a drive that now has 38 GB free.
//
// The fix debounces the alert (ConsecutiveLowSweepsBeforeAlert sweeps in a row),
// stamps the observation time into the message, and exposes IsStillFull so the
// GUI re-checks immediately before showing the dialog. This test pins all of it
// by driving the monitor with a scripted free-space sequence.

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Services;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  \u2713 " : "  \u2717 FAIL: ") + msg);
    if (!cond) failures++;
}

const long GB = 1024L * 1024 * 1024;
const long Low = 200L * 1024 * 1024;   // 200 MB -- genuinely below the 1 GB floor

string root = Path.Combine(Path.GetTempPath(), "lithic_destspace_" + Guid.NewGuid().ToString("N"));
string destDir = Path.Combine(root, "dest");
Directory.CreateDirectory(destDir);
string destRoot = Path.GetPathRoot(destDir)!;

var repo = new SqliteCatalogRepository(Path.Combine(root, "catalog.db"));

async Task<BackupSet> AddSetAsync(ICatalogRepository target, string name, bool continuous) =>
    await target.CreateBackupSetAsync(new BackupSet
    {
        Name = name,
        SourceRoots = new List<string> { root },
        CreatedUtc = DateTime.UtcNow,
        DefaultMediaType = MediaType.Directory,
        JobOptions = new JobOptions
        {
            TargetDirectory = destDir,
            Schedule = new BackupSchedule
            {
                Enabled = true,
                Mode = continuous ? ScheduleMode.Continuous : ScheduleMode.Interval,
            },
        },
    });

var continuousSet = await AddSetAsync(repo, "continuous set", continuous: true);

// The free-space reading the monitor will see on its next sweep.
long freeNow = 500L * GB;
var monitor = new DestinationSpaceMonitor(repo, new FixedResolver(destDir), _ => freeNow);

var alerts = new List<DestinationFullAlert>();
monitor.DestinationFull += a => alerts.Add(a);

var statusSweeps = new List<IReadOnlyDictionary<int, DestinationSpaceStatus>>();
monitor.StatusUpdated += s => statusSweeps.Add(s);

Console.WriteLine($"Default debounce: {monitor.ConsecutiveLowSweepsBeforeAlert} consecutive sweeps");
Console.WriteLine();

// ---------------------------------------------------------------------------
Console.WriteLine("1. Plenty of space -> no alert");
await monitor.CheckAsync();
Check(alerts.Count == 0, "no alert while the drive is healthy");
Check(statusSweeps.Count == 1 && !statusSweeps[0][continuousSet.Id].IsFull,
      "per-row status reports not-full");

// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("2. A transient dip shorter than the debounce window -> no alert");
// This is the field scenario: VSS eats the volume for a couple of sweeps, then
// Windows discards the shadow copies and the space comes back.
freeNow = Low;
await monitor.CheckAsync();
Check(alerts.Count == 0, "one low sweep does not alert");
await monitor.CheckAsync();
Check(alerts.Count == 0, "two low sweeps still do not alert");

// The row status must NOT be debounced -- it is a live readout, and hiding a
// real out-of-space condition there would be worse than an over-eager modal.
Check(statusSweeps[^1][continuousSet.Id].IsFull,
      "per-row status reports full immediately, undebounced");

freeNow = 500L * GB;   // recovered on its own
await monitor.CheckAsync();
Check(alerts.Count == 0, "recovery before the third sweep means the user is never interrupted");

// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("3. A sustained outage -> exactly one alert");
freeNow = Low;
await monitor.CheckAsync();
Check(alerts.Count == 0, "sweep 1 of the new run: silent");
await monitor.CheckAsync();
Check(alerts.Count == 0, "sweep 2: still silent");
await monitor.CheckAsync();
Check(alerts.Count == 1, $"sweep 3 raises the alert (got {alerts.Count})");

await monitor.CheckAsync();
await monitor.CheckAsync();
Check(alerts.Count == 1, "still full -> no repeat alerts (one per fill event)");

var alert = alerts[0];
Check(alert.SetName == "continuous set", "alert names the set");
Check(string.Equals(alert.Root.TrimEnd('\\'), destRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
      $"alert names the destination root (got '{alert.Root}')");
Check(alert.Message.Contains("ran out of space at "),
      "message is time-stamped, not an unqualified present-tense 'is full'");
Check(alert.Message.Contains(alert.ObservedAt.ToLocalTime().ToString("t")),
      "message states the observation time");

// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("4. IsStillFull re-check gate");
Check(monitor.IsStillFull(alert.Root), "still full while the drive is still full");
freeNow = 38L * GB;   // what the user actually saw in Explorer
Check(!monitor.IsStillFull(alert.Root),
      "reports recovered once space is back, so the GUI drops the stale dialog");

// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("5. Re-arming after recovery");
await monitor.CheckAsync();   // 38 GB free: above threshold + recovery margin
freeNow = Low;
await monitor.CheckAsync();
await monitor.CheckAsync();
await monitor.CheckAsync();
Check(alerts.Count == 2, $"a second, distinct fill event alerts again (got {alerts.Count})");
monitor.Dispose();

// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("6. Non-continuous sets never raise the modal");
// They run interactively, so the user is already watching; the persistent row
// status carries the same information without an interruption.
string interactiveRoot = Path.Combine(root, "interactive");
Directory.CreateDirectory(interactiveRoot);
var interactiveRepo = new SqliteCatalogRepository(Path.Combine(interactiveRoot, "catalog.db"));
await AddSetAsync(interactiveRepo, "interactive set", continuous: false);

var quiet = new List<DestinationFullAlert>();
IReadOnlyDictionary<int, DestinationSpaceStatus>? lastStatus = null;
var interactiveOnly = new DestinationSpaceMonitor(
    interactiveRepo, new FixedResolver(destDir), _ => Low);
interactiveOnly.DestinationFull += a => quiet.Add(a);
interactiveOnly.StatusUpdated += s => lastStatus = s;
for (int i = 0; i < 5; i++)
    await interactiveOnly.CheckAsync();
Check(quiet.Count == 0, "no modal for an interactive set");
Check(lastStatus is { Count: > 0 } && lastStatus.Values.All(v => v.IsFull),
      "but its row status still shows full");
interactiveOnly.Dispose();

// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("7. An unreadable drive breaks the streak rather than counting as low");
var streakAlerts = new List<DestinationFullAlert>();
bool readable = true;
var flaky = new DestinationSpaceMonitor(
    repo, new FixedResolver(destDir), _ => readable ? Low : (long?)null);
flaky.DestinationFull += a => streakAlerts.Add(a);
await flaky.CheckAsync();                      // low (streak 1)
await flaky.CheckAsync();                      // low (streak 2)
readable = false; await flaky.CheckAsync();    // unreadable -> streak discarded
readable = true;  await flaky.CheckAsync();    // low (streak 1 again)
await flaky.CheckAsync();                      // low (streak 2)
Check(streakAlerts.Count == 0, "a gap in the readings restarts the count instead of completing it");
await flaky.CheckAsync();                      // low (streak 3)
Check(streakAlerts.Count == 1, "three uninterrupted low readings do alert");
flaky.Dispose();

try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures;

/// <summary>
/// Resolver stub: every set resolves to the same connected path, so the test can
/// focus on the free-space logic rather than on volume identity.
/// </summary>
file sealed class FixedResolver(string livePath) : IDestinationResolver
{
    public DestinationResolution Resolve(JobOptions options) =>
        new(LivePath: livePath, IsConnected: true, LetterChanged: false,
            PreviousPath: livePath, MetadataChanged: false);
}
