// Tests for MachineSettings: the settings that govern backups, kept in one file
// every account reads, so that the Worker (LocalSystem) sees what the app saves.
// See "the Worker never saw the settings the app saves" in known-issues.md.
//
//   1. The first start carries the settings over from the per-user file, where
//      they were kept before, under the same names, and writes the machine file.
//   2. Once the machine file exists it wins; the per-user file is not read again.
//   3. With neither file there are defaults, and nothing is written.
//   4. A missing file and an unreadable one are told apart; a UTF-8 byte-order
//      mark is read past.
//   5. A save replaces the file in one step: a reader looping alongside never
//      sees part of one, and no save fails because the reader has it open. A
//      control shows that a reader in the middle of an in-place write does.
//   6. Two saves at once both land whole, and leave no temporary file.
//   7. AtomicFile replaces a file that a reader holds open sharing delete access,
//      which File.Move cannot (shown alongside as a control).
//   8. UserSettings writes back fields it does not know, so the old backup
//      settings stay in the per-user file until a carry-over succeeds.
//   9. LiveMemoryPolicy: a running backup keeps the job's own budget until the
//      settings file is written, then follows it; it looks about once a second,
//      and an unreadable file leaves the budget as it was.
//  10. The time the file was written moves with each save.
//  11. MachineSettingsReader reads again only after a save, keeps the last good
//      settings while the file is unreadable, and says so once.
//  12. A save sweeps away temporary files a crash left more than a minute ago,
//      and TrySave says why it failed.
//
// Every file lives in a temp folder; the real machine file is never touched.

using System.Text;
using System.Text.Json;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

Console.OutputEncoding = Encoding.UTF8;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "lithic_settings_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Sub(string name) => Path.Combine(root, name, "settings.json");

try
{
    // 1 ---------------------------------------------------------------------
    // A per-user file as the app wrote it before the split: the three backup
    // settings at the top level, among the user's own preferences.
    var rules = new ContinuousRules();
    rules.MaxWaitTiers.Clear();
    string legacyJson = JsonSerializer.Serialize(new
    {
        SuppressBackupSuggestions = true,
        MemoryBudget = new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 3 },
        DiscStagingMode = DiscStagingMode.InPlace,
        CheckForUpdates = false,
        ReconcileMode = ReconcileAfterEditMode.Never,
        ContinuousRules = rules,
        LastSettingsTab = 2,
    });
    string machinePath = Sub("machine");
    string legacyPath = Sub("user");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    File.WriteAllText(legacyPath, legacyJson);

    bool carriedOver = MachineSettings.CarryOverIfMissing(machinePath, legacyPath);
    var carried = MachineSettings.Load(machinePath);
    Check(carriedOver && carried.MemoryBudget.Mode == MemoryBudgetMode.Fixed && carried.MemoryBudget.FixedGb == 3
          && carried.DiscStagingMode == DiscStagingMode.InPlace
          && carried.ContinuousRules.MaxWaitTiers.Count == 0,
        "1 the backup settings carry over from the per-user file into the machine file");

    // 2 ---------------------------------------------------------------------
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
    {
        MemoryBudget = new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 9 },
    }));
    bool again = MachineSettings.CarryOverIfMissing(machinePath, legacyPath);
    Check(!again && MachineSettings.Load(machinePath).MemoryBudget.FixedGb == 3,
        "2 once the machine file exists, the per-user file is not read again");

    // 3 ---------------------------------------------------------------------
    string emptyMachine = Sub("none");
    bool fromNothing = MachineSettings.CarryOverIfMissing(emptyMachine, Sub("nobody"));
    var defaults = MachineSettings.Load(emptyMachine);
    Check(!fromNothing && !File.Exists(emptyMachine)
          && defaults.DiscStagingMode == DiscStagingMode.TemporaryCopy
          && defaults.MemoryBudget.Mode == new MemoryBudgetOptions().Mode,
        "3 with neither file there are defaults, and nothing is written");

    // 4 ---------------------------------------------------------------------
    Check(MachineSettings.TryLoad(out _, emptyMachine) == MachineSettings.LoadResult.Absent,
        "4 a missing file reads as absent");
    string brokenPath = Sub("broken");
    Directory.CreateDirectory(Path.GetDirectoryName(brokenPath)!);
    File.WriteAllText(brokenPath, "{ \"MemoryBudget\": ");
    Check(MachineSettings.TryLoad(out _, brokenPath) == MachineSettings.LoadResult.Unreadable,
        "4 a broken file reads as unreadable, not as the defaults");
    string bomPath = Sub("bom");
    Directory.CreateDirectory(Path.GetDirectoryName(bomPath)!);
    File.WriteAllText(bomPath, JsonSerializer.Serialize(new { DiscStagingMode = DiscStagingMode.InPlace }),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    Check(File.ReadAllBytes(bomPath)[0] == 0xEF
          && MachineSettings.TryLoad(out var withBom, bomPath) == MachineSettings.LoadResult.Loaded
          && withBom.DiscStagingMode == DiscStagingMode.InPlace,
        "4 a file that starts with a byte-order mark is read");

    // 5 ---------------------------------------------------------------------
    // The marker is InPlace: the default, TemporaryCopy, is what a torn or
    // unreadable file reads back as.
    var marked = new MachineSettings { DiscStagingMode = DiscStagingMode.InPlace };
    for (int i = 0; i < 400; i++)
        marked.ContinuousRules.MaxWaitTiers.Add(new MaxWaitMaskTier { Name = $"tier {i}", IncludeMasks = [$"*.pattern{i}"], MaxWaitSeconds = i });
    string racePath = Sub("race");
    marked.Save(racePath);

    int torn = 0, failedSaves = 0;
    using (var stop = new CancellationTokenSource())
    {
        // Every 5 ms, some two hundred times as often as a running backup looks.
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (MachineSettings.TryLoad(out var seen, racePath) != MachineSettings.LoadResult.Loaded
                    || seen.DiscStagingMode != DiscStagingMode.InPlace)
                    Interlocked.Increment(ref torn);
                Thread.Sleep(5);
            }
        });
        // Back to back: each save also meets the virus scanner looking at the
        // file the previous one made.
        for (int i = 0; i < 300; i++)
        {
            marked.MemoryBudget.FixedGb = i;
            if (!marked.Save(racePath))
                failedSaves++;
        }
        stop.Cancel();
        reader.Wait();
    }
    Check(torn == 0, $"5 a reader alongside 300 saves never sees part of a file ({torn} torn reads)");
    Check(failedSaves == 0 && MachineSettings.Load(racePath).MemoryBudget.FixedGb == 299,
        $"5 and no save fails because the reader has the file open ({failedSaves} failed)");

    // The control, made certain rather than left to timing: the reader looks
    // while an in-place write has written half the file.
    byte[] whole = JsonSerializer.SerializeToUtf8Bytes(marked);
    MachineSettings.LoadResult midWrite;
    using (var stream = new FileStream(racePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
    {
        stream.Write(whole, 0, whole.Length / 2);
        stream.Flush();
        midWrite = MachineSettings.TryLoad(out _, racePath);
        stream.Write(whole, whole.Length / 2, whole.Length - whole.Length / 2);
    }
    Check(midWrite == MachineSettings.LoadResult.Unreadable,
        "5 control: a reader in the middle of an in-place write gets part of a file, which the one-step replace prevents");

    // 6 ---------------------------------------------------------------------
    string pairPath = Sub("pair");
    var first = new MachineSettings { DiscStagingMode = DiscStagingMode.InPlace };
    first.MemoryBudget.FixedGb = 1;
    var second = new MachineSettings { DiscStagingMode = DiscStagingMode.InPlace };
    second.MemoryBudget.FixedGb = 2;
    bool okA = true, okB = true;
    for (int i = 0; i < 50; i++)
    {
        var a = Task.Run(() => first.Save(pairPath));
        var b = Task.Run(() => second.Save(pairPath));
        okA &= a.Result;
        okB &= b.Result;
    }
    double finalGb = MachineSettings.Load(pairPath).MemoryBudget.FixedGb;
    bool leftovers = Directory.EnumerateFiles(Path.GetDirectoryName(pairPath)!, "*.tmp").Any();
    Check(okA && okB && (finalGb == 1 || finalGb == 2) && !leftovers,
        $"6 two saves at once both land whole and leave no temporary file (saved {okA}/{okB}, file holds {finalGb})");

    // 7 ---------------------------------------------------------------------
    string heldPath = Sub("held");
    Directory.CreateDirectory(Path.GetDirectoryName(heldPath)!);
    File.WriteAllText(heldPath, "old");
    File.WriteAllText(heldPath + ".new", "new");
    File.WriteAllText(heldPath + ".move", "moved");
    using (var held = new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    {
        bool moveFailed = false;
        try { File.Move(heldPath + ".move", heldPath, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moveFailed = true; }
        Check(moveFailed, "7 control: File.Move cannot replace a file a reader holds, even sharing delete");

        var result = AtomicFile.TryReplace(heldPath + ".new", heldPath);
        string oldSeen = new StreamReader(held).ReadToEnd();
        Check(result == AtomicFile.ReplaceResult.Replaced && oldSeen == "old",
            $"7 AtomicFile replaces it all the same, and the reader keeps the old content ({result}, read \"{oldSeen}\")");
    }
    Check(File.ReadAllText(heldPath) == "new", "7 and the path then names the new file");

    // 8 ---------------------------------------------------------------------
    var user = JsonSerializer.Deserialize<UserSettings>(legacyJson)!;
    user.LastSettingsTab = 5;
    using var rewritten = JsonDocument.Parse(JsonSerializer.Serialize(user));
    var top = rewritten.RootElement;
    Check(top.TryGetProperty("MemoryBudget", out var budget) && budget.GetProperty("FixedGb").GetDouble() == 3
          && top.TryGetProperty("DiscStagingMode", out _) && top.TryGetProperty("ContinuousRules", out _)
          && top.GetProperty("LastSettingsTab").GetInt32() == 5,
        "8 the per-user file keeps the old backup settings when the app saves it, until they are carried over");
    bool forgot = user.ForgetBackupSettings();
    using var pruned = JsonDocument.Parse(JsonSerializer.Serialize(user));
    Check(forgot && !pruned.RootElement.TryGetProperty("MemoryBudget", out _)
          && !pruned.RootElement.TryGetProperty("DiscStagingMode", out _)
          && !pruned.RootElement.TryGetProperty("ContinuousRules", out _)
          && pruned.RootElement.GetProperty("LastSettingsTab").GetInt32() == 5
          && !user.ForgetBackupSettings(),
        "8 once carried over they can be forgotten, so they cannot come back however old");

    // 9 ---------------------------------------------------------------------
    string livePath = Sub("live");
    var fileBudget = new MachineSettings { MemoryBudget = new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 9 } };
    fileBudget.Save(livePath);
    var seed = new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 0 };
    long clock = 0;
    var policy = new LiveMemoryPolicy(seed, settingsPath: livePath, clockMs: () => clock);
    clock = 5000;
    Check(policy.Current().FixedGb == 0,
        "9 a running backup keeps the job's own budget while the settings file is unchanged");

    Thread.Sleep(50);
    fileBudget.MemoryBudget.FixedGb = 4;
    fileBudget.Save(livePath);
    clock = 5500;
    double withinSecond = policy.Current().FixedGb;
    clock = 6100;
    double afterSecond = policy.Current().FixedGb;
    Check(withinSecond == 0 && afterSecond == 4,
        $"9 once the file is written it follows it, at the next look a second on ({withinSecond}, then {afterSecond})");

    Thread.Sleep(50);
    File.WriteAllText(livePath, "{ broken");
    clock = 7200;
    double whileBroken = policy.Current().FixedGb;
    Thread.Sleep(50);
    fileBudget.MemoryBudget.FixedGb = 6;
    fileBudget.Save(livePath);
    clock = 8300;
    Check(whileBroken == 4 && policy.Current().FixedGb == 6,
        $"9 an unreadable file leaves the budget as it was, and the next good save is followed ({whileBroken}, then 6)");

    // The seed was read, then the job was planned for a while, and a save landed
    // before the run began: the run follows that save from its first look.
    var readAt = MachineSettings.LastWriteTimeUtc(livePath);
    Thread.Sleep(50);
    fileBudget.MemoryBudget.FixedGb = 7;
    fileBudget.Save(livePath);
    long clock2 = 0;
    var planned = new LiveMemoryPolicy(new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 6 },
        seedWrittenUtc: readAt, settingsPath: livePath, clockMs: () => clock2);
    Check(planned.Current().FixedGb == 7,
        "9 a save made while the job was being planned is followed from the run's first look");

    // 11 --------------------------------------------------------------------
    string readerPath = Sub("reader");
    var good = new MachineSettings { DiscStagingMode = DiscStagingMode.InPlace };
    good.Save(readerPath);
    var reader11 = new MachineSettingsReader(readerPath);
    bool firstUnreadable = reader11.Refresh();
    var firstRead = reader11.Current;
    reader11.Refresh();
    Check(!firstUnreadable && firstRead.DiscStagingMode == DiscStagingMode.InPlace
          && ReferenceEquals(firstRead, reader11.Current),
        "11 the reader reads the file, and not again while it is unchanged");

    Thread.Sleep(50);
    File.WriteAllText(readerPath, "{ broken");
    bool saidOnce = reader11.Refresh();
    bool saidAgain = reader11.Refresh();
    Check(saidOnce && !saidAgain && reader11.Unreadable
          && reader11.Current.DiscStagingMode == DiscStagingMode.InPlace,
        "11 an unreadable file keeps the last good settings, and is reported once");

    Thread.Sleep(50);
    good.DiscStagingMode = DiscStagingMode.TemporaryCopy;
    good.MemoryBudget.FixedGb = 8;
    good.Save(readerPath);
    reader11.Refresh();
    Check(!reader11.Unreadable && reader11.Current.MemoryBudget.FixedGb == 8,
        "11 and the next good save is read");

    // 12 --------------------------------------------------------------------
    string sweepPath = Sub("sweep");
    Directory.CreateDirectory(Path.GetDirectoryName(sweepPath)!);
    string abandoned = $"{sweepPath}.{Guid.NewGuid():N}.tmp";
    string recent = $"{sweepPath}.{Guid.NewGuid():N}.tmp";
    File.WriteAllText(abandoned, "left by a crash");
    File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-1));
    File.WriteAllText(recent, "another save, in progress");
    new MachineSettings().Save(sweepPath);
    Check(!File.Exists(abandoned) && File.Exists(recent),
        "12 a save removes a temporary file abandoned an hour ago, and leaves one just made");

    string blocker = Path.Combine(root, "not-a-folder");
    File.WriteAllText(blocker, "a file where the folder should be");
    bool savedIntoFile = new MachineSettings().TrySave(out string? why, Path.Combine(blocker, "settings.json"));
    Check(!savedIntoFile && !string.IsNullOrEmpty(why), $"12 a save that cannot be made says why (\"{why}\")");

    // 10 --------------------------------------------------------------------
    var before = MachineSettings.LastWriteTimeUtc(machinePath);
    Thread.Sleep(50);
    carried.ContinuousRules.MaxWaitTiers.Add(new MaxWaitMaskTier { Name = "later", IncludeMasks = ["*.later"], MaxWaitSeconds = 5 });
    carried.Save(machinePath);
    var after = MachineSettings.LastWriteTimeUtc(machinePath);
    Check(before is not null && after is not null && after > before,
        "10 the file's write time moves with a save, which is what the Worker polls");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
