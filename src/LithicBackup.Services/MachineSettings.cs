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
/// <para>Only the app writes the file, atomically, so the Worker never reads half
/// of one. A setting added here must mean something to the Worker, or at least do
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

    /// <summary>The saved settings, or the defaults when there are none or they cannot be read.</summary>
    public static MachineSettings Load(string? path = null)
        => Read(path ?? DefaultPath) ?? new();

    /// <summary>
    /// The saved settings; if there is no machine file yet, those the calling
    /// account's <see cref="UserSettings"/> file holds, under the same names from
    /// when they lived there, saved as the machine file so the Worker sees them.
    /// </summary>
    /// <remarks>
    /// For the app only. The Worker runs as LocalSystem, whose per-user file is
    /// one nothing writes, and seeding from it would put the defaults in place
    /// before the user's app had carried over the real choices.
    /// </remarks>
    /// <param name="path">The machine file; null for <see cref="DefaultPath"/>.</param>
    /// <param name="legacyUserSettingsPath">The per-user file to carry over from;
    /// null for this account's <see cref="UserSettings"/> file.</param>
    public static MachineSettings LoadOrCarryOver(string? path = null, string? legacyUserSettingsPath = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            return Load(path);

        var carried = Read(legacyUserSettingsPath ?? UserSettings.FilePath);
        if (carried is null)
            return new();

        carried.Save(path);
        return carried;
    }

    /// <summary>
    /// Write the settings, replacing the file in one step, so the Worker, which
    /// reads it while the app runs, sees the old settings or the new ones, never
    /// part of a file.
    /// </summary>
    /// <param name="path">The machine file; null for <see cref="DefaultPath"/>,
    /// whose folder is created with an ACL every account can write.</param>
    /// <returns>Whether the file was written. Failures are not thrown, as for
    /// <see cref="UserSettings"/>.</returns>
    public bool Save(string? path = null)
    {
        string? temp = null;
        try
        {
            if (path is null)
            {
                CatalogLocation.SharedDirectory();
                path = DefaultPath;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }

            temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

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
            try { if (temp is not null) File.Delete(temp); } catch { }
            return false;
        }
    }

    private static MachineSettings? Read(string path)
    {
        try
        {
            // Copy the bytes and let go before parsing: a save cannot replace the
            // file while any handle is open, and parsing a large file from the
            // stream held it for milliseconds at a time.
            byte[] bytes;
            using (var stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            return JsonSerializer.Deserialize<MachineSettings>(bytes);
        }
        catch
        {
            return null;
        }
    }
}
