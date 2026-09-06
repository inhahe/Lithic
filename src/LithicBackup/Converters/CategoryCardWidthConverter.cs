using System.Globalization;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Width for one Cleanup category card: the width it was dragged to, or an equal
/// share of the row when it has not been dragged.
///
/// <para>Inputs, in order: the container's available width, the "categories per
/// row" setting, and the card's own <c>WidthOverride</c> (NaN until dragged).</para>
///
/// <para>The cards live in a wrap layout precisely so a dragged card can push its
/// neighbour along — which is what makes the boundary between two cards feel like
/// a divider you can move. A uniform grid cannot do that: it gives every cell the
/// same width and a child can only be smaller than its cell, never wider.</para>
/// </summary>
public class CategoryCardWidthConverter : IMultiValueConverter
{
    /// <summary>Below this a card cannot show a path at all, so dragging stops here.</summary>
    public const double MinimumCardWidth = 220;

    /// <summary>Card margin (right + bottom) declared in the item template.</summary>
    private const double CardMargin = 8;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double available = values.Length > 0 && values[0] is double a ? a : double.NaN;
        int columns = values.Length > 1 && values[1] is int c ? c : 3;
        double overrideWidth = values.Length > 2 && values[2] is double o ? o : double.NaN;

        if (!double.IsNaN(overrideWidth))
            return Math.Max(MinimumCardWidth, overrideWidth);

        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
            return double.NaN;   // not measured yet — let it size naturally

        if (columns < 1) columns = 1;

        // Subtract the margin per card, and a pixel of slack: a card that is
        // exactly one Nth of the width can still wrap to the next row on a
        // rounding error, which reads as a layout bug.
        double share = (available / columns) - CardMargin - 1;
        return Math.Max(MinimumCardWidth, share);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
