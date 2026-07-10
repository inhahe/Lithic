using System.Windows;
using System.Windows.Controls;

namespace LithicBackup.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Suppress WPF's automatic scroll-into-view when a TreeViewItem gains
    /// selection (e.g. clicking a checkbox), which would otherwise jolt the
    /// list by one row on every check/uncheck. This tree uses checkbox-based
    /// selection, not item selection.
    /// </summary>
    internal void TreeViewItem_RequestBringIntoView(
        object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }
}
