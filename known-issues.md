# LithicBackup — Known Issues & Tech Debt

## Catalog bloat: duplicate `Files` rows from repeated seed/import runs

**Status:** Data residue in existing catalogs. Root cause in old code; current code
(with the seed idempotency guard in `DirectoryBackupService` import path) no longer
creates these. Latent query bug fixed (see below). Existing duplicate rows remain
until a cleanup pass is run.

**Symptom:** The `Files` table accumulates many byte-identical non-deleted rows for
the same `(SourcePath, Version)`. In the reference catalog (`%LocalAppData%\LithicBackup\catalog.db`,
backup set 4) there were ~1.63M non-deleted rows for ~1.02M distinct paths — i.e.
~611k duplicate rows. A single VHDX had 5 identical v1 rows across discs 13–17.

**How it happened:** Discs 13–18 (2026-05-19) and disc 19 (2026-05-27) were full
seed/import runs executed on a build *before* the idempotency guard existed. Each run
re-inserted every path as a fresh `Version = 1` record (discs 13–18 are 100% v1).
Disc 20 (2026-06-08) is healthy (15 new + 17 changed), confirming current code does
not reproduce the bloat.

**Impact:** Bloat only. Verified that **0** duplicate groups differ in size or hash —
all tied rows are byte-identical — so change-detection and restore return correct
results. No data loss. Costs: larger DB, slower queries, and the latent
non-determinism described next.

**Fixed (code):** `SqliteCatalogRepository.GetLatestVersionInfoAsync` previously used
`MAX(Version)` with bare columns, relying on SQLite's non-standard single-aggregate
rule. With duplicate max-version rows it returned an *arbitrary* tied row's
size/mtime — harmless only because the dups are identical. Rewritten to use
`ROW_NUMBER() OVER (PARTITION BY SourcePath ORDER BY Version DESC, Id DESC)` for a
deterministic newest-row pick.

**Cleanup performed (2026-06-09):** Ran on the reference catalog with the service
stopped. Backed up `catalog.db`/`-wal`/`-shm` to `catalog_backup_20260609_115129`,
checkpointed the WAL, deleted 608,803 duplicate rows (non-deleted: 1,631,141 →
1,022,338; distinct paths unchanged at 1,019,448; 0 duplicate groups remaining),
then `VACUUM` (1.11 GB → 770 MB). Integrity `ok` before and after. Phantom
empty-hash/size-0 rows were intentionally left in place. The procedure below is
retained for future catalogs.

**Cleanup procedure (destructive, needs the service stopped + a DB backup first):**
1. Stop the LithicBackup Worker service (releases the DB / WAL).
2. Back up `catalog.db` (+ `-wal`, `-shm`).
3. De-duplicate, keeping the lowest `Id` per `(SourcePath, Version)` among non-deleted rows:
   ```sql
   DELETE FROM Files
   WHERE IsDeleted = 0
     AND Id NOT IN (
       SELECT MIN(Id) FROM Files WHERE IsDeleted = 0
       GROUP BY SourcePath, Version
     );
   ```
   (Scope to the set via the `Discs` join if multiple sets ever exist.)
4. `VACUUM;` to reclaim space.
5. Restart the service.

Note: also consider whether the ~6,014 non-deleted empty-hash/size-0 "phantom" rows
are legitimate (genuinely empty files in the mirror at seed time) before deleting —
in the reference data `forum.py`'s size-0 v1 appears to reflect a real 0-byte mirror
copy later superseded by real content (v2), i.e. valid history, not corruption.

## Obsolete per-node version-retention fields removed (2026-06-11)

**Status:** Done — recorded for history. Previously a doc/code inconsistency: the
README's Source Selection section described per-node/per-directory retention
mechanisms whose backing model fields were `[Obsolete]` and no longer consumed.
Resolved by deleting the fields and the stale README bullets (option (b)).

**What was removed:** From `SourceSelection.cs`, the four obsolete fields —
`VersionTierSetName`, `KeepVersionHistory`, `VersionExcludedPatterns`,
`VersionIncludedPatterns`. They were referenced only within that file; versioning
is governed entirely by `VersionTierSet.FilePatterns` / `FileExemptPatterns` (the
global pattern-based tier resolver in `VersionTierSet.BuildTierResolver`), and
exclusion by `JobOptions.ExcludedExtensions`. The two README bullets
("Per-directory version retention filters" and "Per-node retention tier set
assignment") were dropped; the Version Retention section now documents the
FilePatterns-based assignment. Old serialized `SourceSelections` JSON still
deserializes fine (System.Text.Json ignores unknown members).

## Per-set dedup block index over a destination-shared block store (2026-06-11)

**Status:** RESOLVED (2026-06-11). Fixed by eliminating the catalog block index
entirely rather than relocating it — see "Resolution" below.

**Resolution:** Investigation showed the `DeduplicationBlocks` catalog table was
**not load-bearing**: nothing read `BlockReference.BlockId` or `ReferenceCount`,
restore reassembles from `_blocks/{hash}.blk` by hash (`RestoreService.cs`), and
`WriteNewBlocksAsync` already guards every physical write with `File.Exists`. So
the destination's content-addressed `_blocks/` store was always the real index.
`BlockDeduplicationEngine` now decides block presence directly with
`File.Exists(<<blockStoreDir>>/{hash}.blk)` (plus an in-recipe `HashSet` for
repeats within one file) — exactly how whole-file dedup already works against
`_filestore/{hash}.dat`. The engine takes the resolved live `_blocks` directory
(`DeduplicateAsync(blockStoreDir, …)`) instead of a `backupSetId`, so dedup is
shared by every set on the destination (true cross-set dedup) and follows the
drive across letter changes via the destination resolver. The three catalog block
methods (`FindBlockByHashAsync`/`CreateBlockAsync`/`IncrementBlockReferenceAsync`)
were removed from `ICatalogRepository`, `SqliteCatalogRepository`, and
`SqliteSetDatabase`; `BlockDeduplicationEngine` no longer depends on the catalog.
This also dissolves the unsafe-GC concern (no per-set index to diverge) and makes
a "reseed block info from destination" operation unnecessary — there is no index
to rebuild because the store is authoritative. The per-set `DeduplicationBlocks`
table definition + one-time legacy import remain but are now inert (never read).

**Bonus finding (fixed):** `BackupOrchestrator` (optical/disc backups) called the
dedup engine and **discarded the recipe** apart from a progress message — it never
stored blocks (optical media has no persistent `_blocks` store). That dead call was
removed; block dedup is now explicitly a directory-backup-only feature. Disc-level
block dedup was therefore never actually functional and is not a regression.

---

**Original context (for history):** The per-set catalog split (migration 005) moved the
`DeduplicationBlocks` table into each set's database (`sets/set-{id}.db`). But the
*physical* block store `_blocks/{hash}.blk` lives under `JobOptions.TargetDirectory`
and is **shared by every set that targets the same destination path**
(`DirectoryBackupService.cs:143`). So the dedup *index* is per-set while the store
it indexes is per-destination.

**Consequences:**
1. **No cross-set block dedup.** Two sets pointing at the same destination don't
   see each other's blocks in the catalog, so each independently re-hashes and
   re-writes byte-identical `.blk` files. Harmless (content-addressed, idempotent)
   but wasteful.
2. **Ref-count-based GC would be unsafe.** `DeduplicationBlock.ReferenceCount` is
   currently only ever *incremented* — there is **no** block GC / `.blk` deletion
   anywhere in the code today, so this is latent. But if block pruning is ever
   added, a per-set index cannot see another set's references to a shared `.blk`
   and could delete a block another set still needs.

**Original plan (superseded):** The first plan was to relocate the block index to a
per-destination DB (`<<destination>>/_dedup.db`) next to `_blocks/`. The actual fix
went further and removed the index altogether (see "Resolution"), since the
content-addressed store already provides everything a `_dedup.db` would have — and
with no second source of truth to keep in sync.

## Per-folder exclusion patterns removed (2026-06-11)

**Status:** Done — recorded for history. The per-directory file *exclusion* /
re-include pattern feature was fully dead (no live callers) and has been removed:
`SourceSelection.ExcludedPatterns`/`IncludedPatterns`, `GlobMatcher.CreateCombinedFilter`/
`CreateTreeFilter`/`CollectTreePatterns`, and the orphaned `ExclusionEditorDialog` +
`ExclusionEditorViewModel`. Old serialized `SourceSelections` JSON containing these
properties still deserializes fine (System.Text.Json ignores unknown members). Kept:
`GlobMatcher.CreateFilter` (used by tier-set patterns + global excluded extensions).

## Block-dedup pre-pass holds all recipes in memory (2026-06-12)

**Status:** Tech debt — acceptable for typical directory backups, may matter for very
large ones. Not currently a bug.

**Context:** `DirectoryBackupService.ExecuteAsync` now runs a block-dedup pre-pass
(only when block-level dedup is enabled) so it can decide, before writing anything,
which files actually have duplicate blocks — a file becomes `.dedup` only if it shares
a block with another file in the run, with an earlier version in the store, or with
itself; otherwise it is stored as a plain, normally-named file. Cross-file sharing
within one run can't be detected file-by-file, hence the up-front pass. It builds
`preRecipes` (path → full `DeduplicationRecipe`, i.e. the ordered list of block hashes
per file), `wholeFileCount`, and `blockOccur`, and keeps `preRecipes` for the whole
run so the main loop can reuse each file's hash + recipe without re-reading it.

**Impact:** Peak memory for `preRecipes` is O(total blocks across all new/changed
files) ≈ one 64-char hash string + object overhead per 64 KB of data (~0.1–0.2 % of
the data size). For a few-GB backup this is negligible; for a 100 GB+ run with
millions of small blocks it could be hundreds of MB.

**Separate, bounded buffer (2026-06-12, single-read optimization):** The pre-pass now
also reads each file's *bytes* into an in-memory cache (`bufferedContent`) so the main
loop writes that file's blocks / plain copy straight from memory — every file is read
from disk exactly once for both analysis and writing (previously `.dedup` files were
read twice: once to analyse, once by `WriteNewBlocksAsync`). This byte cache is capped
by a configurable memory budget — `MemoryBudget.Resolve(job.MemoryBudget)`, a
machine-global `UserSettings` policy (File → Settings), defaulting to Auto =
`min(50% of total RAM, available − 2 GB)` — and released per file as the main loop
consumes it, so a backup larger than the budget still works (over-budget files fall
back to the old streaming re-read; a budget of 0 disables buffering entirely and is
still correct). The `preRecipes`/`wholeFileCount`/`blockOccur` hash maps above are
NOT subject to that cap — they are the residual unbounded structure.

**Proper fix if the hash maps ever bite:** stream the analysis — keep only
`wholeFileCount` + `blockOccur` (both O(unique hashes), much smaller) during counting,
drop per-file recipes, and recompute each file's recipe in the main loop only for files
that turn out to be `.dedup`. That trades one extra read of the dedup'd files for
bounded memory. Alternatively spill `preRecipes` to a temp SQLite/disk structure.

## Leftover `_filestore` blobs after old→new format conversion (2026-06-13)

**Status:** Minor data residue on the i: backup. Content is safe; no catalog rows
reference these blobs. Low priority — they can be reclaimed with a re-run.

**Symptom:** After converting the i: backup (catalog set 11) to the new format with
`tools/lithic_convert_to_new_format.py`, 4 stray `.dat` blobs (≈568 bytes total)
remained under `i:\lithicbackup\_filestore` even though the orphan-deletion pass ran
(it reported deleting 2,825 orphans / 1.4 GB). Zero catalog rows point at these 4
blobs, so they are pure residue, not referenced content.

**Likely cause:** The orphan scan in `convert()` (the `--delete-orphans` path) computes
the orphan set from the catalog snapshot at scan time; a small number of blobs whose
referencing rows had already been flipped to plain / rewritten as filerefs earlier in
the same run can fall outside that snapshot and be missed. The `_filestore` directory
is intentionally retained (not `rmdir`'d) when any blob remains, which is why the
directory survives.

**Fix / cleanup:** Re-run the converter against set 11 with `--delete-orphans` (it is
idempotent — anchors are already placed, so it will only re-scan and remove the 4
stray blobs, then `rmdir` the now-empty `_filestore`). Proper code fix: recompute the
orphan set from the *post-flip* catalog state (after `finish()` commits) rather than
the pre-flip snapshot, so late-flipped rows' blobs are included in the orphan sweep.

## Dangling `.fileref` files for deleted history on i: (2026-06-14)

**Status:** Confirmed cruft, **no active data loss**. The set-11 reconcile dry-run
flagged 124 "MISSING CONTENT" groups; a full read-only disk+catalog diagnostic
classified all 124. Low priority — optional space-reclaim cleanup.

**What it is:** 124 duplicate `.fileref` files remain on disk under `i:\lithicbackup`
pointing at content hashes for which no backing bytes exist anywhere (no `_filestore`
blob, no plain anchor copy). The diagnostic confirmed **every one of the 124 is
referenced only by soft-deleted catalog rows** (`IsDeleted=1`): "LOST with >=1 active
(non-deleted) catalog row: **0**; LOST with only deleted/historical rows: **124**".
So no current/restorable file is missing its content — the live backup is intact.

**Three benign buckets:** (1) test-harness cruft swept into the backup
(`test_out_new\…\test_source\…`, `tools\_newfmt_harness\…`, `_newfmt_catalog\…`
including `catalog.db-wal/-shm` and `set-1.db-shm`); (2) stale git loose objects
under `…\os\.git\objects\…` that git later repacked (source deleted them, retention
deleted the rows; the live data is in the backed-up packfiles); (3) aged-out
historical versions `D_prev\…*.vNN` that retention evicted.

**Mechanism:** During conversion the orphan sweep correctly reclaimed the backing
blobs (nothing active referenced them), but left the *duplicate* `.fileref` files on
disk, so they now dangle. They never surface in the GUI Verify Integrity (it only
walks non-deleted records) and the reconciler leaves them "untouched". They only
waste a little space.

**Cleanup (optional):** delete the dangling `.fileref` files (every `.fileref` whose
hash has zero non-deleted catalog rows and no resolvable plain copy). A proper code
fix is to make retention/eviction remove the duplicate `.fileref` files from disk when
it deletes the catalog row + reclaims the anchor, so duplicates don't outlive their
content. The separate test-artifact directories should also be excluded from the i:
source set so they stop being backed up at all.

## Post-burn verification disc reload is best-effort / hardware-dependent (2026-06-13)

**Status:** Implemented but untested on real hardware (no burner with media available
in this environment). The simulated burner path is fully exercised; the IMAPI2 path's
reload logic is best-effort.

**Context:** The "Verify after burn" option is now functional — previously the
checkbox flowed all the way to `BurnOptions.VerifyAfterBurn` but `Imapi2DiscBurner`
never read it (dead control). Both burners now run a real read-back via
`BurnVerifier.VerifyAsync`, which re-hashes every staged source file against its copy
on the disc (SHA-256) and throws `BurnException` on any missing/size/content mismatch.

**Caveat (IMAPI2 path):** To read the freshly written filesystem back, the disc must
be remounted. `Imapi2DiscBurner.VerifyBurnedDisc` calls `recorder.EjectMedia()` +
`recorder.CloseTray()` then polls `recorder.VolumePathNames` (up to 60 s) for a
readable mount. This is inherently drive-dependent: slot loaders, drives that can't
programmatically close the tray, and multisession discs left open (not finalized) may
not remount automatically. In those cases verification throws a `BurnException`
telling the user the burn completed but the disc couldn't be read back, and to re-run
Verify Integrity manually — it does **not** falsely report a burn failure of the write
itself. **Needs validation on real optical hardware** before relying on it; consider a
more robust remount (volume-arrival notification instead of fixed polling) and special
handling for open multisession discs (which may only be readable after the session is
closed).
