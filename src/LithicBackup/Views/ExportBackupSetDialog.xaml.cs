using System.Windows;

namespace LithicBackup.Views;

/// <summary>Asks what to include before a backup set is exported.</summary>
public partial class ExportBackupSetDialog : Window
{
    public ExportBackupSetDialog()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
