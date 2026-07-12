using System.Windows;
using System.Windows.Controls;

namespace LithicBackup.Views;

public partial class BackupReviewView : UserControl
{
    public BackupReviewView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Suppress WPF's automatic scroll-into-view when a TreeViewItem receives
    /// selection (e.g. when the user clicks a checkbox inside the row). This
    /// tree uses checkbox-based selection, so the built-in "bring focused item
    /// into view" behaviour just causes an unwanted scroll on every toggle.
    /// </summary>
    internal void TreeViewItem_RequestBringIntoView(
        object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }
}
