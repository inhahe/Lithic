using System.Windows;

namespace LithicBackup.Views;

/// <summary>
/// Read-only, sortable review dialog used after a set's sources are edited to
/// preview either the backed-up files that are no longer covered (purge) or the
/// files newly covered (additions).  Both previews are all-or-nothing — there
/// are no per-item checkboxes — so <see cref="Window.DialogResult"/> is
/// <c>true</c> only when the user confirms the whole action.  The window's
/// title, header, and confirm-button text come from the bound
/// <see cref="ViewModels.ReviewTreeViewModel"/>.
/// </summary>
public partial class ReviewTreeDialog : Window
{
    public ReviewTreeDialog()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void NotNow_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
