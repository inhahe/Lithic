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
}
