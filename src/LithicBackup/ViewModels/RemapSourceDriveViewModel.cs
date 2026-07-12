namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the "Remap Source Drive" dialog. Lets the user pick which
/// existing source drive letter to remap and the new (currently present) drive
/// letter the same data now lives on. The catalog's source paths and the set's
/// source configuration are rewritten to the new drive so future backups treat
/// the already-backed-up files as present instead of re-copying everything.
/// </summary>
public class RemapSourceDriveViewModel : ViewModelBase
{
    private readonly Dictionary<char, int> _counts;

    public RemapSourceDriveViewModel(
        IReadOnlyList<char> sourceDrives,
        IReadOnlyList<char> targetDrives,
        Dictionary<char, int> countsByDrive)
    {
        _counts = countsByDrive;
        SourceDriveItems = sourceDrives.Select(c => $"{c}:").ToList();
        TargetDriveItems = targetDrives.Select(c => $"{c}:").ToList();
        _selectedSourceDrive = SourceDriveItems.FirstOrDefault();
        _selectedTargetDrive = TargetDriveItems.FirstOrDefault();
        UpdatePreview();
    }

    /// <summary>Source drive letters currently used by the set, as "E:" items.</summary>
    public List<string> SourceDriveItems { get; }

    /// <summary>Ready drive letters available as remap targets, as "F:" items.</summary>
    public List<string> TargetDriveItems { get; }

    private string? _selectedSourceDrive;
    public string? SelectedSourceDrive
    {
        get => _selectedSourceDrive;
        set { if (SetProperty(ref _selectedSourceDrive, value)) UpdatePreview(); }
    }

    private string? _selectedTargetDrive;
    public string? SelectedTargetDrive
    {
        get => _selectedTargetDrive;
        set => SetProperty(ref _selectedTargetDrive, value);
    }

    private string _previewText = string.Empty;
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    /// <summary>Uppercase drive letter chosen as the old source, or null.</summary>
    public char? SourceDriveLetter => ParseDrive(_selectedSourceDrive);

    /// <summary>Uppercase drive letter chosen as the new source, or null.</summary>
    public char? TargetDriveLetter => ParseDrive(_selectedTargetDrive);

    private static char? ParseDrive(string? s) =>
        !string.IsNullOrEmpty(s) && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]) : null;

    private void UpdatePreview()
    {
        if (SourceDriveLetter is char c && _counts.TryGetValue(c, out var n))
            PreviewText = $"{n:N0} catalog record{(n == 1 ? "" : "s")} will be remapped to the new drive.";
        else
            PreviewText = string.Empty;
    }
}
