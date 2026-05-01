using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the per-file failure dialog.
/// </summary>
public class FailureDialogViewModel : ViewModelBase
{
    private string _filePath = string.Empty;
    private string _errorMessage = string.Empty;
    private BurnFailureAction _chosenAction = BurnFailureAction.Skip;
    private bool _isDirectoryMode;

    /// <summary>The path of the file that failed.</summary>
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    /// <summary>The error message from the failure.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>The action chosen by the user.</summary>
    public BurnFailureAction ChosenAction
    {
        get => _chosenAction;
        set => SetProperty(ref _chosenAction, value);
    }

    /// <summary>
    /// When <c>true</c>, the dialog is shown during a directory backup
    /// instead of a disc burn. Hides disc-specific options (Zip, Skip All
    /// for Disc) and shows Abort.
    /// </summary>
    public bool IsDirectoryMode
    {
        get => _isDirectoryMode;
        set => SetProperty(ref _isDirectoryMode, value);
    }
}
