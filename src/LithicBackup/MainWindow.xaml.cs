using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using LithicBackup.Views;

namespace LithicBackup;

public partial class MainWindow : Window
{
    // Session-end messages the MSI's Restart Manager (WiX util:CloseApplication
    // with EndSessionMessage="yes") posts to this window's HWND during an upgrade.
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    public MainWindow()
    {
        InitializeComponent();
        ContentRendered += OnContentRendered;
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Hook the raw window procedure so we can catch the Restart Manager's
    /// WM_QUERYENDSESSION / WM_ENDSESSION messages. WiX's util:CloseApplication
    /// posts these directly to the visible main window during an MSI upgrade, but
    /// WPF only routes session-end handling through Application.SessionEnding for
    /// messages that hit its own hidden management window — so without this hook
    /// the message falls through to OnClosing, which minimizes to tray and leaves
    /// LithicBackup.exe locked, breaking the upgrade with "Setup was unable to
    /// automatically close all requested applications."
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Application.Current is not App app)
            return IntPtr.Zero;

        switch (msg)
        {
            case WM_QUERYENDSESSION:
                // Approve the close so a subsequent WM_ENDSESSION/close is allowed
                // to proceed instead of minimizing to tray. Let the default proc
                // reply TRUE (handled stays false).
                app.MarkRestartManagerExit();
                break;

            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero)
                    // The session end is really happening — tear the process down
                    // (OnExplicitShutdown means closing the window alone won't).
                    app.ShutdownForRestartManager();
                else
                    // The session end was vetoed elsewhere — keep running in tray.
                    app.AbortRestartManagerExit();
                break;
        }

        return IntPtr.Zero;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;

        // Set MaxHeight to the screen's working area so SizeToContent
        // can never push the window beyond the screen edge.
        ApplyScreenMaxHeight();

        // Re-clamp whenever the window grows (e.g. backup-set buttons
        // appear, or user navigates to a taller view).
        SizeChanged += OnSizeChanged;
    }

    private void ApplyScreenMaxHeight()
    {
        var source = PresentationSource.FromVisual(this);
        double dpiScale = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var workArea = screen.WorkingArea;

        MaxHeight = workArea.Height / dpiScale;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || WindowState != WindowState.Normal)
            return;

        // Shift window up if the bottom edge overflows the working area.
        var source = PresentationSource.FromVisual(this);
        double dpiScale = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var workArea = screen.WorkingArea;

        double workTop = workArea.Top / dpiScale;
        double workBottom = workArea.Bottom / dpiScale;

        if (Top + ActualHeight > workBottom)
            Top = Math.Max(workTop, workBottom - ActualHeight);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.ExitApplication();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.OpenSettings(this);
    }

    /// <summary>
    /// Intercept the window close (X button, Alt+F4) and minimize to the
    /// system tray instead. The app only truly exits via the tray icon's
    /// Exit menu item or File &gt; Exit, which sets <see cref="App.IsExiting"/>.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App app && !app.IsExiting)
        {
            e.Cancel = true;
            app.MinimizeToTray();
            return;
        }

        base.OnClosing(e);
    }
}
