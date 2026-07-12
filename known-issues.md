# LithicBackup — Known Issues & Tech Debt

## FIXED: Cleanup "cleaned but reappears" — read-only files silently fail deletion (2026-07-12)

**Status:** Fixed 2026-07-12 in `OrphanedDirectoriesViewModel.PurgeSelected`
(cleanup UI) and `DirectoryBackupService` (retention + fileref materialisation).

**Symptom (user report):** In the Cleanup view, "Scan Destination Filesystem"
listed "untracked files" and "catalog-deleted (still on disk)" entries; the user
selected all and cleaned them, re-scanned, and the exact same entries came back —
"the same bug it already had that you thought you fixed."

**Root cause (forensically confirmed on the LIVE catalog + destination):**
`FileInfo.Delete()` / `File.Delete()` throw `UnauthorizedAccessException` on a
**read-only** file. A large fraction of backed-up content is read-only: git
object/pack files are always read-only, and anything copied with `File.Copy`
inherits the source's read-only attribute. The cleanup purge's physical-delete
loop caught the exception, counted it as a soft failure, and moved on — so the
file survived on disk and the next scan re-reported it, forever. On the live
destination, **920 of 940** "catalog-deleted (still on disk)" files were
read-only. That is the actual "cleaned but keeps coming back" bug.

**Where the read-only files came from:** `DirectoryBackupService`'s primary
content write streams into a fresh temp file (never read-only), but
`TryPromoteFileRefToPlainAsync` used `File.Copy(sourceBytesPath, plainAbsPath,
overwrite:true)`, which **preserves** the source read-only flag — planting
read-only plain files on the destination. Retention's `File.Delete(prevPath)` and
the fileref-manifest `File.Delete(refAbsPath)` then threw on those files and (for
retention) left them behind on every pass.

**Fix (both symptom and source):**
- `OrphanedDirectoriesViewModel` purge phase-2: clear `fi.IsReadOnly` before
  `fi.Delete()`.
- `DirectoryBackupService`: added `ForceDeleteFile` (clears read-only then
  deletes) used at the retention delete and fileref-manifest delete; added
  `ClearReadOnly` called right after the `File.Copy` in
  `TryPromoteFileRefToPlainAsync` so newly-materialised plain files are writable.

**Correction to an earlier (2026-07-12) diagnosis in this file:** an initial pass
claimed "298,688 of 834,035 untracked hits" and a "stale catalog out of sync"
condition. Those numbers came from the **abandoned legacy** catalog at
`C:\Users\<user>\AppData\Local\LithicBackup\catalog.db` (dated 2026-06-09), not the
live catalog. The live catalog is the per-set DB at
`C:\ProgramData\LithicBackup\sets\set-4.db` (current, consistent with the
destination): 833,981 of 834,035 files are correctly tracked. The catalog is NOT
stale — the timestamp confusion was reading the wrong file (see
`CatalogLocation.cs`, LocalApplicationData → CommonApplicationData migration).

## FIXED (defensive): Cleanup could mislabel materialised `.fileref`/`.dedup` content as "Untracked" (2026-07-12)

**Status:** Fixed 2026-07-12 in `OrphanedDirectoriesViewModel.WalkDestination`
(commit dd6584a). Correct and worth keeping, but low-impact on live data (it
reclassifies **1** file on the live set, not the hundreds of thousands the
original write-up implied).

**Root cause:** A deduplicated file's catalog `DiscPath` carries a manifest suffix
(`…lama-cleaner-main.zip.fileref`), but the manifest can be **materialised** back
into a plain, suffix-less file whose bytes *are* the referenced content.
`WalkDestination` keyed `discPathLookup` only by the raw catalog `DiscPath`, so a
plain on-disk file wouldn't match its `.fileref`/`.dedup` record and could be
reported as untracked — risking "cleanup" of live backup content.

**Fix:** In `WalkDestination`, when a disk file's exact relative path isn't in the
catalog, also try `<path>.fileref` and `<path>.dedup` before declaring it
untracked. Exact match still wins; the fallbacks only fire for plain suffix-less
files, so genuine untracked files are unaffected.

**Actual data repair (2026-07-12):** the stale rows themselves are now repairable
via the **Reconcile Catalog with Destination** tool in the Cleanup view
(`CatalogReconcileService`). It flips `IsFileRef=1` rows whose stripped path holds
a hash-matching plain file back to plain (`IsFileRef=0`, stripped `DiscPath`) and
prunes active rows whose content is missing. It is dry-run first (Analyze → Apply)
and never prunes when the destination is absent/empty. This is the "separate
reconcile pass" the earlier write-up said would be needed.

## OPEN (related): disc-burn staging copies inherit source read-only

`BackupOrchestrator` File.Copy sites (lines ~883, ~1018, ~1139) copy source files
into the disc-burn staging dir and, like the fileref path, preserve the source's
read-only attribute. This is the disc-backup path (not the directory-backup path
the user hit), so it wasn't fixed in the 2026-07-12 pass. If staging cleanup or
retention there ever fails on read-only files, apply the same `ClearReadOnly` /
`ForceDeleteFile` treatment.

## FIXED: Cleanup "Clean Selected" never persisted for RemovedFromSources / DeletedFromDisk (2026-07-11)

**Status:** Fixed 2026-07-11 in `SqliteSetDatabase.cs`. Root cause was a SQL
`LIKE ... ESCAPE '\'` bug; two methods were affected.

**Symptom (user report):** After running "Clean Selected" in the Cleanup view and
re-scanning, all the same entries reappeared in the "deleted from disk"
(`DeletedFromDisk`) and "not in source selection" (`RemovedFromSources`) sections.
The cleanup appeared to do nothing that stuck.

**Root cause:** Those two categories route through the purge's `else` branch →
`ICatalogRepository.MarkFilesDeletedByDirectoryAsync(setId, DirectoryPath)`, whose
SQL is `... AND (SourcePath LIKE $prefix ESCAPE '\' OR SourcePath = $exact)`.
The prefix is a Windows source path (e.g. `D:\some\dir\%`). Because `\` is declared
as the LIKE escape character and the code escaped `[`, `%`, `_` **but not the
backslash itself**, every path-separator `\` in the pattern was treated as an escape
char and swallowed the next character. The pattern matched **zero** rows, so
`UPDATE Files SET IsDeleted = 1` affected nothing. The transaction committed cleanly
(no error), the in-memory `_activeFiles` list was trimmed, and disk files were
deleted — but the catalog rows stayed `IsDeleted = 0`. On the next scan, `_activeFiles`
reloaded from the catalog and the still-active rows re-surfaced. Verified empirically:
`'D:\some\dir\file.txt' LIKE 'D:\some\dir\%' ESCAPE '\'` → **0**; doubling the
backslashes → **1**.

**Second method with the identical bug:** `GetFileRecordsUnderDirectoryAsync` used the
same broken escape chain. Its only caller is `DirectoryBackupService.MoveTargetedAsync`
for **directory** moves — so a renamed/moved watched folder always got back 0 records,
always returned `FellBack`, and re-copied the entire subtree as fresh files (leaving the
old copy to be pruned later) instead of taking the fast `Directory.Move` relocate path.
Fixed as part of the same change.

**Fix:** Prepend `.Replace("\\", "\\\\")` (escape the escape char first, before adding
the `\[` `\%` `\_` escapes) in both `MarkFilesDeletedByDirectoryAsync` and
`GetFileRecordsUnderDirectoryAsync`, mirroring the already-correct `SearchAsync`. The
`SourcePath = $exact` fallback only ever matched the directory row itself, never the
files under it, which is why it didn't mask the bug.

**Note on existing data:** Catalog rows for previously-"cleaned" RemovedFromSources /
DeletedFromDisk entries whose disk files were already deleted are now
"catalog-deleted (still... actually gone)" — i.e. `IsDeleted = 0` rows whose bytes are
gone. Re-running Clean Selected on them now correctly flips `IsDeleted` (the disk-delete
pass simply finds nothing to delete). No separate reconcile needed.

## "Catalog-deleted (still on disk)" `_prev .v1` records + retention hardening (2026-07-11)

**Status:** Code hardened 2026-07-11 (retention now confirms physical removal
before flipping `IsDeleted`). Existing residue in old catalogs remains until a
cleanup/reconcile pass runs. A separate user-config finding (below) is NOT a bug.

**Symptom (user report):** The Cleanup view listed many "catalog-deleted (still on
disk)" entries, most of them `*.v1` files under `c_prev` / `d_prev`. The concern:
(1) Lithic marks files deleted in the catalog without physically deleting them, and
(2) previous-version files were being deleted despite a "keep versions" intent.

**Forensic findings (reference catalog `%LocalAppData%\LithicBackup\catalog.db`,
set 4 — note this copy is stale, dated 2026-06-09):**
- 609,474 `IsDeleted=1` rows total; only **2,792 are under `_prev`** (2,783
  `D_prev`, 9 `C_prev`), and **every one is Version 1** — a lone prev version per
  path, timestamps clustered on 2026-05-19.
- Current retention **cannot** produce a lone-prev-v1 deletion:
  `VersionRetentionService.ComputeRetentionAsync` only considers `_prev`-path
  versions, protects `newestId`, and trims only when a tier's prev-version count
  exceeds `MaxVersions`. With a single prev version, nothing is ever selected.
- Therefore these 2,792 are **residue from the May 19 re-seed bloat event** (old
  code, before the seed idempotency guard — see the catalog-bloat entry below),
  not from current retention.

**Root-cause bug that was hardened (the "thinks it deleted but didn't" invariant):**
`DirectoryBackupService.ExecuteAsync` retention section previously (a) reconstructed
the version file path via `GetPrevPath(SourcePath, Version, flags)` instead of using
the record's authoritative stored `DiscPath`, and (b) set `fileRecord.IsDeleted =
true` **unconditionally** after a `File.Exists`-guarded delete. If the reconstructed
path diverged from the real `DiscPath` (legacy/migrated rows) or the delete threw,
the bytes survived while the record was still marked deleted → exactly the
"catalog-deleted (still on disk)" state. **Fix:** locate the file via
`Path.Combine(targetDirectory, fileRecord.DiscPath)`, wrap the delete in a
try/catch that `continue`s (leaving record + file consistent) on IO/ACL failure,
and flip `IsDeleted` only when `!File.Exists(prevPath)` confirms the bytes are gone.
Also added a warning doc-comment to the dead `VersionRetentionService.ApplyRetentionAsync`
(no callers) noting it marks `IsDeleted` without any physical delete and must not be
wired into the backup path as-is.

**NOT a bug — user tier-config finding (worth surfacing to the user):** Set 4's
`JobOptions.TierSets` were: **Default = `{MaxAge:null, MaxVersions:1}` (keep only 1
version, all ages)**; "None" = no versioning for build/output dirs; "Custom 1" =
`{<10d: all, <365d: 10, older: 3}` matched to code/doc extensions + `d:\visual
studio projects\*`. So the "keep for a long time" policy applies **only** to Custom
1's files; everything else (e.g. `D:\mp3\...`, most of `C:\`) falls through to
Default and keeps just 1 version. The user believed their policy kept all prev
versions for 365 days — it does not. (Even Custom 1 keeps 10 versions in the
10–365d band, not "all".) If the intent is to keep more history broadly, the
**Default tier set** must be changed.

**Residue cleanup (existing catalogs):** the hardened code prevents recurrence but
does not retroactively repair the 2,792 rows. On the live J: destination most of
those physical `.v1` files are already gone (the records are then correctly
deleted). Where a `.v1` file genuinely still exists and the user wants to keep it,
the record should be un-deleted (`IsDeleted=0`) rather than physically purged — do
NOT run the Cleanup "catalog-deleted (still on disk)" purge on prev versions the
user intends to retain, as that physically removes them.

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

## Fixed: schedule editor silently reverted On/Continuous to Off/Interval

**Status:** Fixed 2026-07-11 in `MainViewModel.RestoreSourceSettings` and
`MainViewModel.RestoreJobOptions`.

**Symptom:** A backup set configured with schedule Enabled=On + Mode=Continuous
would later show up as Off + Interval, silently disabling continuous backup (the
Worker only watches sets whose schedule is Enabled AND Mode==Continuous).

**Root cause:** Both restore methods guarded schedule loading on
`opts.Schedule is { Enabled: true }` and then hardcoded `vm.ScheduleEnabled = true`.
When a set was stored as `{Enabled:false, Mode:Continuous}` (which the active save
path `SyncSettingsToJobOptions` produces whenever the enable checkbox is off — it
sets `opts.Schedule.Enabled = false` in place but preserves Mode on disk), the next
editor open skipped the restore entirely and fell back to the ViewModel field
defaults (`_scheduleEnabled=false`, `_scheduleMode=Interval`). The stored Continuous
Mode was hidden, and a subsequent enable+save wrote the default Interval over it —
permanent Mode loss.

**Fix:** Restore now matches `opts.Schedule is { } sched` (any non-null schedule)
and sets `vm.ScheduleEnabled = sched.Enabled`, always loading Mode/interval/debounce
so the editor reflects the true stored config regardless of Enabled.

**Not the cause (ruled out during investigation):**
- DB/JSON round-trip is fine — `JsonSerializer.Serialize` with no options writes all
  properties (System.Text.Json does NOT omit defaults unless `DefaultIgnoreCondition`
  is set), so `Enabled`/`Mode` persist correctly in `sets`/master DB JobOptions JSON.

## Continuous backup misses changes when the USN journal wraps during downtime (2026-07-11)

**Status:** FIXED 2026-07-11. Continuous mode now detects a lost-continuity journal
(wrap or recreation during downtime) and self-heals by running a full reconciling
backup, then re-seeding the cursor to the journal's current end. See "Fix" below.

**Fix:** `UsnJournalReader.ReadChanges` now reports an `out bool journalTruncated`,
set when `FSCTL_READ_USN_JOURNAL` fails with `ERROR_JOURNAL_ENTRY_DELETED` (1181) —
i.e. the saved start USN was purged. A new `UsnJournalReader.TryRefreshPosition`
re-queries the live `NextUsn`. In `BackupWorker.ReadVolumeChangesAsync`, the old
combined "cursor null OR JournalId mismatch → reset to now, return no changes" branch
was split: a truly first-seen volume (`cursor is null`) still just seeds forward
quietly, but a **JournalId mismatch on an existing cursor** (journal recreated) and a
**truncation during read** (journal wrapped) both re-seed the cursor to the current
end AND return a `Truncated` signal. `CheckContinuousAsync` collects the truncated
drives, flags every continuous set watching them (`SetState.NeedsReconcile`), and runs
a full `RunFullBackupAsync` reconcile (which now returns `bool` so the flag is cleared
only when the scan actually runs — it retries on later polls if the backup lock is
busy or the destination is offline). The debounce pass skips reconcile-pending sets.
Net: no more silently-stuck cursor and no more missed downtime changes.

**Scenario:** A set is on `ScheduleMode.Continuous`. The Worker (or whole machine) is
off for a while, or the source volume sees heavy churn while off. On restart, a
brand-new directory / new files added during the gap should be backed up.

**Normal path (works):** Continuous mode is driven by the NTFS USN change journal,
which NTFS keeps writing to whether or not Lithic runs. `BackupWorker.ReadVolumeChangesAsync`
(BackupWorker.cs:326) loads the per-volume cursor from the `UsnCursors` catalog table,
and as long as the `JournalId` still matches (line 349) it reads **every** record since
that cursor via `UsnJournalReader.ReadChanges` (UsnJournalReader.cs:121) — full catch-up.
New files under the new directory get enqueued (the directory-creation record itself is
skipped at BackupWorker.cs:279 `if (change.IsDirectory) continue;`, but the file
create/write records inside it are picked up), matched by `PathBelongsToSet` (honoring
`AutoIncludeNewSubdirectories`, default true), debounced, and backed up through the same
`DirectoryBackupService.ExecuteTargetedAsync` incremental machinery. So the new directory
**is** backed up in the typical case.

**The bug (journal wrap):** The USN journal has a fixed max size; NTFS purges the oldest
records once it fills. After a long gap or heavy churn, the saved cursor USN can already
be purged. Then `FSCTL_READ_USN_JOURNAL` fails and `ReadChanges` just `break`s
(UsnJournalReader.cs:147-150), returning **empty** with `nextUsn == startUsn`, so
`ReadVolumeChangesAsync` does **not** advance/save the cursor (line 375). A wrap does
**not** change the `JournalId`, so the "reset to current end" branch (line 349) never
fires either. Net effect: the cursor stays stuck on the purged USN and every future poll
silently reads nothing — continuous detection for that volume is **permanently stuck**
until the journal is deleted+recreated (new JournalId), which then resets to "now" and
skips the gap anyway. The gap's changes (including the new directory) are lost to
continuous mode. It's also **silent** — the failing `DeviceIoControl` doesn't throw, so
`ReadVolumeChangesAsync`'s catch/log path (line 367) isn't even hit.

**No fallback:** Continuous sets get no periodic full rescan — `CheckSchedulesAsync`
maps `ScheduleMode.Continuous` to `_ => false` (BackupWorker.cs:218). So nothing walks
the source tree to close the gap. The only recovery today is a **manual backup** (full
`PlanAsync` scan) from the GUI, which would pick up the new directory.

**Proper fix:** (1) Detect the purged-cursor case — check the read failure for
`ERROR_JOURNAL_ENTRY_DELETED` (and/or compare the saved cursor against the journal's
`FirstUsn`/lowest valid USN from `FSCTL_QUERY_USN_JOURNAL`). When the cursor is behind
the journal's start, treat it like a journal reset. (2) On that reset, don't silently
skip — trigger a one-off **full incremental backup** (`RunFullBackupAsync`) to
reconcile the source against the catalog, then re-seed the cursor to the current
`NextUsn`. That makes continuous mode self-healing across any downtime/wrap. Optionally
also run a periodic safety-net full scan for continuous sets (e.g. daily) so a stuck
journal can't hide indefinitely.

## Tech debt: dead schedule-wipe landmine in SaveBackupSetAsync

**Status:** Dead code (no callers), low priority. Landmine if re-wired.

`MainViewModel.ShowJobConfig` has zero callers, so its `PlanCompleted` handler and
`SaveBackupSetAsync` (which rebuilds `JobOptions` from scratch and sets
`Schedule = jobConfig.BuildSchedule()`) are unreachable. `BuildSchedule()` returns
**null** when `ScheduleEnabled` is false, so if this path were ever reconnected it
would wipe a stored schedule to null (→ reverts to Off/Interval on reload) whenever
the job-config checkbox happened to be off. This is inconsistent with the active save
path `SyncSettingsToJobOptions`, which preserves the existing schedule object.

**Proper fix if revived:** either delete `ShowJobConfig`/`SaveBackupSetAsync`/
`BuildSchedule` if truly unused, or make the save path preserve/merge the existing
`Schedule` instead of overwriting it with a possibly-null rebuild.

## Move/rename relocation — Phase 2 shipped: special formats, history & per-file granularity (2026-07-11)

**Status:** Phase 2 IMPLEMENTED 2026-07-11. `MoveTargetedAsync` now relocates
`.dedup`/`.fileref` manifests and the full `{drive}_prev` version history in place,
per-file within a directory, inside a single catalog transaction — closing the
Phase 1 fallbacks below. Only genuinely un-relocatable formats (split/zipped, which
directory backups never produce) still fall back. One residual caveat remains for
**catalog-free** restore only (see "Residual caveat").

**What Phase 1 did (history):** `UsnJournalReader.ReadChanges` captures each record's own
File Reference Number (offset 8) and pairs `RENAME_OLD_NAME`/`RENAME_NEW_NAME` records
sharing that FRN into `UsnMove(OldPath, NewPath, IsDirectory)` intents. A same-volume
directory move emits a single pair on the directory's own FRN (children are **not**
re-journaled), so one move relocates a whole subtree. `BackupWorker.CheckContinuousAsync`
routes moves to every set either endpoint touches (`SetState.PendingMoves`) and applies
them each poll via `RunMovesAsync` (under the backup lock, requeued while the destination
is offline). Phase 1 only relocated a single plain current version; any `.dedup`,
`.fileref`, split, zipped, or `{drive}_prev` history forced a whole-folder `FellBack`
re-copy.

**What Phase 2 does:** `MoveTargetedAsync` fetches all versions
(`GetFileRecordsByPathAsync` / `GetFileRecordsUnderDirectoryAsync` — both current and
every `_prev` version, including `.dedup`/`.fileref` manifests and `IsDeleted`
tombstones), bails to `FellBack` only if any record is `IsSplit`/`IsZipped` (never true
for directory backups), then delegates to:
- `RelocateDirectoryAsync`: `Directory.Move`s **both** the current subtree
  (`{drive}\rel`) and the parallel history subtree (`{drive}_prev\rel`) — same source
  volume, so the `{drive}` prefix is unchanged — then, in one `BeginTransactionAsync`,
  repoints every record's `SourcePath` (via `RemapPathPrefix`) and `DiscPath` (via
  `RemapDiscPath`, which preserves current-vs-`_prev` and the `.dedup`/`.fileref` suffix).
- `RelocateFileAsync`: `File.Move`s each on-disk version (current + every `_prev`, any
  format) and repoints all records in one transaction.
- `RemapDiscPath` / `IsPrevDiscPath`: recompute a record's disc path for its new source,
  detecting `_prev` by testing whether the first path segment ends in `_prev`.

`.dedup`/`.fileref` manifests are just small files in the current/history trees — moving
the manifest and updating its `DiscPath` is sufficient; the shared content-addressed
`_blocks/`/`_filestore/` bytes never move and restore resolves them by **Hash**, so a
path change can't break block/file-dedup content resolution. **Atomicity:** all physical
moves happen *before* the catalog commits; if the transaction throws, the physical moves
are reversed (`TryMoveDirectoryBack` / `File.Move` undo list) and the transaction rolls
back. An interrupted run therefore fails safe toward a harmless re-copy, never a
half-applied relocation. Renames are covered identically (a rename and a move are the
same USN old→new FRN pair — no separate code path).

**Residual caveat (catalog-free restore only):** a `.fileref` manifest carries internal
self-describing `SourcePath` and `ContentPath` fields (the latter a *hint* to where the
anchor bytes live). These are **not** rewritten during a move. The primary restore/verify
path resolves filerefs by **Hash** through the catalog, so it is unaffected. Only the
catalog-free restore/inspection tools (which read `ContentPath` directly) can see a stale
pointer after a move relocates either the fileref or its plain anchor. Proper fix if this
ever matters: rewrite the moved `.fileref` manifests' internal `SourcePath`/`ContentPath`
JSON during relocation, and reuse the existing `UpdateFileRefContentPathsAsync` reverse
lookup to repoint any fileref whose anchor moved out from under it. Deferred as a separate
catalog-free-restore fidelity concern, not part of the move feature's primary correctness.

**Not handled (by design, matches existing continuous-delete behavior):** an item moved
**out** of a set's scope (e.g. to the Recycle Bin or another location outside the
selection) is not marked deleted immediately — its removal is reconciled by the next full
scan. Pure continuous mode has no periodic full rescan (see the journal-wrap issue
above), so out-of-scope move-deletes rely on a manual/scheduled full run, same as
in-place deletes.
