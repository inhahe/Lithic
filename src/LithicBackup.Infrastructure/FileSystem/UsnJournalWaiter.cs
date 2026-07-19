using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Push notifier for a single NTFS volume's USN change journal.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="UsnJournalReader"/> is a <em>pull</em> reader — you call it and
/// it returns whatever records exist right now — this class turns the same journal
/// into a <em>push</em> source. It issues <c>FSCTL_READ_USN_JOURNAL</c> with a
/// non-zero <c>BytesToWaitFor</c>, which parks in the kernel and only returns once
/// at least one byte of new journal data has accumulated past the starting USN.
/// A dedicated background thread runs that blocking wait in a loop and fires
/// <c>onChanged</c> the moment the volume changes, so the worker can react
/// immediately instead of waiting out its poll interval.
/// </para>
/// <para>
/// The waiter never parses records or resolves paths — that remains the reader's
/// job on the worker's poll thread. Its sole responsibility is "wake the worker
/// when this volume changes". Keeping it that narrow means all per-set state stays
/// single-threaded on the poll thread; the waiter only touches its own handles.
/// </para>
/// <para>
/// The blocking read is issued in <em>overlapped</em> mode against a manual-reset
/// event, and the thread waits on both that event and a private cancel event via
/// <see cref="WaitForMultipleObjects"/>. Disposal signals the cancel event and
/// <see cref="CancelIoEx"/>s the pending read, so shutdown never hangs on a parked
/// FSCTL — the reason a naive synchronous blocking read would be unusable here.
/// </para>
/// </remarks>
public sealed class UsnJournalWaiter : IDisposable
{
    private readonly SafeFileHandle _volume;
    private readonly long _journalId;
    private readonly Action _onChanged;

    private readonly IntPtr _opEvent;      // manual-reset, signalled when the read completes
    private readonly IntPtr _cancelEvent;  // manual-reset, signalled on Dispose to unblock the wait
    private readonly IntPtr _overlapped;   // pinned OVERLAPPED reused across iterations
    private readonly IntPtr[] _waitHandles;

    private readonly byte[] _input = new byte[40];   // READ_USN_JOURNAL_DATA_V0
    private readonly byte[] _output = new byte[4096]; // only the trailing next-USN is read
    private GCHandle _inputPin;
    private GCHandle _outputPin;

    private readonly Thread _thread;
    private volatile bool _stopping;

    /// <summary>The volume root this waiter covers, e.g. <c>"C:\"</c>.</summary>
    public string VolumeId { get; }

    private enum WaitOutcome { Changed, Cancelled, Error }

    private UsnJournalWaiter(
        SafeFileHandle volume, string volumeId, long journalId, long startUsn,
        IntPtr opEvent, IntPtr cancelEvent, IntPtr overlapped, Action onChanged)
    {
        _volume = volume;
        VolumeId = volumeId;
        _journalId = journalId;
        _opEvent = opEvent;
        _cancelEvent = cancelEvent;
        _overlapped = overlapped;
        _onChanged = onChanged;
        _waitHandles = new[] { _opEvent, _cancelEvent };

        _inputPin = GCHandle.Alloc(_input, GCHandleType.Pinned);
        _outputPin = GCHandle.Alloc(_output, GCHandleType.Pinned);

        _thread = new Thread(() => RunLoop(startUsn))
        {
            IsBackground = true,
            Name = $"UsnWaiter-{volumeId}",
        };
        _thread.Start();
    }

    /// <summary>
    /// Open a push waiter for a drive, or return null when the volume has no usable
    /// USN journal (non-NTFS or inaccessible). The caller should only create a
    /// waiter for volumes whose journal a <see cref="UsnJournalReader"/> has already
    /// opened/created, so the journal is guaranteed to exist here.
    /// </summary>
    /// <param name="driveLetter">The volume's drive letter.</param>
    /// <param name="onChanged">
    /// Invoked (on the waiter's own thread) each time the volume changes. Must be
    /// cheap and thread-safe — typically it just signals the worker to wake.
    /// </param>
    public static UsnJournalWaiter? TryOpen(char driveLetter, Action onChanged)
    {
        var volumePath = $"\\\\.\\{char.ToUpperInvariant(driveLetter)}:";
        var volumeId = $"{char.ToUpperInvariant(driveLetter)}:\\";

        SafeFileHandle handle = CreateFileW(
            volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        IntPtr opEvent = CreateEventW(IntPtr.Zero, true, false, null);
        IntPtr cancelEvent = CreateEventW(IntPtr.Zero, true, false, null);
        IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());

        void Cleanup()
        {
            handle.Dispose();
            if (opEvent != IntPtr.Zero) CloseHandle(opEvent);
            if (cancelEvent != IntPtr.Zero) CloseHandle(cancelEvent);
            if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
        }

        if (opEvent == IntPtr.Zero || cancelEvent == IntPtr.Zero)
        {
            Cleanup();
            return null;
        }

        if (!QueryJournal(handle, opEvent, overlapped, out long journalId, out long nextUsn))
        {
            Cleanup();
            return null;
        }

        return new UsnJournalWaiter(
            handle, volumeId, journalId, nextUsn, opEvent, cancelEvent, overlapped, onChanged);
    }

    private void RunLoop(long cursor)
    {
        while (!_stopping)
        {
            var outcome = WaitForNewData(ref cursor);
            if (_stopping)
                break;

            if (outcome == WaitOutcome.Changed)
            {
                try { _onChanged(); }
                catch { /* the worker's periodic poll is the backstop; never crash the thread */ }

                // Coalesce bursts: under sustained writes BytesToWaitFor=1 would
                // return almost continuously, so pause briefly before re-arming.
                // This caps wake-ups at ~4/sec per volume (the worker debounces the
                // actual backup anyway) while staying instantly cancellable — the
                // wait returns early the moment Dispose signals the cancel event.
                if (WaitForSingleObject(_cancelEvent, CoalesceMs) == WAIT_OBJECT_0)
                    break;
            }
            else
            {
                // Cancelled, journal wrapped, or any other read failure: stop pushing
                // for this volume. The worker's periodic poll remains the correctness
                // backstop and will reopen a fresh waiter on a later cycle if needed.
                break;
            }
        }
    }

    /// <summary>
    /// Block until at least one byte of new journal data exists past
    /// <paramref name="cursor"/>, then advance the cursor to the journal's new end.
    /// Returns <see cref="WaitOutcome.Cancelled"/> when Dispose signalled the cancel
    /// event, or <see cref="WaitOutcome.Error"/> on any read failure.
    /// </summary>
    private WaitOutcome WaitForNewData(ref long cursor)
    {
        Array.Clear(_input, 0, _input.Length);
        BitConverter.TryWriteBytes(_input.AsSpan(0), cursor);       // StartUsn
        BitConverter.TryWriteBytes(_input.AsSpan(8), 0xFFFFFFFFu);  // ReasonMask: all
        // ReturnOnlyOnClose (12) = 0, Timeout (16) = 0
        BitConverter.TryWriteBytes(_input.AsSpan(24), 1UL);         // BytesToWaitFor: block until any change
        BitConverter.TryWriteBytes(_input.AsSpan(32), _journalId);  // UsnJournalID

        ResetEvent(_opEvent);
        var nov = new NativeOverlapped { EventHandle = _opEvent };
        Marshal.StructureToPtr(nov, _overlapped, false);

        bool ok = DeviceIoControl(
            _volume, FSCTL_READ_USN_JOURNAL,
            _inputPin.AddrOfPinnedObject(), _input.Length,
            _outputPin.AddrOfPinnedObject(), _output.Length,
            out int bytes, _overlapped);

        if (!ok)
        {
            if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                return WaitOutcome.Error;

            uint w = WaitForMultipleObjects(2, _waitHandles, false, INFINITE);
            if (w != WAIT_OBJECT_0)
            {
                // Cancel requested (or the wait itself failed): abort the pending
                // read and drain its result so the handle is left quiescent.
                CancelIoEx(_volume, _overlapped);
                GetOverlappedResult(_volume, _overlapped, out _, true);
                return WaitOutcome.Cancelled;
            }

            if (!GetOverlappedResult(_volume, _overlapped, out bytes, true))
                return WaitOutcome.Error;
        }

        // The output buffer begins with the journal's next USN; advance past it so
        // the following wait blocks for data beyond what we just observed.
        if (bytes >= sizeof(long))
            cursor = BitConverter.ToInt64(_output, 0);
        return WaitOutcome.Changed;
    }

    /// <summary>
    /// Query the journal's live identity and next-USN over an overlapped handle
    /// (the query completes effectively immediately; we wait only for form's sake).
    /// </summary>
    private static bool QueryJournal(
        SafeFileHandle handle, IntPtr opEvent, IntPtr overlapped, out long journalId, out long nextUsn)
    {
        journalId = 0;
        nextUsn = 0;

        var outBuffer = new byte[64];
        var pin = GCHandle.Alloc(outBuffer, GCHandleType.Pinned);
        try
        {
            ResetEvent(opEvent);
            var nov = new NativeOverlapped { EventHandle = opEvent };
            Marshal.StructureToPtr(nov, overlapped, false);

            bool ok = DeviceIoControl(
                handle, FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero, 0, pin.AddrOfPinnedObject(), outBuffer.Length,
                out int bytes, overlapped);

            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                    return false;
                WaitForSingleObject(opEvent, INFINITE);
                if (!GetOverlappedResult(handle, overlapped, out bytes, true))
                    return false;
            }

            if (bytes < 24)
                return false;

            journalId = BitConverter.ToInt64(outBuffer, 0);   // UsnJournalID
            nextUsn = BitConverter.ToInt64(outBuffer, 16);    // NextUsn
            return true;
        }
        finally
        {
            pin.Free();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_cancelEvent != IntPtr.Zero)
            SetEvent(_cancelEvent);

        try { _thread.Join(TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }

        _volume.Dispose();

        if (_inputPin.IsAllocated) _inputPin.Free();
        if (_outputPin.IsAllocated) _outputPin.Free();
        if (_overlapped != IntPtr.Zero) Marshal.FreeHGlobal(_overlapped);
        if (_opEvent != IntPtr.Zero) CloseHandle(_opEvent);
        if (_cancelEvent != IntPtr.Zero) CloseHandle(_cancelEvent);
    }

    // ------------------------------------------------------------------
    // Native interop
    // ------------------------------------------------------------------

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;

    private const int ERROR_IO_PENDING = 997;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    /// <summary>Burst-coalescing pause after each wake (milliseconds).</summary>
    private const uint CoalesceMs = 250;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile, IntPtr lpOverlapped, out int lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(
        uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);
}
