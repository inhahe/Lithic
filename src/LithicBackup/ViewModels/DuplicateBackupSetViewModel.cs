using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the "Duplicate Backup Set" dialog. Lets the user pick a name
/// for the new set, choose which grouped parts of the source set to carry over
/// (sources, settings, schedule), and set the destination directory directly.
///
/// The destination is pre-filled with the source set's target directory so the
/// usual edits are quick: clear or change it to back up the same sources to a
/// different place, or keep it to back up to the same location. The "backup
/// history" option is only meaningful when the destination still matches the
/// original target, so it is automatically disabled (and cleared) whenever the
/// destination is changed away from it.
/// </summary>
public class DuplicateBackupSetViewModel : ViewModelBase
{
    private readonly string _originalTargetDirectory;

    public DuplicateBackupSetViewModel(string sourceName, string? sourceTargetDirectory)
    {
        _name = $"Copy of {sourceName}";
        _originalTargetDirectory = sourceTargetDirectory ?? string.Empty;
        _targetDirectory = _originalTargetDirectory;

        BrowseTargetCommand = new RelayCommand(_ => BrowseTarget());
    }

    private string _name;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private bool _copySourceSelections = true;
    /// <summary>Files/directories to back up.</summary>
    public bool CopySourceSelections
    {
        get => _copySourceSelections;
        set => SetProperty(ref _copySourceSelections, value);
    }

    private bool _copySettings = true;
    /// <summary>Deduplication, filesystem, verification and other job options.</summary>
    public bool CopySettings
    {
        get => _copySettings;
        set => SetProperty(ref _copySettings, value);
    }

    private bool _copyTierSets = true;
    /// <summary>Version-retention tier-set definitions (the Default tiers and any custom sets).</summary>
    public bool CopyTierSets
    {
        get => _copyTierSets;
        set => SetProperty(ref _copyTierSets, value);
    }

    private bool _copyExclusionPatterns = true;
    /// <summary>The set's list of exclusion patterns (filename and full-path wildcards).</summary>
    public bool CopyExclusionPatterns
    {
        get => _copyExclusionPatterns;
        set => SetProperty(ref _copyExclusionPatterns, value);
    }

    private bool _copySchedule = true;
    /// <summary>Scheduling mode and timing.</summary>
    public bool CopySchedule
    {
        get => _copySchedule;
        set => SetProperty(ref _copySchedule, value);
    }

    private string _targetDirectory;
    /// <summary>
    /// Destination directory for the new set. Pre-filled with the source set's
    /// target. Leave empty to choose a location later. Changing it away from the
    /// original disables (and clears) the backup-history option, since the
    /// catalog records describe files at the original destination.
    /// </summary>
    public string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            if (SetProperty(ref _targetDirectory, value))
            {
                if (!CanCopyBackupHistory)
                    CopyBackupHistory = false;
                OnPropertyChanged(nameof(CanCopyBackupHistory));
            }
        }
    }

    private bool _copyBackupHistory;
    /// <summary>
    /// The catalog records of which files are already backed up. Off by default;
    /// only valid when the destination still matches the original target
    /// (otherwise the records would describe files at the wrong destination).
    /// </summary>
    public bool CopyBackupHistory
    {
        get => _copyBackupHistory;
        set => SetProperty(ref _copyBackupHistory, value);
    }

    /// <summary>
    /// Backup history can only be copied when the destination is unchanged from
    /// the source set's original target directory.
    /// </summary>
    public bool CanCopyBackupHistory =>
        !string.IsNullOrWhiteSpace(_targetDirectory)
        && PathsEqual(_targetDirectory, _originalTargetDirectory);

    /// <summary>True when the destination still points at the original target.</summary>
    public bool KeepsOriginalTarget => CanCopyBackupHistory;

    public ICommand BrowseTargetCommand { get; }

    private void BrowseTarget()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select destination directory for the new backup set",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (!string.IsNullOrWhiteSpace(TargetDirectory) && System.IO.Directory.Exists(TargetDirectory))
            dialog.SelectedPath = TargetDirectory;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TargetDirectory = dialog.SelectedPath;
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.Trim().TrimEnd('\\', '/'),
            b.Trim().TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
