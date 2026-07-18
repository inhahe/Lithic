using System.Text.Json;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Persistent, machine-global user preferences stored as JSON in the app data
/// directory. Shared by the interactive app and the headless Worker so settings
/// like the memory budget apply to both manual and scheduled backups.
/// </summary>
public class UserSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LithicBackup", "settings.json");

    /// <summary>
    /// When true, suppress the system tray balloon tip that suggests running
    /// a backup after file changes accumulate.
    /// </summary>
    public bool SuppressBackupSuggestions { get; set; }

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
    /// When true (default), the app quietly checks GitHub Releases for a newer
    /// version shortly after startup and shows an in-window banner if one is
    /// available. The "Check for updates" menu item always works regardless of
    /// this setting.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// The latest-release version the user explicitly dismissed. While the newest
    /// available release equals this value the startup check stays silent; a newer
    /// release than this supersedes the dismissal and the banner returns.
    /// </summary>
    public string? DismissedUpdateVersion { get; set; }

    /// <summary>
    /// When true (default), after you edit a backup set's source selection the app
    /// reconciles the destination with the change: it offers to purge copies of
    /// folders you removed from the set and to back up folders you newly added.
    /// This requires scanning the affected folders. When false, the reconcile (and
    /// its scan) is skipped entirely — added/removed folders are then only synced
    /// the next time you run a full backup of the set. The scan already runs only
    /// when you actually toggle a checkbox (merely browsing/expanding the tree
    /// never triggers it); this switch lets you turn it off even for real edits.
    /// </summary>
    public bool ReconcileAfterEdit { get; set; } = true;

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<UserSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
