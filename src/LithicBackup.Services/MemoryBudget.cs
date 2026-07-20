using System.Runtime.InteropServices;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Resolves a <see cref="MemoryBudgetOptions"/> policy into a concrete byte
/// budget for the directory backup's in-memory file buffer, using live system
/// memory figures. All numbers are best-effort: if the OS memory query fails,
/// a conservative fallback is used.
/// </summary>
public static class MemoryBudget
{
    private const double GiB = 1024d * 1024d * 1024d;

    /// <summary>
    /// Fallback budget used when total system memory can't be determined and
    /// the policy needs it (Auto mode). Deliberately modest.
    /// </summary>
    private const long FallbackBytes = 512L * 1024 * 1024; // 512 MiB

    /// <summary>
    /// Total and currently-available physical memory in bytes, or (0, 0) if the
    /// query is unavailable.
    /// </summary>
    public static (long TotalBytes, long AvailableBytes) GetSystemMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
        }
        catch { /* fall through */ }
        return (0, 0);
    }

    /// <summary>
    /// Resolve a policy to a byte budget. Never negative; may be 0, which
    /// effectively disables in-memory buffering (every file is streamed, which
    /// is always correct, just with the old two-read cost for files that would
    /// otherwise have been buffered).
    /// </summary>
    public static long Resolve(MemoryBudgetOptions options)
    {
        options ??= MemoryBudgetOptions.Default;

        if (options.Mode == MemoryBudgetMode.Fixed)
            return Math.Max(0, (long)(options.FixedGb * GiB));

        // Auto: min(percentage of total, available - reserve).
        var (total, available) = GetSystemMemory();
        if (total <= 0)
            return FallbackBytes;

        long byPercent = (long)(total * (Math.Clamp(options.PercentOfTotal, 0, 100) / 100.0));
        long byAvailable = available - (long)(Math.Max(0, options.ReserveGb) * GiB);
        long budget = Math.Min(byPercent, byAvailable);
        return Math.Max(0, budget);
    }

    /// <summary>
    /// Live, per-file admission check for the in-memory file buffer. Unlike
    /// <see cref="Resolve"/> — which snapshots a single budget once at the start
    /// of a backup — this re-queries system <b>and</b> process memory on EVERY
    /// call, so the user's two guarantees hold <i>continuously</i> as the backup
    /// (and other programs) consume RAM, instead of only at the instant the
    /// backup began:
    /// <list type="bullet">
    ///   <item>the process never exceeds <see cref="MemoryBudgetOptions.PercentOfTotal"/>
    ///   of total physical RAM — measured against the live process working set
    ///   (what Task Manager reports), so buffer bytes AND .NET heap / large-object-
    ///   heap overhead both count; the old buffer-only budget ignored that overhead
    ///   and could let the process balloon well past the cap; and</item>
    ///   <item>currently-available physical RAM never drops below
    ///   <see cref="MemoryBudgetOptions.ReserveGb"/> left free for other programs —
    ///   checked against the <i>current</i> figure, not a stale start-of-run
    ///   snapshot, so the reserve is actually honoured as RAM fills up.</item>
    /// </list>
    /// Returns false to make the caller stream the file (read-twice) instead of
    /// buffering it — always correct, just without the single-read speed-up.
    /// </summary>
    public static bool CanBuffer(MemoryBudgetOptions options, long incomingBytes)
    {
        options ??= MemoryBudgetOptions.Default;

        // Empty/degenerate file: buffering it costs no memory, always allow.
        if (incomingBytes <= 0)
            return true;

        if (options.Mode == MemoryBudgetMode.Fixed)
            return incomingBytes <= Math.Max(0, (long)(options.FixedGb * GiB));

        var (total, available) = GetSystemMemory();
        if (total <= 0)
            return incomingBytes <= FallbackBytes;

        // (1) Keep the whole process under the percentage cap, measured against the
        // live working set — this is what the user sees the app "taking" in Task
        // Manager, and it includes the buffer plus GC/LOH overhead.
        long percentCap = (long)(total * (Math.Clamp(options.PercentOfTotal, 0, 100) / 100.0));
        if (CurrentProcessBytes() + incomingBytes > percentCap)
            return false;

        // (2) Never push live available memory below the reserve. 'available' is
        // system-wide, so this also throttles the backup when OTHER programs are
        // the ones eating RAM, honouring the reserve against the current state.
        long reserve = (long)(Math.Max(0, options.ReserveGb) * GiB);
        if (available - incomingBytes < reserve)
            return false;

        return true;
    }

    /// <summary>
    /// The current process's physical memory footprint (working set) in bytes —
    /// the same figure Task Manager shows — falling back to the managed heap size
    /// if the OS query is unavailable.
    /// </summary>
    private static long CurrentProcessBytes()
    {
        try
        {
            long ws = Environment.WorkingSet;
            if (ws > 0)
                return ws;
        }
        catch { /* fall through */ }
        return GC.GetTotalMemory(false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
