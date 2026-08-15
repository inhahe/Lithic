r"""
Post-purge sweep and verification for purge_extend.py.

Removes the now-empty $Extend directory skeletons left on the destinations and
confirms that nothing -- catalog row or byte on disk -- survived.
"""

import os
import sqlite3

TREES = [
    r"I:\lithicbackup\C\$Extend",
    r"I:\lithicbackup\D\$Extend",
    r"I:\lithicbackup\C_prev\$Extend",
    r"I:\lithicbackup\D_prev\$Extend",
    r"J:\backup4all\Mirror backup to external SSD (J)(1)\C\$Extend",
    r"J:\backup4all\Mirror backup to external SSD (J)(1)\D\$Extend",
]

WHERE = (
    r"SourcePath LIKE 'C:\$Extend\%'"
    r" OR SourcePath LIKE 'D:\$Extend\%'"
    r" OR SourcePath IN ('C:\$Extend', 'D:\$Extend')"
)

print("--- leftover files on destination ---")
total_files = total_bytes = 0
for tree in TREES:
    if not os.path.isdir(tree):
        print(f"  {tree:<62} (gone)")
        continue
    n = b = 0
    for root, _dirs, files in os.walk(tree):
        for f in files:
            try:
                b += os.path.getsize(os.path.join(root, f))
                n += 1
            except OSError:
                pass
    total_files += n
    total_bytes += b
    print(f"  {tree:<62} {n:>5} files {b/1e9:8.3f} GB")

print("\n--- removing empty directories ---")
removed = 0
for tree in TREES:
    if not os.path.isdir(tree):
        continue
    # Deepest-first so parents empty out as children go.
    for root, dirs, _files in os.walk(tree, topdown=False):
        for d in dirs:
            p = os.path.join(root, d)
            try:
                os.rmdir(p)
                removed += 1
            except OSError:
                pass
    try:
        os.rmdir(tree)
        removed += 1
        print(f"  removed {tree}")
    except OSError as exc:
        print(f"  kept    {tree}  ({exc.strerror})")
print(f"  directories removed: {removed}")

print("\n--- catalog ---")
for sid in (4, 11):
    con = sqlite3.connect(
        f"file:C:/ProgramData/LithicBackup/sets/set-{sid}.db?mode=ro", uri=True
    )
    strict = con.execute("SELECT COUNT(*) FROM Files WHERE " + WHERE).fetchone()[0]
    loose = con.execute(
        "SELECT COUNT(*) FROM Files WHERE SourcePath LIKE '%$Extend%'"
    ).fetchone()[0]
    chunks = con.execute(
        "SELECT COUNT(*) FROM FileChunks WHERE FileRecordId NOT IN (SELECT Id FROM Files)"
    ).fetchone()[0]
    integrity = con.execute("PRAGMA quick_check").fetchone()[0]
    print(
        f"  set-{sid}: $Extend rows={strict} (loose match={loose})  "
        f"orphan FileChunks={chunks}  quick_check={integrity}"
    )
    con.close()

print(f"\nleftover bytes under $Extend trees: {total_bytes/1e9:.3f} GB in {total_files} files")
