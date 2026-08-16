r"""
Compare the two different "free space" numbers Windows reports for a volume.

DriveInfo.AvailableFreeSpace (what DestinationSpaceMonitor uses) maps to
GetDiskFreeSpaceEx's lpFreeBytesAvailable -- free space available *to the calling
user*, which disk quotas reduce. Get-Volume's SizeRemaining and
DriveInfo.TotalFreeSpace map to lpTotalNumberOfFreeBytes -- free space on the
volume overall. When a quota is in force the two diverge, and code that shows the
user one number while deciding on the other looks like it is lying.
"""

import ctypes
import ctypes.wintypes as wt

PATHS = [
    "D:\\",
    "D:\\visual studio projects\\backup\\test_out\\",
    "C:\\",
    "I:\\",
    "J:\\",
]

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetDiskFreeSpaceExW.argtypes = [
    wt.LPCWSTR,
    ctypes.POINTER(ctypes.c_ulonglong),
    ctypes.POINTER(ctypes.c_ulonglong),
    ctypes.POINTER(ctypes.c_ulonglong),
]

print(f"{'path':<50} {'AvailToUser':>13} {'TotalFree':>13} {'Size':>13}")
for path in PATHS:
    avail = ctypes.c_ulonglong()
    total = ctypes.c_ulonglong()
    free_total = ctypes.c_ulonglong()
    if not k32.GetDiskFreeSpaceExW(
        path, ctypes.byref(avail), ctypes.byref(total), ctypes.byref(free_total)
    ):
        print(f"{path:<50} FAILED err={ctypes.get_last_error()}")
        continue
    flag = "  <-- QUOTA" if free_total.value - avail.value > 1 << 30 else ""
    print(
        f"{path:<50} {avail.value/1e9:10.3f} GB {free_total.value/1e9:10.3f} GB "
        f"{total.value/1e9:10.3f} GB{flag}"
    )
