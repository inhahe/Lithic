# LithicBackup

Incremental backup software for Windows with disc and directory support, built-in deduplication, configurable version retention, and automated scheduling.

LithicBackup is designed to make optical media backups practical and painless — handling disc spanning, multisession incremental burns, and automatic consolidation — while also supporting directory-based backups to external drives or network shares with continuous file-watching.

## Features

### Backup Targets

- **Optical media** — CD, DVD, Blu-Ray, and M-Disc with full IMAPI2 burning support
- **Directory** — any local, external, or network-attached folder

### Disc Storage

Backing up to optical media comes with challenges that LithicBackup handles automatically:

- **Incremental multisession burns**: each backup appends to the last disc rather than wasting a new one, until the disc is full
- **Automatic disc spanning**: files too large for a single disc are split across multiple discs and reassembled transparently during restore
- **Bin-packing**: files are packed onto discs using a first-fit-decreasing algorithm that typically achieves 90%+ disc utilization
- **Automatic consolidation**: when a backup set accumulates too many incremental discs (configurable threshold, default 5), LithicBackup repacks everything onto fewer optimally-filled discs — keeping the set manageable
- **Catalog on disc**: the SQLite backup catalog is written to the last disc in each set, so you can always identify and locate your files even without the original system
- **Post-burn verification**: data integrity is verified against the disc after each burn
- **Capacity overrides**: override the reported disc capacity for media that over-reports (common with M-Disc)
- **Automatic zipping for incompatible paths**: files whose names or paths are too long or contain characters that the target disc filesystem doesn't support can be automatically zipped before burning. Three modes are available: zip all files, zip only incompatible files, or never zip. Zipping is also offered as a fallback when a file fails to stage normally.
- **Filesystem options**: ISO 9660, Joliet, and UDF — UDF is the default and is required for Blu-Ray

### Deduplication

LithicBackup provides two independent deduplication strategies. Both can be enabled simultaneously — file-level dedup is checked first, and block-level kicks in for files that aren't exact duplicates but share content.

- **File-level deduplication**: identical files (matched by SHA-256 hash) are stored once in a content-addressed filestore. Each duplicate is replaced with a lightweight `.fileref` manifest pointing to the canonical copy. Effective when many files are exact copies (e.g., dependencies duplicated across projects).

- **Block-level deduplication**: files are split into fixed-size blocks (default 64 KB, configurable), and each block is hashed with SHA-256. Blocks that already exist in the catalog are referenced rather than stored again. Only files that actually save space are stored in deduplicated format — otherwise they're copied as-is. Block size is recorded per-file, so changing it between runs is safe; old backups always restore correctly with their original block size.

### Version Retention

LithicBackup keeps previous versions of files as they change over time. Retention is managed through **named tier sets** — each tier set defines a policy with any number of tiers, and each tier specifies an age threshold and a maximum version count.

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

Tier sets are assigned per-file or per-directory in the source selection tree. Nodes inherit their parent's tier set by default — assign a tier set to a top-level directory and everything underneath follows, with the option to override at any level. For example, you could use "Default" for most files but assign "None" to build output directories.

### Scheduled and Continuous Backups

A Windows Service (LithicBackup Worker) runs in the background and backs up your files automatically. Three scheduling modes are available:

- **Interval** — run a backup every N hours (default: 24)
- **Daily** — run once per day at a specific time (default: 2:00 AM)
- **Continuous** — watch source directories for file changes and trigger a backup after a configurable quiet period (default: 60 seconds of no changes). This prevents thrashing while you're actively editing files — the backup waits until you stop.

Each backup set can have its own schedule and mode. The Worker Service is installed, started, and stopped directly from the GUI — no command-line work required.

Only directory-mode backup sets can be scheduled (disc burns require physical media interaction).

### Source Selection

- Treeview file browser with tristate checkboxes — select entire drives, individual directories, or specific files
- New subdirectories are automatically included under fully-selected parents
- **Per-directory exclusion patterns** (glob syntax): attach patterns like `*.log`, `temp_*`, `*/bin/*` to any directory node. Patterns are inherited by all child directories automatically.
- **Per-directory re-include patterns**: override inherited exclusions at any level. For example, exclude `*.dll` at the project root but re-include it under a `lib/` subdirectory.
- Per-node retention tier set assignment with inheritance from parent directories — assign any custom tier set to any file or directory, and children inherit unless overridden

### Restore

- Browse all backed-up files and select what to restore
- Handles all storage formats transparently: plain files, split files, zipped files, file-deduplicated, and block-deduplicated
- Multi-disc restore with guided disc insertion prompts
- Preserves original directory structure

### Testing Without Hardware

Pass `--simulate-burner` on the command line to replace the real IMAPI2 disc burner with a simulated drive. The simulated burner copies files to a local "disc shelf" directory (`%LOCALAPPDATA%/LithicBackup/simulated-discs/`) instead of writing to physical media, so scan, burn, verify, and restore workflows can all be tested without an optical drive.

Each simulated burn creates a `disc-N/` directory containing the actual file content and a `_manifest.json` with per-file metadata (path, size, SHA-256 hash). Burns simulate realistic timing based on a configurable speed multiplier.

While a simulated burn is in progress, the UI displays failure injection buttons:

- **File Write Error** -- causes the next file write to fail with an I/O error
- **Catastrophic Disc Failure** -- triggers an immediate unrecoverable disc error (simulates laser failure or disc ejection)
- **Arm Erase Failure** -- causes the next erase operation to fail

These buttons only appear in `--simulate-burner` mode and reset automatically when a new burn starts.

### Other Features

- **Orphaned directory cleanup** — detect directories that were removed from source selections, deleted from disk, or match exclusion patterns, and purge them from the catalog
- **Copy backup sets** — duplicate a backup set's configuration (sources, options, schedule, retention tiers) as a starting point for a new set
- **Edit backup sets** — modify sources, options, name, and schedule of existing sets
- **Change destination** — relocate a backup set's target directory (e.g., when an external drive changes drive letters) without re-running the backup

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

## Requirements

- Windows 10 or later
- .NET 8.0
- Optical drive (for disc backups only)

## Building

```
dotnet build LithicBackup.sln
```

## License

See [LICENSE](LICENSE) for details.
