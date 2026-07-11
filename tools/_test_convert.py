#!/usr/bin/env python3
r"""End-to-end test harness for lithic_convert_to_new_format.py.

Builds a synthetic OLD-format backup tree + matching per-set SQLite catalog,
then exercises: dry-run, real conversion, mid-run interruption + resume,
idempotent re-run, restore round-trip, and catalog-flip correctness.

Run:  python _test_convert.py
"""

import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
CONVERTER = os.path.join(HERE, "lithic_convert_to_new_format.py")
RESTORE = os.path.join(HERE, "lithic_restore.py")
SET_ID = 99

sys.path.insert(0, HERE)
import lithic_convert_to_new_format as conv  # noqa: E402

FAILURES = []


def check(cond, msg):
    status = "ok  " if cond else "FAIL"
    print(f"  [{status}] {msg}")
    if not cond:
        FAILURES.append(msg)


def sha256_bytes(b):
    return hashlib.sha256(b).hexdigest()


def write_old_fileref(path, original_name, content):
    """Old 3-field fileref."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    manifest = {
        "OriginalName": original_name,
        "OriginalSize": len(content),
        "Hash": sha256_bytes(content),
    }
    with open(path, "w", encoding="utf-8") as f:
        f.write(json.dumps(manifest, indent=2))


def build_tree(root):
    """Create an old-format tree. Returns dict hash -> bytes and a list of
    (tree_rel_of_fileref, original_name, content) describing every fileref."""
    filestore = os.path.join(root, "_filestore")
    os.makedirs(filestore, exist_ok=True)

    # Content blobs.
    cA = b"AAAA content alpha " * 100      # referenced by 3 files (2 current, 1 prev)
    cB = b"BBBB content bravo " * 50       # referenced by 1 file (unique)
    cC = b"CCCC content charlie " * 200    # referenced by 2 files, both _prev only
    cD = b"DDDD content delta " * 10       # orphan blob (no fileref)
    contents = {sha256_bytes(c): c for c in (cA, cB, cC, cD)}

    refs = []  # (fileref_tree_rel, original_name, content)

    def add(rel, name, content):
        full = os.path.join(root, rel.replace("/", os.sep))
        write_old_fileref(full, name, content)
        refs.append((rel.replace("/", "\\"), name, content))

    # Hash A: two current copies + one prev copy.
    add("C/mIRC/cacert.pem.fileref", "cacert.pem", cA)
    add("C/dup/cacert_copy.pem.fileref", "cacert_copy.pem", cA)
    add("C_prev/mIRC/cacert.pem.v2.fileref", "cacert.pem", cA)

    # Hash B: single unique file.
    add("C/docs/readme.txt.fileref", "readme.txt", cB)

    # Hash C: only prev copies (no current anchor).
    add("D_prev/old/data.bin.v5.fileref", "data.bin", cC)
    add("D_prev/old/data_again.bin.v1.fileref", "data_again.bin", cC)

    # Place all referenced blobs in _filestore (A, B, C). D is the orphan.
    for c in (cA, cB, cC, cD):
        h = sha256_bytes(c)
        with open(os.path.join(filestore, h + ".dat"), "wb") as f:
            f.write(c)

    return contents, refs


def build_catalog(catalog_path, backup_dir, refs):
    """Create master catalog.db (BackupSets) + sets/set-99.db (Files), with
    every fileref as an IsFileRef=1 row whose DiscPath includes '.fileref'."""
    os.makedirs(os.path.dirname(catalog_path), exist_ok=True)
    con = sqlite3.connect(catalog_path)
    con.executescript(
        """
        CREATE TABLE BackupSets (
            Id INTEGER PRIMARY KEY,
            Name TEXT,
            JobOptionsJson TEXT
        );
        """
    )
    opts = json.dumps({"TargetDirectory": backup_dir})
    con.execute("INSERT INTO BackupSets (Id, Name, JobOptionsJson) VALUES (?,?,?)",
                (SET_ID, "test set", opts))
    con.commit()
    con.close()

    set_db = os.path.join(os.path.dirname(catalog_path), "sets", f"set-{SET_ID}.db")
    os.makedirs(os.path.dirname(set_db), exist_ok=True)
    schema = os.path.join(HERE, "..", "src", "LithicBackup.Infrastructure",
                          "Data", "SetSchema.sql")
    con = sqlite3.connect(set_db)
    with open(schema, "r", encoding="utf-8") as f:
        con.executescript(f.read())
    con.execute(
        "INSERT INTO Discs (Id, BackupSetId, Label, SequenceNumber, MediaType, "
        "FilesystemType, Capacity, CreatedUtc) VALUES (1, ?, 'disc1', 1, 0, 0, 0, "
        "'2020-01-01T00:00:00Z')", (SET_ID,))
    for tree_rel, name, content in refs:
        h = sha256_bytes(content)
        # OLD catalog rows: DiscPath ends in .fileref, IsFileRef=1, real size+hash.
        con.execute(
            "INSERT INTO Files (DiscId, SourcePath, DiscPath, SizeBytes, Hash, "
            "IsFileRef, Version, SourceLastWriteUtc, BackedUpUtc) "
            "VALUES (1, ?, ?, ?, ?, 1, 1, ?, ?)",
            ("src::" + name, tree_rel, len(content), h,
             "2020-01-01T00:00:00Z", "2020-01-02T00:00:00Z"))
    con.commit()
    con.close()
    return set_db


def run_converter(backup_dir, catalog_path, extra=None):
    cmd = [sys.executable, CONVERTER, backup_dir, "--catalog", catalog_path]
    if extra:
        cmd += extra
    return subprocess.run(cmd, capture_output=True, text=True)


def catalog_rows(set_db):
    con = sqlite3.connect(set_db)
    rows = con.execute(
        "SELECT DiscPath, IsFileRef, Hash, SizeBytes FROM Files "
        "ORDER BY DiscPath").fetchall()
    con.close()
    return rows


def count_indexes(set_db):
    con = sqlite3.connect(set_db)
    names = [r[0] for r in con.execute(
        "SELECT name FROM sqlite_master WHERE type='index'").fetchall()]
    con.close()
    return names


def main():
    base = tempfile.mkdtemp(prefix="lithicconv_test_")
    print(f"workdir: {base}")
    try:
        # ----------------------------------------------------------------
        print("\n[setup] build old-format tree + catalog")
        backup_dir = os.path.join(base, "dest")
        catalog_path = os.path.join(base, "catalog", "catalog.db")
        contents, refs = build_tree(backup_dir)
        set_db = build_catalog(catalog_path, backup_dir, refs)
        check(len(catalog_rows(set_db)) == 6, "catalog seeded with 6 fileref rows")
        check(all(r[1] == 1 for r in catalog_rows(set_db)),
              "all catalog rows start as IsFileRef=1")

        # ----------------------------------------------------------------
        print("\n[1] dry-run writes nothing")
        before_files = set(_walk(backup_dir))
        before_rows = catalog_rows(set_db)
        r = run_converter(backup_dir, catalog_path, ["--dry-run", "-v"])
        check(r.returncode == 0, "dry-run exit 0")
        check(set(_walk(backup_dir)) == before_files, "dry-run left tree unchanged")
        check(catalog_rows(set_db) == before_rows, "dry-run left catalog unchanged")
        check(os.path.isdir(os.path.join(backup_dir, "_filestore")),
              "dry-run kept _filestore")

        # ----------------------------------------------------------------
        print("\n[2] real conversion")
        r = run_converter(backup_dir, catalog_path, ["-v"])
        check(r.returncode == 0, f"convert exit 0 (got {r.returncode})")
        if r.returncode != 0:
            print(r.stdout)
            print(r.stderr)

        # Anchor for hash A: current-tree, min tree_rel lower ->
        # 'C/dup/cacert_copy.pem' (C/dup < C/mIRC). It should be a plain file.
        anchorA = os.path.join(backup_dir, "C", "dup", "cacert_copy.pem")
        check(os.path.isfile(anchorA), "hash-A anchor placed as plain file (C/dup)")
        check(not os.path.exists(anchorA + ".fileref"),
              "hash-A anchor .fileref deleted")
        with open(anchorA, "rb") as f:
            check(sha256_bytes(f.read()) in contents, "hash-A anchor bytes intact")

        # The other current copy is now a dupe fileref pointing at the anchor.
        dupe = os.path.join(backup_dir, "C", "mIRC", "cacert.pem.fileref")
        check(os.path.isfile(dupe), "hash-A current dupe rewritten as fileref")
        if os.path.isfile(dupe):
            with open(dupe, encoding="utf-8") as f:
                m = json.load(f)
            check(set(m) == {"OriginalName", "OriginalSize", "Hash",
                             "SourcePath", "ContentPath"},
                  "dupe fileref has 5 new-format fields")
            check(m["ContentPath"] == r"C\dup\cacert_copy.pem",
                  f"dupe ContentPath points at anchor (got {m.get('ContentPath')})")
            check(m["SourcePath"] == r"C:\mIRC\cacert.pem",
                  f"dupe SourcePath reconstructed (got {m.get('SourcePath')})")

        # prev copy of A also a dupe fileref.
        prevdupe = os.path.join(backup_dir, "C_prev", "mIRC", "cacert.pem.v2.fileref")
        check(os.path.isfile(prevdupe), "hash-A prev dupe still a fileref")
        if os.path.isfile(prevdupe):
            with open(prevdupe, encoding="utf-8") as f:
                m = json.load(f)
            check(m.get("SourcePath") == r"C:\mIRC\cacert.pem",
                  f"prev dupe SourcePath strips _prev + version (got {m.get('SourcePath')})")

        # Hash B: single file becomes plain, no fileref left.
        plainB = os.path.join(backup_dir, "C", "docs", "readme.txt")
        check(os.path.isfile(plainB), "hash-B unique file placed as plain")
        check(not os.path.exists(plainB + ".fileref"), "hash-B fileref deleted")

        # Hash C: prev-only. Anchor = min tree_rel lower among prev ->
        # 'D_prev/old/data.bin.v5' (the versioned name keeps its .v5, matching
        # the catalog DiscPath with .fileref stripped).
        anchorC = os.path.join(backup_dir, "D_prev", "old", "data.bin.v5")
        check(os.path.isfile(anchorC), "hash-C anchor placed (prev-only group)")
        dupeC = os.path.join(backup_dir, "D_prev", "old", "data_again.bin.v1.fileref")
        check(os.path.isfile(dupeC), "hash-C dupe rewritten as fileref")

        # Orphan D: blob remains, _filestore retained.
        rows = catalog_rows(set_db)
        # Anchors flipped: A(1) + B(1) + C(1) = 3 rows now plain.
        plain_rows = [row for row in rows if row[1] == 0]
        ref_rows = [row for row in rows if row[1] == 1]
        check(len(plain_rows) == 3,
              f"3 catalog rows flipped to plain (got {len(plain_rows)})")
        check(len(ref_rows) == 3,
              f"3 catalog rows remain fileref (got {len(ref_rows)})")
        # Flipped DiscPaths no longer end in .fileref.
        check(all(not row[0].lower().endswith(".fileref") for row in plain_rows),
              "flipped rows have .fileref stripped from DiscPath")
        check(all(row[0].lower().endswith(".fileref") for row in ref_rows),
              "dupe rows keep .fileref in DiscPath")
        # Size + hash preserved on flipped rows.
        check(all(row[3] > 100 and len(row[2]) == 64 for row in plain_rows),
              "flipped rows keep real SizeBytes + Hash")

        # Conversion index cleaned up.
        check("ix_lithicconv_discpath" not in count_indexes(set_db),
              "temp conversion index dropped after finish")

        # ----------------------------------------------------------------
        print("\n[3] idempotent re-run (no-op)")
        files_after = dict(_walk_with_content(backup_dir))
        rows_after = catalog_rows(set_db)
        r = run_converter(backup_dir, catalog_path, ["-v"])
        check(r.returncode == 0, f"re-run exit 0 (got {r.returncode})")
        check(dict(_walk_with_content(backup_dir)) == files_after,
              "re-run left tree byte-identical")
        check(catalog_rows(set_db) == rows_after, "re-run left catalog identical")
        check("flipped" in r.stdout.lower() and "0" in r.stdout,
              "re-run reports near-zero new work")

        # ----------------------------------------------------------------
        print("\n[4] restore round-trip")
        restore_dir = os.path.join(base, "restored")
        rr = subprocess.run(
            [sys.executable, RESTORE, backup_dir,
             "--map", f"C={os.path.join(restore_dir, 'C')}",
             "--map", f"D={os.path.join(restore_dir, 'D')}",
             "--prev", "--overwrite", "-v"],
            capture_output=True, text=True)
        if rr.returncode != 0:
            print("  (restore tool output)")
            print(rr.stdout[-2000:])
            print(rr.stderr[-2000:])
        check(rr.returncode == 0, f"restore exit 0 (got {rr.returncode})")
        # Verify a couple of restored files match original content by hash.
        _check_restored(restore_dir, contents)

        # ----------------------------------------------------------------
        print("\n[5] interruption + resume")
        _interruption_test(base, contents)

        # ----------------------------------------------------------------
        print("\n[6] --no-catalog mode")
        _no_catalog_test(base)

        # ----------------------------------------------------------------
        print("\n[7] --recover-from-source")
        _recovery_test(base)

        # ----------------------------------------------------------------
        print("\n[8] --recover-from-source crash + resume")
        _recovery_resume_test(base)

        # ----------------------------------------------------------------
        print("\n[9] redundant blob (plain anchor pre-exists) reclaim")
        _redundant_blob_test(base)

        # ----------------------------------------------------------------
        print("\n[10] *.dedup DATA file does not block conversion")
        _dedup_data_not_blocked_test(base)

    finally:
        shutil.rmtree(base, ignore_errors=True)

    print("\n" + "=" * 60)
    if FAILURES:
        print(f"FAILED ({len(FAILURES)}):")
        for f in FAILURES:
            print(f"  - {f}")
        sys.exit(1)
    print("ALL TESTS PASSED")
    sys.exit(0)


def _walk(root):
    out = []
    for r, d, files in os.walk(root):
        for f in files:
            out.append(os.path.relpath(os.path.join(r, f), root))
    return out


def _walk_with_content(root):
    out = []
    for r, d, files in os.walk(root):
        for f in files:
            full = os.path.join(r, f)
            with open(full, "rb") as fh:
                out.append((os.path.relpath(full, root), fh.read()))
    return out


def _check_restored(restore_dir, contents):
    found = 0
    for r, d, files in os.walk(restore_dir):
        for f in files:
            with open(os.path.join(r, f), "rb") as fh:
                if sha256_bytes(fh.read()) in contents:
                    found += 1
    check(found >= 4, f"restored files match original content ({found} matched)")


def _interruption_test(base, contents):
    """Rebuild fresh, simulate a crash AFTER the anchor blob is moved and the
    dupe is rewritten but BEFORE the anchor .fileref is deleted / catalog flips
    commit. Then re-run and confirm full recovery."""
    backup_dir = os.path.join(base, "dest2")
    catalog_path = os.path.join(base, "catalog2", "catalog.db")
    contents2, refs = build_tree(backup_dir)
    set_db = build_catalog(catalog_path, backup_dir, refs)

    # Manually emulate a partial conversion of hash A:
    #   anchor C/dup/cacert_copy.pem placed (blob moved), its .fileref still
    #   present, NO dupes rewritten yet, NO catalog flip yet.
    cA = next(c for c in contents2.values()
              if sha256_bytes(c) == sha256_bytes(c) and len(c) > 1000)
    # find hash A content (referenced 3x): the alpha blob
    hA = None
    counts = {}
    for _, _, content in refs:
        counts[sha256_bytes(content)] = counts.get(sha256_bytes(content), 0) + 1
    hA = max(counts, key=counts.get)
    blobA = os.path.join(backup_dir, "_filestore", hA + ".dat")
    anchorA = os.path.join(backup_dir, "C", "dup", "cacert_copy.pem")
    os.replace(blobA, anchorA)  # blob moved; anchor .fileref intact; nothing else

    # Now run the converter to completion (the "restart" after a crash).
    r = run_converter(backup_dir, catalog_path, ["-v"])
    check(r.returncode == 0, f"resume run exit 0 (got {r.returncode})")
    check(os.path.isfile(anchorA), "resume: pre-placed anchor preserved")
    check(not os.path.exists(anchorA + ".fileref"),
          "resume: anchor .fileref now deleted")
    dupe = os.path.join(backup_dir, "C", "mIRC", "cacert.pem.fileref")
    check(os.path.isfile(dupe), "resume: dupe rewritten as fileref")
    # catalog: anchor row should be flipped to plain.
    con = sqlite3.connect(set_db)
    rowA = con.execute(
        "SELECT IsFileRef FROM Files WHERE DiscPath=?",
        (r"C\dup\cacert_copy.pem",)).fetchone()
    con.close()
    check(rowA is not None and rowA[0] == 0,
          "resume: anchor catalog row flipped to plain")

    # Now simulate a HARDER crash: anchor blob moved AND dupe rewritten AND
    # anchor .fileref deleted, but catalog flip lost. Re-run must re-flip via
    # the dupe's ContentPath (no blob, no anchor .fileref).
    backup_dir3 = os.path.join(base, "dest3")
    catalog_path3 = os.path.join(base, "catalog3", "catalog.db")
    contents3, refs3 = build_tree(backup_dir3)
    set_db3 = build_catalog(catalog_path3, backup_dir3, refs3)
    # full file conversion, but with --no-catalog so catalog stays old
    r = run_converter(backup_dir3, catalog_path3, ["--no-catalog"])
    check(r.returncode == 0, "stage harder-crash: files converted, catalog untouched")
    con = sqlite3.connect(set_db3)
    n_ref_before = con.execute(
        "SELECT COUNT(*) FROM Files WHERE IsFileRef=1").fetchone()[0]
    con.close()
    check(n_ref_before == 6, "harder-crash: catalog still all filerefs")
    # Now run WITH catalog: blobs are gone, anchor filerefs gone; must recover
    # anchors via dupe ContentPath / catalog hash fallback and flip rows.
    r = run_converter(backup_dir3, catalog_path3, ["-v"])
    check(r.returncode == 0, f"harder-crash recover exit 0 (got {r.returncode})")
    check("MISSING CONTENT" not in r.stdout,
          "harder-crash: no missing content on recovery")
    con = sqlite3.connect(set_db3)
    n_plain = con.execute(
        "SELECT COUNT(*) FROM Files WHERE IsFileRef=0").fetchone()[0]
    con.close()
    check(n_plain == 3,
          f"harder-crash: catalog re-flipped 3 anchors after lost commit "
          f"(got {n_plain})")


def _row_id(set_db, source_path):
    con = sqlite3.connect(set_db)
    rid = con.execute("SELECT Id FROM Files WHERE SourcePath=?",
                      (source_path,)).fetchone()[0]
    con.close()
    return rid


def _make_set_db(catalog_path, backup_dir, rows):
    """Create master + per-set DB. `rows` is a list of dicts with keys:
    SourcePath, DiscPath, SizeBytes, Hash, IsFileRef, Version."""
    os.makedirs(os.path.dirname(catalog_path), exist_ok=True)
    con = sqlite3.connect(catalog_path)
    con.executescript(
        "CREATE TABLE BackupSets (Id INTEGER PRIMARY KEY, Name TEXT, "
        "JobOptionsJson TEXT);")
    con.execute("INSERT INTO BackupSets VALUES (?,?,?)",
                (SET_ID, "rec", json.dumps({"TargetDirectory": backup_dir})))
    con.commit()
    con.close()
    set_db = os.path.join(os.path.dirname(catalog_path), "sets", f"set-{SET_ID}.db")
    os.makedirs(os.path.dirname(set_db), exist_ok=True)
    schema = os.path.join(HERE, "..", "src", "LithicBackup.Infrastructure",
                          "Data", "SetSchema.sql")
    con = sqlite3.connect(set_db)
    with open(schema, encoding="utf-8") as f:
        con.executescript(f.read())
    con.execute(
        "INSERT INTO Discs (Id, BackupSetId, Label, SequenceNumber, MediaType, "
        "FilesystemType, Capacity, CreatedUtc) VALUES (1, ?, 'd', 1, 0, 0, 0, "
        "'2020-01-01T00:00:00Z')", (SET_ID,))
    for r in rows:
        con.execute(
            "INSERT INTO Files (DiscId, SourcePath, DiscPath, SizeBytes, Hash, "
            "IsFileRef, Version, SourceLastWriteUtc, BackedUpUtc) "
            "VALUES (1,?,?,?,?,?,?,?,?)",
            (r["SourcePath"], r["DiscPath"], r["SizeBytes"], r["Hash"],
             r["IsFileRef"], r["Version"], "2020-01-01T00:00:00Z",
             "2020-01-02T00:00:00Z"))
    con.commit()
    con.close()
    return set_db


def _recovery_test(base):
    """Catalog records files the destination is missing; recover current ones
    from source, skip retention, report source-also-gone, leave satisfied
    rows alone, and converge on re-run."""
    rec = os.path.join(base, "rec")
    src_dir = os.path.join(rec, "src")
    dest = os.path.join(rec, "dest")
    os.makedirs(src_dir, exist_ok=True)
    os.makedirs(dest, exist_ok=True)
    catalog_path = os.path.join(rec, "catalog", "catalog.db")

    def make_src(name, data):
        p = os.path.join(src_dir, name)
        with open(p, "wb") as f:
            f.write(data)
        return p

    def place_plain(disc_path, data):
        p = conv.disk_path(dest, disc_path)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)

    # S1: current, missing on disk, source CHANGED vs catalog hash -> recover.
    cur_now = b"NEW changed current bytes " * 30
    s1 = make_src("changed.bin", cur_now)
    # S2: current, already present on disk -> not recovered.
    s2data = b"satisfied plain " * 10
    s2 = make_src("keep.bin", s2data)
    # S3: has current(v2, present) + retention(v1, missing). Source exists, but
    # the missing row is retention -> skipped.
    s3data = b"retained source " * 12
    s3 = make_src("retainedsrc.bin", s3data)
    # S4: current, missing on disk AND source removed -> unrecoverable.
    s4 = make_src("gonelater.bin", b"will be deleted " * 8)
    # S5: current fileref dupe, missing its own copy but an anchor with the same
    # hash is present -> satisfied via hash.
    s5data = b"dupe content body " * 9
    h5 = sha256_bytes(s5data)
    # S6: current plain with an EMPTY Hash (NEW-format anchor / seeded row),
    # PRESENT on disk. Must be recognised as present by its own DiscPath, not by
    # hash. Regression guard: a purely hash-based check can never match an
    # empty-hash row, so it would wrongly "recover" (or delete) a file that is
    # sitting right there on disk.
    s6data = b"empty-hash anchor body " * 7
    s6 = make_src("emptyhash.bin", s6data)

    tree = conv.sourcepath_to_tree
    rows = [
        # A: current fileref, missing, OLD hash (source changed).
        dict(SourcePath=s1, DiscPath=tree(s1) + ".fileref", SizeBytes=999,
             Hash="0" * 64, IsFileRef=1, Version=1),
        # B: current plain, present.
        dict(SourcePath=s2, DiscPath=tree(s2), SizeBytes=len(s2data),
             Hash=sha256_bytes(s2data), IsFileRef=0, Version=1),
        # C2: current plain for S3, present.
        dict(SourcePath=s3, DiscPath=tree(s3), SizeBytes=len(s3data),
             Hash=sha256_bytes(s3data), IsFileRef=0, Version=2),
        # C1: retention (v1) for S3, missing on disk, with its own (older,
        # nowhere-available) content -> must fall through to the retention skip,
        # NOT be treated as present via the current version's hash.
        dict(SourcePath=s3, DiscPath=tree(s3) + ".v1",
             SizeBytes=len(b"older retained content"),
             Hash=sha256_bytes(b"older retained content"),
             IsFileRef=0, Version=1),
        # D: current fileref, missing, source will be deleted.
        dict(SourcePath=s4, DiscPath=tree(s4) + ".fileref", SizeBytes=10,
             Hash="1" * 64, IsFileRef=1, Version=1),
        # E: current fileref dupe (S5), missing own copy; anchor present.
        dict(SourcePath=os.path.join(src_dir, "dupe.bin"),
             DiscPath=tree(os.path.join(src_dir, "dupe.bin")) + ".fileref",
             SizeBytes=len(s5data), Hash=h5, IsFileRef=1, Version=1),
        # E-anchor: plain present with same hash as the dupe.
        dict(SourcePath=os.path.join(src_dir, "anchor5.bin"),
             DiscPath=tree(os.path.join(src_dir, "anchor5.bin")),
             SizeBytes=len(s5data), Hash=h5, IsFileRef=0, Version=1),
        # F: current plain, EMPTY hash, present on disk -> present via DiscPath.
        dict(SourcePath=s6, DiscPath=tree(s6), SizeBytes=len(s6data),
             Hash="", IsFileRef=0, Version=1),
    ]
    set_db = _make_set_db(catalog_path, dest, rows)

    # Put the "present" plain files on disk.
    place_plain(tree(s2), s2data)
    place_plain(tree(s3), s3data)
    place_plain(tree(os.path.join(src_dir, "anchor5.bin")), s5data)
    place_plain(tree(s6), s6data)  # empty-hash plain present on disk
    # Now delete S4's source so it is unrecoverable.
    os.remove(s4)

    # Dry run first: reports, writes nothing.
    before = catalog_rows(set_db)
    r = run_converter(dest, catalog_path, ["--recover-from-source", "--dry-run", "-v"])
    check(catalog_rows(set_db) == before, "recovery dry-run left catalog unchanged")
    check("Would recover from source        : 1" in r.stdout,
          "dry-run reports exactly 1 recoverable")
    check("Would delete from catalog (1)" in r.stdout,
          "dry-run reports 1 would-be catalog deletion")

    # Real recovery.
    s4_id = _row_id(set_db, s4)
    r = run_converter(dest, catalog_path, ["--recover-from-source", "-v"])
    check("Recovered from source        : 1" in r.stdout,
          f"recovered exactly 1 current file\n{r.stdout}")
    check("Retention versions skipped : 1" in r.stdout,
          "retention version skipped (not recovered from source)")
    check("Deleted from catalog (1)" in r.stdout,
          "source-also-gone file deleted from catalog")
    check(any(os.path.basename(s4) in line for line in r.stdout.splitlines()),
          "deleted source-gone file reported by path")
    # The unrecoverable row is now soft-deleted (IsDeleted=1).
    con = sqlite3.connect(set_db)
    s4_del = con.execute("SELECT IsDeleted FROM Files WHERE Id=?", (s4_id,)).fetchone()
    con.close()
    check(s4_del == (1,), f"source-gone row marked IsDeleted=1 (got {s4_del})")

    # S1 recovered to disk with CURRENT bytes; catalog row updated to plain.
    recovered_abs = conv.disk_path(dest, tree(s1))
    check(os.path.isfile(recovered_abs), "S1 recovered onto disk at current path")
    if os.path.isfile(recovered_abs):
        with open(recovered_abs, "rb") as f:
            check(f.read() == cur_now, "S1 recovered with the CURRENT source bytes")
    con = sqlite3.connect(set_db)
    rowA = con.execute(
        "SELECT IsFileRef, DiscPath, SizeBytes, Hash FROM Files "
        "WHERE SourcePath=?", (s1,)).fetchone()
    con.close()
    check(rowA == (0, tree(s1), len(cur_now), sha256_bytes(cur_now)),
          f"S1 catalog row updated to plain w/ real size+hash (got {rowA})")

    # The empty-hash plain row that is present on disk must be left completely
    # untouched (not recovered, not deleted, hash not invented).
    con = sqlite3.connect(set_db)
    rowF = con.execute(
        "SELECT IsFileRef, DiscPath, Hash, IsDeleted FROM Files "
        "WHERE SourcePath=?", (s6,)).fetchone()
    con.close()
    check(rowF == (0, tree(s6), "", 0),
          f"empty-hash present row untouched (got {rowF})")

    # Satisfied rows untouched; idempotent re-run recovers nothing.
    r2 = run_converter(dest, catalog_path, ["--recover-from-source", "-v"])
    check("Recovered from source        : 0" in r2.stdout,
          "re-run recovers 0 (converged)")


def _recovery_resume_test(base):
    """Crash-safety of --recover-from-source. Recovery stages each copy to a
    .lbtmp, commits the catalog row (plain, REAL hash) and only THEN renames the
    file into place. Emulate a crash in that window: the recovered bytes are
    staged in .lbtmp (NOT yet at the final path) and the catalog row is still
    the old fileref. A re-run must converge to the correct plain record with the
    REAL hash and no data loss; a further run cleans the now-dangling .fileref
    and is idempotent.

    Regression: with the old rename-before-commit ordering, a crash left new
    content at the anchor path while the catalog still said 'fileref, old hash',
    so the next convert adopted it under the STALE hash."""
    rec = os.path.join(base, "recres")
    src_dir = os.path.join(rec, "src")
    dest = os.path.join(rec, "dest")
    os.makedirs(src_dir, exist_ok=True)
    os.makedirs(dest, exist_ok=True)
    catalog_path = os.path.join(rec, "catalog", "catalog.db")

    src_bytes = b"recovered-after-crash payload " * 11
    real_hash = sha256_bytes(src_bytes)
    sp = os.path.join(src_dir, "midcrash.bin")
    with open(sp, "wb") as f:
        f.write(src_bytes)
    tree = conv.sourcepath_to_tree
    fileref_dp = tree(sp) + ".fileref"

    # Current fileref row, OLD hash, content (blob) gone -> "missing".
    rows = [dict(SourcePath=sp, DiscPath=fileref_dp, SizeBytes=999,
                 Hash="0" * 64, IsFileRef=1, Version=1)]
    set_db = _make_set_db(catalog_path, dest, rows)

    # On-disk crash state: the old-format .fileref still present, recovered
    # bytes staged in .lbtmp, NOT renamed into place, catalog flip lost.
    fileref_abs = conv.disk_path(dest, fileref_dp)
    os.makedirs(os.path.dirname(fileref_abs), exist_ok=True)
    with open(fileref_abs, "w", encoding="utf-8") as f:
        json.dump({"OriginalName": "midcrash.bin", "OriginalSize": 999,
                   "Hash": "0" * 64}, f)
    target_abs = conv.disk_path(dest, tree(sp))
    with open(target_abs + ".lbtmp", "wb") as f:
        f.write(b"half-written garbage to be overwritten")

    # First pass = the resume: re-copies from source, commits the row to a plain
    # record with the REAL hash, then lands the bytes. (Exit is non-zero here:
    # convert legitimately observes the gap before recovery fills it.)
    run_converter(dest, catalog_path, ["--recover-from-source", "-v"])
    con = sqlite3.connect(set_db)
    row = con.execute(
        "SELECT IsFileRef, DiscPath, SizeBytes, Hash, IsDeleted FROM Files "
        "WHERE SourcePath=?", (sp,)).fetchone()
    con.close()
    check(row == (0, tree(sp), len(src_bytes), real_hash, 0),
          f"resume-recover converged to plain w/ REAL hash (got {row})")
    check(os.path.isfile(target_abs), "resume-recover: bytes landed at final path")
    if os.path.isfile(target_abs):
        with open(target_abs, "rb") as f:
            check(f.read() == src_bytes, "resume-recover: content matches source")

    # Second pass: nothing left to recover; the dangling .fileref is cleaned.
    r2 = run_converter(dest, catalog_path, ["--recover-from-source", "-v"])
    check(r2.returncode == 0, f"second pass exit 0 (got {r2.returncode})")
    check("Recovered from source        : 0" in r2.stdout,
          "second pass recovers 0 (converged)")
    check(not os.path.exists(fileref_abs),
          "second pass cleaned the dangling .fileref")
    # The real hash survives the convert adopt-pass (flip is a no-op).
    con = sqlite3.connect(set_db)
    row2 = con.execute("SELECT Hash FROM Files WHERE SourcePath=?",
                       (sp,)).fetchone()
    con.close()
    check(row2 == (real_hash,), f"real hash preserved across passes (got {row2})")


def _no_catalog_test(base):
    backup_dir = os.path.join(base, "dest4")
    catalog_path = os.path.join(base, "catalog4", "catalog.db")
    contents, refs = build_tree(backup_dir)
    build_catalog(catalog_path, backup_dir, refs)
    set_db = os.path.join(base, "catalog4", "sets", f"set-{SET_ID}.db")
    rows_before = catalog_rows(set_db)
    r = run_converter(backup_dir, catalog_path, ["--no-catalog"])
    check(r.returncode == 0, f"--no-catalog exit 0 (got {r.returncode})")
    check(catalog_rows(set_db) == rows_before,
          "--no-catalog left catalog untouched")
    check(os.path.isfile(os.path.join(backup_dir, "C", "dup", "cacert_copy.pem")),
          "--no-catalog still converted files")


def _redundant_blob_test(base):
    """A mixed OLD tree where a hash group's anchor location ALREADY holds a
    plain file (so the converter never moves the blob out). The _filestore blob
    is then redundant - its content is in the plain anchor and every dupe will
    reference it. --delete-orphans must reclaim that blob; without the flag it is
    retained but reported. Regression test for the gap where such blobs were
    protected by used_hashes and left behind (J:/set-4 left 51 of them, 243 MB).
    """
    X = b"redundant mixed-tree content " * 100
    hx = sha256_bytes(X)
    dummy_catalog = os.path.join(base, "no_such_catalog", "catalog.db")

    def build(dest):
        fs = os.path.join(dest, "_filestore")
        os.makedirs(fs, exist_ok=True)
        # choose_anchor picks the lexicographically-smallest tree_rel. Use flat
        # names so 'C/a_anchor.bin' < 'C/z_dupe.bin' unambiguously and the anchor
        # is the file that ALREADY has a plain copy on disk (mixed old tree),
        # alongside its now-redundant .fileref.
        anchor_plain = os.path.join(dest, "C", "a_anchor.bin")
        os.makedirs(os.path.dirname(anchor_plain), exist_ok=True)
        with open(anchor_plain, "wb") as f:
            f.write(X)
        write_old_fileref(anchor_plain + ".fileref", "a_anchor.bin", X)
        # A genuine duplicate (must be rewritten to reference the anchor).
        write_old_fileref(os.path.join(dest, "C", "z_dupe.bin.fileref"),
                          "z_dupe.bin", X)
        # The redundant blob still sitting in _filestore.
        with open(os.path.join(fs, hx + ".dat"), "wb") as f:
            f.write(X)
        return fs

    # (a) WITHOUT --delete-orphans: blob retained but reported as reclaimable.
    dest_keep = os.path.join(base, "dest_redkeep")
    fs_keep = build(dest_keep)
    r = run_converter(dest_keep, dummy_catalog, ["--no-catalog", "-v"])
    check(r.returncode == 0, f"redundant/keep exit 0 (got {r.returncode})")
    check(os.path.isfile(os.path.join(fs_keep, hx + ".dat")),
          "redundant blob retained without --delete-orphans")
    check("1" in r.stdout and "blobs" in r.stdout.lower(),
          "redundant blob reported as reclaimable")
    # Conversion itself still happened: plain anchor kept, its .fileref gone,
    # dupe rewritten to a 5-field new-format reference.
    anchor_keep = os.path.join(dest_keep, "C", "a_anchor.bin")
    check(os.path.isfile(anchor_keep) and not os.path.exists(anchor_keep + ".fileref"),
          "anchor stays plain, its .fileref deleted (keep case)")
    dupe_keep = os.path.join(dest_keep, "C", "z_dupe.bin.fileref")
    check(os.path.isfile(dupe_keep), "dupe stays a fileref (keep case)")
    if os.path.isfile(dupe_keep):
        with open(dupe_keep, encoding="utf-8") as f:
            m = json.load(f)
        check(m.get("ContentPath") == r"C\a_anchor.bin",
              f"dupe references plain anchor (got {m.get('ContentPath')})")

    # (b) WITH --delete-orphans: redundant blob reclaimed, _filestore removed.
    dest_del = os.path.join(base, "dest_reddel")
    fs_del = build(dest_del)
    r = run_converter(dest_del, dummy_catalog, ["--no-catalog", "--delete-orphans", "-v"])
    check(r.returncode == 0, f"redundant/delete exit 0 (got {r.returncode})")
    check(not os.path.exists(os.path.join(fs_del, hx + ".dat")),
          "redundant blob removed with --delete-orphans")
    check(not os.path.isdir(fs_del),
          "_filestore removed once empty after reclaiming redundant blob")
    anchor_del = os.path.join(dest_del, "C", "a_anchor.bin")
    with open(anchor_del, "rb") as f:
        check(f.read() == X, "anchor content intact after redundant-blob reclaim")


def _dedup_data_not_blocked_test(base):
    """A file merely NAMED '*.dedup' (with no '_blocks' store) is ordinary
    backed-up data, not a block-dedup manifest, and must not block conversion.
    Regression test for the J:/set-4 false positive: a Worker backup swept a
    folder containing '*.dedup' data files into the destination, and the
    converter wrongly aborted. A real '_blocks' store must still abort.
    """
    dummy_catalog = os.path.join(base, "no_cat_dd", "catalog.db")

    # (a) *.dedup DATA file present, no _blocks -> conversion proceeds.
    dest = os.path.join(base, "dest_dedupdata")
    build_tree(dest)
    datafile = os.path.join(dest, "C", "data", "backup_of_old_dest.txt.dedup")
    os.makedirs(os.path.dirname(datafile), exist_ok=True)
    with open(datafile, "wb") as f:
        f.write(b'{"BlockHashes":["not-a-real-manifest"]}')
    r = run_converter(dest, dummy_catalog, ["--no-catalog", "-v"])
    check(r.returncode == 0,
          f"converts a tree containing *.dedup data (got {r.returncode}): "
          f"{(r.stderr or '').strip()[:120]}")
    check(os.path.isfile(datafile), "*.dedup data file left untouched")
    check(os.path.isfile(os.path.join(dest, "C", "dup", "cacert_copy.pem")),
          "conversion still proceeded (anchor placed)")

    # (b) A real _blocks store MUST still abort.
    dest2 = os.path.join(base, "dest_realblocks")
    build_tree(dest2)
    os.makedirs(os.path.join(dest2, "_blocks"), exist_ok=True)
    with open(os.path.join(dest2, "_blocks", "abc123.blk"), "wb") as f:
        f.write(b"block bytes")
    r = run_converter(dest2, dummy_catalog, ["--no-catalog"])
    check(r.returncode != 0, "still aborts when a real _blocks store is present")
    check("_blocks" in (r.stderr or "") + (r.stdout or ""),
          "abort message names the _blocks store")


if __name__ == "__main__":
    main()
