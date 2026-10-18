using System.Text.Json;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;

namespace LithicBackup.Services;

/// <summary>
/// The settings that govern backups themselves, which the Worker service must see
/// exactly as the app saves them: one file in <see cref="CatalogLocation.RootDirectory"/>,
/// shared by every account, beside the catalog. Preferences of one user's app
/// (prompts, update checks, layout) are <see cref="UserSettings"/>.
/// </summary>
/// <remarks>
/// <para>They cannot live in a per-user file: the Worker runs as LocalSystem, so
/// it would read the system profile's copy, which nothing writes, and back up with
/// the defaults whatever the user chose. The catalog is here for the same
/// reason.</para>
/// <para>Only the app writes the file, atomically and flushed, so a reader sees
/// the old settings or the new, never part of a file. Read it when it is needed,
/// rather than keeping a copy from startup: another account's app may have saved
/// since. A setting added here must mean something to the Worker, or at least do
/// no harm there.</para>
/// </remarks>
public sealed class MachineSettings
{
    /// <summary>
    /// How much RAM directory backups may use to buffer file contents in memory
    /// (read each file once instead of twice). Applies to all backups.
    /// </summary>
    public MemoryBudgetOptions MemoryBudget { get; set; } = new();

    /// <summary>
    /// How plain files are supplied to the disc burner. <see cref="DiscStagingMode.TemporaryCopy"/>
    /// (default) copies each file to temp before burning; <see cref="DiscStagingMode.InPlace"/>
    /// burns directly from the source under a held read lock (no temp copy),
    /// which avoids needing temp space for large media but locks source files
    /// for the duration of the burn. Applies to all disc backups.
    /// </summary>
    public DiscStagingMode DiscStagingMode { get; set; } = DiscStagingMode.TemporaryCopy;

    /// <summary>
    /// Rules for continuous-mode backup timing: size-tiered debounce windows and
    /// mask-tiered max-wait caps. Shared by every continuous set (there are no
    /// per-set debounce/max-wait fields), so all watched sources honor the same
    /// policy. See <see cref="Core.Models.ContinuousRules"/>.
    /// </summary>
    public ContinuousRules ContinuousRules { get; set; } = new();

    /// <summary>The machine settings file.</summary>
    public static string DefaultPath => Path.Combine(CatalogLocation.RootDirectory, "settings.json");

    /// <summary>What <see cref="TryLoad"/> found.</summary>
    public enum LoadResult
    {
        /// <summary>The file was read.</summary>
        Loaded,

        /// <summary>There is no file: the defaults are the settings.</summary>
        Absent,

        /// <summary>The file is there but could not be read or parsed. The defaults
        /// are NOT the settings; a caller that had good ones should keep them.</summary>
        Unreadable,
    }

    /// <summary>
    /// When the settings file was last written, for a caller that polls the
    /// settings and reads them again only after they change. Null when it cannot
    /// be told (the folder unreadable, say): read them anyway.
    /// </summary>
    public static DateTime? LastWriteTimeUtc(string? path = null)
    {
        try { return File.GetLastWriteTimeUtc(path ?? DefaultPath); }
        catch { return null; }
    }

    /// <summary>
    /// Read the settings, telling a missing file (the defaults apply) apart from
    /// one that cannot be read (they do not).
    /// </summary>
    /// <param name="settings">The settings read; the defaults unless
    /// <see cref="LoadResult.Loaded"/>.</param>
    /// <param name="path">The machine file; null for <see cref="DefaultPath"/>.</param>
    public static LoadResult TryLoad(out MachineSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        settings = new MachineSettings();
        byte[] bytes;
        try
        {
            // Copied out and let go before parsing: the File.Move fallback of a
            // save cannot replace the file while any handle is open.
            using (var stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LoadResult.Absent;
        }
        catch
        {
            return LoadResult.Unreadable;
        }

        var parsed = Parse(bytes);
        if (parsed is null)
            return LoadResult.Unreadable;
        settings = parsed;
        return LoadResult.Loaded;
    }

    /// <summary>The saved settings, or the defaults when there are none or they cannot be read.</summary>
    public static MachineSettings Load(string? path = null)
    {
        TryLoad(out var settings, path);
        return settings;
    }

    /// <summary>
    /// If there is no machine file yet, write one from the calling account's
    /// <see cref="UserSettings"/> file, which held these settings under the same
    /// names before they moved here, so that what the user chose there reaches
    /// the Worker. With several accounts, the first to start the app wins: the
    /// settings are one for the whole PC now.
    /// </summary>
    /// <remarks>
    /// For the app only. The Worker runs as LocalSystem, whose per-user file is
    /// one nothing writes, and seeding from it would put the defaults in place
    /// before the user's app had carried over the real choices. A carry-over that
    /// fails is tried again at the next start: <see cref="UserSettings"/> keeps
    /// the old fields it does not know.
    /// </remarks>
    /// <param name="path">The machine file; null for <see cref="DefaultPath"/>.</param>
    /// <param name="legacyUserSettingsPath">The per-user file to carry over from;
    /// null for this account's <see cref="UserSettings"/> file.</param>
    /// <returns>Whether a machine file was written.</returns>
    public static bool CarryOverIfMissing(string? path = null, string? legacyUserSettingsPath = null)
    {
        if (File.Exists(path ?? DefaultPath))
            return false;
        if (TryLoad(out var carried, legacyUserSettingsPath ?? UserSettings.FilePath) != LoadResult.Loaded)
            return false;
        return carried.Save(path);
    }

    /// <summary>
    /// Write the settings, replacing the file in one step, so a reader sees the old
    /// settings or the new ones, never part of a file, even after a crash.
    /// </summary>
    /// <remarks>
    /// Can wait about a second for a handle that does not share delete access, so
    /// keep it off the UI thread.
    /// </remarks>
    /// <param name="path">The machine file; null for <see cref="DefaultPath"/>.
    /// That file's folder is created, whichever way it is named, with an ACL every
    /// account can write.</param>
    /// <returns>Whether the file was written. Failures are not thrown, as for
    /// <see cref="UserSettings"/>.</returns>
    public bool Save(string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (string.Equals(path, Path.GetFullPath(DefaultPath), StringComparison.OrdinalIgnoreCase))
                CatalogLocation.SharedDirectory();
            else
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Flushed to the disk before the rename: otherwise a crash soon after
            // can leave the renamed file full of zeros, and both settings gone.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            // A POSIX-semantics replace goes through while readers that share
            // delete access have the file open (ours do, and virus scanners
            // usually do). One that does not share it is waited out: retried for
            // about a second, at uneven intervals so it cannot fall into step with
            // something that comes round regularly.
            for (int attempt = 1; attempt <= 50; attempt++)
            {
                if (AtomicFile.TryReplace(temp, path) == AtomicFile.ReplaceResult.Replaced)
                    return true;
                Thread.Sleep(5 + Random.Shared.Next(30));
            }
            File.Delete(temp);
            return false;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            return false;
        }
    }

    /// <summary>The settings in <paramref name="bytes"/>, or null when they are
    /// not valid JSON. A UTF-8 byte-order mark, which some editors add, is
    /// skipped: the span overload of the serializer does not.</summary>
    private static MachineSettings? Parse(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
        if (bytes.StartsWith(byteOrderMark))
            bytes = bytes[byteOrderMark.Length..];
        try
        {
            return JsonSerializer.Deserialize<MachineSettings>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
