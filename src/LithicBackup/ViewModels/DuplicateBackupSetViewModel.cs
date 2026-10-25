using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the "Duplicate Backup Set" dialog. Lets the user pick a name
/// for the new set, choose which grouped parts of the source set to carry over
/// (sources, settings, schedule), and set the destination directory directly.
///
/// The destination is pre-filled with a folder of the copy's own, named for it,
/// beside the original's (MainViewModel.SuggestedCopyFolder), so the dialog works
/// as it opens; it follows the name until the destination is edited. The copy
/// needs a backup folder of its own: one that is already a set's is refused (see
/// JobOptions.SharesBackupFolderWith), and the dialog says so as soon as the
/// destination names one. The copy starts with no backup history; it can be
/// seeded from its own folder.
/// </summary>
public class DuplicateBackupSetViewModel : ViewModelBase
{
    private readonly Func<string, string?>? _backupFolderOwner;
    private readonly Func<string, string>? _suggestDestination;

    /// <summary>Whether the user has set the destination, which then no longer follows the name.</summary>
    private bool _destinationEdited;

    /// <param name="sourceName">The name of the set being copied.</param>
    /// <param name="suggestDestination">
    /// The destination to suggest for a copy of a given name; null suggests none.
    /// </param>
    /// <param name="backupFolderOwner">
    /// The name of the set whose backup folder a destination would be, or null.
    /// </param>
    public DuplicateBackupSetViewModel(
        string sourceName, Func<string, string>? suggestDestination, Func<string, string?>? backupFolderOwner = null)
    {
        _name = $"Copy of {sourceName}";
        _suggestDestination = suggestDestination;
        _targetDirectory = suggestDestination?.Invoke(_name) ?? string.Empty;
        _backupFolderOwner = backupFolderOwner;

        BrowseTargetCommand = new RelayCommand(_ => BrowseTarget());
    }

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value) && !_destinationEdited && _suggestDestination is not null)
                SetDestination(_suggestDestination(value.Trim()));
        }
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
    /// Destination directory for the new set. Pre-filled with a suggestion; empty
    /// to choose a location later.
    /// </summary>
    public string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            _destinationEdited = true;
            SetDestination(value);
        }
    }

    private void SetDestination(string value)
    {
        if (SetProperty(ref _targetDirectory, value, nameof(TargetDirectory)))
        {
            OnPropertyChanged(nameof(IsDestinationInUse));
            OnPropertyChanged(nameof(DestinationInUseText));
        }
    }

    /// <summary>The set whose backup folder the destination is, or null.</summary>
    private string? DestinationOwner => string.IsNullOrWhiteSpace(_targetDirectory)
        ? null
        : _backupFolderOwner?.Invoke(_targetDirectory.Trim());

    /// <summary>Whether the destination is already a set's backup folder, which a copy may not have.</summary>
    public bool IsDestinationInUse => DestinationOwner is not null;

    /// <summary>What the dialog says while <see cref="IsDestinationInUse"/>; empty otherwise.</summary>
    public string DestinationInUseText => DestinationOwner is { } owner
        ? $"This is the backup folder of \"{owner}\". The copy needs a folder of its own: change it, "
          + "or leave it empty to choose one later."
        : "";

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
}
