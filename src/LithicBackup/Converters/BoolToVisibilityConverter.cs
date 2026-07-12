using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Standard bool-to-Visibility converter with optional flags via parameter.
/// The parameter is a free-form string; recognised tokens (case-insensitive,
/// any separator) are:
///   "Invert"  – invert the mapping (true → hidden/collapsed, false → visible)
///   "Hidden"  – use <see cref="Visibility.Hidden"/> instead of Collapsed for the
///               false case, so the element still reserves its layout space (avoids
///               surrounding controls shifting when it toggles).
/// e.g. ConverterParameter="Invert,Hidden".
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is true;
        string param = parameter as string ?? "";
        bool invert = param.Contains("Invert", StringComparison.OrdinalIgnoreCase);
        bool useHidden = param.Contains("Hidden", StringComparison.OrdinalIgnoreCase);

        if (invert)
            boolValue = !boolValue;

        return boolValue
            ? Visibility.Visible
            : (useHidden ? Visibility.Hidden : Visibility.Collapsed);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
