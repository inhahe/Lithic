using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

public partial class SourceSelectionView : UserControl
{
    private SourceSelectionViewModel? _subscribedVm;
    private double _savedScrollOffset;

    public SourceSelectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Subscribe to the viewmodel's selection-settle events so we can preserve
    /// the tree's scroll position across a deferred settle pass (see
    /// <see cref="OnSelectionSettleStarting"/> / <see cref="OnSelectionSettleCompleted"/>).
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.SelectionSettleStarting -= OnSelectionSettleStarting;
            _subscribedVm.SelectionSettleCompleted -= OnSelectionSettleCompleted;
            _subscribedVm = null;
        }

        if (e.NewValue is SourceSelectionViewModel vm)
        {
            vm.SelectionSettleStarting += OnSelectionSettleStarting;
            vm.SelectionSettleCompleted += OnSelectionSettleCompleted;
            _subscribedVm = vm;
        }
    }

    /// <summary>
    /// Snapshot the tree's scroll offset before a deferred selection-settle
    /// pass runs its propagation — the resulting layout re-measure can
    /// otherwise nudge the scroll by a row.
    /// </summary>
    private void OnSelectionSettleStarting()
    {
        _savedScrollOffset = GetTreeScroll()?.VerticalOffset ?? 0;
    }

    /// <summary>
    /// Restore the pre-settle scroll offset after the layout pass triggered by
    /// the settle has run.  Deferred to Loaded priority so it lands after the
    /// re-measure that would otherwise move the scroll.
    /// </summary>
    private void OnSelectionSettleCompleted()
    {
        var scroll = GetTreeScroll();
        if (scroll is null)
            return;

        var target = _savedScrollOffset;
        scroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (System.Math.Abs(scroll.VerticalOffset - target) > 0.5)
                scroll.ScrollToVerticalOffset(target);
        }));
    }

    /// <summary>
    /// Resolve the tree's inner ScrollViewer (named "TreeScroll" inside the
    /// TreeView's ControlTemplate).  Template-scoped names aren't exposed as
    /// code-behind fields, so we look it up through the applied template.
    /// </summary>
    private ScrollViewer? GetTreeScroll()
        => SourceTree?.Template?.FindName("TreeScroll", SourceTree) as ScrollViewer;

    /// <summary>
    /// Suppress the automatic scroll-into-view that WPF fires whenever a
    /// TreeViewItem receives selection (e.g. when the user clicks a checkbox
    /// inside the row).  This tree uses its own checkbox-based selection, so
    /// the built-in "bring focused item into view" behaviour just causes an
    /// unwanted one-item scroll on every check/uncheck.
    /// </summary>
    internal void TreeViewItem_RequestBringIntoView(
        object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// Absorb the first-render binding write-backs of a lazily-realized settings
    /// tab so merely viewing a tab isn't mistaken for a user edit.
    ///
    /// The editor's top-level TabControl realizes each tab's content only when it
    /// is first selected.  The settings tabs (General/Options/Retention/Schedule)
    /// bind several controls Mode=TwoWay (radios, combos, the tier-set pattern
    /// boxes); on first render those push their values back to the viewmodel,
    /// raising PropertyChanged.  The VM's dirty tracking treats any such change as
    /// an edit — so switching to a settings tab without changing anything would
    /// pop a bogus "unsaved changes" prompt on Cancel/close.
    ///
    /// Mirror the initial-load absorption (see MainViewModel's post-restore
    /// ContextIdle MarkClean/ResumeDirtyTracking): suspend dirty tracking for the
    /// duration of the tab switch and re-arm one dispatcher cycle later, after the
    /// new content's Render-priority write-backs have flushed.  We deliberately do
    /// NOT MarkClean here, so any genuine unsaved edits made earlier on another tab
    /// survive the switch.
    /// </summary>
    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only the TabControl's own selection change — ignore SelectionChanged
        // bubbling up from inner selectors (e.g. the ZipMode/Filesystem combos).
        if (e.OriginalSource is not TabControl)
            return;
        // Skip the initial selection raised during construction; the initial-load
        // window in MainViewModel already owns dirty tracking at that point.
        if (!IsLoaded)
            return;
        if (DataContext is not SourceSelectionViewModel vm)
            return;

        vm.SuspendDirtyTracking();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(vm.ResumeDirtyTracking));
    }
}
