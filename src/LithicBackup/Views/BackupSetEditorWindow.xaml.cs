using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LithicBackup.Views;

/// <summary>
/// Dialog window for editing a backup set (source selection, job config, etc.).
/// Uses implicit DataTemplates to render the current editor view model.
///
/// Lifecycle:
///   1. Caller does async setup work while showing a wait cursor.
///   2. Caller sets content via <see cref="SetEditorContent"/> (before Show).
///   3. Caller calls Show() — the window starts at Opacity 0.
///   4. The Loaded event fires after the first layout pass.  The handler
///      applies screen clamping, centers the window, sets Opacity 1, and
///      clears the wait cursor.  The user sees the dialog appear fully
///      formed at its final size and position — no intermediate stages.
///   5. Subsequent <see cref="SetEditorContent"/> calls (e.g. navigating
///      to Largest Files and back) just swap the ContentControl content.
/// </summary>
public partial class BackupSetEditorWindow : Window
{
    public BackupSetEditorWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Set the view model displayed in the editor content area.
    /// The implicit DataTemplates in ContentArea.Resources handle rendering.
    /// </summary>
    public void SetEditorContent(object? content)
    {
        ContentArea.Content = content;
    }

    /// <summary>
    /// Fires once after Show() when WPF has completed the first layout pass.
    /// At this point ActualWidth/Height are valid, so we can clamp, center,
    /// and reveal in a single frame.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        ApplyScreenMaxHeight();

        // Force a second layout pass so MaxHeight takes effect before
        // we measure the final position.
        UpdateLayout();

        CenterOnOwner();
        Opacity = 1;
        Mouse.OverrideCursor = null;

        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// Clamp MaxHeight to the screen working area so the window never
    /// pushes past the screen edge.
    /// </summary>
    private void ApplyScreenMaxHeight()
    {
        var source = PresentationSource.FromVisual(this);
        double dpiScale = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var workArea = screen.WorkingArea;

        MaxHeight = workArea.Height / dpiScale;
    }

    /// <summary>
    /// Re-center the window on its owner.  Clamps to the screen working area.
    /// </summary>
    private void CenterOnOwner()
    {
        var source = PresentationSource.FromVisual(this);
        double dpiScale = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var workArea = screen.WorkingArea;

        double workTop = workArea.Top / dpiScale;
        double workBottom = workArea.Bottom / dpiScale;
        double workLeft = workArea.Left / dpiScale;
        double workRight = workArea.Right / dpiScale;

        if (Owner is not null)
        {
            Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
            Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
        }

        // Clamp to the working area.
        if (Top + ActualHeight > workBottom)
            Top = Math.Max(workTop, workBottom - ActualHeight);
        if (Top < workTop)
            Top = workTop;
        if (Left + ActualWidth > workRight)
            Left = Math.Max(workLeft, workRight - ActualWidth);
        if (Left < workLeft)
            Left = workLeft;
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
}
