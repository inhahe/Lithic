using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LithicBackup.Behaviors;

/// <summary>
/// Attached behavior that makes nested scrollable controls (e.g. a
/// <see cref="TreeView"/> inside an outer <see cref="ScrollViewer"/>) forward
/// mouse-wheel events to the nearest ancestor scrollable region once the
/// inner one has hit its top or bottom limit.
///
/// Usage in XAML:
/// <code>
///   xmlns:b="clr-namespace:LithicBackup.Behaviors"
///   ...
///   &lt;TreeView b:BubbleScrollWheel.IsEnabled="True" ... /&gt;
/// </code>
///
/// Without this, the mouse wheel "sticks" inside the inner control whenever
/// the cursor is over it, even after the inner control has been scrolled
/// fully to one end — making it impossible to scroll the outer container by
/// wheel while the cursor sits over the inner one.
/// </summary>
public static class BubbleScrollWheel
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(BubbleScrollWheel),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) =>
        (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) =>
        d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        if ((bool)e.NewValue)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;

        var innerScroll = FindDescendantScrollViewer(d);
        if (innerScroll is null) return;

        // Inner ScrollViewer has nothing to scroll — bubble immediately.
        if (innerScroll.ScrollableHeight <= 0)
        {
            BubbleToParent(sender, e, d);
            return;
        }

        bool atTop = innerScroll.VerticalOffset <= 0.001;
        bool atBottom = innerScroll.VerticalOffset >= innerScroll.ScrollableHeight - 0.001;
        bool scrollingUp = e.Delta > 0;
        bool scrollingDown = e.Delta < 0;

        if ((scrollingUp && atTop) || (scrollingDown && atBottom))
            BubbleToParent(sender, e, d);
    }

    private static void BubbleToParent(object originalSender, MouseWheelEventArgs e, DependencyObject d)
    {
        // Mark the original event handled so the inner control doesn't act on
        // it, then re-raise an equivalent event up the visual tree so an
        // outer ScrollViewer (if any) receives it.
        e.Handled = true;

        if (d is not UIElement element) return;

        var parent = VisualTreeHelper.GetParent(element) as UIElement;
        if (parent is null) return;

        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = originalSender,
        };
        parent.RaiseEvent(args);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        // BFS-ish via VisualTreeHelper.
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindDescendantScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }
}
