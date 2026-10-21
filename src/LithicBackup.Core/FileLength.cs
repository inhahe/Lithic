namespace LithicBackup.Core;

/// <summary>Measuring a file that may come and go while it is measured.</summary>
public static class FileLength
{
    /// <summary>
    /// The length in bytes of the file at <paramref name="path"/> now, or 0 when
    /// it cannot be measured: it does not exist (yet, or any more), or it cannot
    /// be read. For estimates and progress, where 0 is a safe answer.
    /// </summary>
    public static long OrZero(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
