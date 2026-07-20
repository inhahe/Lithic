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
    /// What happens after you edit a set's source selection and save: whether to
    /// reconcile the destination (offer to back up newly-added folders and purge
    /// copies of removed ones), which requires scanning the affected folders.
    /// <see cref="ReconcileAfterEditMode.Ask"/> (default) prompts after each
    /// change; <see cref="ReconcileAfterEditMode.Always"/> reconciles silently;
    /// <see cref="ReconcileAfterEditMode.Never"/> skips it (folders sync on the
    /// next full backup instead). The reconcile only runs when a checkbox was
    /// actually toggled — browsing/expanding the tree never triggers it.
    /// </summary>
    public ReconcileAfterEditMode ReconcileMode { get; set; } = ReconcileAfterEditMode.Ask;

    /// <summary>
    /// Machine-global rules for continuous-mode backup timing: size-tiered
    /// debounce windows and mask-tiered max-wait caps. Shared by every
    /// continuous set (there are no per-set debounce/max-wait fields), so all
    /// watched sources honor the same policy. See <see cref="ContinuousRules"/>.
    /// </summary>
    public ContinuousRules ContinuousRules { get; set; } = new();

    /// <summary>
    /// Index of the tab last active in the Settings dialog. Remembered so the
    /// dialog reopens where the user left off. Pure UI state (not a backup
    /// setting), so it's persisted on close regardless of Save/Cancel and
    /// clamped to the valid range in case the tab layout changes.
    /// </summary>
    public int LastSettingsTab { get; set; }

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
