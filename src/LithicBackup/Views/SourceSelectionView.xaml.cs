using System.Windows;
using System.Windows.Controls;

namespace LithicBackup.Views;

public partial class SourceSelectionView : UserControl
{
    public SourceSelectionView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Suppress the automatic scroll-into-view that WPF fires whenever a
    /// TreeViewItem receives selection (e.g. when the user clicks a checkbox
    /// inside the row).  This tree uses its own checkbox-based selection, so
    /// the built-in "bring focused item into view" behaviour just causes an
    /// unwanted one-item scroll on every check/uncheck.
    /// </summary>
    internal void TreeViewItem_RequestBringIntoView(
        object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }
}
