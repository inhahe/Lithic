namespace LithicBackup.ViewModels;

/// <summary>
/// View model backing <see cref="Views.ProgressDialog"/>: a lightweight modal
/// that shows what a background operation is doing (a fixed <see cref="Message"/>)
/// plus a live <see cref="Detail"/> line (counts / percentage), and — when
/// <see cref="IsCancellable"/> — offers a Cancel button that aborts the work.
///
/// Deliberately dumb: the owning <see cref="Views.ProgressDialog"/> drives all
/// state (updating <see cref="Detail"/> from an <c>IProgress&lt;string&gt;</c>
/// and flipping <see cref="CanCancel"/> off once cancellation is under way).
/// </summary>
public class ProgressDialogViewModel : ViewModelBase
{
    private string _message;
    private string _detail = "";
    private bool _canCancel;

    public ProgressDialogViewModel(string title, string message, bool cancellable)
    {
        Title = title;
        _message = message;
        IsCancellable = cancellable;
        _canCancel = cancellable;
    }

    /// <summary>Window title bar text.</summary>
    public string Title { get; }

    /// <summary>Fixed one-line description of what the operation is doing.</summary>
    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    /// <summary>Live progress line (e.g. "Checked 12,340 files, 87 to remove").</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    /// <summary>Whether this operation can be cancelled at all (shows the button).</summary>
    public bool IsCancellable { get; }

    /// <summary>
    /// Whether the Cancel button is currently clickable. Set false the moment a
    /// cancel is requested so the user can't spam it while the work unwinds.
    /// </summary>
    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }
}
