using System.ComponentModel;
using System.Windows;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

/// <summary>
/// Non-modal host for a single task flow. See the XAML for why it is an
/// independent top-level window rather than one owned by the main window.
/// </summary>
public partial class TaskWindow : Window
{
    public static readonly DependencyProperty FlowProperty =
        DependencyProperty.Register(
            nameof(Flow), typeof(ViewModelBase), typeof(TaskWindow),
            new PropertyMetadata(null));

    public static readonly DependencyProperty WindowTitleProperty =
        DependencyProperty.Register(
            nameof(WindowTitle), typeof(string), typeof(TaskWindow),
            new PropertyMetadata("Lithic Backup"));

    /// <summary>The flow view model this window is showing.</summary>
    public ViewModelBase? Flow
    {
        get => (ViewModelBase?)GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    /// <summary>
    /// Title text. Named <c>WindowTitle</c> rather than reusing
    /// <see cref="Window.Title"/> because the XAML binds it with
    /// <c>RelativeSource Self</c>, and binding Title to itself would be circular.
    /// </summary>
    public string WindowTitle
    {
        get => (string)GetValue(WindowTitleProperty);
        set => SetValue(WindowTitleProperty, value);
    }

    public TaskWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Give a flow with unsaved edits (<see cref="IConfirmClose"/>) its say before
    /// the window goes - however the close was asked for.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || Flow is not IConfirmClose guard)
            return;

        // A forced shutdown (the upgrade installer's signal, Windows ending the
        // session) must never wait on a prompt; unsaved edits are dropped, the
        // same rule the backup-set editor follows.
        if (Application.Current is App { IsForcedShutdown: true })
            return;

        // During File > Exit the close can't be refused, so the flow is told to
        // offer only Save / Don't Save.
        bool exiting = Application.Current is App { IsExiting: true };
        if (!guard.ConfirmClose(canCancel: !exiting) && !exiting)
            e.Cancel = true;
    }
}
