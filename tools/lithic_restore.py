#!/usr/bin/env python3
"""
lithic_restore.py - Standalone disaster-recovery restore for LithicBackup.

Rebuilds your files directly from a LithicBackup *directory* backup, without
LithicBackup itself and without its SQLite catalog. It needs nothing but the
Python standard library, so you can drop this one file on any machine with
Python 3.8+ and recover your data.

It works because a LithicBackup destination tree is fully self-describing:

    {backup}/{drive}/relative                          plain file (real bytes)
    {backup}/{drive}/relative.dedup                     block-deduplicated file
    {backup}/{drive}/relative.fileref                   file-level duplicate
    {backup}/{drive}_prev/relative.v{N}[.dedup|.fileref]  previous versions
    {backup}/_blocks/{hash}.blk                         shared block store

  * A plain file holds its own bytes.
  * A .dedup manifest lists the SHA-256 block hashes that reassemble the file
    from the _blocks store.
  * A .fileref manifest stores no bytes; it carries a SHA-256 Hash plus a
    ContentPath hint pointing at the plain copy that holds the content. The
    hint is verified against the hash, and if it is missing or stale we fall
    back to a content-hash scan of the whole tree.

Drive prefixes: a source path like C:\\Users\\me\\file.txt is stored under a
top-level directory named "C". Paths without a drive letter use "_".

USAGE
  python lithic_restore.py <backup_dir> [options]

  Interactive (recommended): just give the backup folder. The tool lists the
  drives it found and asks where to restore each one (blank = skip):

      python lithic_restore.py D:\\MyBackup

  Non-interactive mapping:

      python lithic_restore.py D:\\MyBackup --map C=E:\\restored --map D=F:\\

  Restore only some files/dirs (wildcards ok, case-insensitive), matched
  against the ORIGINAL source path:

      python lithic_restore.py D:\\MyBackup --map C=E:\\out ^
          --include "C:\\Users\\me\\projects\\*" --include "C:\\*.docx" ^
          --exclude "*\\node_modules\\*"

  See what would happen without writing anything:

      python lithic_restore.py D:\\MyBackup --map C=E:\\out --dry-run

OPTIONS
  --map DRIVE=PATH    Restore the given backup drive into PATH. Repeatable.
                      Drives not mapped are skipped. Suppresses prompting.
  --include PATTERN   Only restore files whose source path matches. Repeatable.
                      A bare directory path matches everything beneath it.
  --exclude PATTERN   Skip files whose source path matches. Repeatable.

  Wildcards in --include / --exclude (case-insensitive, matched against the
  original source path):
      *   matches any run of characters, INCLUDING directory separators, so a
          single '*' spans arbitrarily many directories deep. For example
          'C:\\Users\\*\\notes.txt' matches 'C:\\Users\\me\\a\\b\\notes.txt',
          and '*.docx' matches every .docx anywhere in the backup.
      **  is accepted as a synonym for '*' (it also spans directories), so
          patterns copied from other tools keep working.
      ?   matches any single character.
  --prev              Also restore previous versions (kept with their .v{N}
                      suffix so they don't overwrite current files).
  --verify            Re-hash reassembled .dedup files against their recorded
                      hash and report mismatches.
  --list              List the drives and file counts found, then exit.
  --dry-run           Show what would be restored without writing files.
  --overwrite         Overwrite existing files at the destination (default:
                      skip files that already exist).
  -v, --verbose       Print every file as it is restored.
  -h, --help          Show this help.
"""

import argparse
import fnmatch
import hashlib
import json
import os
import re
import sys

BLOCK_STORE_DIR = "_blocks"
PREV_SUFFIX = "_prev"
READ_CHUNK = 1024 * 1024  # 1 MiB streaming buffer
_VERSION_RE = re.compile(r"\.v\d+$", re.IGNORECASE)


# --------------------------------------------------------------------------
# Small utilities
# --------------------------------------------------------------------------

def sha256_file(path):
    """Lowercase hex SHA-256 of a file's contents."""
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(READ_CHUNK), b""):
            h.update(chunk)
    return h.hexdigest()


def load_json_ci(path):
    """Load JSON and return a case-insensitive dict view (PascalCase tolerant)."""
    with open(path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    return {k.lower(): v for k, v in data.items()} if isinstance(data, dict) else data


def human_bytes(n):
    units = ["B", "KB", "MB", "GB", "TB", "PB"]
    size = float(n)
    i = 0
    while size >= 1024 and i < len(units) - 1:
        size /= 1024.0
        i += 1
    return f"{n:,} B" if i == 0 else f"{size:,.1f} {units[i]}"


def strip_format_suffix(name):
    """Remove a trailing .dedup/.fileref suffix; return (base, kind)."""
    low = name.lower()
    if low.endswith(".fileref"):
        return name[: -len(".fileref")], "fileref"
    if low.endswith(".dedup"):
        return name[: -len(".dedup")], "dedup"
    return name, "plain"


# --------------------------------------------------------------------------
# Pattern matching against original source paths
# --------------------------------------------------------------------------

def _norm(p):
    # Lowercase and unify separators so matching is case-insensitive and
    # platform-independent (recovery may well run on a Linux box). Collapse
    # runs of '*' so that '**' behaves exactly like '*'.
    s = p.replace("/", "\\").lower()
    return re.sub(r"\*\*+", "*", s)


def path_matches(source_path, pattern):
    """
    Case-insensitive glob match of an original source path against a pattern.

    Wildcards (matching LithicBackup's own glob convention):
      *   matches any run of characters INCLUDING directory separators, so a
          single '*' already spans arbitrarily many directories deep
          (e.g. 'C:\\Users\\*\\notes.txt' matches
                 'C:\\Users\\me\\a\\b\\notes.txt').
      **  is accepted as a synonym for '*' (also spans directories).
      ?   matches any single character.
    A bare directory pattern matches everything beneath it.

    We use fnmatchcase on already-lowercased strings (rather than fnmatch,
    which would re-apply os.path.normcase) so the result is identical on every
    platform.
    """
    sp = _norm(source_path)
    pat = _norm(pattern)
    if fnmatch.fnmatchcase(sp, pat):
        return True
    # Directory pattern: match everything underneath it.
    if pat.endswith("\\"):
        return fnmatch.fnmatchcase(sp, pat + "*")
    return fnmatch.fnmatchcase(sp, pat + "\\*")


def should_restore(source_path, includes, excludes):
    if includes and not any(path_matches(source_path, p) for p in includes):
        return False
    if excludes and any(path_matches(source_path, p) for p in excludes):
        return False
    return True


# --------------------------------------------------------------------------
# Backup tree discovery
# --------------------------------------------------------------------------

def discover_drives(backup_dir):
    """
    Return {base_drive: {'current': path|None, 'prev': path|None}} for every
    drive directory in the backup tree. base_drive is the drive prefix with any
    trailing _prev removed (e.g. 'C' from both 'C' and 'C_prev').
    """
    drives = {}
    for name in sorted(os.listdir(backup_dir)):
        full = os.path.join(backup_dir, name)
        if not os.path.isdir(full):
            continue
        if name.lower() == BLOCK_STORE_DIR:
            continue
        is_prev = name.lower().endswith(PREV_SUFFIX)
        base = name[: -len(PREV_SUFFIX)] if is_prev else name
        if not base:
            continue  # malformed dir name like bare "_prev"
        entry = drives.setdefault(base, {"current": None, "prev": None})
        entry["prev" if is_prev else "current"] = full
    return drives


def source_path_for(base, rel):
    """Reconstruct the original source path from a drive prefix + relative path."""
    rel = rel.replace("/", "\\")
    if len(base) == 1 and base.isalpha():
        return f"{base}:\\{rel}"
    return f"{base}\\{rel}"  # non-drive ("_") or unusual prefix


def iter_drive_files(drive_dir, base, is_prev):
    """
    Yield restorable file records under one drive directory.

    Each record: dict(kind, abs_path, out_rel, source_path)
      - kind:        'plain' | 'dedup' | 'fileref'
      - abs_path:    absolute path of the stored file/manifest
      - out_rel:     destination-relative output path (format suffix stripped;
                     for _prev files the .v{N} version tag is preserved)
      - source_path: reconstructed original source path (for include/exclude)
    """
    for root, _dirs, files in os.walk(drive_dir):
        for fname in files:
            low = fname.lower()
            if low.endswith(".lbtmp"):
                continue  # interrupted-copy temp file
            abs_path = os.path.join(root, fname)
            rel = os.path.relpath(abs_path, drive_dir)
            base_name, kind = strip_format_suffix(rel)
            # base_name still has OS separators; logical (version-stripped) path
            # is used only for include/exclude matching.
            logical = _VERSION_RE.sub("", base_name) if is_prev else base_name
            yield {
                "kind": kind,
                "abs_path": abs_path,
                "out_rel": base_name,
                "source_path": source_path_for(base, logical),
            }


# --------------------------------------------------------------------------
# Content resolution / reconstruction
# --------------------------------------------------------------------------

class HashIndex:
    """Lazily-built content-hash -> plain-file-path index over the whole tree."""

    def __init__(self, backup_dir, drives):
        self._backup_dir = backup_dir
        self._drives = drives
        self._index = None

    def find(self, content_hash):
        if self._index is None:
            self._build()
        return self._index.get(content_hash.lower())

    def _build(self):
        print("  (building content-hash index for stale/missing references"
              " - this can take a while...)", flush=True)
        self._index = {}
        for entry in self._drives.values():
            for d in (entry["current"], entry["prev"]):
                if not d:
                    continue
                for root, _dirs, files in os.walk(d):
                    for fname in files:
                        low = fname.lower()
                        if low.endswith((".dedup", ".fileref", ".lbtmp")):
                            continue
                        p = os.path.join(root, fname)
                        try:
                            self._index.setdefault(sha256_file(p), p)
                        except OSError:
                            pass


def reassemble_dedup(manifest_path, block_store_dir, dest_path, verify):
    m = load_json_ci(manifest_path)
    block_hashes = m.get("blockhashes") or []
    h = hashlib.sha256() if verify else None
    with open(dest_path, "wb") as out:
        for bh in block_hashes:
            blk = os.path.join(block_store_dir, bh + ".blk")
            if not os.path.isfile(blk):
                raise FileNotFoundError(f"missing block {bh}.blk in {block_store_dir}")
            with open(blk, "rb") as bf:
                for chunk in iter(lambda: bf.read(READ_CHUNK), b""):
                    out.write(chunk)
                    if h:
                        h.update(chunk)
    if verify:
        want = (m.get("originalhash") or "").lower()
        if want and h.hexdigest() != want:
            raise ValueError(f"hash mismatch after reassembly (expected {want})")


def resolve_fileref(manifest_path, backup_dir, hash_index):
    """Return the absolute path of the plain copy holding a fileref's content."""
    m = load_json_ci(manifest_path)
    content_hash = (m.get("hash") or "").lower()
    hint = m.get("contentpath") or ""

    # 1) Trust-but-verify the ContentPath hint.
    if hint:
        cand = os.path.join(backup_dir, hint.replace("/", os.sep).replace("\\", os.sep))
        if os.path.isfile(cand):
            if not content_hash or sha256_file(cand) == content_hash:
                return cand

    # 2) Fall back to a content-hash scan of the tree.
    if content_hash:
        found = hash_index.find(content_hash)
        if found:
            return found

    raise FileNotFoundError(
        f"could not resolve referenced content {content_hash or '(no hash)'} "
        f"(hint '{hint}' missing or stale)")


def copy_file(src, dest):
    with open(src, "rb") as s, open(dest, "wb") as d:
        for chunk in iter(lambda: s.read(READ_CHUNK), b""):
            d.write(chunk)


# --------------------------------------------------------------------------
# Mapping (drive -> output root)
# --------------------------------------------------------------------------

def parse_map_args(map_args):
    mapping = {}
    for item in map_args or []:
        if "=" not in item:
            sys.exit(f"error: --map expects DRIVE=PATH, got '{item}'")
        drive, path = item.split("=", 1)
        drive = drive.strip().rstrip(":").rstrip("\\/")
        path = path.strip().strip('"')
        if not drive or not path:
            sys.exit(f"error: --map expects DRIVE=PATH, got '{item}'")
        mapping[drive] = path
    return mapping


def prompt_for_mapping(drives):
    print("\nDrives found in this backup:")
    for base in sorted(drives):
        e = drives[base]
        bits = []
        if e["current"]:
            bits.append("current")
        if e["prev"]:
            bits.append("history")
        print(f"  {base}:  ({', '.join(bits)})")
    print("\nFor each drive, enter a destination folder to restore into,")
    print("or press Enter to SKIP that drive.\n")

    mapping = {}
    for base in sorted(drives):
        try:
            ans = input(f"  Restore drive '{base}' to (blank = skip): ").strip().strip('"')
        except EOFError:
            ans = ""
        if ans:
            mapping[base] = ans
        else:
            print(f"    -> skipping '{base}'")
    return mapping


# --------------------------------------------------------------------------
# Main restore driver
# --------------------------------------------------------------------------

def run_restore(backup_dir, drives, mapping, args):
    block_store_dir = os.path.join(backup_dir, BLOCK_STORE_DIR)
    hash_index = HashIndex(backup_dir, drives)

    total_files = 0
    total_bytes = 0
    skipped_existing = 0
    errors = []

    for base in sorted(mapping):
        if base not in drives:
            print(f"warning: drive '{base}' is not in this backup; ignoring.")
            continue
        out_root = mapping[base]
        entry = drives[base]

        dirs_to_walk = [(entry["current"], False)]
        if args.prev and entry["prev"]:
            dirs_to_walk.append((entry["prev"], True))

        for drive_dir, is_prev in dirs_to_walk:
            if not drive_dir:
                continue
            for rec in iter_drive_files(drive_dir, base, is_prev):
                if not should_restore(rec["source_path"], args.include, args.exclude):
                    continue

                dest_path = os.path.join(out_root, rec["out_rel"])

                if os.path.exists(dest_path) and not args.overwrite and not args.dry_run:
                    skipped_existing += 1
                    if args.verbose:
                        print(f"  skip (exists): {dest_path}")
                    continue

                tag = {"plain": "", "dedup": "[dedup] ", "fileref": "[ref] "}[rec["kind"]]
                if args.dry_run:
                    print(f"  would restore {tag}{rec['source_path']} -> {dest_path}")
                    total_files += 1
                    continue

                try:
                    os.makedirs(os.path.dirname(dest_path) or ".", exist_ok=True)
                    if rec["kind"] == "plain":
                        copy_file(rec["abs_path"], dest_path)
                    elif rec["kind"] == "dedup":
                        reassemble_dedup(rec["abs_path"], block_store_dir,
                                         dest_path, args.verify)
                    else:  # fileref
                        content = resolve_fileref(rec["abs_path"], backup_dir, hash_index)
                        copy_file(content, dest_path)

                    sz = os.path.getsize(dest_path)
                    total_files += 1
                    total_bytes += sz
                    if args.verbose:
                        print(f"  restored {tag}{rec['source_path']} -> {dest_path}"
                              f" ({human_bytes(sz)})")
                except Exception as ex:  # noqa: BLE001 - report and continue
                    msg = f"{rec['source_path']}: {ex}"
                    errors.append(msg)
                    print(f"  ERROR {msg}", file=sys.stderr)

    print("\n" + "=" * 60)
    verb = "Would restore" if args.dry_run else "Restored"
    print(f"{verb}: {total_files:,} file(s)" +
          ("" if args.dry_run else f", {human_bytes(total_bytes)}"))
    if skipped_existing:
        print(f"Skipped (already existed): {skipped_existing:,}  "
              f"(use --overwrite to replace)")
    if errors:
        print(f"Errors: {len(errors):,}")
        for e in errors[:20]:
            print(f"  - {e}")
        if len(errors) > 20:
            print(f"  ... and {len(errors) - 20:,} more")
    print("=" * 60)
    return 1 if errors else 0


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="lithic_restore.py",
        description="Standalone catalog-free restore for LithicBackup directory backups.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Run with just a backup folder for an interactive walkthrough.")
    parser.add_argument("backup_dir", help="The LithicBackup destination folder.")
    parser.add_argument("--map", action="append", metavar="DRIVE=PATH",
                        help="Restore backup drive DRIVE into PATH. Repeatable.")
    parser.add_argument("--include", action="append", default=[], metavar="PATTERN",
                        help="Only restore source paths matching PATTERN. Repeatable.")
    parser.add_argument("--exclude", action="append", default=[], metavar="PATTERN",
                        help="Skip source paths matching PATTERN. Repeatable.")
    parser.add_argument("--prev", action="store_true",
                        help="Also restore previous versions (kept with .v{N} tag).")
    parser.add_argument("--verify", action="store_true",
                        help="Re-hash reassembled .dedup files against their recorded hash.")
    parser.add_argument("--list", action="store_true",
                        help="List drives and file counts, then exit.")
    parser.add_argument("--dry-run", action="store_true",
                        help="Show what would be restored without writing files.")
    parser.add_argument("--overwrite", action="store_true",
                        help="Overwrite existing destination files (default: skip).")
    parser.add_argument("-v", "--verbose", action="store_true",
                        help="Print every file as it is restored.")
    args = parser.parse_args(argv)

    backup_dir = os.path.abspath(args.backup_dir)
    if not os.path.isdir(backup_dir):
        sys.exit(f"error: backup folder not found: {backup_dir}")

    drives = discover_drives(backup_dir)
    if not drives:
        sys.exit("error: no drive folders found - is this a LithicBackup "
                 "directory backup? (expected top-level folders like 'C', 'D')")

    if args.list:
        print(f"Backup: {backup_dir}\n")
        for base in sorted(drives):
            entry = drives[base]
            cur = sum(len(f) for _, _, f in os.walk(entry["current"])) if entry["current"] else 0
            prv = sum(len(f) for _, _, f in os.walk(entry["prev"])) if entry["prev"] else 0
            print(f"  Drive {base}:  {cur:,} current file(s), {prv:,} history file(s)")
        return 0

    if args.map:
        mapping = parse_map_args(args.map)
    else:
        mapping = prompt_for_mapping(drives)

    if not mapping:
        print("No drives selected for restore. Nothing to do.")
        return 0

    # Guard: refuse to restore a drive into the backup folder itself.
    for base, out in mapping.items():
        if os.path.abspath(out).rstrip("\\/").lower() == backup_dir.rstrip("\\/").lower():
            sys.exit(f"error: output for drive '{base}' is the backup folder itself.")

    print(f"\nBackup:  {backup_dir}")
    for base in sorted(mapping):
        print(f"  drive {base}  ->  {os.path.abspath(mapping[base])}")
    if args.dry_run:
        print("(dry run - no files will be written)")
    print()

    return run_restore(backup_dir, drives, mapping, args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        sys.exit(130)
