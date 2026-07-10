#!/usr/bin/env python3
"""Generate a test source tree for LithicBackup dedup testing.

Creates a directory containing:
  * Exact-duplicate files (identical bytes)         -> exercises file-level dedup (.fileref)
  * Files that share whole 64 KB blocks but differ  -> exercises block-level dedup (.dedup)
  * Plain unique files                              -> stored as-is

The default LithicBackup dedup block size is 64 KB, so the shared regions below
are built from whole 64 KB blocks and the files exceed that size.
"""
import os
import sys

BLOCK = 64 * 1024  # default dedup block size


def block(seed: int) -> bytes:
    """Deterministic, incompressible-ish 64 KB block keyed by `seed`."""
    b = bytearray(BLOCK)
    x = (seed * 2654435761 + 1) & 0xFFFFFFFF
    for i in range(BLOCK):
        x = (1103515245 * x + 12345) & 0xFFFFFFFF
        b[i] = (x >> 16) & 0xFF
    return bytes(b)


def write(path: str, data: bytes) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    print(f"  {os.path.relpath(path, ROOT):<40} {len(data):>9,} bytes")


def main() -> None:
    global ROOT
    base = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "test_source")
    ROOT = os.path.abspath(base)
    if os.path.exists(ROOT):
        print(f"Refusing to overwrite existing path: {ROOT}", file=sys.stderr)
        sys.exit(1)

    print(f"Creating test source tree at: {ROOT}\n")

    # Pre-build named blocks so we can compose files that share them.
    A = block(1)   # shared block A
    B = block(2)   # shared block B
    C = block(3)   # shared block C
    U1 = block(101)
    U2 = block(102)
    U3 = block(103)
    U4 = block(104)

    # ----- Exact-duplicate files (file-level dedup) ----------------------
    # Identical content in three different locations / names.
    print("Exact-duplicate files (file-level dedup):")
    report = b"LithicBackup test report\n" * 5000  # ~125 KB, well over a block
    write(os.path.join(ROOT, "docs", "report.txt"), report)
    write(os.path.join(ROOT, "docs", "report_copy.txt"), report)
    write(os.path.join(ROOT, "archive", "old", "report-backup.txt"), report)

    # A second set of exact duplicates, binary.
    blob = U1 + U2 + U3  # 192 KB
    write(os.path.join(ROOT, "bin", "data.bin"), blob)
    write(os.path.join(ROOT, "bin", "data_dup.bin"), blob)

    # ----- Block-sharing files (block-level dedup) ----------------------
    # These differ overall but share whole 64 KB blocks with each other.
    print("\nBlock-sharing files (block-level dedup):")
    # file1: A B C U4   (256 KB)
    write(os.path.join(ROOT, "media", "file1.dat"), A + B + C + U4)
    # file2: A B U2 C   shares A,B,C with file1 but reordered/changed (256 KB)
    write(os.path.join(ROOT, "media", "file2.dat"), A + B + U2 + C)
    # file3: U3 A B C   shares A,B,C, different first block (256 KB)
    write(os.path.join(ROOT, "media", "sub", "file3.dat"), U3 + A + B + C)
    # file4: A A B B    repeats shared blocks within one file (256 KB)
    write(os.path.join(ROOT, "media", "file4.dat"), A + A + B + B)

    # ----- Plain unique files ------------------------------------------
    # These share no whole-file content and no blocks with anything else, so
    # they must be stored as plain, normally-named files (not .dedup/.fileref).
    print("\nPlain unique files:")
    write(os.path.join(ROOT, "notes.txt"), b"just a small unique text file\n")
    write(os.path.join(ROOT, "media", "unique.dat"),
          block(900) + block(901) + block(902))

    print(f"\nDone. Back up this folder with LithicBackup (dedup enabled),")
    print(f"then test restore with:  python tools/lithic_restore.py <destination>")


if __name__ == "__main__":
    main()
