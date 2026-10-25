using System.IO.Compression;
using System.Text.Json;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>What <see cref="BackupSetTransfer.ImportAsync"/> created.</summary>
/// <param name="Set">The new backup set, as stored.</param>
/// <param name="IncludedHistory">Whether the file carried the set's backup history.</param>
/// <param name="FileRecords">How many file records (all versions) that history held.</param>
public sealed record BackupSetImportResult(BackupSet Set, bool IncludedHistory, int FileRecords);

/// <summary>
/// Reads and writes backup set export files (<c>.lithic</c>).
///
/// <para><b>Two formats, told apart by their first bytes.</b> Settings only:
/// the <see cref="ExportedBackupSet"/> as indented JSON - what every version
/// before 1.11 wrote, and still what is written when the history is left out,
/// so such a file stays readable and importable by older versions. With
/// history: a zip holding <c>backupset.json</c> (the same JSON) and
/// <c>history.db</c>, a snapshot of the set's per-set catalog database.</para>
///
/// <para><b>Why the history matters.</b> Without it, the imported set's catalog
/// says nothing has been backed up. On a new computer pointed at the same
/// backup drive, its first backup would copy every file again over the copies
/// already there, and the version history on the drive would be invisible to
/// it. With the history, the set carries on exactly where it left off, as long
/// as the destination and source paths still match.</para>
/// </summary>
public static class BackupSetTransfer
{
    /// <summary>The settings entry in a zip export.</summary>
    public const string SettingsEntryName = "backupset.json";

    /// <summary>The history-database entry in a zip export.</summary>
    public const string HistoryEntryName = "history.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Write <paramref name="set"/> to <paramref name="path"/>, with its backup
    /// history when <paramref name="includeHistory"/> is set. The file appears
    /// only once complete; a failure or cancel leaves nothing behind.
    /// </summary>
    /// <param name="progress">
    /// The step under way and how far through it, at the <see cref="ProgressCadence"/>:
    /// copying the history, then compressing it, each counted in bytes.
    /// </param>
    public static async Task ExportAsync(
        ICatalogRepository catalog, BackupSet set, string path, bool includeHistory,
        IProgress<ProgressReport>? progress = null, CancellationToken ct = default)
    {
        var exported = ExportedBackupSet.FromBackupSet(set, includeHistory);
        string json = JsonSerializer.Serialize(exported, JsonOptions);
        // Beside the export, on the drive the user chose for it: a history is
        // gigabytes, and the system drive may not have room for it. Named per
        // run, so two exports to one file cannot delete each other's.
        string stem = $"{Path.GetFullPath(path)}.{Guid.NewGuid():N}";
        string partial = stem + ".partial";

        string? historyDb = null;
        try
        {
            if (!includeHistory)
            {
                await File.WriteAllTextAsync(partial, json, ct);
            }
            else
            {
                historyDb = stem + ".history.partial";

                // The copy is one SQLite statement, but it writes its file as it
                // goes, to about the size of the pages it copies: its growth is
                // the progress.
                long expected = Math.Max(1, catalog.GetSetHistorySizeBytes(set.Id));
                string historyFile = historyDb;
                progress?.Report(new ProgressReport(
                    $"Step 1 of 2: copying the backup history (0 of about {expected:N0} bytes)", 0));
                var copying = catalog.ExportSetHistoryAsync(set.Id, historyDb, ct);
                while (await Task.WhenAny(copying, Task.Delay(ProgressCadence.IntervalMs)) != copying)
                {
                    long written = FileLength.OrZero(historyFile);
                    progress?.Report(new ProgressReport(
                        $"Step 1 of 2: copying the backup history ({written:N0} of about {expected:N0} bytes)",
                        Math.Min(99, written * 100.0 / expected)));
                }
                await copying;
                progress?.Report(new ProgressReport(
                    $"Step 1 of 2: copying the backup history ({FileLength.OrZero(historyFile):N0} of about {expected:N0} bytes)",
                    100));

                await Task.Run(() =>
                {
                    using var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                    var settings = zip.CreateEntry(SettingsEntryName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(settings.Open()))
                        writer.Write(json);
                    // Fastest: a large history is gigabytes, and the default level
                    // roughly doubles the time for a few percent off the size.
                    var history = zip.CreateEntry(HistoryEntryName, CompressionLevel.Fastest);
                    using var input = File.OpenRead(historyFile);
                    using var output = history.Open();
                    CopyCounting(input, output, input.Length, "Step 2 of 2: compressing", progress, ct);
                }, ct);
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            TryDelete(partial);
            if (historyDb is not null)
                TryDelete(historyDb);
        }
    }

    /// <summary>
    /// What an export file holds, without importing anything: its settings, and
    /// whether it carries the backup history. For asking before an import (a
    /// .lithic file opened from Explorer).
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a backup set export.</exception>
    public static async Task<(ExportedBackupSet Settings, bool HasHistory)> ReadSummaryAsync(
        string path, CancellationToken ct = default)
    {
        if (IsZip(path))
        {
            using var zip = ZipFile.OpenRead(path);
            var settings = zip.GetEntry(SettingsEntryName)
                ?? throw new InvalidDataException(
                    $"The file has no {SettingsEntryName}; it is not a backup set export.");
            await using var s = settings.Open();
            return (await DeserializeAsync(s, ct), zip.GetEntry(HistoryEntryName) is not null);
        }

        await using var f = File.OpenRead(path);
        return (await DeserializeAsync(f, ct), false);
    }

    /// <summary>
    /// Make an imported set's destination volume identity this computer's. One
    /// that resolves here is kept: the same disk can carry its GUID to another
    /// computer, and the set then follows it to whatever letter it has here. One
    /// that does not is dropped: another computer may know the drive by another
    /// GUID, which would never resolve here (the destination "not connected" for
    /// good), and a set's folder would compare as another volume's than a local
    /// set's in it. A drive that is not plugged in cannot be told from a foreign
    /// GUID, and loses its identity too. None is looked up from the path now -
    /// that letter may be another disk's here - but at the set's next save or
    /// backup, once the user has checked its destination, as the import asks.
    /// </summary>
    public static void LocalizeDestination(JobOptions options, IDestinationResolver resolver)
    {
        if (string.IsNullOrEmpty(options.DestinationVolumeId) || resolver.Resolve(options).IsConnected)
            return;
        options.DestinationVolumeId = null;
        options.DestinationSubpath = null;
    }

    /// <summary>
    /// Create a backup set from an export file, with its backup history if the
    /// file has it. Automated backups arrive paused
    /// (<see cref="JobOptions.HoldAutomatedBackups"/>). All or nothing: if the
    /// history cannot be imported, the set is removed again.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a backup set export.</exception>
    /// <param name="progress">
    /// The step under way and how far through it, at the <see cref="ProgressCadence"/>:
    /// extracting the history (bytes), then importing it (records).
    /// </param>
    /// <param name="destinations">
    /// This computer's destination resolver, to make the set's destination
    /// identity this computer's (<see cref="LocalizeDestination"/>); null leaves
    /// it as exported.
    /// </param>
    public static async Task<BackupSetImportResult> ImportAsync(
        ICatalogRepository catalog, string path,
        IProgress<ProgressReport>? progress = null, CancellationToken ct = default,
        IDestinationResolver? destinations = null)
    {
        string? historyDb = null;
        try
        {
            ExportedBackupSet exported;
            if (IsZip(path))
            {
                using var zip = ZipFile.OpenRead(path);
                var settings = zip.GetEntry(SettingsEntryName)
                    ?? throw new InvalidDataException(
                        $"The file has no {SettingsEntryName}; it is not a backup set export.");
                await using (var s = settings.Open())
                    exported = await DeserializeAsync(s, ct);

                if (zip.GetEntry(HistoryEntryName) is { } history)
                {
                    // Beside the catalog, whose volume must hold the history anyway.
                    historyDb = Path.Combine(catalog.DataDirectory, $"lithic-import-{Guid.NewGuid():N}.db");
                    string target = historyDb;
                    await Task.Run(() =>
                    {
                        using var input = history.Open();
                        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        CopyCounting(input, output, history.Length, "Step 1 of 2: extracting the backup history", progress, ct);
                    }, ct);
                }
            }
            else
            {
                await using var s = File.OpenRead(path);
                exported = await DeserializeAsync(s, ct);
            }

            bool withHistory = historyDb is not null;
            // Until the first records go in - creating the set, waiting for the
            // catalog's write lock, which a backup may hold, and sizing the history -
            // there is nothing to count.
            if (withHistory)
                progress?.Report(new ProgressReport("Step 2 of 2: importing the backup history (preparing)", null));
            var toCreate = exported.ToBackupSet(withHistory);
            if (destinations is not null && toCreate.JobOptions is { } options)
                LocalizeDestination(options, destinations);
            var newSet = await catalog.CreateBackupSetAsync(toCreate, ct);

            int records = 0;
            if (withHistory)
            {
                var cadence = new ProgressCadence();
                var rows = new SyncProgress<(long Done, long Total)>(r =>
                {
                    if (progress is not null && (cadence.IsDue() || r.Done == r.Total))
                        progress.Report(new ProgressReport(
                            $"Step 2 of 2: importing the backup history ({r.Done:N0} of {r.Total:N0} records)",
                            r.Total > 0 ? r.Done * 100.0 / r.Total : 100));
                });
                try
                {
                    records = await catalog.ImportSetHistoryAsync(newSet.Id, historyDb!, rows, ct);
                }
                catch
                {
                    // A set that says it has history but has none is the very
                    // thing this exists to prevent.
                    try { await catalog.DeleteBackupSetAsync(newSet.Id, CancellationToken.None); }
                    catch { /* best effort */ }
                    throw;
                }
            }

            return new BackupSetImportResult(newSet, withHistory, records);
        }
        finally
        {
            if (historyDb is not null)
            {
                TryDelete(historyDb);
                TryDelete(historyDb + "-journal");
                TryDelete(historyDb + "-wal");
                TryDelete(historyDb + "-shm");
            }
        }
    }

    /// <summary>
    /// Copy <paramref name="input"/> to <paramref name="output"/>, reporting
    /// <paramref name="step"/> with the bytes copied of <paramref name="total"/>
    /// at the <see cref="ProgressCadence"/> and once more when done, and stopping
    /// for a cancel between blocks.
    /// </summary>
    private static void CopyCounting(
        Stream input, Stream output, long total, string step,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        var cadence = new ProgressCadence();
        long copied = 0;
        int read;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (progress is not null && cadence.IsDue())
                progress.Report(new ProgressReport(
                    $"{step} ({copied:N0} of {total:N0} bytes)",
                    total > 0 ? copied * 100.0 / total : 0));
            read = input.Read(buffer, 0, buffer.Length);
            output.Write(buffer, 0, read);
            copied += read;
        }
        while (read > 0);
        progress?.Report(new ProgressReport($"{step} ({copied:N0} of {total:N0} bytes)", 100));
    }

    private static async Task<ExportedBackupSet> DeserializeAsync(Stream s, CancellationToken ct)
    {
        try
        {
            // Any JSON object deserializes - unknown members ignored, missing ones
            // defaulted - so another program's settings file would pass as a
            // nameless set with no sources. Every export writes these two.
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var top = doc.RootElement;
            bool Has(string member) => top.EnumerateObject().Any(
                p => string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase));
            if (top.ValueKind != JsonValueKind.Object
                || !Has(nameof(ExportedBackupSet.Name))
                || !Has(nameof(ExportedBackupSet.SourceRoots)))
                throw new InvalidDataException("The file is not a backup set configuration.");
            return top.Deserialize<ExportedBackupSet>(JsonOptions)
                ?? throw new InvalidDataException("The file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid backup set configuration.", ex);
        }
    }

    /// <summary>Whether the file starts with a zip local-file header ("PK\3\4").</summary>
    private static bool IsZip(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* a temp file; best effort */ }
    }
}
