using System.Windows;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

public partial class DuplicateBackupSetDialog : Window
{
    public DuplicateBackupSetDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DuplicateBackupSetViewModel vm
            && string.IsNullOrWhiteSpace(vm.Name))
        {
            MessageBox.Show(
                "Please enter a name for the new backup set.",
                "Duplicate Backup Set",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
