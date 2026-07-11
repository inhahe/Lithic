#!/usr/bin/env python3
r"""
lithic_convert_to_new_format.py - In-place upgrade of an OLD-format LithicBackup
destination tree (and its catalog) to the NEW format, without re-copying
anything from source.

WHY
  Early LithicBackup stored EVERY file as a .fileref pointer and kept the real
  bytes in a content-addressed "_filestore\<sha256>.dat" pool. The current
  format instead stores the first occurrence of each unique content as a plain,
  normally-named file IN PLACE, and only writes a .fileref for genuine
  duplicates (a second+ file with byte-identical content). This makes the tree
  directly usable (real files with real names) and lets restore/verify and
  future incremental backups work against ordinary files.

  This script performs that upgrade by MOVING (not copying) each content blob
  out of _filestore onto exactly one of the files that reference it - the
  "anchor" - and rewriting the remaining same-content .fileref files into the
  NEW 5-field format that points at the anchor. The move is a same-volume
  rename, so it is fast and needs no extra free space.

  It ALSO converts the LithicBackup catalog (SQLite) in the same pass, so you do
  NOT have to re-seed: for each anchor it flips that file's catalog row from a
  file-reference to a plain record (and points its DiscPath at the now-plain
  file). This is far faster than "Seed from existing backup", which re-walks and
  re-hashes the whole destination. The catalog edit is keyed by the anchor's
  stored path and is idempotent, so it stays correct across re-runs.

WHAT IT DOES, precisely
  Given a backup root containing top-level drive dirs (C, D, ...), optional
  "{drive}_prev" history dirs, and a "_filestore" pool:

    1. Scans every *.fileref in the tree and groups them by their Hash.
    2. For each hash group, picks an anchor (preferring a CURRENT-tree file
       over a _prev file, deterministically). It MOVES _filestore\<hash>.dat
       to the anchor's real path (stripping the .fileref suffix).
    3. Every other .fileref in the group is rewritten as a NEW-format fileref:
         { OriginalName, OriginalSize, Hash, SourcePath, ContentPath }
       where SourcePath is the reconstructed original path and ContentPath is
       the tree-relative path of the anchor's plain copy.
    4. Flips the anchor's catalog row to a plain record (IsFileRef=0, DiscPath
       without the .fileref suffix). Duplicate rows stay as file-references and
       resolve to the anchor by content hash, exactly as a fresh new-format
       backup would record them.
    5. Deletes the anchor's now-redundant .fileref (done LAST, so an interrupted
       run is always recoverable).
    6. Reports orphan blobs (.dat with no referencing fileref) and any groups
       whose content could not be located. If _filestore ends up empty it is
       removed.

SAFETY
  * Run with --dry-run first. It prints what would happen and writes nothing
    (no file moves, no catalog edits).
  * The blob->file mapping is keyed entirely by SHA-256 hash (the .dat filename
    IS the content hash and the .fileref records that same hash), so files can
    never be mixed up. Use --verify to additionally re-hash each blob before
    moving it (reads all data once; slower, but catches pre-existing
    corruption).
  * Crash-safe and resumable. Per hash group the order is: move the blob,
    rewrite the duplicates, flip the catalog row, then (only then) delete the
    anchor's .fileref. So after any interruption the anchor is always
    rediscoverable - either its own .fileref still exists, or a duplicate's
    ContentPath / the catalog points at the already-placed plain copy. The
    catalog edit is an idempotent UPDATE keyed by the anchor's stored path, so
    re-running re-applies anything a lost commit dropped and is a no-op once
    done. Re-running on a fully-converted tree changes nothing.
  * Catalog edits require exclusive access: CLOSE the LithicBackup app and STOP
    the "LithicBackup" Worker service first, or the database will be locked.
  * Block-dedup artifacts (_blocks, *.dedup) are detected and the run aborts if
    present (this converter does not handle them; these old trees never used
    block dedup).

USAGE
  python lithic_convert_to_new_format.py <backup_dir> [options]

OPTIONS
  --catalog PATH    Master catalog database (default:
                    C:\ProgramData\LithicBackup\catalog.db). The matching
                    backup set is found by its destination directory.
  --set-id N        Convert this set's catalog instead of auto-detecting it
                    from <backup_dir>.
  --no-catalog      Convert the destination files only; do not touch any
                    catalog. (You can rebuild the catalog later with "Seed from
                    existing backup".)
  --dry-run         Show what would change; write nothing.
  --verify          Re-hash each _filestore blob against its filename hash
                    before moving it (reads all blob bytes once).
  --delete-orphans  Delete _filestore blobs that no .fileref references
                    (default: leave them and just report the count).
  --recover-from-source
                    After converting, copy any CURRENT file that the catalog
                    records but the destination no longer has on disk back from
                    its original source path. The source is re-read and hashed,
                    so a file that CHANGED since the backup is stored with its
                    real current size/hash/mtime. Older retained versions can't
                    be reconstructed from the (current) source, so MISSING
                    RETENTION VERSIONS ARE LEFT OUT. A current file whose source
                    is ALSO gone was deliberately deleted at the source, so its
                    catalog row is marked deleted (IsDeleted=1). Requires a
                    catalog.
  -v, --verbose     Print every file as it is converted.
  -h, --help        Show this help.
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import sqlite3
import sys

FILESTORE_DIR = "_filestore"
BLOCK_STORE_DIR = "_blocks"
PREV_SUFFIX = "_prev"
READ_CHUNK = 1024 * 1024  # 1 MiB
DEFAULT_CATALOG = r"C:\ProgramData\LithicBackup\catalog.db"
CONV_INDEX = "ix_lithicconv_discpath"
_VERSION_RE = re.compile(r"\.v\d+$", re.IGNORECASE)


# --------------------------------------------------------------------------
# Utilities
# --------------------------------------------------------------------------

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(READ_CHUNK), b""):
            h.update(chunk)
    return h.hexdigest()


def load_fileref(path):
    """Load a .fileref JSON as a case-insensitive dict view (PascalCase)."""
    with open(path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValueError("fileref is not a JSON object")
    return {k.lower(): v for k, v in data.items()}


def human_bytes(n):
    units = ["B", "KB", "MB", "GB", "TB", "PB"]
    size = float(n)
    i = 0
    while size >= 1024 and i < len(units) - 1:
        size /= 1024.0
        i += 1
    return f"{n:,} B" if i == 0 else f"{size:,.1f} {units[i]}"


def to_tree_path(rel):
    r"""Tree-relative path with backslash separators, as LithicBackup stores
    DiscPath / ContentPath on Windows (e.g. 'C\mIRC\cacert.pem')."""
    return rel.replace("/", "\\")


def reconstruct_source_path(tree_rel):
    r"""Rebuild the original source path from a fileref's tree-relative path.

    'C/mIRC/cacert.pem.fileref'         -> 'C:\mIRC\cacert.pem'
    'C_prev/mIRC/cacert.pem.v3.fileref' -> 'C:\mIRC\cacert.pem'
    '_/some/no-drive/file.txt.fileref'  -> '_\some\no-drive\file.txt'
    """
    parts = tree_rel.replace("\\", "/").split("/")
    drive_seg = parts[0]
    rest = parts[1:]

    is_prev = drive_seg.lower().endswith(PREV_SUFFIX)
    base = drive_seg[: -len(PREV_SUFFIX)] if is_prev else drive_seg

    if rest:
        leaf = rest[-1]
        if leaf.lower().endswith(".fileref"):
            leaf = leaf[: -len(".fileref")]
        if is_prev:
            leaf = _VERSION_RE.sub("", leaf)
        rest[-1] = leaf

    rel = "\\".join(rest)
    if len(base) == 1 and base.isalpha():
        return f"{base}:\\{rel}"
    return f"{base}\\{rel}"


def sourcepath_to_tree(source_path):
    r"""Inverse of reconstruct_source_path: map an original source path to the
    backup tree's current-version relative path (backslash separators).

    'D:\Python314\DLLs\_asyncio.pyd' -> 'D\Python314\DLLs\_asyncio.pyd'
    '\\\\server\\share\\f.txt'       -> '_\server\share\f.txt'
    """
    s = source_path.replace("/", "\\")
    if len(s) >= 2 and s[1] == ":" and s[0].isalpha():
        rest = s[2:].lstrip("\\")
        return f"{s[0]}\\{rest}" if rest else s[0]
    return "_\\" + s.lstrip("\\")


def disk_path(backup_dir, tree_rel):
    """Absolute on-disk path for a tree-relative (DiscPath) value."""
    return os.path.join(backup_dir,
                        tree_rel.replace("\\", os.sep).replace("/", os.sep))


def _iso_utc(ts):
    return datetime.datetime.fromtimestamp(
        ts, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"


def _now_utc_iso():
    return datetime.datetime.now(
        datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"


def copy_to_temp(src, dst):
    """Stream-copy src -> dst+'.lbtmp' computing the SHA-256 in the same pass.
    Leaves the bytes in the temp file; the caller commits by os.replace(tmp,dst)
    AFTER the catalog row is durable, so a crash never exposes the final path
    with content the catalog hasn't recorded yet. Returns
    (hash_hex, size_bytes, source_mtime_epoch, tmp_path)."""
    h = hashlib.sha256()
    size = 0
    parent = os.path.dirname(dst)
    if parent:
        os.makedirs(parent, exist_ok=True)
    tmp = dst + ".lbtmp"
    with open(src, "rb") as fi, open(tmp, "wb") as fo:
        for chunk in iter(lambda: fi.read(READ_CHUNK), b""):
            h.update(chunk)
            fo.write(chunk)
            size += len(chunk)
    return h.hexdigest(), size, os.path.getmtime(src), tmp


def copy_from_source(src, dst):
    """Stream-copy src -> dst computing the SHA-256 in the same pass. Atomic
    (temp + replace). Returns (hash_hex, size_bytes, source_mtime_epoch)."""
    hash_hex, size, mtime, tmp = copy_to_temp(src, dst)
    os.replace(tmp, dst)
    return hash_hex, size, mtime


# --------------------------------------------------------------------------
# Tree scanning
# --------------------------------------------------------------------------

class FileRef:
    __slots__ = ("abs_path", "tree_rel", "is_current", "manifest")

    def __init__(self, abs_path, tree_rel, is_current, manifest):
        self.abs_path = abs_path        # absolute path of the .fileref
        self.tree_rel = tree_rel        # backup-root-relative path of the .fileref
        self.is_current = is_current    # True if in a current drive dir, not _prev
        self.manifest = manifest        # {originalname, originalsize, hash, ...}

    @property
    def plain_abs(self):
        return self.abs_path[: -len(".fileref")]

    @property
    def plain_tree_rel(self):
        return to_tree_path(self.tree_rel[: -len(".fileref")])


def scan_filerefs(backup_dir):
    """Walk the tree, returning (filerefs_by_hash, problems)."""
    by_hash = {}
    problems = []
    for root, dirs, files in os.walk(backup_dir):
        rel_root = os.path.relpath(root, backup_dir)
        top = rel_root.split(os.sep)[0]
        if top in (FILESTORE_DIR, BLOCK_STORE_DIR):
            dirs[:] = []
            continue
        for fname in files:
            if not fname.lower().endswith(".fileref"):
                continue
            abs_path = os.path.join(root, fname)
            tree_rel = os.path.relpath(abs_path, backup_dir)
            drive_seg = tree_rel.replace("\\", "/").split("/")[0]
            is_current = not drive_seg.lower().endswith(PREV_SUFFIX)
            try:
                manifest = load_fileref(abs_path)
            except Exception as ex:  # noqa: BLE001
                problems.append(f"unreadable fileref {tree_rel}: {ex}")
                continue
            content_hash = (manifest.get("hash") or "").lower()
            if not content_hash:
                problems.append(f"fileref without Hash: {tree_rel}")
                continue
            by_hash.setdefault(content_hash, []).append(
                FileRef(abs_path, tree_rel, is_current, manifest))
    return by_hash, problems


def choose_anchor(refs):
    """Pick the anchor fileref for a hash group: prefer a current-tree file,
    then a deterministic (sorted) choice for stability across re-runs."""
    current = [r for r in refs if r.is_current]
    pool = current if current else refs
    return min(pool, key=lambda r: r.tree_rel.lower())


def write_new_fileref(ref, anchor_tree_rel, dry_run):
    """Rewrite a .fileref into the NEW 5-field format (atomic replace)."""
    m = ref.manifest
    manifest = {
        "OriginalName": m.get("originalname") or os.path.basename(ref.plain_abs),
        "OriginalSize": int(m.get("originalsize") or 0),
        "Hash": (m.get("hash") or "").lower(),
        "SourcePath": reconstruct_source_path(ref.tree_rel),
        "ContentPath": anchor_tree_rel,
    }
    if dry_run:
        return
    text = json.dumps(manifest, indent=2)
    tmp = ref.abs_path + ".lbtmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(tmp, ref.abs_path)


# --------------------------------------------------------------------------
# Catalog conversion
# --------------------------------------------------------------------------

def find_set_for_backup_dir(catalog_path, backup_dir):
    """Return (set_id, set_db_path) for the backup set whose destination is
    backup_dir, or None if it can't be matched."""
    target = os.path.abspath(backup_dir).rstrip("\\/").casefold()
    con = sqlite3.connect(catalog_path)
    try:
        rows = con.execute(
            "SELECT Id, JobOptionsJson FROM BackupSets").fetchall()
    finally:
        con.close()
    for set_id, opts_json in rows:
        try:
            opts = json.loads(opts_json) if opts_json else {}
        except Exception:  # noqa: BLE001
            continue
        td = opts.get("TargetDirectory") or opts.get("targetDirectory")
        if not td:
            continue
        if os.path.abspath(td).rstrip("\\/").casefold() == target:
            set_db = os.path.join(
                os.path.dirname(catalog_path), "sets", f"set-{set_id}.db")
            return set_id, set_db
    return None


class CatalogConverter:
    """Applies anchor->plain flips to a per-set catalog database."""

    def __init__(self, set_db_path, dry_run):
        self.path = set_db_path
        self.dry = dry_run
        self.con = sqlite3.connect(set_db_path, timeout=30)
        self.con.execute("PRAGMA busy_timeout=10000")
        self._pending = []
        self._start_changes = self.con.total_changes
        # hash(lower) -> existing plain DiscPath, for content that is already a
        # plain file in the catalog (used as an anchor fallback on mixed trees).
        self.hash_to_plain = {}
        for h, dp in self.con.execute(
                "SELECT Hash, DiscPath FROM Files "
                "WHERE IsFileRef=0 AND IsDeleted=0 AND Hash IS NOT NULL AND Hash<>''"):
            self.hash_to_plain.setdefault((h or "").lower(), dp)
        if not dry_run:
            # Guard: refuse to run against a locked database.
            try:
                self.con.execute("BEGIN IMMEDIATE")
                self.con.rollback()
            except sqlite3.OperationalError as ex:
                raise SystemExit(
                    f"error: catalog '{set_db_path}' is locked ({ex}). "
                    "Close the LithicBackup app and stop the LithicBackup "
                    "Worker service, then retry.")
            self.con.execute(
                f"CREATE INDEX IF NOT EXISTS {CONV_INDEX} ON Files(DiscPath)")
            self.con.commit()

    def plain_for_hash(self, content_hash):
        """A catalog-recorded plain DiscPath for this hash, if any."""
        return self.hash_to_plain.get(content_hash.lower())

    def flip_anchor(self, fileref_discpath, plain_discpath):
        """Queue: turn the file-reference row at fileref_discpath into a plain
        record at plain_discpath. Idempotent (no-op once already plain)."""
        if self.dry:
            return
        self._pending.append((plain_discpath, fileref_discpath))
        if len(self._pending) >= 5000:
            self._flush()

    def _flush(self):
        if self.dry or not self._pending:
            self._pending.clear()
            return
        self.con.executemany(
            "UPDATE Files SET IsFileRef=0, DiscPath=? "
            "WHERE DiscPath=? AND IsFileRef=1",
            self._pending)
        self.con.commit()
        self._pending.clear()

    def reconcile_from_disk(self, backup_dir):
        """Flip any remaining file-reference rows whose .fileref is gone from
        disk but whose plain copy is present. This self-heals unique files that
        were converted in a prior --no-catalog (file-only) run - they leave no
        .fileref behind for the tree scan to key on, so the row would otherwise
        stay a stale file-reference. Disk-verified (only flips when the plain
        file exists and the .fileref does not), so it is safe on mixed/stale
        catalogs and never touches a still-old or still-duplicate row."""
        if self.dry:
            return
        self._flush()
        rows = self.con.execute(
            "SELECT DiscPath FROM Files "
            "WHERE IsFileRef=1 AND IsDeleted=0 AND DiscPath LIKE '%.fileref'"
        ).fetchall()
        for (dp,) in rows:
            plain = dp[: -len(".fileref")]
            sep_dp = dp.replace("\\", os.sep).replace("/", os.sep)
            sep_plain = plain.replace("\\", os.sep).replace("/", os.sep)
            fileref_abs = os.path.join(backup_dir, sep_dp)
            plain_abs = os.path.join(backup_dir, sep_plain)
            if os.path.isfile(plain_abs) and not os.path.exists(fileref_abs):
                self._pending.append((plain, dp))
                if len(self._pending) >= 5000:
                    self._flush()
        self._flush()

    def finish(self):
        if self.dry:
            return 0
        self._flush()
        changed = self.con.total_changes - self._start_changes
        try:
            self.con.execute(f"DROP INDEX IF EXISTS {CONV_INDEX}")
            self.con.commit()
        except sqlite3.OperationalError:
            pass
        return changed

    def close(self):
        try:
            self._flush()
        finally:
            self.con.close()


# --------------------------------------------------------------------------
# Conversion driver
# --------------------------------------------------------------------------

def convert(backup_dir, args, catalog):
    filestore = os.path.join(backup_dir, FILESTORE_DIR)

    by_hash, problems = scan_filerefs(backup_dir)
    for p in problems:
        print(f"  WARN: {p}", file=sys.stderr)

    used_hashes = set()
    anchors_placed = 0
    refs_converted = 0
    bytes_moved = 0
    deleted_stale = 0
    missing_content = []  # (hash, where-it-would-anchor)
    anchor_filerefs_to_delete = []  # abs paths, deleted AFTER catalog commit
    redundant_blobs = []  # _filestore .dat whose content is already a plain anchor

    for content_hash, refs in sorted(by_hash.items()):
        dat = os.path.join(filestore, content_hash + ".dat")
        anchor = choose_anchor(refs)  # deterministic; may be a dupe on a re-run

        anchor_member = None     # the group .fileref that owns the plain bytes
        anchor_tree_rel = None   # tree-relative path of the plain copy

        if os.path.isfile(anchor.plain_abs):
            # Anchor already placed (its .fileref still present): resume.
            anchor_member = anchor
            anchor_tree_rel = anchor.plain_tree_rel
            # A plain copy of this content already exists on disk (a mixed old
            # tree that kept BOTH a plain file and a .fileref+blob for the same
            # content). The blob was never moved, so if it's still in _filestore
            # it is now redundant: the plain anchor holds the bytes and every
            # dupe will reference it. Reclaim it like an orphan (its filename IS
            # the content hash, identical to the anchor's content).
            if os.path.isfile(dat):
                redundant_blobs.append(dat)
        elif os.path.isfile(dat):
            if args.verify:
                actual = sha256_file(dat)
                if actual != content_hash:
                    print(f"  ERROR: blob {content_hash}.dat hash mismatch "
                          f"(got {actual}); skipping group", file=sys.stderr)
                    continue
            if args.verbose:
                verb = "would move" if args.dry_run else "move"
                print(f"  {verb} {content_hash}.dat -> {anchor.plain_tree_rel}")
            if not args.dry_run:
                os.replace(dat, anchor.plain_abs)
            try:
                bytes_moved += int(anchor.manifest.get("originalsize") or 0)
            except (TypeError, ValueError):
                pass
            anchors_placed += 1
            anchor_member = anchor
            anchor_tree_rel = anchor.plain_tree_rel
        else:
            # Blob already moved out (re-run) - find the placed plain copy via
            # a duplicate's ContentPath, or via the catalog (mixed trees).
            for r in refs:
                cp = (r.manifest.get("contentpath") or "").strip()
                if cp:
                    cand = os.path.join(
                        backup_dir, cp.replace("\\", os.sep).replace("/", os.sep))
                    if os.path.isfile(cand):
                        anchor_tree_rel = to_tree_path(cp)
                        break
            if anchor_tree_rel is None and catalog is not None:
                cp = catalog.plain_for_hash(content_hash)
                if cp:
                    cand = os.path.join(
                        backup_dir, cp.replace("\\", os.sep).replace("/", os.sep))
                    if os.path.isfile(cand):
                        anchor_tree_rel = to_tree_path(cp)
            if anchor_tree_rel is None:
                missing_content.append((content_hash, anchor.plain_tree_rel))
                continue

        used_hashes.add(content_hash)

        # Rewrite every duplicate to point at the anchor (must happen BEFORE
        # the anchor's own .fileref is deleted, so a crash leaves the anchor
        # rediscoverable through a duplicate's ContentPath).
        for ref in refs:
            if anchor_member is not None and ref.abs_path == anchor_member.abs_path:
                continue
            if os.path.isfile(ref.plain_abs):
                # A stale leftover plain copy for this member: its .fileref is
                # redundant. Remove it.
                if not args.dry_run and os.path.isfile(ref.abs_path):
                    os.remove(ref.abs_path)
                deleted_stale += 1
                continue
            cur_cp = (ref.manifest.get("contentpath") or "").replace("/", "\\")
            if (cur_cp.lower() == anchor_tree_rel.lower()
                    and (ref.manifest.get("sourcepath") or "")):
                continue  # already a correct new-format reference
            if args.verbose:
                verb = "would convert" if args.dry_run else "convert"
                print(f"  {verb} dupe {ref.tree_rel} -> ref ({anchor_tree_rel})")
            write_new_fileref(ref, anchor_tree_rel, args.dry_run)
            refs_converted += 1

        # Flip the anchor's catalog row to a plain record. Idempotent: keyed by
        # the anchor's .fileref path, so it re-applies anything a lost commit
        # dropped and is a no-op once the row is already plain.
        if catalog is not None:
            catalog.flip_anchor(anchor_tree_rel + ".fileref", anchor_tree_rel)

        # Defer deleting the anchor's .fileref until AFTER the catalog flips are
        # committed (below). The .fileref is the last recovery pointer for a
        # unique-content group, so removing it before the flip is durable would
        # make a mid-run crash unrecoverable for that group.
        if anchor_member is not None and not args.dry_run:
            anchor_filerefs_to_delete.append(anchor_member.abs_path)

    # Self-heal rows left behind by any prior file-only run, then commit every
    # catalog flip durably BEFORE we delete a single anchor .fileref.
    if catalog is not None:
        catalog.reconcile_from_disk(backup_dir)
    catalog_flips = catalog.finish() if catalog is not None else 0

    # Now that the catalog is durable, delete the anchors' redundant .filerefs.
    for ref_abs in anchor_filerefs_to_delete:
        if os.path.isfile(ref_abs):
            os.remove(ref_abs)

    # Orphan blobs: .dat files no fileref referenced.
    orphans, orphan_bytes = [], 0
    if os.path.isdir(filestore):
        for fname in os.listdir(filestore):
            if not fname.lower().endswith(".dat"):
                continue
            if fname[: -len(".dat")].lower() in used_hashes:
                continue
            full = os.path.join(filestore, fname)
            orphans.append(full)
            try:
                orphan_bytes += os.path.getsize(full)
            except OSError:
                pass

    # Redundant blobs (content already present as a plain anchor) are reclaimable
    # exactly like orphans; fold them in so --delete-orphans removes them and the
    # report counts them. They are protected by used_hashes from the scan above.
    seen = {os.path.normcase(p) for p in orphans}
    for dat in redundant_blobs:
        key = os.path.normcase(dat)
        if key in seen or not os.path.isfile(dat):
            continue
        seen.add(key)
        orphans.append(dat)
        try:
            orphan_bytes += os.path.getsize(dat)
        except OSError:
            pass

    if orphans and args.delete_orphans:
        for o in orphans:
            if args.verbose:
                verb = "would delete orphan" if args.dry_run else "delete orphan"
                print(f"  {verb} {os.path.basename(o)}")
            if not args.dry_run:
                try:
                    os.remove(o)
                except OSError as ex:
                    print(f"  WARN: could not delete {o}: {ex}", file=sys.stderr)

    filestore_removed = False
    if not args.dry_run and os.path.isdir(filestore):
        try:
            if not os.listdir(filestore):
                os.rmdir(filestore)
                filestore_removed = True
        except OSError:
            pass

    # ----------------------------- summary -------------------------------
    print("\n" + "=" * 64)
    if args.dry_run:
        print("DRY RUN - nothing was written.\n")
    print(f"Unique content hashes referenced : {len(by_hash):,}")
    print(f"Anchors placed (blobs moved out) : {anchors_placed:,}"
          f"  ({human_bytes(bytes_moved)})")
    print(f"Duplicates rewritten as filerefs : {refs_converted:,}")
    if deleted_stale:
        print(f"Stale leftover filerefs removed  : {deleted_stale:,}")
    if catalog is not None:
        verb = "would flip" if args.dry_run else "flipped"
        print(f"Catalog rows {verb} to plain     : {catalog_flips:,}"
              if not args.dry_run
              else f"Catalog: rows will be flipped to plain (dry run skips writes)")
    if missing_content:
        print(f"\nMISSING CONTENT ({len(missing_content):,} group(s)) - "
              f"no blob and no placed plain copy; left untouched:")
        for h, where in missing_content[:20]:
            print(f"  - {h}  (would anchor at {where})")
        if len(missing_content) > 20:
            print(f"  ... and {len(missing_content) - 20:,} more")
    if orphans:
        action = ("deleted" if (args.delete_orphans and not args.dry_run)
                  else "would delete" if (args.delete_orphans and args.dry_run)
                  else "left in place")
        print(f"\nOrphan blobs ({action}): {len(orphans):,}  "
              f"({human_bytes(orphan_bytes)})")
        if not args.delete_orphans:
            print("  (re-run with --delete-orphans to remove them)")
    if filestore_removed:
        print("\n_filestore was empty and has been removed.")
    elif not args.dry_run and os.path.isdir(filestore):
        print("\n_filestore retained (still contains blobs).")
    print("=" * 64)

    return 1 if (missing_content or problems) else 0


# --------------------------------------------------------------------------
# Recover files missing from the destination by copying them from the source
# --------------------------------------------------------------------------

def recover_missing_from_source(backup_dir, args, catalog):
    r"""Fill gaps where the catalog records a file the destination no longer
    has on disk, by copying the CURRENT version from its original source.

    Scope (per the design):
      * Only the CURRENT version of each source path is recovered (the active
        catalog row with the highest Version). Older retained versions cannot
        be reconstructed from the source - the source only holds the current
        bytes - so missing retention versions are deliberately LEFT OUT.
      * The source file may have changed since the backup, so we read it fresh:
        the copy is hashed in one pass and the catalog row is updated with the
        ACTUAL current size/hash/mtime. Future incremental backups then see the
        file as already up to date and won't re-copy it.
      * A file whose source is also gone cannot be recovered. Deletion from the
        source was almost certainly deliberate, so its catalog row is marked
        deleted (IsDeleted=1) - the same soft-delete the app uses when a backup
        finds a source file removed.

    Resumable: each file is written atomically (temp + replace) to its
    current-tree path, then its catalog row is flipped to a plain record. We
    never overwrite a path another active catalog row already claims (e.g. a
    retained version sitting at that path), so a re-run re-copies only what is
    still missing and converges.
    """
    con = catalog.con
    print("\nScanning catalog for files missing from disk "
          "(this reads the whole catalog and stats the tree)...")

    # Highest active version per source path => the "current" version.
    maxver = {}
    for sp, mv in con.execute(
            "SELECT SourcePath, MAX(Version) FROM Files "
            "WHERE IsDeleted=0 GROUP BY SourcePath"):
        maxver[sp] = mv

    # Content whose bytes are retrievable from the DESTINATION (so the file is
    # NOT really missing and must not be re-fetched from source). A hash counts
    # as present if a plain copy of it exists on disk OR its bytes are still
    # pooled as a _filestore blob. Including blobs makes a --dry-run match what
    # a real run does after it places the anchors, and - critically - stops it
    # from ever proposing to delete a catalog row whose content is safe on the
    # destination but simply not yet materialised as a plain file.
    present_hashes = set()
    plain_path_owner = {}  # discpath.lower() -> file Id
    for fid, h, dp in con.execute(
            "SELECT Id, Hash, DiscPath FROM Files "
            "WHERE IsDeleted=0 AND IsFileRef=0"):
        if os.path.isfile(disk_path(backup_dir, dp)):
            plain_path_owner[dp.lower()] = fid
            if h:
                present_hashes.add(h.lower())
    filestore = os.path.join(backup_dir, FILESTORE_DIR)
    if os.path.isdir(filestore):
        for fname in os.listdir(filestore):
            if fname.lower().endswith(".dat"):
                present_hashes.add(fname[: -len(".dat")].lower())

    rows = con.execute(
        "SELECT Id, SourcePath, DiscPath, Hash, IsFileRef, Version "
        "FROM Files WHERE IsDeleted=0").fetchall()

    recovered = 0
    recovered_bytes = 0
    retention_skipped = 0
    src_gone = []
    conflicts = []
    updates = []  # (plain_discpath, size, hash, src_mtime_iso, now_iso, file_id)
    staged = []   # (tmp_path, final_abs) parallel to `updates`

    def flush():
        # Commit-before-rename: persist the catalog rows (plain record, REAL
        # hash) FIRST, then move each staged copy into its final place. This
        # ordering is what makes recovery crash-safe with correct metadata - the
        # recovered file only appears at its final, convert-adoptable path AFTER
        # the catalog already records it as a plain row with the real hash, so a
        # crash can never leave convert adopting new content under a stale hash.
        if args.dry_run:
            updates.clear()
            staged.clear()
            return
        if updates:
            con.executemany(
                "UPDATE Files SET IsFileRef=0, IsDeduped=0, DiscPath=?, "
                "SizeBytes=?, Hash=?, SourceLastWriteUtc=?, BackedUpUtc=? "
                "WHERE Id=?", updates)
            con.commit()
        for tmp, final in staged:
            os.replace(tmp, final)
        updates.clear()
        staged.clear()

    for fid, sp, dp, h, isref, ver in rows:
        # Present if the content is retrievable on the destination. Two ways:
        #  1. A plain copy of this hash exists, or its bytes are still pooled as
        #     a _filestore blob (covers de-duplicated content and filerefs).
        #  2. This row's OWN plain file is physically on disk. Plain rows can
        #     carry an EMPTY Hash (a NEW-format anchor records no hash, and some
        #     trees were seeded that way), so the hash test in (1) can never see
        #     them - they must be matched by their own DiscPath. plain_path_owner
        #     holds every existing plain DiscPath (built above, hash or not).
        if (h or "").lower() in present_hashes:
            continue
        if not isref and dp and dp.lower() in plain_path_owner:
            continue
        if ver != maxver.get(sp):
            retention_skipped += 1
            continue
        if not (sp and os.path.isfile(sp)):
            src_gone.append((fid, sp))
            continue

        target_tree = sourcepath_to_tree(sp)
        owner = plain_path_owner.get(target_tree.lower())
        if owner is not None and owner != fid:
            # Another active row already occupies this path (e.g. a retained
            # version). Never overwrite it.
            conflicts.append((sp, target_tree))
            continue

        target_abs = disk_path(backup_dir, target_tree)
        if args.verbose:
            verb = "would recover" if args.dry_run else "recover"
            print(f"  {verb} {sp} -> {target_tree}")
        if args.dry_run:
            recovered += 1
            try:
                recovered_bytes += os.path.getsize(sp)
            except OSError:
                pass
            continue

        try:
            new_hash, new_size, mtime, tmp = copy_to_temp(sp, target_abs)
        except OSError as ex:
            print(f"  WARN: could not copy {sp}: {ex}", file=sys.stderr)
            continue
        updates.append((target_tree, new_size, new_hash,
                        _iso_utc(mtime), _now_utc_iso(), fid))
        staged.append((tmp, target_abs))
        present_hashes.add(new_hash.lower())
        plain_path_owner[target_tree.lower()] = fid
        recovered += 1
        recovered_bytes += new_size
        if len(updates) >= 1000:
            flush()
    flush()

    # Files gone from both disk and source were deliberately deleted at the
    # source; mark their catalog rows deleted (soft-delete, as the app does).
    deleted_gone = 0
    if src_gone and not args.dry_run:
        del_ids = [(fid,) for fid, _ in src_gone]
        for i in range(0, len(del_ids), 1000):
            con.executemany("UPDATE Files SET IsDeleted=1 WHERE Id=?",
                            del_ids[i:i + 1000])
        con.commit()
        deleted_gone = len(del_ids)

    print("\n" + "=" * 64)
    verb = "Would recover" if args.dry_run else "Recovered"
    print(f"{verb} from source        : {recovered:,}  "
          f"({human_bytes(recovered_bytes)})")
    print(f"Retention versions skipped : {retention_skipped:,}")
    if src_gone:
        head = ("Would delete from catalog" if args.dry_run
                else "Deleted from catalog")
        print(f"\n{head} ({len(src_gone):,}) - gone from disk AND source "
              "(deleted at source):")
        for _fid, sp in src_gone[:20]:
            print(f"  - {sp}")
        if len(src_gone) > 20:
            print(f"  ... and {len(src_gone) - 20:,} more")
    if conflicts:
        print(f"\nPath conflicts ({len(conflicts):,}) - target path already "
              f"held by another catalog entry; skipped:")
        for sp, tt in conflicts[:20]:
            print(f"  - {sp}  (wanted {tt})")
        if len(conflicts) > 20:
            print(f"  ... and {len(conflicts) - 20:,} more")
    print("=" * 64)
    return 1 if conflicts else 0


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

def abort_if_block_dedup(backup_dir):
    # The reliable signal that THIS backup uses block-level dedup is a top-level
    # "_blocks" store: a .dedup manifest only lists block hashes and is useless
    # without _blocks/<hash>.blk, so a real block-dedup tree always has it.
    #
    # We deliberately do NOT scan for files merely *named* "*.dedup": those can
    # be ordinary backed-up user data (e.g. a backup of another LithicBackup
    # destination, whose .dedup manifest files are stored as plain content).
    # The converter only ever inspects "*.fileref" files and leaves "*.dedup"
    # files untouched, so such data is safe; aborting on it was a false positive
    # that blocked legitimate conversions.
    if os.path.isdir(os.path.join(backup_dir, BLOCK_STORE_DIR)):
        sys.exit(f"error: '{BLOCK_STORE_DIR}' present - this backup uses block "
                 "dedup, which this converter does not handle. Aborting.")


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="lithic_convert_to_new_format.py",
        description="Upgrade an OLD-format LithicBackup tree (all filerefs + "
                    "_filestore) and its catalog to the NEW format (plain files "
                    "in place; only dupes as filerefs), in place, without "
                    "re-copying from source.",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("backup_dir", help="The LithicBackup destination folder.")
    parser.add_argument("--catalog", default=DEFAULT_CATALOG,
                        help=f"Master catalog DB (default: {DEFAULT_CATALOG}).")
    parser.add_argument("--set-id", type=int, default=None,
                        help="Convert this set's catalog instead of auto-detecting.")
    parser.add_argument("--no-catalog", action="store_true",
                        help="Convert destination files only; touch no catalog.")
    parser.add_argument("--dry-run", action="store_true",
                        help="Show what would change; write nothing.")
    parser.add_argument("--verify", action="store_true",
                        help="Re-hash each blob against its filename before moving it.")
    parser.add_argument("--delete-orphans", action="store_true",
                        help="Delete _filestore blobs no fileref references.")
    parser.add_argument("--recover-from-source", action="store_true",
                        help="After converting, copy any CURRENT file the "
                             "catalog records but the destination is missing "
                             "back from its original source (requires a "
                             "catalog; retained versions are left out).")
    parser.add_argument("-v", "--verbose", action="store_true",
                        help="Print every file as it is converted.")
    args = parser.parse_args(argv)

    # Backup trees contain paths with characters the console's default code
    # page (e.g. cp1252) can't encode; force UTF-8 so printing never crashes.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
        except (AttributeError, ValueError):
            pass

    if args.recover_from_source and args.no_catalog:
        sys.exit("error: --recover-from-source needs the catalog to find what "
                 "is missing; it cannot be combined with --no-catalog.")

    backup_dir = os.path.abspath(args.backup_dir)
    if not os.path.isdir(backup_dir):
        sys.exit(f"error: backup folder not found: {backup_dir}")

    filestore = os.path.join(backup_dir, FILESTORE_DIR)
    if not os.path.isdir(filestore):
        print(f"note: no '{FILESTORE_DIR}' directory found at {backup_dir}.\n"
              "      This tree may already be in the new format. Continuing "
              "(scan only).")

    abort_if_block_dedup(backup_dir)

    # Resolve the catalog (unless suppressed).
    catalog = None
    if not args.no_catalog:
        if not os.path.isfile(args.catalog):
            print(f"note: catalog '{args.catalog}' not found - converting files "
                  "only. (Rebuild the catalog later with 'Seed from existing "
                  "backup', or pass --catalog.)")
        else:
            if args.set_id is not None:
                set_id = args.set_id
                set_db = os.path.join(
                    os.path.dirname(args.catalog), "sets", f"set-{set_id}.db")
            else:
                match = find_set_for_backup_dir(args.catalog, backup_dir)
                if match is None:
                    sys.exit(
                        f"error: no backup set in '{args.catalog}' has "
                        f"destination '{backup_dir}'. Pass --set-id to choose "
                        "one explicitly, or --no-catalog to skip the catalog.")
                set_id, set_db = match
            if not os.path.isfile(set_db):
                sys.exit(f"error: per-set database not found: {set_db}")
            print(f"Catalog set: {set_id}  ({set_db})")
            catalog = CatalogConverter(set_db, args.dry_run)

    print(f"Backup: {backup_dir}")
    if args.dry_run:
        print("(dry run - no files or catalog rows will be written)")
    print()

    if args.recover_from_source and catalog is None:
        sys.exit("error: --recover-from-source needs a catalog, but none was "
                 "resolved. Pass --catalog/--set-id to point at one.")

    try:
        rc = convert(backup_dir, args, catalog)
        if args.recover_from_source:
            rc |= recover_missing_from_source(backup_dir, args, catalog)
        return rc
    finally:
        if catalog is not None:
            catalog.close()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        sys.exit(130)
