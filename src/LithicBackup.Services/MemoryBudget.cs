using System.Runtime;
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

        // Both modes are enforced against the LIVE process working set (what Task
        // Manager shows), so the cap bounds the whole backup's footprint — buffered
        // file bytes plus GC/LOH overhead — cumulatively, not per-file. (An earlier
        // version checked Fixed mode per-file only, which let unlimited sub-cap files
        // all buffer at once.)
        long cap = ProcessCap(options);
        if (cap >= 0 && CurrentProcessBytes() + incomingBytes > cap)
            return false;

        // Auto mode additionally honours the free-RAM reserve against the CURRENT
        // system-wide figure, so the backup also backs off when OTHER programs are
        // the ones eating RAM.
        if (options.Mode != MemoryBudgetMode.Fixed)
        {
            var (total, available) = GetSystemMemory();
            if (total > 0)
            {
                long reserve = (long)(Math.Max(0, options.ReserveGb) * GiB);
                if (available - incomingBytes < reserve)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the process working set already exceeds the policy's cap, i.e.
    /// memory should be actively reclaimed (buffers dropped + heap compacted) —
    /// e.g. after the user tightens the budget mid-backup. Returns false if no
    /// finite cap applies.
    /// </summary>
    public static bool IsOverBudget(MemoryBudgetOptions options)
    {
        options ??= MemoryBudgetOptions.Default;
        long cap = ProcessCap(options);
        return cap >= 0 && CurrentProcessBytes() > cap;
    }

    /// <summary>
    /// Force the .NET runtime to return freed memory (notably large byte[] file
    /// buffers on the Large Object Heap) to the OS. A plain GC.Collect does not
    /// compact the LOH, so freed large arrays leave the working set unchanged;
    /// requesting a one-shot compaction is what actually shrinks the footprint
    /// the user sees in Task Manager. Costs a blocking gen-2 collection, so only
    /// call it when the budget is genuinely exceeded.
    /// </summary>
    public static void ReclaimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { /* best-effort reclaim */ }
    }

    /// <summary>
    /// The absolute working-set cap in bytes for a policy: the Fixed budget, or
    /// the Auto percentage of total physical RAM. Returns -1 when no finite cap
    /// can be determined (Auto with an unavailable memory query).
    /// </summary>
    private static long ProcessCap(MemoryBudgetOptions options)
    {
        if (options.Mode == MemoryBudgetMode.Fixed)
            return Math.Max(0, (long)(options.FixedGb * GiB));

        var (total, _) = GetSystemMemory();
        if (total <= 0)
            return -1;
        return (long)(total * (Math.Clamp(options.PercentOfTotal, 0, 100) / 100.0));
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
