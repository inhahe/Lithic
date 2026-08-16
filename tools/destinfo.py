r"""
Dump every backup set's name, schedule mode, and resolved destination root, next
to the live free space of that root.

Reproduces exactly what DestinationSpaceMonitor computes -- resolve the stored
DestinationVolumeId to its current mount point, join DestinationSubpath, take the
path root, ask GetDiskFreeSpaceEx -- so we can see which set (if any) is actually
below the 1 GB "full" threshold.
"""

import ctypes
import ctypes.wintypes as wt
import json
import os
import sqlite3
import subprocess

THRESHOLD = 1 << 30

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetDiskFreeSpaceExW.argtypes = [
    wt.LPCWSTR,
    ctypes.POINTER(ctypes.c_ulonglong),
    ctypes.POINTER(ctypes.c_ulonglong),
    ctypes.POINTER(ctypes.c_ulonglong),
]


def mount_points():
    """volume GUID path -> current mount point, as mountvol reports it."""
    out = subprocess.run(["mountvol"], capture_output=True, text=True).stdout
    table, vol = {}, None
    for line in out.splitlines():
        s = line.strip()
        if s.startswith("\\\\?\\Volume{"):
            vol = s
        elif vol and s and not s.startswith("***"):
            table[vol.lower()] = s
            vol = None
        elif vol and s.startswith("***"):
            vol = None
    return table


def free_bytes(root):
    avail = ctypes.c_ulonglong()
    total = ctypes.c_ulonglong()
    free_total = ctypes.c_ulonglong()
    if not k32.GetDiskFreeSpaceExW(
        root, ctypes.byref(avail), ctypes.byref(total), ctypes.byref(free_total)
    ):
        return None
    return avail.value


MODES = {0: "Manual", 1: "Interval?", 2: "Continuous", 3: "Daily?"}

mounts = mount_points()
con = sqlite3.connect("file:C:/ProgramData/LithicBackup/catalog.db?mode=ro", uri=True)
rows = con.execute("SELECT Id, Name, JobOptionsJson FROM BackupSets ORDER BY Id").fetchall()

print(f"{'id':>3} {'name':<28} {'sched':<12} {'root':<6} {'free':>12}  dest")
for sid, name, blob in rows:
    opts = json.loads(blob) if blob else {}
    sched = opts.get("Schedule") or {}
    mode = MODES.get(sched.get("Mode"), str(sched.get("Mode")))
    label = f"{mode}{'' if sched.get('Enabled') else ' (off)'}"

    vol = (opts.get("DestinationVolumeId") or "").lower()
    sub = opts.get("DestinationSubpath") or ""
    mount = mounts.get(vol)
    if mount is None:
        print(f"{sid:>3} {name:<28} {label:<12} {'-':<6} {'not connected':>12}  {opts.get('TargetDirectory')}")
        continue

    live = os.path.join(mount, sub) if sub else mount
    root = os.path.splitdrive(live)[0] + "\\"
    fb = free_bytes(root)
    flag = "  <-- BELOW 1 GB" if fb is not None and fb < THRESHOLD else ""
    exists = "" if os.path.isdir(live) else "   [dest dir MISSING]"
    print(
        f"{sid:>3} {name:<28} {label:<12} {root:<6} "
        f"{(fb or 0)/1e9:9.3f} GB  {live}{exists}{flag}"
    )
con.close()
