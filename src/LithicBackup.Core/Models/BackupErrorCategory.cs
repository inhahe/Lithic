namespace LithicBackup.Core.Models;

/// <summary>
/// Coarse category of a per-file backup failure, used to group failures so the
/// user can choose to skip all failures of one kind (e.g. every "permission
/// denied") without being prompted again for that category. The mapping is
/// intentionally coarse — distinct exceptions that mean the same thing to the
/// user collapse to one category.
/// </summary>
public enum BackupErrorCategory
{
    /// <summary>Any failure that doesn't fit a more specific category.</summary>
    Other,

    /// <summary>File is locked / shared by another process.</summary>
    Locked,

    /// <summary>Access/permission denied.</summary>
    PermissionDenied,

    /// <summary>Path or filename is too long for the destination.</summary>
    PathTooLong,

    /// <summary>Source file or directory no longer exists.</summary>
    NotFound,

    /// <summary>Destination is out of space.</summary>
    DiskFull,
}

/// <summary>
/// Classifies exceptions raised while copying a single file into a coarse
/// <see cref="BackupErrorCategory"/>, and provides a human-readable label for
/// each category (used in the failure prompt's "skip all of this type" option).
/// </summary>
public static class BackupErrorClassifier
{
    public static BackupErrorCategory Classify(Exception ex)
    {
        if (ex is IOException)
        {
            int win32 = ex.HResult & 0xFFFF;
            switch (win32)
            {
                case 0x0020: // ERROR_SHARING_VIOLATION
                case 0x0021: // ERROR_LOCK_VIOLATION
                    return BackupErrorCategory.Locked;
                case 0x0070: // ERROR_DISK_FULL
                    return BackupErrorCategory.DiskFull;
                case 0x00CE: // ERROR_FILENAME_EXCED_RANGE
                    return BackupErrorCategory.PathTooLong;
            }
        }

        return ex switch
        {
            UnauthorizedAccessException => BackupErrorCategory.PermissionDenied,
            PathTooLongException => BackupErrorCategory.PathTooLong,
            FileNotFoundException => BackupErrorCategory.NotFound,
            DirectoryNotFoundException => BackupErrorCategory.NotFound,
            _ => BackupErrorCategory.Other,
        };
    }

    /// <summary>A short human-readable label for a category.</summary>
    public static string Describe(BackupErrorCategory category) => category switch
    {
        BackupErrorCategory.Locked => "file locked by another process",
        BackupErrorCategory.PermissionDenied => "permission denied",
        BackupErrorCategory.PathTooLong => "path too long",
        BackupErrorCategory.NotFound => "file no longer exists",
        BackupErrorCategory.DiskFull => "disk full",
        _ => "this kind of error",
    };
}
