using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LithicBackup.Converters;
using LithicBackup.ViewModels;

namespace LithicBackup.Views;

public partial class OrphanedDirectoriesView : UserControl
{
    public OrphanedDirectoriesView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Drag the divider on a category card's right edge.
    ///
    /// <para>The width is taken from the card's CURRENT rendered width rather
    /// than accumulated from the override, so the first drag of a card that is
    /// still on its default width starts from where the user sees it instead of
    /// jumping.</para>
    /// </summary>
    private void CategoryDivider_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb)
            return;
        if (thumb.DataContext is not OrphanedCategoryViewModel category)
            return;

        var card = FindCard(thumb);
        if (card is null)
            return;

        double current = double.IsNaN(category.WidthOverride)
            ? card.ActualWidth
            : category.WidthOverride;

        double proposed = current + e.HorizontalChange;
        if (proposed < CategoryCardWidthConverter.MinimumCardWidth)
            proposed = CategoryCardWidthConverter.MinimumCardWidth;

        category.WidthOverride = proposed;
    }

    /// <summary>Double-click the divider to hand the card back to the row's share.</summary>
    private void CategoryDivider_Reset(object sender, RoutedEventArgs e)
    {
        if (sender is Thumb { DataContext: OrphanedCategoryViewModel category })
            category.WidthOverride = double.NaN;
    }

    /// <summary>
    /// Walk up to the card Border whose Width the divider controls. Done by
    /// walking parents rather than by name because the card lives in a
    /// DataTemplate, where names are not unique across instances.
    /// </summary>
    private static FrameworkElement? FindCard(DependencyObject start)
    {
        DependencyObject? node = start;
        while (node is not null)
        {
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            if (node is Border border && border.DataContext is OrphanedCategoryViewModel)
                return border;
        }
        return null;
    }
}
