namespace LithicBackup.ViewModels;

/// <summary>Byte counts as people read them: "512 bytes", "3.4 GB".</summary>
public static class ByteSize
{
    public static string Readable(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:N1} {units[unit]}";
    }
}
