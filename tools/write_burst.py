r"""
Find what was writing to a volume during a given window.

Used to explain a volume that briefly ran out of space: NTFS keeps no free-space
history, but every file carries an mtime, so a walk of the volume filtered to the
window shows what landed there. (Files deleted since won't appear -- that is what
the USN journal would show, but FSCTL_READ_USN_JOURNAL needs elevation.)

Usage:
    python write_burst.py <root> <start ISO> <end ISO> [--top N]
    python write_burst.py D:\ 2026-08-15T18:30 2026-08-15T19:10
"""

import argparse
import os
import sys
from datetime import datetime

SKIP_DIRS = {"$RECYCLE.BIN", "System Volume Information", "$Extend"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("start")
    ap.add_argument("end")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--min-mb", type=float, default=0.0)
    args = ap.parse_args()

    start = datetime.fromisoformat(args.start).timestamp()
    end = datetime.fromisoformat(args.end).timestamp()
    floor = args.min_mb * 1e6

    hits = []
    total = 0
    scanned = 0
    by_dir = {}

    for root, dirs, files in os.walk(args.root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            path = os.path.join(root, name)
            try:
                st = os.stat(path)
            except OSError:
                continue
            scanned += 1
            if not (start <= st.st_mtime <= end):
                continue
            total += st.st_size
            # Aggregate by directory too: a burst is often thousands of small
            # files in one tree, which no single-file top-N would surface.
            by_dir[root] = by_dir.get(root, 0) + st.st_size
            if st.st_size >= floor:
                hits.append((st.st_size, st.st_mtime, path))

    print(f"scanned {scanned:,} files under {args.root}")
    print(f"written in window: {len(hits):,} files, {total/1e9:.3f} GB total\n")

    print(f"--- largest {args.top} files in window ---")
    for size, mtime, path in sorted(hits, reverse=True)[: args.top]:
        stamp = datetime.fromtimestamp(mtime).strftime("%H:%M:%S")
        print(f"  {size/1e6:10.1f} MB  {stamp}  {path}")

    print(f"\n--- top {args.top} directories by bytes in window ---")
    for path, size in sorted(by_dir.items(), key=lambda kv: -kv[1])[: args.top]:
        print(f"  {size/1e6:10.1f} MB  {path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
