using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound string value is
/// non-null and non-empty, <see cref="Visibility.Collapsed"/> otherwise.
/// </summary>
public class NonEmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
