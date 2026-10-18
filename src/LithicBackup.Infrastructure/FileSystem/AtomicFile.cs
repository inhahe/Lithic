using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Replacing a file in one step, so a reader sees the old content or the new,
/// never part of a write.
/// </summary>
/// <remarks>
/// <see cref="File.Move(string, string, bool)"/> replaces a file only when no
/// other handle has it open, whatever that handle shares: a reader holding it for
/// a moment, or a virus scanner looking at a file just written, makes the replace
/// fail, and back-to-back saves met one often enough to fail after a second of
/// retries. A rename with POSIX semantics (NTFS, Windows 10 1709 and later)
/// replaces the name even while handles that share delete access have the file
/// open; those handles keep reading the old content.
/// </remarks>
public static class AtomicFile
{
    /// <summary>What <see cref="TryReplace"/> did.</summary>
    public enum ReplaceResult
    {
        /// <summary><paramref name="destination"/> now names the new file.</summary>
        Replaced,

        /// <summary>A handle that does not share delete access has the destination
        /// open, or another passing condition: worth trying again shortly.</summary>
        Busy,
    }

    /// <summary>
    /// Rename <paramref name="source"/> over <paramref name="destination"/>,
    /// replacing it, with POSIX semantics where the volume supports them and an
    /// ordinary replacing move where it does not.
    /// </summary>
    /// <param name="source">The new file, finished and closed, on the same volume.</param>
    /// <param name="destination">The file to replace; it need not exist.</param>
    public static ReplaceResult TryReplace(string source, string destination)
    {
        switch (TryRenamePosix(source, destination))
        {
            case PosixOutcome.Done:
                return ReplaceResult.Replaced;
            case PosixOutcome.Busy:
                return ReplaceResult.Busy;
        }

        try
        {
            File.Move(source, destination, overwrite: true);
            return ReplaceResult.Replaced;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReplaceResult.Busy;
        }
    }

    private enum PosixOutcome { Done, Busy, Unsupported }

    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidFunction = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, uint bufferSize);

    private static PosixOutcome TryRenamePosix(string source, string destination)
    {
        using var handle = CreateFileW(
            Path.GetFullPath(source), Delete | Synchronize, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return PosixOutcome.Busy;

        // FILE_RENAME_INFO: a DWORD of flags (in a union with a BOOLEAN), then a
        // HANDLE aligned to its size, a DWORD byte length, and the UTF-16 name.
        string target = Path.GetFullPath(destination);
        int handleOffset = IntPtr.Size;
        int lengthOffset = handleOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        int nameBytes = target.Length * sizeof(char);
        int size = nameOffset + nameBytes + sizeof(char);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++)
                Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, unchecked((int)(ReplaceIfExists | PosixSemantics)));
            Marshal.WriteIntPtr(buffer, handleOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes);
            Marshal.Copy(target.ToCharArray(), 0, buffer + nameOffset, target.Length);

            if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size))
                return PosixOutcome.Done;

            int error = Marshal.GetLastWin32Error();
            return error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction
                ? PosixOutcome.Unsupported
                : PosixOutcome.Busy;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
