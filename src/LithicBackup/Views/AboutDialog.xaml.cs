using System.Reflection;
using System.Windows;

namespace LithicBackup.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public string Version
    {
        get
        {
            var ver = typeof(App).Assembly.GetName().Version;
            return ver is not null ? $"Version {ver.ToString(3)}" : "Version 1.0.0";
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e) => Close();
}
