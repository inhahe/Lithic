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
