using System.Globalization;
using System.Windows.Data;

namespace LithicBackup.Converters;

/// <summary>
/// Converts a byte count to a human-readable file size string.
/// </summary>
public class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
            return "0";

        return $"{bytes:N0}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
