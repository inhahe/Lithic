# Lithic Backup

Incremental backup software for Windows with disc and directory support, built-in deduplication, configurable version retention, and automated scheduling.

Lithic Backup is designed to make optical media backups practical and painless — handling disc spanning, multisession incremental burns, and automatic consolidation — while also supporting directory-based backups to external drives or network shares with continuous file-watching.

## Features

### Backup Targets

- **Optical media** — CD, DVD, Blu-Ray, and M-Disc with full IMAPI2 burning support
- **Directory** — any local, external, or network-attached folder

### Disc Storage

Backing up to optical media comes with challenges that Lithic Backup handles automatically:

- **Incremental multisession burns**: each backup appends to the last disc rather than wasting a new one, until the disc is full
- **Automatic disc spanning**: files too large for a single disc are split across multiple discs and reassembled transparently during restore
- **Bin-packing**: files are packed onto discs using a first-fit-decreasing algorithm that typically achieves 90%+ disc utilization
- **Automatic consolidation**: when a backup set accumulates too many incremental discs (configurable threshold, default 5), Lithic Backup repacks everything onto fewer optimally-filled discs — keeping the set manageable
- **Catalog on disc**: the SQLite backup catalog is written to the last disc in each set, so you can always identify and locate your files even without the original system
- **Post-burn verification**: when enabled (a per-set option, on by default), every file is read back off the freshly burned disc and its SHA-256 compared against the source — catching a disc that wrote "successfully" but recorded corrupt or truncated data. The drive reloads the media first so the just-written filesystem is mounted; if it can't be re-read the burn is reported complete but unverified rather than failed
- **Capacity overrides**: override the reported disc capacity for media that over-reports (common with M-Disc)
- **Automatic zipping for incompatible paths**: files whose names or paths are too long or contain characters that the target disc filesystem doesn't support can be automatically zipped before burning. Three modes are available: zip all files, zip only incompatible files, or never zip. Zipping is also offered as a fallback when a file fails to stage normally.
- **Filesystem options**: ISO 9660, Joliet, and UDF — UDF is the default and is required for Blu-Ray

### Deduplication

Lithic Backup provides two independent deduplication strategies. Both can be enabled simultaneously — a whole-file hash is checked first, and block-level dedup handles only the files that actually share blocks. In every mode, a file with no duplicate content is stored as an ordinary, normally-named copy, so the destination tree always contains directly-usable files.

- **File-level deduplication**: identical files (matched by SHA-256 hash) are stored only once. Unique content is written as an ordinary, normally-named file; every byte-identical duplicate is written as a lightweight `.fileref` manifest that stores no bytes of its own and resolves, by content hash via the catalog, to whichever plain copy holds the real bytes. There is no separate filestore — exactly one plain copy of each unique content exists somewhere in the backup tree (current or a `_prev` version), and retention guarantees that copy is never deleted while a reference still points to it. Each `.fileref` manifest is self-describing for human inspection and disaster recovery: besides the content hash it records the file's own original source path and a `ContentPath` hint pointing at the plain copy that holds its bytes (the hint is kept up to date as plain copies move between eviction and `_prev`, but the hash remains the authoritative anchor). Effective when many files are exact copies (e.g., dependencies duplicated across projects).

- **Block-level deduplication**: files are split into fixed-size blocks (default 64 KB, configurable), and each block is hashed with SHA-256. A file is stored as a `.dedup` manifest **only when it actually has a duplicate block** — one shared with another file in the same backup, one already in the block store from an earlier version, or one repeated within the file itself. Files with no duplicate blocks are written as ordinary, normally-named copies, exactly as in a non-dedup backup. Each duplicate block is stored once in a content-addressed block store (`_blocks/{hash}.blk`) on the destination; a block whose `.blk` file is already present is referenced rather than stored again. Because the block store lives on the destination and is keyed purely by content hash, it is shared by every backup set that targets the same destination — two sets backing up overlapping data to one drive deduplicate against each other automatically. Block size is recorded per-file, so changing it between runs is safe; old backups always restore correctly with their original block size. (Block dedup applies to directory backups; optical/disc backups do not use it.) To detect cross-file block sharing within a single run, the backup makes a quick analysis pass over the candidate files before writing anything.

- **Combined behavior**: when both are enabled, an exact whole-file duplicate is stored as a `.fileref` (cheapest — nothing new is written). The first copy of that content is kept as an ordinary plain file to anchor the references, so file-level dedup keeps working even with block dedup on. Any other file is stored as a `.dedup` manifest only if it has a duplicate block (e.g. an append-only log that reuses the unchanged blocks of its previous version); a file whose content is entirely unique is written as a plain, normally-named copy. In short: duplicate whole files → `.fileref`, files with some shared blocks → `.dedup`, everything else → a plain file.

### Version Retention

Lithic Backup keeps previous versions of files as they change over time. Retention is managed through **named tier sets** — each tier set defines a policy with any number of tiers, and each tier specifies an age threshold and a maximum version count.

Two built-in tier sets are always available:

- **Default** — age-based tiered retention (configurable tiers, see below)
- **None** — no version history; files are overwritten in place

You can create additional custom tier sets for different types of data. Each tier set contains any number of tiers:

| Tier | Age | Versions Kept |
|------|-----|---------------|
| 1 | < 10 days | All |
| 2 | 10 days – 1 year | 3 |
| 3 | > 1 year | 1 |

This is the default configuration, but every aspect is customizable. You can add, remove, or modify tiers freely — keep 50 versions for the first week, then 10 for a month, then 2 for a year, then 1 forever. The newest version of every file is always preserved regardless of tier rules.

Tier sets are assigned to files by **glob patterns** matched against the file path. Each custom tier set carries a list of file patterns (e.g. `*.iso`, `*\build\*`) plus optional exempt patterns that re-include paths a broad pattern would otherwise capture. Sets are evaluated in display order and the first non-Default set whose patterns match wins; the "Default" set is the implicit fallback for unmatched files. For example, you could assign the "None" tier set the pattern `*\bin\*` so build output is never versioned, while everything else keeps the Default policy.

### Scheduled and Continuous Backups

A Windows Service (Lithic Backup Worker) runs in the background and backs up your files automatically. Three scheduling modes are available:

- **Interval** — run a backup every N hours (default: 24)
- **Daily** — run once per day at a specific time (default: 2:00 AM)
- **Continuous** — track file changes and back up exactly the files that changed, after a configurable quiet period (default: 60 seconds of no changes). On NTFS volumes this uses the USN change journal, which records every change the moment it happens — including those that occur while the Worker is offline — so nothing is missed across reboots or service restarts, and only changed files are versioned rather than rescanning whole drives. Each file is debounced individually (with a 5-minute cap so a constantly-written file can't starve its own backup), which prevents thrashing while you're actively editing.
  - **Non-NTFS volumes** (exFAT, FAT32, network shares, etc.) are supported too, via a live file-system watcher instead of the journal. Because a watcher only sees changes while the Worker is running, these sets are reconciled with a full timestamp/size scan whenever the Worker starts (catching anything changed while it was offline), and if a burst of changes overflows the watcher's buffer the affected folders are rescanned wholesale so nothing is dropped. Between those reconciles, changes stream into the same per-file debounce pipeline as NTFS.
  - **Move/rename detection** — when you rename or move a file or folder within the same drive, continuous mode relocates the existing backup copy in place instead of re-copying its contents. Because the USN journal reports a directory move as a single event, moving a large folder relocates its entire backed-up subtree instantly rather than re-uploading gigabytes. Deduplicated files, file-level references, and the full version history all move along with it — a whole folder relocates even if only some of its files are stored in special formats. (Moves across drives fall back to a normal copy.) If you move a file or folder *out* of a set's selection, its backup is treated exactly like a deletion — the copy and version history are kept until your next Cleanup, then purged.

Each backup set can have its own schedule and mode. The Worker Service is installed, started, and stopped directly from the GUI — no command-line work required.

Only directory-mode backup sets can be scheduled (disc burns require physical media interaction).

### Source Selection

- Treeview file browser with tristate checkboxes — select entire drives, individual directories, or specific files
- New subdirectories are automatically included for parents with "Auto-include new" checked
- **Global exclusion patterns**: each backup set carries a list of glob patterns (one per line, e.g. `*.log`, `temp_*`, `*\bin\*`) that exclude matching files from the backup entirely. Filename patterns match against the file name; patterns containing a path separator match against the full path. Excluded files are never copied or versioned.
- **Pre-backup size calculator**: calculate how much data will be written before actually running the backup. Shows new files, changed files, per-source-root breakdown, destination free space, and whether the data will fit.
- **Post-scan file review**: after a scan, inspect exactly what's about to be backed up in a tristate treeview showing only the incremental delta (new and changed files) with per-directory and per-file sizes. Each row shows a status — files are *New* or *Changed*, and directories aggregate their contents as *New*, *Changed*, or *Mixed*. Click the **Name**, **Status**, **Files**, or **Size** column headers to sort (click again to reverse); the tree defaults to largest-first by size. Deselect anything you don't want, and the running total shows whether the selection now fits the destination's free space. Opens automatically when the destination lacks room for the full backup, or on demand via the set's **Review Files & Back Up…** context-menu item. An optional checkbox also removes the deselected files/folders from the set's sources so future backups skip them too.

### Restore

- Browse backed-up files in a checkbox treeview of directories and files (tristate checkboxes — check a folder to select everything under it) and pick exactly what to restore
- Handles all storage formats transparently: plain files, split files, zipped files, file-deduplicated, and block-deduplicated
- Multi-disc restore with guided disc insertion prompts
- Per-drive destinations: backups spanning multiple drives can be restored to multiple destinations, with one editable target folder per source drive — or restore everything back to its original location in one click
- Preserves original directory structure under each destination

#### Restore Without Catalog (disaster recovery)

If the backup catalog is lost or corrupted, the **Restore Without Catalog** action (on the home screen) rebuilds your files using only the backup destination folder. It picks no backup set — there is none to read — and instead walks the destination tree directly: plain files are copied as-is, block-deduplicated files are reassembled from the shared `_blocks` store, and file-level references follow each `.fileref`'s `ContentPath` hint (verified against the recorded content hash, with a content-hash scan of the tree as a fallback if a hint is stale). The reconstructed files, including any `_prev` version history, are written to an output folder you choose. This is possible because the destination tree is fully self-describing — no database required.

For the worst case — when you can't run Lithic Backup at all — a standalone Python script, `tools/lithic_restore.py`, performs the same catalog-free restore using only the Python standard library (no dependencies, Python 3.8+). Drop it on any machine with Python and point it at a backup folder:

```
python lithic_restore.py D:\MyBackup
```

It lists the drives found in the backup and asks where to restore each one (press Enter to skip a drive). For non-interactive or scripted recovery, map drives explicitly and optionally restore just part of the tree with wildcard include/exclude patterns matched against the original source paths:

```
python lithic_restore.py D:\MyBackup --map C=E:\restored --map D=F:\ ^
    --include "C:\Users\me\projects\*" --exclude "*\node_modules\*"
```

In include/exclude patterns `*` matches across directory separators (so a single `*` spans arbitrarily many folders deep, matching Lithic Backup's own glob convention), `**` is accepted as a synonym, and `?` matches a single character. Other flags: `--prev` (also restore previous versions), `--list` (show drives and file counts), `--dry-run` (preview without writing), `--overwrite` (replace existing files), `--verify` (re-hash reassembled deduplicated files), `-v` (verbose). It handles plain, block-deduplicated, and file-referenced files exactly like the in-app catalog-free restore.

#### Upgrading an old-format backup

Early versions stored *every* file as a `.fileref` pointer with the real bytes pooled in a content-addressed `_filestore` directory. Current backups instead keep the first copy of each unique file as a plain, normally-named file in place and write a `.fileref` only for genuine duplicates. A standalone Python script, `tools/lithic_convert_to_new_format.py`, upgrades an old-format destination in place without re-copying anything from the source — it **moves** each content blob out of `_filestore` onto one of the files that reference it (preferring a current copy over a `_prev` history copy) and rewrites the remaining same-content `.fileref` files into the current format. The mapping is keyed entirely by SHA-256 hash, the move is a fast same-volume rename, and the run is crash-safe and resumable.

The same run also **converts the catalog**, so you do not have to re-seed. For each placed file it flips that file's catalog row from a file-reference to a plain record (and points its `DiscPath` at the now-plain file). Because the old file-reference rows already store the real size and content hash, this is far faster than **Seed from existing backup** (which re-walks and re-hashes the whole destination). The matching backup set is found automatically from the destination directory; the catalog edit is idempotent (keyed by each anchor's stored path), and a disk-verified reconciliation pass self-heals any rows left behind by an earlier file-only run. **Close the Lithic Backup app and stop the Lithic Backup Worker service first**, or the database will be locked.

Run it with `--dry-run` first to preview, `--verify` to re-hash every blob before moving it, and `--delete-orphans` to remove pooled blobs that nothing references:

```
python lithic_convert_to_new_format.py D:\MyBackup --dry-run
python lithic_convert_to_new_format.py D:\MyBackup
```

By default the catalog is taken from `C:\ProgramData\LithicBackup\catalog.db`; use `--catalog PATH` to point elsewhere, `--set-id N` to pick a set explicitly instead of auto-detecting it from the destination, or `--no-catalog` to convert the destination files only (you can rebuild the catalog later with **Seed from existing backup**). Block-deduplicated backups (`_blocks`, `.dedup`) are not handled and the run aborts if it detects them.

If a destination has drifted out of sync with its catalog — the catalog records files that are no longer on the backup disk — add `--recover-from-source` to fill the gaps. After converting, it copies each **current** missing file back from its original source path, re-reading and re-hashing it so a file that *changed* since the backup is stored with its real current size, hash, and timestamp (so the next incremental backup won't re-copy it). Only current files are recovered: older **retained versions can't be reconstructed from the current source, so they are deliberately left out**, and a current file whose source is *also* gone was deliberately deleted at the source, so its catalog row is marked deleted (the same soft-delete the app applies when a backup finds a source file removed). It never overwrites a path another catalog entry already claims, and re-running only copies what is still missing. This requires a catalog (it can't be combined with `--no-catalog`) and the relevant source drives must be mounted; run it with `--dry-run` first to see exactly what would be recovered, skipped, or reported as unrecoverable.

### Testing Without Hardware

Pass `--test-mode` on the command line to unlock the testing features. Test mode does not change anything on its own — it surfaces opt-in checkboxes that you must tick to actually use a simulated drive or stubbed content. Until you do, real hardware and real backups are used exactly as in normal operation.

When configuring a **disc** backup in test mode, a **Use simulated burner (test mode)** checkbox appears. Tick it to replace the real IMAPI2 disc burner with a simulated drive. The simulated burner copies files to a local "disc shelf" directory (`%LOCALAPPDATA%/LithicBackup/simulated-discs/`) instead of writing to physical media, so scan, burn, verify, and restore workflows can all be tested without an optical drive. A warning notes that no disc is written and the operation is non-functional.

Each simulated burn creates a `disc-N/` directory containing the actual file content and a `_manifest.json` with per-file metadata (path, size, SHA-256 hash). Burns simulate realistic timing based on a configurable speed multiplier.

A **Metadata only** toggle (under "Disc shelf storage" on the home screen) recreates the full directory tree and real filenames on the shelf, but writes a tiny stub holding each file's hash and size in place of the real bytes — so the shelf mirrors the true structure for easy inspection without duplicating tens of gigabytes onto your system drive. This is sufficient for exercising burn timing, capacity/bin-packing, disc spanning, and failure injection. The trade-off is that *restore* (and block-dedup round-trips that read stored bytes back) can't reconstruct content from a metadata-only disc, so leave the toggle off when testing those.

In test mode the home screen exposes simulated-burner failure injection controls split by when each failure can occur.

**Pre-burn failures** are armable toggles on the home screen — set them before starting a backup to test errors that happen before any data is written:

- **No recorder** -- the drive enumeration returns no recorders, as if no optical drive were attached
- **No media** -- a recorder is present but reports no disc inserted
- **Erase fails** -- the next erase operation fails

**While-burning failures** appear as buttons on the running set's progress display, for errors that occur during the burn:

- **File Write Error** -- causes the next file write to fail with an I/O error
- **Catastrophic Disc Failure** -- triggers an immediate unrecoverable disc error (simulates laser failure or disc ejection)
- **Verify Fails** -- the post-burn verification reports a data mismatch on the disc

The while-burning buttons reset automatically when a new burn starts; the pre-burn toggles persist until you turn them off. All of these controls only appear in test mode.

For **directory** backups, test mode adds a **Stub plain (non-deduped) file content** checkbox in the directory-backup options (alongside the deduplication settings). It helps exercise deduplication without filling your drive: when enabled, directory backups write plain files as tiny hash+size stubs while keeping the block store (`_blocks`) and all `.dedup`/`.fileref` manifests fully real — so both block- and file-level dedup still restore and verify correctly, but any plain-copied file is non-functional and can't be restored. The result is not a real backup, only a testing aid; the checkbox carries a warning to that effect, and it must never be used for a backup you intend to keep.

### Other Features

- **Cleanup** — a single unified view that surfaces files and directories worth removing across the catalog and the backup destination. Categories include directories removed from sources, directories deleted from disk, files matching configured or manual exclusion patterns, excess versions beyond the retention tiers (limited to actual `_prev` records on disk, so the category stays empty when no real history exists yet), and catalog duplicates (stray rows pointing at the current copy of a file — typically left over from re-running "Seed from existing backup" on the same destination). An optional "Scan destination filesystem" step adds two more categories — untracked files (present on disk but not in the catalog) and catalog-deleted files (marked deleted but still physically present). The "Clean Selected" action both purges the matching catalog records and physically removes the corresponding files from the destination (when one is configured), then sweeps any newly-empty directories. Catalog-duplicate cleanups remove only the extra rows; the physical file is left alone. The same view also offers a **Reconcile Catalog with Destination** tool that repairs catalog rows that have drifted out of sync with what's actually on disk: it flips stale file-reference rows back to plain when their content has been materialised into a real file (hash-verified before any change), and prunes active rows whose backing content is missing from the destination. Reconcile is dry-run first — **Analyze** reports exactly what it would change and nothing is modified until you click **Apply** — and it never prunes when the destination is disconnected or empty, so an unmounted drive can't be mistaken for a mass deletion.
- **Copy backup sets** — duplicate a backup set as a starting point for a new one. A dialog lets you name the copy and choose which parts to carry over with checkboxes: source selections, settings (deduplication, filesystem, verification), retention tier sets, exclusion patterns, and schedule. The destination directory is an editable field pre-filled with the original's target — keep it to back up to the same place, or change/clear it to back up the same sources to a *different* location (the default intent). By default the new set starts with an empty backup history so it treats every source file as new on its first run. The **Backup history** option (copy the catalog's record of what's already backed up so the duplicate picks up incrementally) becomes available only when the destination is left unchanged from the original.
- **Edit backup sets** — modify sources, options, name, and schedule of existing sets. When you *save* a change to a set's source selection, Lithic Backup reconciles the destination with the new sources instead of waiting for the next Cleanup: if you dropped folders, it shows a read-only review tree of the backed-up files that are no longer covered and offers to remove their destination copies immediately — an all-or-nothing purge that also marks the catalog rows deleted and repairs any references left stale by the deletions (the same reconcile the Cleanup view runs). If you added folders, it shows a matching preview tree of the files newly covered and offers to back them up right away. Both review trees group files by location (deeper folders collapsed) and are click-sortable by name, file count, or size (shown in bytes, as elsewhere); neither has per-item checkboxes since each action is all-or-nothing. The two offers are independent, so both can appear after an edit that both added and removed sources; declining either just leaves that work for Cleanup or the next backup. Nothing is prompted when you close the dialog without saving. The modify dialog also includes a **Clear Backup History** action that wipes the catalog's record of what's been backed up (disc and file entries) while keeping all settings — the next backup then re-copies everything. Files already written to the destination are not deleted.
- **Drive-letter resilience** — each directory-backup set remembers both its destination *and* its source drives by their stable Windows volume identities (volume GUIDs), not just their drive letters. If Windows reassigns a drive a different letter (common with external/USB drives), Lithic Backup detects this when the set next runs, follows the volume to its new letter automatically, updates the stored path (for a source, it rewrites the drive letter in the set's source selection), and notes the change (a status message in the app, a log entry in the background service). You keep picking and seeing a familiar drive letter; the durable identity underneath does the work. Sets created before this feature backfill their volume identities the first time each drive is connected and resolvable.
- **Missing-source warning** — if a set's source location isn't present when a backup starts (e.g. an unplugged external drive, so a scan would otherwise silently find nothing to copy), Lithic Backup speaks up instead of quietly backing up nothing. In the app it warns and, when *some* sources are still reachable, asks whether to proceed with those; when *none* are reachable it refuses to start. The background Worker logs the same and skips the run so it retries once the drive returns. (An absent source never causes catalog entries to be treated as deletions — those files simply stay as they are until the source is back.)
- **Change destination** — relocate a backup set's target directory to a different drive or folder (where the files have already been moved) without re-running the backup. Routine drive-letter changes no longer need this — they're followed automatically (see above); this is for genuine relocations. Changing the destination re-captures the new location's volume identity.
- **Remap source drive** — point a backup set's catalog at a different *source* drive that holds the same directory structure (e.g. the data that was on `E:\` now lives on `F:\`). Lithic Backup rewrites the source drive letter in every catalog record and in the set's own source selection, so the next backup treats the already-backed-up files as present instead of re-copying everything. The destination backup files aren't moved — they migrate naturally as source files change. Available from the backup set's right-click menu.
- **Seed from existing backup** — import files from an existing mirror-format backup directory (e.g. a backup4all mirror or robocopy mirror) into the catalog so future backups are incremental rather than re-copying everything. The seed operation scans the destination directory, hashes each plain file, and creates catalog entries. It also imports Lithic Backup's own stored-content manifests: a `.fileref` (a file-level duplicate) is recorded as a file-reference entry and a `.dedup` (a block-deduplicated file) as a deduplicated entry, taking each file's real size, content hash, and (for new-format filerefs) original source path straight from the manifest rather than re-hashing — so duplicates and deduplicated files are represented in the seeded catalog instead of being skipped. Idempotent: re-running it on the same destination skips files already in the catalog, and files under any `_prev` subdirectory are ignored so versioned-history copies from the source tool don't pollute the catalog.
- **Concurrent backups** — each backup set runs independently in the GUI, with its own progress display, pause/abort controls, and completion summary shown inline on the set's row. Starting a backup on one set never freezes the window or disables the actions on the others, so multiple sets can scan, copy, and verify at the same time.
- **Verify integrity** — a per-set action (and right-click menu item) that re-scans the sources and checks the backup against them: every current source file is still represented in the catalog, every backed-up file's content actually exists on disk (plain copies, `.dedup` manifests and the blocks they reference, and `.fileref` manifests), and every `.fileref` resolves to a real plain copy of its content. A **Verify file contents** option goes further: when enabled it reads every stored file and block back off the destination and re-computes its SHA-256, catching silent corruption / bit-rot that an existence check can't see — at the cost of reading the whole backup. With the option off (the default) the check is fast and existence-only, but it *still* confirms that every block a `.dedup` manifest references is present in the block store. Toggle the option and click **Verify** to re-run without reopening the dialog. Any broken references, missing files, missing blocks, or content mismatches are listed so you can re-run the backup to repair them. (Directory-mode backups only — verify an optical set by inserting its discs through Restore.)
- **Test disc** — the optical-set counterpart to Verify integrity: a per-set right-click action that integrity-tests one already-burned disc against the catalog. Pick which disc from the set you're testing and insert it; Lithic Backup auto-detects the optical drive by matching the disc's volume label (with a drive dropdown and Refresh as fallback), then confirms every non-deleted file the catalog says lives on that disc is physically present at the right size. By default it also does a full **Verify file contents** pass — reading each stored file back and re-computing its SHA-256 to catch bit-rot on a decaying disc, which almost always shows up in the middle of a file's data where an existence-and-size check can't see it. It understands every on-disc storage form (plain, zipped, split, `.dedup` block store, and `.fileref` file-level dedup). You can untick content verification for a faster existence + size only pass. If the disc fails, two repairs are offered: **Re-burn whole disc** re-stages every file the disc held from the live sources and burns a fresh replacement disc, and **Re-burn N affected file(s)** re-burns only the files that failed onto a new supplementary disc — keeping the old disc's good files while superseding the bad records so restores resolve to the fresh copies. Both repairs read from the live source files, so a source that no longer exists on disk is skipped (recover it from another good disc instead). (Optical/disc-based sets only — directory-mode sets use Verify integrity.)
- **Right-click context menu** on backup sets — quick access to all operations (Backup, Restore, Modify, Cleanup, Backup Coverage, Verify Integrity, Test Disc, Largest Files, Copy, Change Destination, Remap Source Drive, Export)
- **Memory budget** — directory backups can read each file from disk only once, buffering its contents in RAM to serve both the deduplication analysis pass and the actual write (instead of re-reading it). How much memory this may use is configurable under **File → Settings**. The default is **Automatic**: use up to a percentage of total RAM (50%) but always leave a set amount of currently-available RAM free for other programs (2 GB) — i.e. `min(percent of total, available − reserve)` — so a backup never starves the rest of the system. You can instead pick a **Fixed** amount in GB, and the dialog shows your live system memory and the resulting budget. The setting is machine-global and shared by the GUI and the background Worker, so it applies to both manual and scheduled backups. Files larger than the remaining budget fall back to streaming from disk, so any budget (including 0, which disables buffering) is always correct — it only affects speed.
- **Diagnostic & crash logging** — both the GUI and the background Worker Service capture unhandled exceptions and write full stack traces (plus process/user/OS context) to a shared logs directory at `C:\ProgramData\LithicBackup\logs`. Each fatal error also produces a standalone, timestamped `crash-{component}-{timestamp}.log` report that's easy to attach to a bug report, while routine activity goes to a rolling daily `lithic-{component}-{date}.log`. If the GUI hits an unexpected error it tells you where the report was written. Because the Worker normally logs to the Windows Event Log (awkward to inspect), this gives the service the same durable, human-readable trail — so a hang or crash mid-backup leaves evidence behind. Managed handlers can't catch every crash, though: on modern .NET, *native* faults such as access violations, stack overflows and COM/interop crashes bypass them entirely. To cover those, the Worker Service (which runs as LocalSystem and can write the required machine-wide registry keys) enables **Windows Error Reporting local dumps** for both executables, so a native crash drops a post-mortem `.dmp` into `C:\ProgramData\LithicBackup\logs\dumps` for later analysis.

## Architecture

```
LithicBackup.sln
├── LithicBackup.Core           Models, interfaces, exceptions
├── LithicBackup.Infrastructure  File scanning, deduplication engines,
│                                disc burning (IMAPI2), SQLite catalog
├── LithicBackup.Services        Backup orchestration, restore, retention,
│                                directory backup, planning, consolidation
├── LithicBackup                 WPF desktop application
└── LithicBackup.Worker          Windows Service for automated backups
```

## Installation

Run the **Lithic Backup MSI installer** (`LithicBackup-<version>-x64.msi`). It:

- installs the app to `C:\Program Files\Lithic Backup` (self-contained — no
  separate .NET runtime needed on the target machine),
- adds a **Start Menu** shortcut, so Lithic Backup shows up like any other app,
- installs and starts the **Lithic Backup Worker** Windows service automatically,
  so scheduled/continuous backups work out of the box,
- registers a normal **Add or Remove Programs** entry that stops and removes the
  service and deletes all files on uninstall, and
- offers a **Launch Lithic Backup** checkbox on the final wizard page (ticked by
  default) so the app starts as soon as you finish installing.

Because it installs a service, the installer requests administrator elevation.

> The Worker service can still be installed/started/stopped from within the GUI
> (see *Scheduled and Continuous Backups*) — the installer just does it for you
> up front. If you previously installed the service manually from the GUI,
> remove it there (or with `sc delete "Lithic Backup"`) before running the MSI so
> the installer can manage its own copy.

## Requirements

- Windows 10 or later (64-bit)
- Optical drive (for disc backups only)

The MSI is self-contained, so end users do **not** need the .NET runtime
installed. Building from source requires the .NET 8.0 SDK.

## Building

Build the apps:

```
dotnet build LithicBackup.sln
```

### Building the installer

The MSI is built with the [WiX Toolset](https://wixtoolset.org/) v5 (invoked via
the `wix` dotnet tool). The build script publishes both executables self-contained
and compiles the MSI:

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.0.0
```

It installs the `wix` tool and the WiX UI and Util extensions on first run if
they're missing, then writes `installer\LithicBackup-<version>-x64.msi`. The WiX
source lives in `installer\Package.wxs`.

## License

See [LICENSE](LICENSE) for details.
