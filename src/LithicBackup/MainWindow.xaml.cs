using System.ComponentModel;
using System.Windows;
using LithicBackup.Views;

namespace LithicBackup;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentRendered += OnContentRendered;
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
