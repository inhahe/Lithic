using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Standard bool-to-Visibility converter with optional inversion via parameter.
/// Pass "Invert" as ConverterParameter to invert the mapping.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is true;
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);

        if (invert)
            boolValue = !boolValue;

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
