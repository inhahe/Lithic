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

--cow answers a third, narrower question: how much did this churn cost a VSS
shadow copy? Not the same number, and usually far smaller. A shadow copy's diff
area only grows when *existing* blocks are overwritten, because only then is
there old content to preserve. A log file that is appended to allocates fresh
clusters and costs the diff area nothing -- yet Lithic still records a whole new
version of it, since Lithic re-copies any file whose mtime moved. So summing
version sizes wildly over-attributes diff-area growth to append-only logs.

--cow separates them per file by walking its versions in order: a version whose
size grew is treated as an append (charging only a rewrite of the file's tail),
while one that shrank or stayed the same size is an in-place rewrite and is
charged in full. This is still an estimate -- it cannot see a same-size in-place
edit of one block, and it cannot see delete-then-reallocate churn at all (freed
clusters that NTFS hands to a new file must also be preserved while a snapshot
holds them) -- but it separates the two populations, which the raw sum does not.

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
    ap.add_argument("--cow", action="store_true",
                    help="estimate VSS diff-area cost, separating appends from "
                         "in-place rewrites")
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

    if args.cow:
        report_cow(dbs, col, start, end, vol, args.top)
    return 0


CLUSTER = 4096


def report_cow(dbs, col, start, end, vol, top):
    """Split the churn into the part a shadow copy pays for and the part it doesn't."""
    # A path is normally present in several sets; take whichever set recorded the
    # most versions of it, rather than summing sets and counting the same write
    # once per backup destination.
    best = {}
    for db in dbs:
        with ro(db) as conn:
            names = {r[0] for r in conn.execute(
                "select name from sqlite_master where type='table'")}
            if "Files" not in names:
                continue
            per_path = {}
            for path, size, ver, when in conn.execute(
                    f"select SourcePath, SizeBytes, Version, {col} from Files "
                    f"order by SourcePath, Version"):
                if vol and not path.lower().startswith(vol):
                    continue
                per_path.setdefault(path, []).append((ver, size or 0, when))
            for path, versions in per_path.items():
                if len(versions) > len(best.get(path, ())):
                    best[path] = versions

    lo, hi = start.isoformat(), end.isoformat()
    appended = {}
    rewritten = {}
    for path, versions in best.items():
        for (_, prev_size, _), (_, size, when) in zip(versions, versions[1:]):
            if not (when and lo <= when.replace(" ", "T") <= hi + "\uffff"):
                continue
            if size > prev_size:
                # Grew: the new bytes land in freshly allocated clusters, which
                # have no prior contents to preserve. Only the cluster holding
                # the old end-of-file is genuinely overwritten.
                appended[path] = appended.get(path, 0) + (size - prev_size)
            else:
                # Same size or smaller: rewritten in place (or truncated and
                # refilled). Charge the old extent -- an upper bound.
                rewritten[path] = rewritten.get(path, 0) + prev_size

    grown = sum(appended.values())
    cow = sum(rewritten.values()) + len(appended) * CLUSTER
    print(f"\n--- shadow-copy cost estimate ---")
    print(f"  appended (free to a snapshot):   {grown/1e9:8.3f} GB "
          f"across {len(appended):,} files")
    print(f"  rewritten in place (costs CoW):  {cow/1e9:8.3f} GB "
          f"across {len(rewritten):,} files")
    print(f"\n--- top {top} by shadow-copy cost ---")
    for path, size in sorted(rewritten.items(), key=lambda kv: -kv[1])[:top]:
        print(f"  {size/1e6:10.1f} MB  {path}")


if __name__ == "__main__":
    sys.exit(main())
