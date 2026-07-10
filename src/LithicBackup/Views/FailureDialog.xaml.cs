using System;
using System.Windows;
using LithicBackup.Core.Models;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

/// <summary>
/// Dialog window shown when a file fails during backup.
/// Reports the user's chosen <see cref="BurnFailureAction"/> via
/// <see cref="ActionChosen"/>. Shown <em>modelessly</em> (via <c>Show()</c>) so
/// that a failure in one concurrent backup never blocks interaction with the
/// other running sets — which rules out setting <c>DialogResult</c> (that is
/// only valid for modal <c>ShowDialog()</c> windows).
/// </summary>
public partial class FailureDialog : Window
{
    public FailureDialog()
    {
        InitializeComponent();
    }

    public FailureDialogViewModel ViewModel => (FailureDialogViewModel)DataContext;

    /// <summary>
    /// Raised exactly once, when the user picks an action (just before the
    /// window closes). The caller awaits this to resume the paused backup.
    /// </summary>
    public event Action<BurnFailureAction>? ActionChosen;

    private void CloseWith(BurnFailureAction action)
    {
        ViewModel.ChosenAction = action;
        ActionChosen?.Invoke(action);
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Skip);
    private void OnRetry(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Retry);
    private void OnZip(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Zip);
    private void OnZipAllForDisc(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.ZipAllForDisc);
    private void OnSkipAllForDisc(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.SkipAllForDisc);
    private void OnSkipAllPermanently(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.SkipAllPermanently);
    private void OnSkipAllOfThisType(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.SkipAllOfThisType);
    private void OnAbort(object sender, RoutedEventArgs e) => CloseWith(BurnFailureAction.Abort);
}
