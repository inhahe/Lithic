using System.Windows;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

public partial class RemapSourceDriveDialog : Window
{
    public RemapSourceDriveDialog()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is RemapSourceDriveViewModel vm)
        {
            if (vm.SourceDriveLetter is not char oldDrive || vm.TargetDriveLetter is not char newDrive)
            {
                MessageBox.Show(
                    "Please choose both a source drive and a target drive.",
                    "Remap Source Drive",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (oldDrive == newDrive)
            {
                MessageBox.Show(
                    "The target drive must be different from the source drive.",
                    "Remap Source Drive",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
