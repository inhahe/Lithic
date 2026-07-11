using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>
/// One row in the restore destination editor: a source drive from the backup
/// set (e.g. "D:") paired with the folder its files should be restored under.
/// </summary>
public class DriveDestinationViewModel : ViewModelBase
{
    private string _destinationPath = "";

    public DriveDestinationViewModel(string driveLetter)
    {
        DriveLetter = driveLetter;
        BrowseCommand = new RelayCommand(_ => Browse());
    }

    /// <summary>Source drive as it appears in paths, e.g. "D:".</summary>
    public string DriveLetter { get; }

    /// <summary>Uppercase drive letter without the colon, e.g. "D".</summary>
    public string DriveKey => DriveLetter.TrimEnd(':').ToUpperInvariant();

    /// <summary>The drive's own root, used when restoring to original locations.</summary>
    public string OriginalRoot =>
        DriveLetter.EndsWith(':') ? DriveLetter + "\\" : DriveLetter;

    /// <summary>Folder under which this drive's files are recreated.</summary>
    public string DestinationPath
    {
        get => _destinationPath;
        set => SetProperty(ref _destinationPath, value);
    }

    public ICommand BrowseCommand { get; }

    private void Browse()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Select destination folder for files from {DriveLetter}",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DestinationPath = dialog.SelectedPath;
    }
}
