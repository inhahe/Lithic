namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the "Export Backup Set" dialog: whether to include the set's
/// backup history. On by default whenever there is any, since an export is how
/// a set moves to another computer, and without its history it arrives knowing
/// nothing about the backup already on its destination.
/// </summary>
public class ExportBackupSetViewModel : ViewModelBase
{
    public ExportBackupSetViewModel(string setName, bool hasHistory, long historyBytes)
    {
        SetName = setName;
        HasHistory = hasHistory;
        _includeHistory = hasHistory;
        HistorySizeText = !hasHistory
            ? "This set has no backup history yet, so there is none to include."
            : historyBytes > 0
                ? $"About {FormatSize(historyBytes)} before compression."
                : "";
    }

    public string SetName { get; }

    public string Title => $"Export \"{SetName}\"";

    /// <summary>Whether the set has any history to include.</summary>
    public bool HasHistory { get; }

    private bool _includeHistory;
    /// <summary>Include the catalog records of what is already backed up.</summary>
    public bool IncludeHistory
    {
        get => _includeHistory;
        set => SetProperty(ref _includeHistory, value);
    }

    /// <summary>The history's size, or why the option is unavailable.</summary>
    public string HistorySizeText { get; }

    private static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} bytes" : $"{size:N1} {units[i]}";
    }
}
