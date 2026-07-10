using System.Runtime.InteropServices;
using System.Text;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Win32 implementation of <see cref="IVolumeResolver"/> built on the
/// <c>GetVolumePathName</c> / <c>GetVolumeNameForVolumeMountPoint</c> /
/// <c>GetVolumePathNamesForVolumeName</c> family of kernel32 APIs.
///
/// <para>
/// The volume GUID path returned by <see cref="GetVolumeId"/> is stable for the
/// life of the volume — it does not change when Windows reassigns drive letters
/// — which is exactly the durable identity backup destinations are stored
/// against.  Reformatting the volume mints a new GUID (a genuinely different
/// destination), which is the correct behaviour: the old backup store is gone.
/// </para>
/// </summary>
public sealed class Win32VolumeResolver : IVolumeResolver
{
    public string? GetVolumeId(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // 1. Find the mount point (volume root) that the path lives under.
        //    Handles both plain drive letters and mounted-folder paths.
        var mountPoint = new StringBuilder(260);
        if (!GetVolumePathNameW(path, mountPoint, (uint)mountPoint.Capacity))
            return null;

        string root = mountPoint.ToString();
        if (!root.EndsWith('\\'))
            root += "\\";

        // 2. Map the mount point to its stable volume GUID path.
        var volumeName = new StringBuilder(50); // "\\?\Volume{GUID}\" is 49 chars + NUL
        if (!GetVolumeNameForVolumeMountPointW(root, volumeName, (uint)volumeName.Capacity))
            return null;

        return volumeName.ToString();
    }

    public string? GetCurrentMountPoint(string volumeId)
    {
        if (string.IsNullOrWhiteSpace(volumeId))
            return null;

        var mountPoints = GetMountPoints(volumeId);
        if (mountPoints.Count == 0)
            return null;

        // Prefer a drive-letter root ("X:\") over a mounted-folder path so the
        // UI can show a friendly letter whenever the volume has one.
        foreach (var mp in mountPoints)
            if (mp.Length == 3 && char.IsLetter(mp[0]) && mp[1] == ':' && mp[2] == '\\')
                return mp;

        return mountPoints[0];
    }

    public string? GetVolumeLabel(string volumeId)
    {
        if (string.IsNullOrWhiteSpace(volumeId))
            return null;

        // GetVolumeInformation requires a trailing backslash on the GUID path.
        string root = volumeId.EndsWith('\\') ? volumeId : volumeId + "\\";

        var label = new StringBuilder(261);
        bool ok = GetVolumeInformationW(
            root, label, (uint)label.Capacity,
            out _, out _, out _, null, 0);

        if (!ok)
            return null;

        string text = label.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Enumerate every mount point (drive-letter roots and mounted-folder paths)
    /// currently assigned to a volume GUID.  Returns an empty list if the volume
    /// is not mounted.
    /// </summary>
    private static List<string> GetMountPoints(string volumeId)
    {
        string volumeName = volumeId.EndsWith('\\') ? volumeId : volumeId + "\\";

        uint required = 0;
        // First call to size the buffer; expected to fail with
        // ERROR_MORE_DATA (234) when the volume has mount points.
        GetVolumePathNamesForVolumeNameW(volumeName, null, 0, out required);
        if (required == 0)
            return [];

        var buffer = new char[required];
        if (!GetVolumePathNamesForVolumeNameW(volumeName, buffer, required, out _))
            return [];

        // The result is a double-NUL-terminated list of NUL-separated strings.
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '\0')
            {
                if (i == start) // empty string => end of list
                    break;
                result.Add(new string(buffer, start, i - start));
                start = i + 1;
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Native interop
    // ------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathNameW(
        string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathNamesForVolumeNameW(
        string lpszVolumeName, char[]? lpszVolumePathNames, uint cchBufferLength,
        out uint lpcchReturnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName, StringBuilder? lpVolumeNameBuffer, uint nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags, StringBuilder? lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}
