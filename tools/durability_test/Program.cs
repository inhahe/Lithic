// Copies are made durable before the catalog records them (known-issues.md:
// "a copy is recorded as backed up before its data reaches the destination
// disk").
//
//  1-4. The pieces: naming the volume a folder is really on (through subst
//       drives and junctions, where a drive letter misleads), flushing a volume
//       or a file, and holding a folder to notice its volume going.
//  5.   The service's pipe, against a server in this process.
//  6.   DestinationFlush, the flush runs use, where this process may not flush a
//       volume (not elevated) and no service answers.
//  7-11. DirectoryBackupService with a flush that records what it is given, or
//       fails as a drive that has gone would: every record a commit writes is
//       of a file flushed before it, and a flush that fails stops the commit -
//       of a batch, of the batch a failing run saves on its way out, of a
//       folder move, of a seed.
//  12.  A whole run with the real flush.
//  13.  Retention making a reference a plain copy, from the bytes of the last
//       plain copy it is about to delete: that copy is flushed first, and when
//       it cannot be, the bytes stay.
//
// Not covered: a USB drive that drops off and comes back (VolumeWatch.WasLost
// true), which takes the hardware.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
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
    Console.WriteLine((cond ? "  \u2713 " : "  \u2717 FAIL: ") + msg);
    if (!cond) failures++;
}

bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine(elevated ? "(running elevated)" : "(not elevated)");

string root = Path.Combine(Path.GetTempPath(), "lithic_durability_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
// A folder on another volume than the temp folder, for the junction: beside
// this harness's binaries, which in this repository are on another drive.
string elsewhere = Path.Combine(AppContext.BaseDirectory, "scratch_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(elsewhere);
string? substLetter = null;
SqliteCatalogRepository? repo = null;

try
{
    // ===================================================================
    Console.WriteLine("=== 1. The volume a folder is on ===");
    // ===================================================================
    string? tempVolume = DurableFlush.VolumeOf(root, out string? why1);
    Check(tempVolume is not null && DurableFlush.IsVolumeName(tempVolume),
        $"the temp folder is on {tempVolume ?? "(none: " + why1 + ")"}");
    string? elsewhereVolume = DurableFlush.VolumeOf(elsewhere, out _);
    Check(elsewhereVolume is not null, $"this harness's folder is on {elsewhereVolume}");
    bool twoVolumes = tempVolume is not null && elsewhereVolume is not null
        && !string.Equals(tempVolume, elsewhereVolume, StringComparison.OrdinalIgnoreCase);

    Check(DurableFlush.VolumeOf(Path.Combine(root, "not-there"), out string? missingWhy) is null && missingWhy is not null,
        $"a folder that is not there is on none ({missingWhy})");

    // A subst drive: Windows will not say what volume its letter is on
    // (GetVolumePathName fails on it), and the service, in another logon
    // session, does not see the letter at all.
    string substTarget = Path.Combine(root, "subst-target");
    Directory.CreateDirectory(Path.Combine(substTarget, "inside"));
    substLetter = FreeDriveLetter();
    if (substLetter is not null && Run("subst", substLetter + ":", substTarget) == 0)
    {
        string? substVolume = DurableFlush.VolumeOf(substLetter + @":\inside", out string? substWhy);
        Check(substVolume is not null && string.Equals(substVolume, tempVolume, StringComparison.OrdinalIgnoreCase),
            $"a folder on subst drive {substLetter}: is on the volume the drive stands for ({substVolume ?? substWhy})");
    }
    else
    {
        Console.WriteLine("  (no subst drive could be made: skipped)");
        substLetter = null;
    }

    // A junction in one volume to a folder on another.
    if (twoVolumes)
    {
        string junction = Path.Combine(root, "junction");
        if (Run("cmd.exe", "/c", "mklink", "/J", junction, elsewhere) == 0)
        {
            string? junctionVolume = DurableFlush.VolumeOf(junction, out _);
            Check(string.Equals(junctionVolume, elsewhereVolume, StringComparison.OrdinalIgnoreCase),
                $"a junction to a folder on another volume is on that volume ({junctionVolume}), not its own ({tempVolume})");
            Directory.Delete(junction);   // the junction, not what it leads to
        }
        else
        {
            Console.WriteLine("  (no junction could be made: skipped)");
        }
    }
    else
    {
        Console.WriteLine("  (the temp folder and this harness are on one volume: junction check skipped)");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 2. Flushing a volume ===");
    // ===================================================================
    foreach (string notAVolume in new[]
             {
                 @"C:\", @"\\.\C:", @"C:\Windows",
                 tempVolume is null ? "" : tempVolume.TrimEnd('\\'),
                 tempVolume is null ? "" : tempVolume + "Windows",
                 @"\\?\GLOBALROOT\Device\HarddiskVolume1\", @"\\.\pipe\" + VolumeFlushPipe.Name,
             })
    {
        Check(!DurableFlush.IsVolumeName(notAVolume)
              && DurableFlush.TryFlushVolume(notAVolume, out _) == DurableFlush.Outcome.Failed,
            $"\"{notAVolume}\" is refused: not a volume name");
    }
    if (tempVolume is not null)
    {
        var outcome = DurableFlush.TryFlushVolume(tempVolume, out string? flushWhy);
        Check(outcome == (elevated ? DurableFlush.Outcome.Flushed : DurableFlush.Outcome.NotPermitted),
            $"flushing the temp folder's volume here: {outcome} ({flushWhy ?? "no error"})");
    }
    Check(DurableFlush.TryFlushVolume(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\", out string? noneWhy)
          == DurableFlush.Outcome.Failed,
        $"a volume that does not exist fails ({noneWhy})");

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 3. Flushing a file or folder ===");
    // ===================================================================
    string plain = Path.Combine(root, "plain.bin");
    File.WriteAllBytes(plain, RandomNumberGenerator.GetBytes(10_000));
    Check(DurableFlush.TryFlushPath(plain, out string? plainWhy), $"a file ({plainWhy ?? "flushed"})");

    string readOnly = Path.Combine(root, "readonly.bin");
    File.WriteAllBytes(readOnly, RandomNumberGenerator.GetBytes(10_000));
    File.SetAttributes(readOnly, FileAttributes.ReadOnly);
    Check(DurableFlush.TryFlushPath(readOnly, out string? roWhy), $"a read-only file ({roWhy ?? "flushed"})");
    Check((File.GetAttributes(readOnly) & FileAttributes.ReadOnly) != 0, "and it is still read-only");
    File.SetAttributes(readOnly, FileAttributes.Normal);

    Check(DurableFlush.TryFlushPath(root, out _), "a folder");
    Check(!DurableFlush.TryFlushPath(Path.Combine(root, "gone.bin"), out string? goneWhy),
        $"a file that is not there is not flushed ({goneWhy})");

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 4. Watching a folder's volume ===");
    // ===================================================================
    using (var watch = VolumeWatch.Open(root))
    {
        Check(watch is not null, "a folder that is there can be held");
        Check(watch is not null && !watch.WasLost(out _), "and its volume has not gone");
    }
    Check(VolumeWatch.Open(Path.Combine(root, "not-there")) is null, "a folder that is not there cannot");

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 5. The service's pipe ===");
    // ===================================================================
    string testPipe = VolumeFlushPipe.Name + ".test-" + Guid.NewGuid().ToString("N");
    var sw = Stopwatch.StartNew();
    var (nobody, nobodyWhy) = await VolumeFlushPipe.RequestAsync(tempVolume ?? "", CancellationToken.None, testPipe);
    sw.Stop();
    Check(nobody is null && nobodyWhy is not null, $"with no service, no outcome ({nobodyWhy})");
    Check(sw.Elapsed < TimeSpan.FromSeconds(3), $"and it is known in {sw.ElapsedMilliseconds} ms");

    var serverErrors = new List<Exception>();
    using var stopServer = new CancellationTokenSource();
    var server = VolumeFlushPipe.ServeAsync(stopServer.Token, ex => { lock (serverErrors) serverErrors.Add(ex); }, testPipe);
    if (tempVolume is not null)
    {
        var (served, servedWhy) = await VolumeFlushPipe.RequestAsync(tempVolume, CancellationToken.None, testPipe);
        // This process's server may do no more than this process may.
        Check(served == (elevated ? DurableFlush.Outcome.Flushed : DurableFlush.Outcome.NotPermitted),
            $"a server answers a volume's name: {served} ({servedWhy ?? "no error"})");
    }
    var (refused, refusedWhy) = await VolumeFlushPipe.RequestAsync(@"C:\Windows", CancellationToken.None, testPipe);
    Check(refused == DurableFlush.Outcome.Failed, $"and refuses a path: {refused} ({refusedWhy})");

    var many = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
        VolumeFlushPipe.RequestAsync(tempVolume ?? "", CancellationToken.None, testPipe)));
    Check(many.All(r => r.Outcome is not null), $"8 requests at once are all answered ({many.Count(r => r.Outcome is not null)})");

    stopServer.Cancel();
    Check(await Task.WhenAny(server, Task.Delay(5000)) == server && server.IsCompletedSuccessfully,
        "the server stops when told to");
    Check(serverErrors.Count == 0, $"and reported no errors ({string.Join("; ", serverErrors.Select(e => e.Message))})");

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 6. DestinationFlush ===");
    // ===================================================================
    var flush = new DestinationFlush(testPipe);   // no server on it now
    string dest6 = Path.Combine(root, "dest6");
    Directory.CreateDirectory(Path.Combine(dest6, "sub"));
    var written6 = Enumerable.Range(0, 20).Select(i =>
    {
        string p = Path.Combine(dest6, i % 2 == 0 ? "sub" : "", $"f{i}.bin");
        File.WriteAllBytes(p, RandomNumberGenerator.GetBytes(1000));
        return p;
    }).Append(Path.Combine(dest6, "renamed-away-since.bin")).ToList();

    sw.Restart();
    Exception? flushError = await Try(() => flush.FlushAsync(dest6, written6, CancellationToken.None));
    long firstMs = sw.ElapsedMilliseconds;
    Check(flushError is null, $"what was written is flushed, file by file ({flushError?.Message ?? $"{firstMs} ms"})");
    sw.Restart();
    flushError = await Try(() => flush.FlushAsync(dest6, written6, CancellationToken.None));
    long secondMs = sw.ElapsedMilliseconds;
    Check(flushError is null && (elevated || secondMs < firstMs || secondMs < 500),
        $"and again, without waiting for the absent service again ({secondMs} ms after {firstMs} ms)");

    if (substLetter is not null)
    {
        string substDest = substLetter + @":\inside";
        string substFile = Path.Combine(substDest, "on-subst.bin");
        File.WriteAllBytes(substFile, RandomNumberGenerator.GetBytes(1000));
        flushError = await Try(() => flush.FlushAsync(substDest, [substFile], CancellationToken.None));
        Check(flushError is null, $"a destination on a subst drive is flushed ({flushError?.Message ?? "ok"})");
    }

    string gone6 = Path.Combine(root, "dest6-gone");
    flushError = await Try(() => flush.FlushAsync(gone6, [Path.Combine(gone6, "x.bin")], CancellationToken.None));
    Check(flushError is IOException, $"a destination that is gone fails ({flushError?.Message ?? "no error"})");

    // ===================================================================
    // 7-11: DirectoryBackupService.
    // ===================================================================
    string catalogDir = Path.Combine(root, "catalog");
    Directory.CreateDirectory(catalogDir);
    repo = new SqliteCatalogRepository(Path.Combine(catalogDir, "catalog.db"));

    async Task<(BackupSet Set, string Source, string Target)> NewSet(string name, int files)
    {
        string src = Path.Combine(root, name, "src");
        string dst = Path.Combine(root, name, "dst");
        Directory.CreateDirectory(dst);
        for (int i = 0; i < files; i++)
        {
            string p = Path.Combine(src, $"d{i % 7}", $"file{i:D5}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, $"{name} file {i} {Guid.NewGuid()}");
        }
        var set = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = name,
            SourceRoots = [src],
            SourceSelections = [new SourceSelection { Path = src, IsDirectory = true, IsSelected = true }],
            CreatedUtc = DateTime.UtcNow,
            DefaultMediaType = MediaType.Directory,
            JobOptions = new JobOptions { TargetDirectory = dst },
        });
        return (set, src, dst);
    }

    BackupJob Job(BackupSet set, string src, string dst) => new()
    {
        BackupSetId = set.Id,
        Sources = [new SourceSelection { Path = src, IsDirectory = true, IsSelected = true }],
        EnableFileDeduplication = true,
        EnableDeduplication = false,
        TargetDirectory = dst,
        ZipMode = ZipMode.None,
    };

    DirectoryBackupService Service(IDestinationFlush f) =>
        new(repo, new FileScanner(repo), new VersionRetentionService(repo), destinationFlush: f);

    async Task<List<string>> RecordedFiles(BackupSet set, string dst) =>
        (await repo.GetAllFilesForBackupSetAsync(set.Id))
            .Select(r => Path.GetFullPath(Path.Combine(dst, r.DiscPath)))
            .ToList();

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 7. A run: every record is of a file flushed before its commit ===");
    // ===================================================================
    {
        var (set, src, dst) = await NewSet("run", 1_200);
        var recording = new RecordingFlush();
        var result = await Service(recording).ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None);
        Check(result.Success, "the run succeeds");
        var recorded = await RecordedFiles(set, dst);
        Check(recorded.Count == 1_200, $"and records every file ({recorded.Count})");
        Check(recording.Calls.Count >= 3, $"in several commits, each flushed first ({recording.Calls.Count} flushes)");
        var flushed = recording.Flushed();
        Check(recorded.All(flushed.Contains), $"every recorded file was flushed ({recorded.Count(p => !flushed.Contains(p))} not)");
        Check(recording.Calls.All(c => c.Destination == dst), "each flush is of the run's destination");

        // The next run files the changed copies away into the history: those
        // renames, and the new copies, are flushed too.
        foreach (string p in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Take(10))
            File.AppendAllText(p, " changed");
        var before = (await repo.GetAllFilesForBackupSetAsync(set.Id))
            .Select(r => (r.Id, r.DiscPath)).ToHashSet();
        recording.Calls.Clear();
        result = await Service(recording).ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None);
        Check(result.Success, "a second run, after 10 changes, succeeds");
        var changed = (await repo.GetAllFilesForBackupSetAsync(set.Id))
            .Where(r => !before.Contains((r.Id, r.DiscPath)))
            .Select(r => Path.GetFullPath(Path.Combine(dst, r.DiscPath)))
            .ToList();
        flushed = recording.Flushed();
        Check(changed.Count == 20, $"it records 10 new copies and 10 moved into the history ({changed.Count})");
        Check(changed.All(flushed.Contains), $"all of them flushed first ({changed.Count(p => !flushed.Contains(p))} not)");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 8. A run whose drive goes: what was not flushed is not recorded ===");
    // ===================================================================
    {
        var (set, src, dst) = await NewSet("drive-goes", 1_200);
        // The first commit's flush works; then the drive is gone.
        var recording = new RecordingFlush { Fails = call => call >= 2 };
        var error = await Try(() => Service(recording).ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None));
        Check(error is IOException, $"the run fails ({error?.GetType().Name}: {error?.Message})");
        var recorded = await RecordedFiles(set, dst);
        var flushed = recording.Flushed();
        Check(recorded.Count > 0 && recorded.Count == flushed.Count,
            $"what the first commit flushed is recorded, and nothing else ({recorded.Count} recorded, {flushed.Count} flushed)");
        Check(recorded.All(flushed.Contains), "every recorded file was flushed");
        Check(recording.Calls.Count == 3,
            $"the failing commit was tried, and again on the way out ({recording.Calls.Count} flushes)");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 9. A flush that fails once: the retry on the way out is given the same files ===");
    // ===================================================================
    {
        var (set, src, dst) = await NewSet("fails-once", 1_200);
        var recording = new RecordingFlush { Fails = call => call == 2 };
        var error = await Try(() => Service(recording).ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None));
        Check(error is IOException, $"the run fails ({error?.GetType().Name})");
        Check(recording.Calls.Count == 3 && recording.Calls[2].Written.Count > 0
              && recording.Calls[2].Written.SequenceEqual(recording.Calls[1].Written),
            $"the retry was handed what the failed flush was ({(recording.Calls.Count == 3 ? recording.Calls[2].Written.Count : -1)} "
            + $"of {(recording.Calls.Count >= 2 ? recording.Calls[1].Written.Count : -1)})");
        var recorded = await RecordedFiles(set, dst);
        var flushed = recording.Flushed();
        Check(recorded.Count > 0 && recorded.All(flushed.Contains),
            $"so all {recorded.Count} recorded files were flushed ({recorded.Count(p => !flushed.Contains(p))} not)");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 10. A cancelled run saves its batch: flushed first ===");
    // ===================================================================
    {
        var (set, src, dst) = await NewSet("cancelled", 1_200);
        using var cancel = new CancellationTokenSource();
        var recording = new RecordingFlush();
        int filesAfterFirstCommit = 0;
        var progress = new SyncProgress<BackupProgress>(p =>
        {
            if (recording.Calls.Count >= 1 && !string.IsNullOrEmpty(p.CurrentFile) && p.CurrentFileBytesWritten == 0
                && ++filesAfterFirstCommit == 100)
                cancel.Cancel();
        });
        var error = await Try(() => Service(recording).ExecuteAsync(Job(set, src, dst), dst, null, progress, cancel.Token));
        Check(error is OperationCanceledException, $"the run is cancelled ({error?.GetType().Name})");
        var recorded = await RecordedFiles(set, dst);
        var flushed = recording.Flushed();
        Check(recording.Calls.Count == 2, $"its batch was flushed on the way out ({recording.Calls.Count} flushes)");
        Check(recording.Calls.Count > 0 && recorded.Count > recording.Calls[0].Written.Count
              && recorded.All(flushed.Contains),
            $"and recorded: {recorded.Count} files, all flushed ({recorded.Count(p => !flushed.Contains(p))} not)");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 11. A folder move, and a seed ===");
    // ===================================================================
    {
        var (set, src, dst) = await NewSet("move", 20);
        var ok = new RecordingFlush();
        var svc = Service(ok);
        Check((await svc.ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None)).Success, "backed up");
        string oldDir = Path.Combine(src, "d1");
        string newDir = Path.Combine(src, "d1-renamed");
        Directory.Move(oldDir, newDir);
        string oldCopy = Path.Combine(dst, oldDir[..1], oldDir[Path.GetPathRoot(oldDir)!.Length..]);
        string newCopy = Path.Combine(dst, newDir[..1], newDir[Path.GetPathRoot(newDir)!.Length..]);

        // The drive is gone by the time the move is to be recorded.
        var gone = new RecordingFlush { Fails = _ => true };
        var outcome = await Service(gone).MoveTargetedAsync(Job(set, src, dst), dst, oldDir, newDir, isDirectory: true,
            CancellationToken.None);
        Check(outcome == TargetedMoveOutcome.FellBack, $"a move whose flush fails falls back ({outcome})");
        Check(gone.Calls.Count == 1, $"after one flush ({gone.Calls.Count})");
        Check(Directory.Exists(oldCopy) && !Directory.Exists(newCopy), "its copy is put back where it was");
        Check((await repo.GetFileRecordsUnderDirectoryAsync(set.Id, oldDir)).Count > 0
              && (await repo.GetFileRecordsUnderDirectoryAsync(set.Id, newDir)).Count == 0,
            "and its records still say the old place");

        ok.Calls.Clear();
        outcome = await svc.MoveTargetedAsync(Job(set, src, dst), dst, oldDir, newDir, isDirectory: true, CancellationToken.None);
        Check(outcome == TargetedMoveOutcome.Relocated, $"with a working flush it relocates ({outcome})");
        Check(ok.Calls.Count == 1 && ok.Calls[0].Written.Contains(newCopy, StringComparer.OrdinalIgnoreCase),
            "after flushing the copy's new name");

        // A seed moves an older stored form at a file's current location into the
        // history: an empty-content .fileref older than the plain copy beside it.
        string anyCopy = Directory.EnumerateFiles(newCopy).First();
        await File.WriteAllTextAsync(anyCopy + ".fileref", JsonSerializer.Serialize(new FileRefManifest
        {
            OriginalName = Path.GetFileName(anyCopy), OriginalSize = 0,
            Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            SourcePath = "", ContentPath = "",
        }));
        File.SetLastWriteTimeUtc(anyCopy + ".fileref", DateTime.UtcNow.AddDays(-30));
        string seedFailing = Path.Combine(root, "seed-failing");
        string seedWorking = Path.Combine(root, "seed-working");
        CopyTree(dst, seedFailing);
        CopyTree(dst, seedWorking);

        var seedSetA = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "seed failing", SourceRoots = [src], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        var seedGone = new RecordingFlush { Fails = _ => true };
        var seedError = await Try(() => Service(seedGone).SeedFromExistingDirectoryAsync(seedSetA.Id, seedFailing));
        Check(seedError is IOException, $"a seed whose flush fails fails ({seedError?.GetType().Name})");
        Check((await repo.GetAllFilesForBackupSetAsync(seedSetA.Id)).Count == 0, "and records nothing");

        var seedSetB = await repo.CreateBackupSetAsync(new BackupSet
        {
            Name = "seed working", SourceRoots = [src], CreatedUtc = DateTime.UtcNow, DefaultMediaType = MediaType.Directory,
        });
        var seedOk = new RecordingFlush();
        var seeded = await Service(seedOk).SeedFromExistingDirectoryAsync(seedSetB.Id, seedWorking);
        Check(seeded.MovedToHistory == 1, $"a seed moves the older form into the history ({seeded.MovedToHistory})");
        var movedRecord = (await repo.GetAllFilesForBackupSetAsync(seedSetB.Id))
            .FirstOrDefault(r => r.IsFileRef && r.DiscPath.Contains("_prev", StringComparison.OrdinalIgnoreCase));
        string? movedTo = movedRecord is null ? null : Path.GetFullPath(Path.Combine(seedWorking, movedRecord.DiscPath));
        Check(movedTo is not null && seedOk.Flushed().Contains(movedTo),
            $"and flushes it at its new place before recording it there ({movedTo})");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 12. A run with the real flush ===");
    // ===================================================================
    {
        var (set, src, dst) = await NewSet("real", 50);
        var real = new DirectoryBackupService(repo, new FileScanner(repo), new VersionRetentionService(repo));
        sw.Restart();
        var result = await real.ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None);
        Check(result.Success && (await RecordedFiles(set, dst)).Count == 50,
            $"succeeds and records its 50 files ({sw.ElapsedMilliseconds} ms)");
    }

    // ===================================================================
    Console.WriteLine();
    Console.WriteLine("=== 13. Retention: a reference given the bytes of the last plain copy has them on the disk first ===");
    // ===================================================================
    // Two files with one content: the first is stored plain, the second as a
    // .fileref resolving to it. The first changes twice, so its old plain copy
    // moves into the history and then stops being the newest version there, and
    // retention (one earlier version kept) removes it - the last plain copy of
    // that content. So the reference is made a plain copy from
    // those bytes first, and that copy must be on the disk before they go.
    foreach (bool flushWorks in new[] { true, false })
    {
        var (set, src, dst) = await NewSet(flushWorks ? "promote" : "promote-fails", 0);
        string a = Path.Combine(src, "a.txt");
        string b = Path.Combine(src, "b.txt");
        Directory.CreateDirectory(src);
        File.WriteAllText(a, "one content, two files");
        File.WriteAllText(b, "one content, two files");
        var first = Service(new RecordingFlush());
        Check((await first.ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None)).Success,
            $"{set.Name}: backed up");
        var bBefore = (await repo.GetFileRecordsByPathAsync(set.Id, b)).Single();
        Check(bBefore.IsFileRef && !(await repo.GetFileRecordsByPathAsync(set.Id, a)).Single().IsFileRef,
            "a.txt is stored plain, b.txt as a reference to it");

        File.WriteAllText(a, "a changed");
        Check((await first.ExecuteAsync(Job(set, src, dst), dst, null, null, CancellationToken.None)).Success,
            "a.txt changed: backed up again");
        File.WriteAllText(a, "a changed again");
        string aV1 = Path.Combine(dst, a[..1] + "_prev", a[Path.GetPathRoot(a)!.Length..] + ".v1");
        string bPlain = Path.GetFullPath(Path.Combine(dst, bBefore.DiscPath[..^".fileref".Length]));
        bool? sourceThereAtFlush = null;
        var recording = new RecordingFlush
        {
            FailsFor = written => !flushWorks && written.Contains(bPlain, StringComparer.OrdinalIgnoreCase),
            OnFlush = written =>
            {
                if (written.Contains(bPlain, StringComparer.OrdinalIgnoreCase))
                    sourceThereAtFlush = File.Exists(aV1);
            },
        };
        var second = await Service(recording).ExecuteAsync(Job(set, src, dst), dst,
            [new VersionRetentionTier { MaxVersions = 1 }], null, CancellationToken.None);
        Check(second.Success, "a.txt changed again: the run keeping one earlier version succeeds");
        Check(sourceThereAtFlush == true,
            $"b.txt's new plain copy was flushed while the bytes it came from were still there ({sourceThereAtFlush?.ToString() ?? "never flushed"})");

        var bAfter = (await repo.GetFileRecordsByPathAsync(set.Id, b)).Single();
        var aOld = await repo.GetFileRecordByPathAndVersionAsync(set.Id, a, 1);
        if (flushWorks)
        {
            Check(!bAfter.IsFileRef && File.Exists(Path.Combine(dst, bAfter.DiscPath))
                  && File.ReadAllText(Path.Combine(dst, bAfter.DiscPath)) == "one content, two files",
                "b.txt is a plain copy now, holding the content");
            Check(aOld is not null && aOld.IsDeleted && !File.Exists(aV1), "and a.txt's old version is gone");
        }
        else
        {
            Check(bAfter.IsFileRef, "when that flush fails, b.txt stays a reference");
            Check(aOld is not null && !aOld.IsDeleted && File.Exists(aV1),
                "and a.txt's old version, the bytes it resolves to, is kept");
        }
    }
}
finally
{
    repo?.Dispose();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (substLetter is not null)
        Run("subst", substLetter + ":", "/D");
    TryDelete(root);
    TryDelete(elsewhere);
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL PASS");
    return 0;
}
Console.WriteLine($"{failures} CHECK(S) FAILED");
return 1;

static async Task<Exception?> Try(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (Exception ex)
    {
        return ex;
    }
}

static int Run(string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in args)
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode;
}

static string? FreeDriveLetter()
{
    var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
    for (char c = 'Z'; c >= 'M'; c--)
        if (!used.Contains(c))
            return c.ToString();
    return null;
}

static void CopyTree(string from, string to)
{
    foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        string dest = Path.Combine(to, Path.GetRelativePath(from, f));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(f, dest);
        File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(f));
    }
}

static void TryDelete(string dir)
{
    try
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (could not delete {dir}: {ex.Message})");
    }
}

/// <summary>A flush that does nothing but note what it was given, or fails as a gone drive would.</summary>
sealed class RecordingFlush : IDestinationFlush
{
    public List<(string Destination, List<string> Written, bool Failed)> Calls { get; } = [];

    /// <summary>Whether the call with this 1-based number fails.</summary>
    public Func<int, bool> Fails { get; init; } = _ => false;

    /// <summary>Whether a call given these (full) paths fails.</summary>
    public Func<List<string>, bool> FailsFor { get; init; } = _ => false;

    /// <summary>Told of each call's (full) paths as it is made.</summary>
    public Action<List<string>>? OnFlush { get; init; }

    public Task FlushAsync(string destination, IReadOnlyCollection<string> written, CancellationToken ct)
    {
        var paths = written.Select(Path.GetFullPath).ToList();
        OnFlush?.Invoke(paths);
        bool fails = Fails(Calls.Count + 1) || FailsFor(paths);
        Calls.Add((destination, paths, fails));
        if (fails)
            throw new IOException($"simulated: the drive is gone (flush {Calls.Count})");
        return Task.CompletedTask;
    }

    /// <summary>Every path a flush that succeeded was given.</summary>
    public HashSet<string> Flushed() =>
        Calls.Where(c => !c.Failed).SelectMany(c => c.Written).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
