r"""
Purge NTFS volume-metadata ($Extend) records and their backed-up bytes.

Background
----------
$Extend\$Deleted is where NTFS parks files that are open-but-unlinked. It is
volume metadata, invisible in Explorer, and nothing in it is restorable or
meaningful -- almost all of what got captured here is Windows Defender
definition files (mpasbase.vdm etc.) caught mid-deletion.

It was never an explicit selection in any backup set; it got swept in because
C:\ and D:\ are fully checked and it enumerates as a subdirectory. v1.0.51
(commit 04dcbf4) added the IsVolumeMetadata exclusion so nothing new is
captured. This script removes what was captured before that.

Order of operations matters
---------------------------
Physical file first, catalog row only after the file is confirmed gone. Doing
it the other way round recreates the "catalog-deleted but bytes still on disk"
inconsistency that harden-retention (a072e58) exists to prevent.

Safety properties verified before this was written, for these 950 rows:
  * IsDeduped = 0 for all      -> no DeduplicationBlocks.ReferenceCount to decrement
  * IsSplit / IsZipped = 0     -> no multi-part reassembly to consider
  * 0 FileChunks rows          -> no orphan chunk rows left behind
  * .fileref rows point at an already-backed-up REAL file elsewhere in the
    destination (ContentPath), not a refcounted _filestore blob, so deleting
    the manifest orphans nothing.

Usage:
    python purge_extend.py --dry-run     # report only, no mutations
    python purge_extend.py --commit      # do it
"""

import argparse
import json
import os
import sqlite3
import sys
import time

SETS_DIR = r"C:\ProgramData\LithicBackup\sets"
SET_IDS = (4, 11)

# Matches C:\$Extend, D:\$Extend and everything beneath them, and nothing else.
# Verified to cover 100% of rows whose SourcePath contains "$Extend".
WHERE = (
    r"SourcePath LIKE 'C:\$Extend\%'"
    r" OR SourcePath LIKE 'D:\$Extend\%'"
    r" OR SourcePath IN ('C:\$Extend', 'D:\$Extend')"
)


def resolve_disc_file(label, disc_path):
    """
    Physical location of a record's bytes on the destination.

    DiscPath is inconsistent about the .fileref suffix -- some fileref rows
    carry it, some don't -- so try both rather than trusting IsFileRef.
    """
    base = os.path.join(label, disc_path)
    for cand in (base, base + ".fileref"):
        if os.path.isfile(cand):
            return cand
    return None


def main():
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--dry-run", action="store_true")
    g.add_argument("--commit", action="store_true")
    args = ap.parse_args()

    grand = dict(rows=0, deleted=0, missing=0, failed=0, bytes=0)

    for sid in SET_IDS:
        db = os.path.join(SETS_DIR, f"set-{sid}.db")
        mode = "ro" if args.dry_run else "rw"
        con = sqlite3.connect(f"file:{db.replace(os.sep, '/')}?mode={mode}", uri=True, timeout=30)
        print(f"=== set-{sid} ===")

        # Guard: no real (non-$Extend) source may live under a $Extend disc dir,
        # or deleting the tree would take a legitimate backup with it.
        bad = con.execute(
            "SELECT COUNT(*) FROM Files WHERE DiscPath LIKE '%$Extend%' AND NOT (" + WHERE + ")"
        ).fetchone()[0]
        if bad:
            print(f"  ABORT: {bad} non-$Extend rows live under a $Extend disc dir")
            return 1
        print("  guard ok: no non-$Extend source lives under a $Extend disc dir")

        rows = con.execute(
            "SELECT f.Id, f.DiscId, f.DiscPath, f.SourcePath, d.Label "
            "FROM Files f JOIN Discs d ON f.DiscId = d.Id WHERE " + WHERE
        ).fetchall()
        print(f"  matching rows: {len(rows):,}")

        # Snapshot the rows before touching anything, so the catalog side is
        # reversible even though the bytes are not.
        if args.commit:
            snap = os.path.join(
                SETS_DIR, f"purged-extend-set-{sid}-{time.strftime('%Y%m%d_%H%M%S')}.json"
            )
            full = con.execute("SELECT * FROM Files WHERE " + WHERE).fetchall()
            cols = [d[0] for d in con.execute("SELECT * FROM Files LIMIT 0").description]
            with open(snap, "w", encoding="utf-8") as fh:
                json.dump({"columns": cols, "rows": full}, fh)
            print(f"  catalog snapshot -> {snap}")

        deleted = missing = failed = 0
        freed = 0
        per_disc = {}
        ok_ids = []

        for fid, did, dpath, spath, label in rows:
            phys = resolve_disc_file(label, dpath)
            if phys is None:
                missing += 1
                ok_ids.append(fid)  # nothing on disk; row is safe to drop
                continue
            size = os.path.getsize(phys)
            if args.dry_run:
                deleted += 1
                freed += size
                per_disc[did] = per_disc.get(did, 0) + size
                ok_ids.append(fid)
                continue
            try:
                os.chmod(phys, 0o666)  # read-only attr would silently fail the unlink
            except OSError:
                pass
            try:
                os.remove(phys)
            except OSError as exc:
                failed += 1
                print(f"    could not delete {phys!r}: {exc}")
                continue  # row stays -- never mark deleted while bytes remain
            deleted += 1
            freed += size
            per_disc[did] = per_disc.get(did, 0) + size
            ok_ids.append(fid)

        print(f"  files deleted: {deleted:,}   already gone: {missing:,}   failed: {failed:,}")
        print(f"  bytes reclaimed: {freed / 1e9:.3f} GB")

        if args.commit and ok_ids:
            con.execute("BEGIN")
            con.executemany("DELETE FROM Files WHERE Id = ?", [(i,) for i in ok_ids])
            # Keep Discs.BytesUsed honest so capacity planning isn't skewed.
            for did, nbytes in per_disc.items():
                con.execute(
                    "UPDATE Discs SET BytesUsed = MAX(0, BytesUsed - ?) WHERE Id = ?", (nbytes, did)
                )
            con.commit()
            left = con.execute("SELECT COUNT(*) FROM Files WHERE " + WHERE).fetchone()[0]
            print(f"  catalog rows removed: {len(ok_ids):,}   remaining $Extend rows: {left}")

        con.close()

        grand["rows"] += len(rows)
        grand["deleted"] += deleted
        grand["missing"] += missing
        grand["failed"] += failed
        grand["bytes"] += freed
        print()

    print(
        "TOTAL rows={rows:,}  files deleted={deleted:,}  already gone={missing:,} "
        " failed={failed:,}  reclaimed={gb:.3f} GB".format(gb=grand["bytes"] / 1e9, **grand)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
