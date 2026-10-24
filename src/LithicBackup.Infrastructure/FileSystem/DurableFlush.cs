using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Pushing what has been written out of the Windows file cache onto the disk
/// itself, so a drive that drops off (a USB disk losing its connection) keeps it.
/// A write is "done" once its bytes are in the cache; the lazy writer puts them
/// on the disk seconds later.
/// </summary>
public static class DurableFlush
{
    /// <summary>How a flush of a volume went.</summary>
    public enum Outcome
    {
        /// <summary>Everything written to the volume is on the disk.</summary>
        Flushed,

        /// <summary>
        /// This process may not do it: flushing a volume needs write access to its
        /// device, which only an elevated process has. Someone else may.
        /// </summary>
        NotPermitted,

        /// <summary>
        /// It was tried and did not happen: the device is gone, or reported an
        /// error. What was written must not be taken as durable on its account.
        /// </summary>
        Failed,
    }

    /// <summary>
    /// The local volume <paramref name="folder"/> is really on, through junctions,
    /// symbolic links, subst drives and folders a volume is mounted in, named
    /// <c>\\?\Volume{GUID}\</c>. That name means the same in every logon session,
    /// unlike a drive letter, so it can be handed to the service.
    /// </summary>
    /// <param name="folder">An existing folder (or file).</param>
    /// <param name="error">Why there is none; null when there is.</param>
    /// <returns>
    /// Null when there is none: the folder is gone, or on a file system with no
    /// volume names (a network share, WSL's, some RAM disks).
    /// </returns>
    public static string? VolumeOf(string folder, out string? error)
    {
        using var handle = OpenForAttributes(folder);
        if (handle.IsInvalid)
        {
            error = $"{folder}: {LastError()}";
            return null;
        }

        var buffer = new char[1024];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VOLUME_NAME_GUID);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VOLUME_NAME_GUID);
        }
        if (length == 0 || length >= buffer.Length)
        {
            error = $"{folder}: {LastError()}";
            return null;
        }

        string final = new(buffer, 0, (int)length);
        int end = final.IndexOf("}\\", StringComparison.Ordinal);
        if (end < 0 || !IsVolumeName(final[..(end + 2)]))
        {
            error = $"{folder} is on \"{final}\", not on a volume";
            return null;
        }
        error = null;
        return final[..(end + 2)];
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a volume name of the form
    /// <see cref="VolumeOf"/> gives, and nothing more.
    /// </summary>
    public static bool IsVolumeName(string name) => VolumeName.IsMatch(name);

    private static readonly Regex VolumeName = new(
        @"^\\\\\?\\Volume\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\\$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Flush everything written to a local volume: every file's data and the file
    /// system's own records (names, renames), in one call and one flush of the
    /// device's cache. Takes as long as writing out what is still in the cache.
    /// </summary>
    /// <param name="volume">The volume, as <see cref="VolumeOf"/> names it.</param>
    /// <param name="error">Why it was not flushed, for a log; null when it was.</param>
    public static Outcome TryFlushVolume(string volume, out string? error)
    {
        if (!IsVolumeName(volume))
        {
            error = $"\"{volume}\" is not a volume name";
            return Outcome.Failed;
        }

        string device = volume.TrimEnd('\\');   // the device, not its root folder
        using var handle = CreateFileW(device, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            error = $"{device}: {new Win32Exception(code).Message}";
            return code == ERROR_ACCESS_DENIED ? Outcome.NotPermitted : Outcome.Failed;
        }
        if (!FlushFileBuffers(handle))
        {
            error = $"{device}: {LastError()}";
            return Outcome.Failed;
        }
        error = null;
        return Outcome.Flushed;
    }

    /// <summary>
    /// Flush one file's data, and the file system's records of it, onto the disk.
    /// A folder is flushed the same way where the file system allows it (NTFS
    /// does); where it does not, that is not counted as a failure, the folder's
    /// own records going out with the next file flushed on its volume.
    /// </summary>
    /// <param name="path">A file or folder the caller wrote or renamed.</param>
    /// <param name="error">Why it was not flushed; null when it was.</param>
    /// <returns>False when <paramref name="path"/> is gone or could not be flushed.</returns>
    public static bool TryFlushPath(string path, out string? error)
    {
        error = null;
        bool isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
        {
            error = $"{path}: it is no longer there";
            return false;
        }

        // A read-only file cannot be opened for writing, and flushing needs that:
        // its read-only flag is lifted for the flush and put back.
        bool readOnly = false;
        if (!isFolder)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                readOnly = true;
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        try
        {
            using var handle = CreateFileW(LongPath(Path.GetFullPath(path)),
                GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, isFolder ? FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
            if (handle.IsInvalid || !FlushFileBuffers(handle))
            {
                if (isFolder)
                    return true;
                error = $"{path}: {LastError()}";
                return false;
            }
            return true;
        }
        finally
        {
            if (readOnly)
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// <paramref name="path"/> opened to read its attributes only, sharing
    /// everything: a folder as readily as a file. Invalid when it cannot be.
    /// </summary>
    internal static SafeFileHandle OpenForAttributes(string path) =>
        CreateFileW(LongPath(Path.GetFullPath(path)), FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    /// <summary>The calling thread's last Win32 error, in words.</summary>
    internal static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    /// <summary>
    /// <paramref name="full"/>, a full path, in the form CreateFileW takes past
    /// MAX_PATH: <c>\\?\C:\...</c>, or <c>\\?\UNC\server\share\...</c> for a share.
    /// </summary>
    private static string LongPath(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint VOLUME_NAME_GUID = 0x1;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}

/// <summary>
/// A folder held open while a run writes under it, to tell afterwards whether its
/// volume went away in between. A USB drive that drops off and comes back under
/// the same letter loses what was still in the cache, yet its paths work again,
/// and flushing it then flushes nothing of what was lost; a handle opened before
/// the drop stays dead.
/// </summary>
public sealed class VolumeWatch : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _folder;

    private VolumeWatch(SafeFileHandle handle, string folder)
    {
        _handle = handle;
        _folder = folder;
    }

    /// <summary>
    /// Start watching <paramref name="folder"/>'s volume; null when the folder
    /// cannot be held (it is not there), so there is nothing to watch.
    /// </summary>
    public static VolumeWatch? Open(string folder)
    {
        var handle = DurableFlush.OpenForAttributes(folder);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out _))
        {
            handle.Dispose();
            return null;
        }
        return new VolumeWatch(handle, folder);
    }

    /// <summary>
    /// Whether the folder's volume has gone away since <see cref="Open"/>, even if
    /// it is back now. Asks the file system about the held folder, which it
    /// answers for a handle on the volume's current mount only.
    /// </summary>
    /// <param name="error">What the file system said; null when not lost.</param>
    public bool WasLost(out string? error)
    {
        if (GetFileInformationByHandle(_handle, out _))
        {
            error = null;
            return false;
        }
        error = $"{_folder}: {DurableFlush.LastError()}";
        return true;
    }

    public void Dispose() => _handle.Dispose();

    // FILETIMEs are pairs of DWORDs, aligned on 4.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);
}
