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
/// A file or directory that was renamed or moved <em>within the same volume</em>,
/// detected by pairing the journal's <c>RENAME_OLD_NAME</c> and
/// <c>RENAME_NEW_NAME</c> records — which share the item's File Reference Number
/// because a same-volume rename/move does not change its MFT identity. A moved
/// directory produces a single pair (its children are not re-journaled), so one
/// move describes the relocation of an entire subtree.
/// </summary>
/// <param name="OldPath">Absolute path before the move.</param>
/// <param name="NewPath">Absolute path after the move.</param>
/// <param name="IsDirectory">Whether the moved item is a directory.</param>
public readonly record struct UsnMove(string OldPath, string NewPath, bool IsDirectory);

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
    /// <param name="journalTruncated">
    /// Set to <c>true</c> when <paramref name="startUsn"/> refers to a record that
    /// has already been purged from the journal (the journal wrapped, so the
    /// history between the saved cursor and now is gone). The caller cannot read
    /// the gap incrementally and should reconcile with a full scan.
    /// </param>
    /// <param name="moves">
    /// Same-volume renames/moves detected by pairing <c>RENAME_OLD_NAME</c> and
    /// <c>RENAME_NEW_NAME</c> records that share a File Reference Number. These are
    /// reported separately from <paramref name="journalTruncated"/> changes so the
    /// backup can relocate the destination copy instead of re-copying it. A moved
    /// item is <em>not</em> also present in the returned change list unless it was
    /// additionally modified (e.g. content edited in the same window).
    /// </param>
    public IReadOnlyList<UsnChange> ReadChanges(
        long startUsn, out long nextUsn, out bool journalTruncated,
        out IReadOnlyList<UsnMove> moves, CancellationToken ct = default)
    {
        journalTruncated = false;

        // Deduplicate by path — a single edit produces many records (extend,
        // overwrite, close, ...); we only care that the file changed.
        var byPath = new Dictionary<string, UsnChange>(StringComparer.OrdinalIgnoreCase);

        // Accumulate rename halves keyed by the item's own File Reference Number.
        // A same-volume rename/move emits RENAME_OLD_NAME (old parent + old name)
        // and RENAME_NEW_NAME (new parent + new name) sharing this FRN.
        var renameByFrn = new Dictionary<long, RenameAccum>();

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
                // ERROR_JOURNAL_ENTRY_DELETED means our start USN has been purged
                // because the journal wrapped (records older than the retained
                // window are gone). The gap can't be read incrementally — flag it
                // so the caller reconciles with a full scan. Any other failure
                // just stops, keeping the cursor so the next poll retries.
                if (Marshal.GetLastWin32Error() == ERROR_JOURNAL_ENTRY_DELETED)
                    journalTruncated = true;
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
                    long ownFrn = BitConverter.ToInt64(buffer, offset + 8);
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

                            // Route the rename halves into the FRN-keyed accumulator
                            // so we can pair old→new. Everything else (or a rename
                            // combined with a content change) still lands in byPath.
                            if ((reason & USN_REASON_RENAME_OLD_NAME) != 0)
                            {
                                var accum = renameByFrn.TryGetValue(ownFrn, out var a) ? a : new RenameAccum { IsDir = isDir };
                                accum.OldPath = full;
                                accum.IsDir = isDir;
                                renameByFrn[ownFrn] = accum;
                            }
                            if ((reason & USN_REASON_RENAME_NEW_NAME) != 0)
                            {
                                var accum = renameByFrn.TryGetValue(ownFrn, out var a) ? a : new RenameAccum { IsDir = isDir };
                                accum.NewPath = full;
                                accum.IsDir = isDir;
                                accum.NewReason |= reason;
                                renameByFrn[ownFrn] = accum;
                            }

                            // Content/create/delete changes (anything beyond the
                            // rename+close bookkeeping) still count as a change.
                            uint changeReason = reason & ~NonChangeMask;
                            if (changeReason != 0)
                            {
                                if (byPath.TryGetValue(full, out var existing))
                                    byPath[full] = existing with { Reason = existing.Reason | changeReason };
                                else
                                    byPath[full] = new UsnChange(full, changeReason, isDir);
                            }
                        }
                    }
                }

                offset += recordLength;
            }

            cursor = batchNext;
        }

        // Resolve accumulated rename halves into moves. Only a complete pair with
        // both endpoints resolved is a true intra-volume move. A lone new-name
        // half (old side purged/out of window) is treated as a fresh change at the
        // new path so its content is still backed up; a lone old-name half means
        // the item left our view (renamed to somewhere we couldn't resolve) and is
        // handled by the deletion path elsewhere, so we drop it here.
        List<UsnMove>? moveList = null;
        foreach (var accum in renameByFrn.Values)
        {
            if (accum.OldPath is not null && accum.NewPath is not null)
            {
                if (string.Equals(accum.OldPath, accum.NewPath, StringComparison.OrdinalIgnoreCase))
                    continue; // no-op rename (case-only handled by the FS itself)

                (moveList ??= new List<UsnMove>()).Add(
                    new UsnMove(accum.OldPath, accum.NewPath, accum.IsDir));
            }
            else if (accum.NewPath is not null)
            {
                // New name with no pairable old name — back it up as a change.
                if (!byPath.ContainsKey(accum.NewPath))
                    byPath[accum.NewPath] = new UsnChange(
                        accum.NewPath, USN_REASON_RENAME_NEW_NAME, accum.IsDir);
            }
        }

        nextUsn = cursor;
        moves = (IReadOnlyList<UsnMove>?)moveList ?? Array.Empty<UsnMove>();
        return byPath.Values.ToList();
    }

    /// <summary>Mutable accumulator that pairs the two halves of a rename by FRN.</summary>
    private sealed class RenameAccum
    {
        public string? OldPath;
        public string? NewPath;
        public bool IsDir;
        public uint NewReason;
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

    /// <summary>
    /// Re-query the journal's live identity and next-USN position. Used to
    /// re-seed the resume cursor to the journal's current end after it wrapped,
    /// so live change detection resumes instead of endlessly re-reading a purged
    /// start USN.
    /// </summary>
    public bool TryRefreshPosition(out long journalId, out long nextUsn)
        => TryQueryJournal(_volume, out journalId, out nextUsn);

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

    /// <summary>The requested start USN has been purged from the journal (it wrapped).</summary>
    private const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    /// <summary>
    /// USN reason flag meaning the file or directory was newly created. Exposed
    /// so consumers can distinguish a brand-new directory (worth materialising as
    /// an explicit selection) from a mere metadata change on an existing one.
    /// </summary>
    public const uint UsnReasonFileCreate = 0x00000100;

    // USN_REASON_* flags relevant to move detection.
    private const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    private const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    private const uint USN_REASON_CLOSE = 0x80000000;

    /// <summary>
    /// Reason bits that are pure bookkeeping and do not, on their own, mean the
    /// item's <em>content</em> changed: the two rename halves (handled as moves)
    /// and the trailing CLOSE that terminates every record sequence.
    /// </summary>
    private const uint NonChangeMask =
        USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME | USN_REASON_CLOSE;

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
