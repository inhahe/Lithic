using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Multi-value converter: compares two integer values and returns
/// Visible when they are equal, Collapsed otherwise.
/// Handles nullable int (int?) for the second value — null never matches.
/// Pass ConverterParameter="Invert" to invert the result.
/// </summary>
public class IntEqualsVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool equals = false;
        if (values.Length == 2
            && values[0] is int a
            && values[1] is int b)
        {
            equals = a == b;
        }

        bool invert = parameter is string s
            && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        if (invert) equals = !equals;

        return equals ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
