using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// A single change reported by the NTFS USN change journal.
/// </summary>
/// <param name="FullPath">Resolved absolute path of the changed item.</param>
/// <param name="Reason">Bitmask of <c>USN_REASON_*</c> flags for the record.</param>
/// <param name="IsDirectory">Whether the changed item is a directory.</param>
public readonly record struct UsnChange(string FullPath, uint Reason, bool IsDirectory);

/// <summary>
/// Reads change records from a single NTFS volume's USN change journal.
/// </summary>
/// <remarks>
/// The journal is a persistent, ordered log of every change to every file on
/// an NTFS volume. Unlike <see cref="FileSystemMonitorImpl"/>, it never drops
/// events and records changes that happened while this process was not running,
/// which makes it a reliable source of truth for continuous backup.
///
/// Reading the journal requires opening a raw volume handle (<c>\\.\C:</c>),
/// which Windows restricts to administrators. The LithicBackup worker runs as
/// the <c>LocalSystem</c> service account, which has this privilege, so no
/// interactive elevation is ever required.
///
/// Records identify files by 64-bit File Reference Numbers, not paths. We
/// resolve each record's <em>parent</em> directory via <c>OpenFileById</c> +
/// <c>GetFinalPathNameByHandle</c> (cached per parent) and append the record's
/// file name. Resolving the parent rather than the file itself means that
/// just-deleted files still resolve, as long as their parent still exists.
/// </remarks>
public sealed class UsnJournalReader : IDisposable
{
    private readonly SafeFileHandle _volume;

    /// <summary>Cache of parent File Reference Number → directory path.</summary>
    private readonly Dictionary<long, string?> _parentPathCache = new();

    /// <summary>Identity of this volume's journal (detects re-creation).</summary>
    public long JournalId { get; }

    /// <summary>The next USN at the moment the journal was queried (open time).</summary>
    public long CurrentNextUsn { get; }

    /// <summary>The volume root this reader covers, e.g. <c>"C:\"</c>.</summary>
    public string VolumeId { get; }

    private UsnJournalReader(SafeFileHandle volume, string volumeId, long journalId, long nextUsn)
    {
        _volume = volume;
        VolumeId = volumeId;
        JournalId = journalId;
        CurrentNextUsn = nextUsn;
    }

    /// <summary>
    /// Attempt to open the USN journal for a drive. Returns null when the drive
    /// is not NTFS, the journal is unavailable, or access is denied (e.g. when
    /// not running with sufficient privilege). Creates the journal on demand if
    /// the volume supports it but none is currently active.
    /// </summary>
    public static UsnJournalReader? TryOpen(char driveLetter)
    {
        var volumePath = $"\\\\.\\{char.ToUpperInvariant(driveLetter)}:";
        var volumeId = $"{char.ToUpperInvariant(driveLetter)}:\\";

        SafeFileHandle handle = CreateFileW(
            volumePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            // Retry read-only — creating/writing the journal needs write access,
            // but plain reads only need GENERIC_READ.
            handle.Dispose();
            handle = CreateFileW(
                volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
        }

        try
        {
            if (!TryQueryJournal(handle, out long journalId, out long nextUsn))
            {
                // Journal may simply not be active yet — try to create it.
                if (!TryCreateJournal(handle) ||
                    !TryQueryJournal(handle, out journalId, out nextUsn))
                {
                    handle.Dispose();
                    return null;
                }
            }

            return new UsnJournalReader(handle, volumeId, journalId, nextUsn);
        }
        catch
        {
            handle.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Read all change records on or after <paramref name="startUsn"/>, resolving
    /// each to an absolute path. Returns the distinct set of changed items and
    /// the USN to resume from next time via <paramref name="nextUsn"/>.
    /// </summary>
    public IReadOnlyList<UsnChange> ReadChanges(long startUsn, out long nextUsn, CancellationToken ct = default)
    {
        // Deduplicate by path — a single edit produces many records (extend,
        // overwrite, close, ...); we only care that the file changed.
        var byPath = new Dictionary<string, UsnChange>(StringComparer.OrdinalIgnoreCase);

        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        long cursor = startUsn;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var input = new byte[40]; // READ_USN_JOURNAL_DATA_V0
            BitConverter.GetBytes(cursor).CopyTo(input, 0);          // StartUsn
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(input, 8);     // ReasonMask: all
            BitConverter.GetBytes(0u).CopyTo(input, 12);             // ReturnOnlyOnClose
            BitConverter.GetBytes(0UL).CopyTo(input, 16);            // Timeout
            BitConverter.GetBytes(0UL).CopyTo(input, 24);            // BytesToWaitFor
            BitConverter.GetBytes(JournalId).CopyTo(input, 32);      // UsnJournalID

            if (!DeviceIoControl(_volume, FSCTL_READ_USN_JOURNAL,
                    input, input.Length, buffer, buffer.Length,
                    out int bytesReturned, IntPtr.Zero))
            {
                // On any read failure, stop and keep the cursor where it was so
                // the caller does not skip changes; the next poll retries.
                break;
            }

            if (bytesReturned <= sizeof(long))
            {
                // Only the trailing next-USN was returned — no more records.
                cursor = BitConverter.ToInt64(buffer, 0);
                break;
            }

            long batchNext = BitConverter.ToInt64(buffer, 0);
            int offset = sizeof(long);

            while (offset < bytesReturned)
            {
                int recordLength = BitConverter.ToInt32(buffer, offset);
                if (recordLength <= 0 || offset + recordLength > bytesReturned)
                    break;

                short major = BitConverter.ToInt16(buffer, offset + 4);
                if (major == 2)
                {
                    long parentFrn = BitConverter.ToInt64(buffer, offset + 16);
                    uint reason = BitConverter.ToUInt32(buffer, offset + 40);
                    uint attrs = BitConverter.ToUInt32(buffer, offset + 52);
                    ushort nameLen = BitConverter.ToUInt16(buffer, offset + 56);
                    ushort nameOff = BitConverter.ToUInt16(buffer, offset + 58);

                    if (nameLen > 0 && offset + nameOff + nameLen <= bytesReturned)
                    {
                        string fileName = Encoding.Unicode.GetString(buffer, offset + nameOff, nameLen);
                        bool isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;

                        string? parentDir = ResolveParentPath(parentFrn);
                        if (parentDir is not null)
                        {
                            string full = Path.Combine(parentDir, fileName);
                            // Last reason wins is fine; we OR so deletes/renames
                            // are still visible if mixed with content changes.
                            if (byPath.TryGetValue(full, out var existing))
                                byPath[full] = existing with { Reason = existing.Reason | reason };
                            else
                                byPath[full] = new UsnChange(full, reason, isDir);
                        }
                    }
                }

                offset += recordLength;
            }

            cursor = batchNext;
        }

        nextUsn = cursor;
        return byPath.Values.ToList();
    }

    /// <summary>
    /// Resolve a parent directory's File Reference Number to an absolute path,
    /// caching results (many files share a parent, and deleted parents stay
    /// cached as null to avoid repeated failing opens).
    /// </summary>
    private string? ResolveParentPath(long frn)
    {
        if (_parentPathCache.TryGetValue(frn, out var cached))
            return cached;

        string? resolved = null;

        var descriptor = new FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
            Type = 0, // FileIdType
            FileId = frn,
            FileIdHigh = 0,
        };

        SafeFileHandle fileHandle = OpenFileById(
            _volume,
            ref descriptor,
            0, // query-only access is enough for GetFinalPathNameByHandle
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (!fileHandle.IsInvalid)
        {
            var sb = new StringBuilder(1024);
            uint len = GetFinalPathNameByHandle(fileHandle, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
            if (len > 0 && len < sb.Capacity)
            {
                string path = sb.ToString();
                // GetFinalPathNameByHandle returns the \\?\ prefixed form.
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    path = path[4..];
                resolved = path;
            }
        }

        fileHandle.Dispose();
        _parentPathCache[frn] = resolved;
        return resolved;
    }

    private static bool TryQueryJournal(SafeFileHandle handle, out long journalId, out long nextUsn)
    {
        journalId = 0;
        nextUsn = 0;

        var outBuffer = new byte[64]; // USN_JOURNAL_DATA_V0 is 56 bytes
        if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL,
                null, 0, outBuffer, outBuffer.Length, out int bytesReturned, IntPtr.Zero)
            || bytesReturned < 24)
        {
            return false;
        }

        journalId = BitConverter.ToInt64(outBuffer, 0);  // UsnJournalID
        nextUsn = BitConverter.ToInt64(outBuffer, 16);   // NextUsn
        return true;
    }

    private static bool TryCreateJournal(SafeFileHandle handle)
    {
        // CREATE_USN_JOURNAL_DATA { DWORDLONG MaximumSize; DWORDLONG AllocationDelta; }
        // Zeros request system defaults.
        var input = new byte[16];
        return DeviceIoControl(handle, FSCTL_CREATE_USN_JOURNAL,
            input, input.Length, null, 0, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        _volume.Dispose();
    }

    // ------------------------------------------------------------------
    // Native interop
    // ------------------------------------------------------------------

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_NAME_NORMALIZED = 0x0;

    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;
    private const uint FSCTL_CREATE_USN_JOURNAL = 0x000900e7;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;
        public long FileId;     // low 64 bits of the 128-bit union (FileIdType)
        public long FileIdHigh; // padding to cover the 16-byte union
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, int nInBufferSize,
        byte[]? lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint, ref FILE_ID_DESCRIPTOR lpFileId,
        uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}
