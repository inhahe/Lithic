using System.IO;
using System.Text.Json;

namespace LithicBackup;

/// <summary>
/// Persistent user preferences stored as JSON in the app data directory.
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
