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
    private bool _applyToAll;

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

    /// <summary>Whether to apply the chosen action to all remaining failures on this disc.</summary>
    public bool ApplyToAll
    {
        get => _applyToAll;
        set => SetProperty(ref _applyToAll, value);
    }
}
