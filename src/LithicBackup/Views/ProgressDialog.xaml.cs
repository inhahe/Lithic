using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

/// <summary>
/// A small modal progress window that runs a background operation while showing
/// what it's doing (a fixed message) and a live detail line (counts / percentage),
/// and — when the operation is cancellable — lets the user abort it by clicking
/// Cancel or closing the window.
///
/// Use the static <see cref="RunAsync{T}"/> helper rather than constructing this
/// directly: it wires the work up to an <c>IProgress&lt;string&gt;</c> and a
/// <see cref="CancellationToken"/>, shows the dialog, auto-closes it when the
/// work finishes, and reports whether the operation completed or was cancelled.
///
/// This replaced the old <c>Mouse.OverrideCursor = Cursors.Wait</c> pattern for
/// the post-edit destination-reconcile scan/purge: the busy cursor gave no
/// feedback, couldn't be cancelled, and (being a global override cleared only in
/// a <c>finally</c>) could visually "stick" if a long reconcile ran on with no
/// status. A dialog whose lifetime is deterministically tied to the work — closed
/// in the work's completion continuation — can't leak that way.
/// </summary>
public partial class ProgressDialog : Window
{
    private readonly ProgressDialogViewModel _vm;
    private readonly CancellationTokenSource? _cts;

    // Gate on the window's own close: the user's X / Alt+F4 is reinterpreted as
    // "cancel" (it can't just tear the window down while work is still running).
    // Only the work-completion continuation sets this true to actually close.
    private bool _allowClose;

    private ProgressDialog(ProgressDialogViewModel vm, CancellationTokenSource? cts)
    {
        InitializeComponent();
        _vm = vm;
        _cts = cts;
        DataContext = vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => RequestCancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing via the title-bar X (or Alt+F4) before the work is done: treat
        // it as a cancel request and keep the window up until the work unwinds
        // and the continuation closes it. When _allowClose is set (work done, or
        // there was nothing to cancel) let the close proceed.
        if (!_allowClose)
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Signal the running work to stop (if cancellable) and reflect it in the UI.
    /// A no-op for a non-cancellable operation beyond disabling the button.
    /// </summary>
    private void RequestCancel()
    {
        if (!_vm.CanCancel)
            return;

        _vm.CanCancel = false;
        if (_cts is not null)
        {
            _vm.Detail = "Cancelling\u2026";
            _cts.Cancel();
        }
    }

    private void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Run <paramref name="work"/> on a background thread while showing this
    /// dialog, marshalling its <c>IProgress&lt;string&gt;</c> reports to the
    /// dialog's detail line and closing the dialog when the work finishes.
    /// </summary>
    /// <param name="owner">Dialog owner (defaults to the app main window).</param>
    /// <param name="title">Window title.</param>
    /// <param name="message">Fixed description of what's happening.</param>
    /// <param name="cancellable">
    /// When true, a Cancel button (and window-close) aborts the work by cancelling
    /// the token; <paramref name="work"/> is expected to observe it and throw
    /// <see cref="OperationCanceledException"/> (e.g. via
    /// <c>token.ThrowIfCancellationRequested()</c>).
    /// </param>
    /// <param name="work">
    /// The operation to run. Receives a progress reporter (for the detail line)
    /// and a cancellation token.
    /// </param>
    /// <returns>
    /// <c>(Completed: true, Result)</c> when the work ran to completion, or
    /// <c>(Completed: false, default)</c> when it was cancelled.
    /// </returns>
    public static async Task<(bool Completed, T Result)> RunAsync<T>(
        Window? owner,
        string title,
        string message,
        bool cancellable,
        Func<IProgress<string>, CancellationToken, T> work)
    {
        var cts = cancellable ? new CancellationTokenSource() : null;
        var vm = new ProgressDialogViewModel(title, message, cancellable);
        var dlg = new ProgressDialog(vm, cts)
        {
            Owner = owner ?? Application.Current.MainWindow,
        };

        // Progress<T> captures the current (UI) SynchronizationContext, so the
        // detail updates marshal back to the UI thread automatically.
        var progress = new Progress<string>(s => vm.Detail = s);
        var token = cts?.Token ?? CancellationToken.None;

        T result = default!;
        Exception? error = null;
        bool cancelled = false;

        var workTask = Task.Run(() =>
        {
            try
            {
                result = work(progress, token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        // Close the dialog once the work is done — whether it completed, threw,
        // or was cancelled. BeginInvoke hops back to the UI thread that owns the
        // window. ForceClose flips the close gate so OnClosing lets it through.
        _ = workTask.ContinueWith(
            _ => dlg.Dispatcher.BeginInvoke(new Action(dlg.ForceClose)),
            TaskScheduler.Default);

        dlg.ShowDialog();

        // Work is essentially already finished here (its continuation is what
        // closed the dialog), but await to observe its outcome / rethrow.
        await workTask;

        if (error is not null)
            throw error;
        if (cancelled)
            return (false, default!);
        return (true, result);
    }
}
