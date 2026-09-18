using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LithicBackup.Behaviors;

/// <summary>
/// Makes Page Up, Page Down, Home and End scroll whichever list the mouse is
/// over, or — failing that — the only scrollable list on screen.
///
/// <para>
/// WPF gives these keys to the <em>keyboard-focused</em> control and nowhere
/// else. That is a poor fit for this app: most of its lists are things you read
/// rather than edit (cleanup results, coverage, largest files, the review
/// tree), and reaching them by mouse wheel alone is slow on a set with a
/// million rows. Before this, hovering a list and pressing Page Down did
/// nothing at all unless you had first clicked into it — and clicking into a
/// results tree changes the selection, which is not what someone who only
/// wants to scroll is asking for.
/// </para>
///
/// <para>
/// Installed once, app-wide, via <see cref="Install"/> — a class handler on
/// <see cref="Window"/> rather than an attached property per control. There are
/// nineteen views; opting each list in by hand would guarantee that the next
/// one added is forgotten.
/// </para>
///
/// <para><b>It only ever adds behaviour, never replaces it.</b> Two guards
/// enforce that, and both matter:</para>
/// <list type="bullet">
///   <item>If focus is in a text editor, it does nothing. All four keys have
///   caret meanings there, and stealing them mid-typing would be worse than
///   the problem being solved.</item>
///   <item>If focus is already inside an <see cref="ItemsControl"/>, it does
///   nothing, and WPF's own handling runs. That handling moves the
///   <em>selection</em>, which is correct for a focused list and is
///   deliberately not what the hover path does.</item>
/// </list>
/// </summary>
public static class ListPagingKeys
{
    /// <summary>
    /// Registers the app-wide handler. Call once during startup, before any
    /// window is shown.
    /// </summary>
    public static void Install()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown));
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;

        if (e.Key is not (Key.PageUp or Key.PageDown or Key.Home or Key.End))
            return;

        // Modified combinations mean other things (Ctrl+Home, Shift+PageDown
        // extend a selection). Leave every one of them to whoever owns focus.
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        var focused = Keyboard.FocusedElement as DependencyObject;

        // Guard 1: never take these keys away from a text editor.
        if (IsTextEditor(focused))
            return;

        // Guard 2: a focused list already handles them, and moves selection
        // while doing so. That is the right behaviour for a focused list.
        if (FindAncestor<ItemsControl>(focused) is not null)
            return;

        var target = FindScrollableUnderMouse() ?? FindSoleScrollableIn(sender as DependencyObject);
        if (target is null)
            return;

        switch (e.Key)
        {
            case Key.PageUp:   target.PageUp();      break;
            case Key.PageDown: target.PageDown();    break;
            case Key.Home:     target.ScrollToTop(); break;
            case Key.End:      target.ScrollToEnd(); break;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The innermost scrollable region under the cursor that actually has
    /// somewhere to scroll. Walking outwards matters: a list nested in a page
    /// ScrollViewer must win over the page, and a list already scrolled to its
    /// limit should hand off to the container around it - the same rule
    /// <see cref="BubbleScrollWheel"/> applies to the wheel.
    /// </summary>
    private static ScrollViewer? FindScrollableUnderMouse()
    {
        if (Mouse.DirectlyOver is not DependencyObject hit)
            return null;

        for (var node = hit; node is not null; node = GetParent(node))
        {
            if (node is ScrollViewer sv && sv.ScrollableHeight > 0.001)
                return sv;

            // Over a list's blank area, DirectlyOver can be the control itself
            // rather than anything inside its template.
            if (node is ItemsControl ic
                && FindDescendantScrollViewer(ic) is { } inner
                && inner.ScrollableHeight > 0.001)
            {
                return inner;
            }
        }
        return null;
    }

    /// <summary>
    /// Fallback for when the cursor is not over any list - the case of pressing
    /// Page Down with the mouse parked over a toolbar or off to one side. Only
    /// acts when the answer is unambiguous: exactly one scrollable list in the
    /// window. With two or more, guessing would be worse than doing nothing.
    /// </summary>
    private static ScrollViewer? FindSoleScrollableIn(DependencyObject? root)
    {
        if (root is null)
            return null;

        ScrollViewer? only = null;
        int found = 0;
        CollectScrollables(root, ref only, ref found);
        return found == 1 ? only : null;
    }

    private static void CollectScrollables(DependencyObject node, ref ScrollViewer? only, ref int found)
    {
        if (found > 1)
            return;

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);

            if (child is ScrollViewer sv
                && sv.ScrollableHeight > 0.001
                && sv.IsVisible)
            {
                only = sv;
                if (++found > 1)
                    return;
                // Do not descend into a counted ScrollViewer: a nested one
                // would make an otherwise clear case look ambiguous.
                continue;
            }

            CollectScrollables(child, ref only, ref found);
            if (found > 1)
                return;
        }
    }

    private static bool IsTextEditor(DependencyObject? d) => d switch
    {
        TextBoxBase => true,
        PasswordBox => true,
        ComboBox { IsEditable: true } => true,
        _ => false,
    };

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (; node is not null; node = GetParent(node))
        {
            if (node is T match)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Visual parent, falling back to the logical one. The focused element can
    /// be a ContentElement (a Run inside a TextBlock, say), which has no visual
    /// parent at all and would otherwise end the walk immediately.
    /// </summary>
    private static DependencyObject? GetParent(DependencyObject node)
    {
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var visual = VisualTreeHelper.GetParent(node);
            if (visual is not null)
                return visual;
        }
        return LogicalTreeHelper.GetParent(node);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindDescendantScrollViewer(child) is { } found)
                return found;
        }
        return null;
    }
}
