r"""
Ask Lithic's own catalog what changed on a source volume during a window.

Lithic's per-set database keeps a row per file *version* (Files.Version) with
the source file's last-write time and its size, so for any set whose sources
live on the volume under investigation it is a write journal in everything but
name -- and unlike a plain mtime walk it still shows files that have since been
deleted, because the catalog row outlives the file.

Two clocks are available and they answer different questions:
  --by mtime  what the source file claims it was written  (was it written then?)
  --by backup when Lithic copied it                       (what was Lithic doing then?)

Opens the live databases read-only; the Worker may be writing them.

Usage:
    python catalog_writes.py <start ISO> <end ISO> [--volume D:] [--by mtime|backup]
    python catalog_writes.py 2026-08-15T18:20 2026-08-15T19:15
"""

import argparse
import os
import sqlite3
import sys
from datetime import datetime, timezone

CATALOG_DIR = r"C:\ProgramData\LithicBackup"

TIMECOL = {"mtime": "SourceLastWriteUtc", "backup": "BackedUpUtc"}


def ro(path):
    uri = "file:" + path.replace("\\", "/").replace("#", "%23") + "?mode=ro"
    return sqlite3.connect(uri, uri=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("start")
    ap.add_argument("end")
    ap.add_argument("--volume", default="")
    ap.add_argument("--by", choices=sorted(TIMECOL), default="mtime")
    ap.add_argument("--top", type=int, default=30)
    args = ap.parse_args()

    col = TIMECOL[args.by]
    # The catalog stores naive UTC ISO strings, so compare in UTC and print
    # local time; a window given in local time would otherwise silently miss
    # everything by the UTC offset.
    def to_utc(text):
        return (datetime.fromisoformat(text).astimezone()
                .astimezone(timezone.utc).replace(tzinfo=None))

    start = to_utc(args.start)
    end = to_utc(args.end)
    vol = args.volume.lower()

    sets_dir = os.path.join(CATALOG_DIR, "sets")
    dbs = [os.path.join(sets_dir, f) for f in sorted(os.listdir(sets_dir))
           if f.endswith(".db")]

    print(f"window {start} .. {end} UTC  (by {col})\n")

    hits = []
    for db in dbs:
        try:
            conn = ro(db)
        except sqlite3.Error as exc:
            print(f"  {os.path.basename(db)}: {exc}", file=sys.stderr)
            continue
        with conn:
            names = {r[0] for r in conn.execute(
                "select name from sqlite_master where type='table'")}
            if "Files" not in names:
                continue
            q = (f"select SourcePath, SizeBytes, {col}, Version, IsDeleted "
                 f"from Files where {col} >= ? and {col} <= ?")
            rows = conn.execute(q, (start.isoformat(sep=" "), end.isoformat(sep=" "))).fetchall()
            if not rows:
                rows = conn.execute(q, (start.isoformat(), end.isoformat())).fetchall()
            for path, size, when, ver, deleted in rows:
                if vol and not path.lower().startswith(vol):
                    continue
                hits.append((size or 0, when, path, ver, deleted,
                             os.path.basename(db)))

    total = sum(h[0] for h in hits)
    print(f"{len(hits):,} file versions in window, {total/1e9:.3f} GB\n")

    by_dir = {}
    for size, _, path, _, _, _ in hits:
        d = os.path.dirname(path)
        by_dir[d] = by_dir.get(d, 0) + size

    print(f"--- largest {args.top} ---")
    for size, when, path, ver, deleted, db in sorted(hits, reverse=True)[: args.top]:
        flag = " DELETED" if deleted else ""
        print(f"  {size/1e6:10.1f} MB  {when}  v{ver}{flag}  {path}  [{db}]")

    print(f"\n--- top {args.top} directories ---")
    for d, size in sorted(by_dir.items(), key=lambda kv: -kv[1])[: args.top]:
        print(f"  {size/1e6:10.1f} MB  {d}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
