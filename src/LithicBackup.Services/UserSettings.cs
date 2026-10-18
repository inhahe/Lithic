using System.Text.Json;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// One user's preferences for the app, stored as JSON in that account's local app
/// data: prompts, update checks, dialog and column layout. Nothing the Worker
/// service needs belongs here, since it runs as another account; settings that
/// govern backups are <see cref="MachineSettings"/>.
/// </summary>
public class UserSettings
{
    /// <summary>This account's settings file. <see cref="MachineSettings"/> carries
    /// its settings over from here, where they used to be kept.</summary>
    internal static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LithicBackup", "settings.json");

    /// <summary>
    /// When true, suppress the system tray balloon tip that suggests running
    /// a backup after file changes accumulate.
    /// </summary>
    public bool SuppressBackupSuggestions { get; set; }

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
    /// Index of the tab last active in the Settings dialog. Remembered so the
    /// dialog reopens where the user left off. Pure UI state (not a backup
    /// setting), so it's persisted on close regardless of Save/Cancel and
    /// clamped to the valid range in case the tab layout changes.
    /// </summary>
    public int LastSettingsTab { get; set; }

    /// <summary>
    /// How many category cards Cleanup lays out per row (1-3).
    ///
    /// <para>Three fits the most categories on screen but gives each card a
    /// third of the window, and the path tree indents 19px per level - so on a
    /// deep tree the path is squeezed out of a ~160px name column. One column
    /// trades seeing several categories at once for being able to read the
    /// paths in the one you are working on.</para>
    /// </summary>
    public int CleanupCategoryColumns { get; set; } = 3;

    /// <summary>
    /// Width of Cleanup's "Files" column. Narrowing it widens the Directory
    /// column, which is a star column taking whatever is left.
    /// </summary>
    public double CleanupFilesColumnWidth { get; set; } = 56;

    /// <summary>Width of Cleanup's "Size" column. See <see cref="CleanupFilesColumnWidth"/>.</summary>
    public double CleanupSizeColumnWidth { get; set; } = 80;

    /// <summary>
    /// Fields in the file that this version does not know, written back as they
    /// were: the backup settings this file held before <see cref="MachineSettings"/>
    /// (kept until a carry-over succeeds), and any a newer version added.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}
