// Tests for MachineSettings: the settings that govern backups, kept in one file
// every account reads, so that the Worker (LocalSystem) sees what the app saves.
// See "the Worker never saw the settings the app saves" in known-issues.md.
//
//   1. The first start carries the settings over from the per-user file, where
//      they were kept before, under the same names, and writes the machine file.
//   2. Once the machine file exists it wins; the per-user file is not read again.
//   3. With neither file there are defaults, and nothing is written.
//   4. A save replaces the file in one step: a reader looping alongside never
//      sees part of one, and no save fails because the reader has it open. A
//      control writes the same file in place under the same reader, to show the
//      step matters.
//   5. The time the file was written moves with each save.
//   6. AtomicFile replaces a file that a reader holds open sharing delete access,
//      which File.Move cannot (shown alongside as a control).
//
// Every file lives in a temp folder; the real machine file is never touched.

using System.Text.Json;
using LithicBackup.Core.Models;
using LithicBackup.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

string root = Path.Combine(Path.GetTempPath(), "lithic_settings_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string machinePath = Path.Combine(root, "machine", "settings.json");
string legacyPath = Path.Combine(root, "user", "settings.json");

try
{
    // 1 ---------------------------------------------------------------------
    // A per-user file as the app wrote it before the split: the three backup
    // settings at the top level, among the user's own preferences.
    var rules = new ContinuousRules();
    rules.MaxWaitTiers.Clear();
    var legacy = new
    {
        SuppressBackupSuggestions = true,
        MemoryBudget = new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 3 },
        DiscStagingMode = DiscStagingMode.InPlace,
        CheckForUpdates = false,
        ReconcileMode = ReconcileAfterEditMode.Never,
        ContinuousRules = rules,
        LastSettingsTab = 2,
    };
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));

    var carried = MachineSettings.LoadOrCarryOver(machinePath, legacyPath);
    Check(carried.MemoryBudget.Mode == MemoryBudgetMode.Fixed && carried.MemoryBudget.FixedGb == 3
          && carried.DiscStagingMode == DiscStagingMode.InPlace
          && carried.ContinuousRules.MaxWaitTiers.Count == 0,
        "1 the backup settings carry over from the per-user file");
    var reread = MachineSettings.Load(machinePath);
    Check(File.Exists(machinePath) && reread.MemoryBudget.FixedGb == 3 && reread.DiscStagingMode == DiscStagingMode.InPlace,
        "1 and are written to the machine file, where the Worker reads them");

    // 2 ---------------------------------------------------------------------
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
    {
        MemoryBudget = new MemoryBudgetOptions { Mode = MemoryBudgetMode.Fixed, FixedGb = 9 },
        DiscStagingMode = DiscStagingMode.TemporaryCopy,
    }));
    var second = MachineSettings.LoadOrCarryOver(machinePath, legacyPath);
    Check(second.MemoryBudget.FixedGb == 3 && second.DiscStagingMode == DiscStagingMode.InPlace,
        "2 once the machine file exists, the per-user file is not read again");

    // 3 ---------------------------------------------------------------------
    string emptyMachine = Path.Combine(root, "none", "settings.json");
    var defaults = MachineSettings.LoadOrCarryOver(emptyMachine, Path.Combine(root, "nobody", "settings.json"));
    Check(defaults.DiscStagingMode == DiscStagingMode.TemporaryCopy
          && defaults.MemoryBudget.Mode == new MemoryBudgetOptions().Mode
          && !File.Exists(emptyMachine),
        "3 with neither file there are defaults, and nothing is written");

    // 4 ---------------------------------------------------------------------
    // The marker is InPlace: the default, TemporaryCopy, is what a torn or
    // unreadable file reads back as.
    var marked = new MachineSettings { DiscStagingMode = DiscStagingMode.InPlace };
    for (int i = 0; i < 400; i++)
        marked.ContinuousRules.MaxWaitTiers.Add(new MaxWaitMaskTier { Name = $"tier {i}", IncludeMasks = [$"*.pattern{i}"], MaxWaitSeconds = i });
    string racePath = Path.Combine(root, "race", "settings.json");
    marked.Save(racePath);

    (int torn, int failedSaves) Race(Action<int> write)
    {
        int torn = 0, failedSaves = 0;
        using var stop = new CancellationTokenSource();
        // Every 5 ms: some two hundred times as often as a running backup reads it.
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (MachineSettings.Load(racePath).DiscStagingMode != DiscStagingMode.InPlace)
                    Interlocked.Increment(ref torn);
                Thread.Sleep(5);
            }
        });
        for (int i = 0; i < 300; i++)
        {
            try { write(i); }
            catch { failedSaves++; }
        }
        stop.Cancel();
        reader.Wait();
        return (torn, failedSaves);
    }

    // Back to back, against the reader every 5 ms: each save also meets the
    // virus scanner looking at the file the previous one made.
    int reported = 0;
    var (tornAtomic, _) = Race(i =>
    {
        marked.MemoryBudget.FixedGb = i;
        if (!marked.Save(racePath))
            reported++;
    });
    Check(tornAtomic == 0, $"4 a reader alongside 300 saves never sees part of a file ({tornAtomic} torn reads)");
    Check(reported == 0 && MachineSettings.Load(racePath).MemoryBudget.FixedGb == 299,
        $"4 and no save fails because the reader has the file open ({reported} failed)");
    Check(!File.Exists(racePath + ".tmp"), "4 no temporary file is left behind");

    string json = JsonSerializer.Serialize(marked);
    var (tornInPlace, _) = Race(_ =>
    {
        using var stream = new FileStream(racePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.Write(json);
    });
    Console.WriteLine($"     control: writing the file in place, the same reader saw {tornInPlace} torn reads");
    Check(tornInPlace > 0, "4 control: writing in place does tear, so replacing in one step is what prevents it");

    // 6 ---------------------------------------------------------------------
    // Held open with delete sharing, as the readers hold it: the POSIX replace
    // goes through, and the reader keeps the old content; File.Move does not.
    string heldPath = Path.Combine(root, "held", "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(heldPath)!);
    File.WriteAllText(heldPath, "old");
    File.WriteAllText(heldPath + ".new", "new");
    File.WriteAllText(heldPath + ".move", "moved");
    using (var held = new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    {
        bool moveFailed = false;
        try { File.Move(heldPath + ".move", heldPath, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moveFailed = true; }
        Check(moveFailed, "6 control: File.Move cannot replace a file a reader holds, even sharing delete");

        var result = LithicBackup.Infrastructure.FileSystem.AtomicFile.TryReplace(heldPath + ".new", heldPath);
        string oldSeen = new StreamReader(held).ReadToEnd();
        Check(result == LithicBackup.Infrastructure.FileSystem.AtomicFile.ReplaceResult.Replaced && oldSeen == "old",
            $"6 AtomicFile replaces it all the same, and the reader keeps the old content ({result}, read \"{oldSeen}\")");
    }
    Check(File.ReadAllText(heldPath) == "new", "6 and the path then names the new file");

    // 5 ---------------------------------------------------------------------
    var before = MachineSettings.LastWriteTimeUtc(machinePath);
    Thread.Sleep(50);
    reread.ContinuousRules.MaxWaitTiers.Add(new MaxWaitMaskTier { Name = "later", IncludeMasks = ["*.later"], MaxWaitSeconds = 5 });
    reread.Save(machinePath);
    var after = MachineSettings.LastWriteTimeUtc(machinePath);
    Check(before is not null && after is not null && after > before,
        "5 the file's write time moves with a save, which is what the Worker polls");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
