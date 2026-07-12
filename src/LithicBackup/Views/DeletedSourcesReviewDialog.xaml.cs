using System.Windows;

namespace LithicBackup.Views;

/// <summary>
/// Read-only review dialog listing the backed-up files that are no longer
/// covered by a set's sources after an edit.  All-or-nothing: the user either
/// removes everything listed or dismisses.  <see cref="Window.DialogResult"/>
/// is <c>true</c> when the user chose to remove.
/// </summary>
public partial class DeletedSourcesReviewDialog : Window
{
    public DeletedSourcesReviewDialog()
    {
        InitializeComponent();
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void NotNow_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
