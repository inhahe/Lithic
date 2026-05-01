using System.Windows;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

public partial class ExclusionEditorDialog : Window
{
    public ExclusionEditorDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is ExclusionEditorViewModel vm && !vm.IsIncludeOnlyMode)
                ExcludeBox.Focus();
        };
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
