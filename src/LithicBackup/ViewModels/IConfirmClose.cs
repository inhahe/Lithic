namespace LithicBackup.ViewModels;

/// <summary>
/// A task flow that can hold edits the user has not saved, and so gets a say
/// before its window closes. <see cref="Views.TaskWindow"/> asks on every close,
/// whether it comes from the flow's own Close button or from the window's ✕.
/// </summary>
public interface IConfirmClose
{
    /// <summary>
    /// Called as the window is about to close. With nothing unsaved, return true
    /// without asking anything. Otherwise ask Save / Don't Save (/ Cancel), act on
    /// the answer, and return false only to keep the window open.
    /// </summary>
    /// <param name="canCancel">
    /// False while the app is exiting: the close cannot be stopped, so offer only
    /// Save or Don't Save, and return true either way.
    /// </param>
    bool ConfirmClose(bool canCancel);
}
