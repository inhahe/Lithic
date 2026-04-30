namespace LithicBackup.Core.Exceptions;

public class BurnException : Exception
{
    public BurnException() { }
    public BurnException(string message) : base(message) { }
    public BurnException(string message, Exception innerException) : base(message, innerException) { }
}

public class MediaFullException : BurnException
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }

    public MediaFullException(long requiredBytes, long availableBytes)
        : base($"Not enough space on disc: need {requiredBytes:N0} bytes, only {availableBytes:N0} available.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }
}

public class IncompatiblePathException : BurnException
{
    public string FilePath { get; }
    public string Reason { get; }

    public IncompatiblePathException(string filePath, string reason)
        : base($"Path incompatible with disc filesystem: {filePath} ({reason})")
    {
        FilePath = filePath;
        Reason = reason;
    }
}
