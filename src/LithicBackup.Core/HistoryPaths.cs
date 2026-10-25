namespace LithicBackup.Core;

/// <summary>
/// Where a directory backup keeps earlier versions. The current copy of
/// <c>D:\docs\a.txt</c> is stored at <c>D\docs\a.txt</c> under the destination,
/// and its earlier versions at <c>D_prev\docs\a.txt.v1</c>, <c>.v2</c>, ...: the
/// first segment of a stored file's destination-relative path (the drive
/// folder) ends with <c>_prev</c> exactly when the file is an earlier version.
/// </summary>
public static class HistoryPaths
{
    /// <summary>
    /// Whether <paramref name="discPath"/>, a record's destination-relative path,
    /// is an earlier version: its first segment ends with <c>_prev</c>. Either
    /// separator is accepted, case-insensitively. A deeper segment named so (a
    /// source folder called <c>old_prev</c>) does not count.
    /// </summary>
    public static bool IsPreviousVersion(string discPath)
    {
        if (string.IsNullOrEmpty(discPath))
            return false;
        int sep = discPath.IndexOfAny(['\\', '/']);
        return sep > 0
            && discPath.AsSpan(0, sep).EndsWith("_prev", StringComparison.OrdinalIgnoreCase);
    }
}
