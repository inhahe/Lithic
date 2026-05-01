using System.Windows;
using LithicBackup.Core.Models;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

/// <summary>
/// Dialog window shown when a file fails during backup.
/// Returns the user's chosen <see cref="BurnFailureAction"/>.
/// </summary>
public partial class FailureDialog : Window
{
    public FailureDialog()
    {
        InitializeComponent();
    }

    public FailureDialogViewModel ViewModel => (FailureDialogViewModel)DataContext;

    private void CloseWith(BurnFailureAction action)
    {
        ViewModel.ChosenAction = action;
        DialogResult = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Skip);
    private void OnRetry(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Retry);
    private void OnZip(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Zip);
    private void OnZipAllForDisc(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.ZipAllForDisc);
    private void OnSkipAllForDisc(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.SkipAllForDisc);
    private void OnSkipAllPermanently(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.SkipAllPermanently);
    private void OnAbort(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Abort);
}
