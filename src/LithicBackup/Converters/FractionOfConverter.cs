using System.Globalization;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Multiplies a length by a fraction supplied as the converter parameter, for
/// binding one element's Max/Min size to a proportion of a container's
/// <c>ActualHeight</c> / <c>ActualWidth</c>.
///
/// <para>Bind only to a container whose size does <i>not</i> depend on the
/// element being constrained, or the constraint feeds back into the measure
/// that produced it and the layout can oscillate.  The safe target is a panel
/// that fills its parent (its size comes from above, not from its children);
/// the unsafe one is a sibling in the same Grid, whose size is exactly what
/// the constraint changes.</para>
///
/// <para>Returns <see cref="double.PositiveInfinity"/> — i.e. "no constraint" —
/// for a non-positive or not-yet-measured input, so the first layout pass
/// (where <c>ActualHeight</c> is still 0) doesn't collapse the target to
/// nothing.  The binding re-evaluates as soon as the container has a real
/// size, because <c>ActualHeight</c> is a dependency property.</para>
/// </summary>
public class FractionOfConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double available ||
            double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
        {
            return double.PositiveInfinity;
        }

        var fraction = ParseFraction(parameter);
        if (fraction <= 0)
            return double.PositiveInfinity;

        return available * fraction;
    }

    private static double ParseFraction(object parameter)
    {
        if (parameter is double d)
            return d;
        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
