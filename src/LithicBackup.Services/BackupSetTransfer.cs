using System.IO.Compression;
using System.Text.Json;
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
/// backup drive, its first backup copied every file again over the copies
/// already there, and the version history on the drive was invisible to it.
/// That is what happened to the first two sets ever moved this way: they
/// arrived saying "Never backed up", and the Worker began a 1.6 TB re-copy a
/// minute later. With the history, the set carries on exactly where it left
/// off, as long as the destination and source paths still match.</para>
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
    public static async Task ExportAsync(
        ICatalogRepository catalog, BackupSet set, string path, bool includeHistory,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var exported = ExportedBackupSet.FromBackupSet(set, includeHistory);
        string json = JsonSerializer.Serialize(exported, JsonOptions);
        string partial = path + ".partial";

        string? historyDb = null;
        try
        {
            if (!includeHistory)
            {
                await File.WriteAllTextAsync(partial, json, ct);
            }
            else
            {
                // Beside the export, on the drive the user chose for it: a history
                // is gigabytes, and the system drive may not have room for it.
                historyDb = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                    $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.history.partial");
                status?.Report("Copying the backup history…");
                await catalog.ExportSetHistoryAsync(set.Id, historyDb, ct);

                status?.Report("Compressing…");
                await Task.Run(() =>
                {
                    using var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                    var settings = zip.CreateEntry(SettingsEntryName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(settings.Open()))
                        writer.Write(json);
                    // Fastest: a large history is gigabytes, and the default level
                    // roughly doubles the time for a few percent off the size.
                    zip.CreateEntryFromFile(historyDb, HistoryEntryName, CompressionLevel.Fastest);
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
    /// Create a backup set from an export file, with its backup history if the
    /// file has it. Automated backups arrive paused
    /// (<see cref="JobOptions.HoldAutomatedBackups"/>). All or nothing: if the
    /// history cannot be imported, the set is removed again.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a backup set export.</exception>
    public static async Task<BackupSetImportResult> ImportAsync(
        ICatalogRepository catalog, string path,
        IProgress<string>? status = null, CancellationToken ct = default)
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
                    status?.Report("Extracting the backup history…");
                    // Beside the catalog, whose volume must hold the history anyway.
                    historyDb = Path.Combine(catalog.DataDirectory, $"lithic-import-{Guid.NewGuid():N}.db");
                    await Task.Run(() => history.ExtractToFile(historyDb), ct);
                }
            }
            else
            {
                await using var s = File.OpenRead(path);
                exported = await DeserializeAsync(s, ct);
            }

            bool withHistory = historyDb is not null;
            var newSet = await catalog.CreateBackupSetAsync(exported.ToBackupSet(withHistory), ct);

            int records = 0;
            if (withHistory)
            {
                status?.Report("Importing the backup history…");
                try
                {
                    records = await catalog.ImportSetHistoryAsync(newSet.Id, historyDb!, ct);
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

    private static async Task<ExportedBackupSet> DeserializeAsync(Stream s, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<ExportedBackupSet>(s, JsonOptions, ct)
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
