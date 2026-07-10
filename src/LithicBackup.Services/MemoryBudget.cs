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
