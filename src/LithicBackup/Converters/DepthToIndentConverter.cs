using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Converts an integer depth to a left-margin Thickness for tree indentation.
/// Each level indents by 19 device-independent pixels (matching standard WPF tree indent).
/// </summary>
public class DepthToIndentConverter : IValueConverter
{
    private const double IndentPerLevel = 19;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int depth = value is int d ? d : 0;
        return new Thickness(depth * IndentPerLevel, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
