# LithicBackup — Known Issues & Tech Debt

## FIXED (both parts): Atomically-saved files (e.g. KeyNote .knt) never accumulate versions (2026-07-15)

**Symptom.** A file edited by an application that saves *atomically* — writing a
temp file then replacing/renaming the original (KeyNote NF `.knt`, and many text
editors) — is re-copied on every save but **never accumulates versions in `_prev`**,
and its version number stays stuck at 1. Diagnosed on the user's live catalogs:
`D:\youtube\philosophy\philosophy.knt` had two catalog rows, **both Version 1, both
`IsDeleted = 1`**, different DiscIds, **different hashes** (content genuinely changed
2026-07-12 → 2026-07-15). No `_prev` row was ever cut. Meanwhile files KeyNote writes
in place (its `_BAK@Dn`/`_BAK@Wn` rotation copies) *did* version correctly
(`philosophy_BAK@D5.knt` reached v4 with `.v3`/`.v2` in `D_prev`).

**Root cause.** An atomic save briefly removes the original file. Continuous backup
sees the disappearance and soft-deletes the catalog record (`IsDeleted = 1`), then
sees the replacement as a brand-new file. `SqliteSetDatabase.GetLatestVersionInfoAsync`
filters `WHERE IsDeleted = 0`, so the next backup finds **no prior version** →
`hasExistingInfo = false` → the `if (isChanged && keepVersions && hasExistingInfo)`
block in `DirectoryBackupService` that moves the old copy into `_prev` never runs, and
the version resets to 1. History is discarded on every save.

**Fix (this commit) — version-chain resurrection.** New
`ICatalogRepository.GetOrphanedVersionInfoAsync` returns the "orphaned history" set:
paths whose *entire* history is tombstoned (latest row `IsDeleted = 1`), so they're
absent from `GetLatestVersionInfoAsync`. `DirectoryBackupService.ExecuteAsync` now
loads this alongside `versionInfo` and, when the live lookup misses, falls back to it
(`resurrecting = true`): it continues the chain (`version = MaxVersion + 1`), the
`_prev` move picks up the old copy still at its live on-disk path and **un-deletes**
that record as a retained version, and the content-identity short-circuit revives an
identical reappeared file in place (un-delete + refresh) instead of leaving it a ghost.
Source-agnostic: any deleted-then-reappeared path now keeps its history.

**Fix part 2 — atomic-save-aware change detection (stops the churn at the source).**
Resurrection *repairs* the chain, but the churn root was that every atomic save
tombstoned the record and cut a fresh `v1` row (the two `philosophy.knt` rows) and
duplicated DiscPaths. The tombstone came from the Worker's move path: an atomic save
renames the original out to a temp/backup name (a move whose *new* name is outside the
set), which `RunMovesAsync` treats as "moved OUT of the set" and hands to
`MarkMovedOutAsync` to soft-delete — even though the app immediately re-creates a file
at the original path. `BackupWorker.MarkMovedOutAsync` now **guards on on-disk
presence**: before tombstoning a "vacated" path it checks `File.Exists`/`Directory.Exists`,
and if something still occupies the path (an in-place replace, not a real removal) it
skips the tombstone, returns `false`, and the caller enqueues the path for a normal
backup so it versions in place. This applies to both the `FellBack` (within-set move
that couldn't relocate) and `oldIn`-only (moved-out) branches. The two fixes compose:
if a poll happens to land inside the save's brief file-absent window and does tombstone,
part 1 (resurrection) still recovers the chain on the next backup. Net result: an
atomically-saved file now versions on the normal in-place path (v→v+1, old copy into
`_prev`) with no tombstone churn.

*Not covered by an automated test:* the guard lives deep in the Worker's USN
move-application path (`RunMovesAsync`), which needs a live NTFS USN journal and the
full worker dependency graph to exercise end-to-end; a proportionate harness doesn't
exist yet. The guard itself is a simple on-disk-presence check, and part 1's
resurrection (covered by `tools/resurrection_test`) is the safety net for the residual
race. Worth adding a worker-level move-classification harness later.

---

## FIXED: Case-only path changes corrupt version history + orphan _prev files (2026-07-15)

**Fixed 2026-07-15** — the new-backup half of this bug is resolved. Every
`SourcePath` comparison/partition in `SqliteSetDatabase` is now `COLLATE NOCASE`
(`GetLatestVersionInfoAsync` `PARTITION BY`, `GetFileCountForBackupSetAsync`
`COUNT(DISTINCT …)`, `GetFileRecordByPathAndVersionAsync`, `GetFileRecordsByPathAsync`,
`GetFileRecordsUnderDirectoryAsync` exact match, `MarkFilesDeletedByDirectoryAsync`
exact match, `MarkFilesDeletedBySourcePathsAsync`). A case-only rename now advances the
single version chain and repoints the old row into `_prev` instead of forking the chain
and orphaning the file. Covered by `tools/case_rename_test` (end-to-end: two real
directory backups against a real per-set catalog with a case-only dir rename in
between; the test fails with the `COLLATE NOCASE` clauses removed). `tools/dir_dedup_test`
still passes (no dedup regression).

**Still open — data repair.** This fix only stops NEW corruption. Existing catalogs
that already forked (e.g. set-11's `forward raytracer\ROADMAP.md` with parallel
`ROADMAP.md`/`roadmap.md` chains and four v4 rows) still need a one-time reconcile:
fold case-variant `SourcePath`s into one chain, renumber versions, drop redundant
"current" rows. The 708 already-orphaned `_prev` files remain cleanable via the
"Untracked Files (destination scan)" cleanup. Sketch below under **Proper fix**.

---

**Symptom:** the destination "Untracked Files" scan lists many `_prev` version
files that have no catalog row. In set-11 (I:\lithicbackup) a direct disk-vs-catalog
diff found **708 orphaned `_prev` files** under `D_prev` (392 `.fileref`, 316 plain)
that no catalog `DiscPath` references. Most (706/708) are legacy from a June catalog
rollback, but **2 are live (July 14–15)** and pinpoint an ongoing bug.

**Root cause — inconsistent case sensitivity in catalog path handling.** Windows
paths are case-insensitive, but the catalog treats `SourcePath` case-*sensitively* in
some places and case-*insensitively* in others:
- `SqliteSetDatabase.GetLatestVersionInfoAsync` partitions with
  `ROW_NUMBER() OVER (PARTITION BY f.SourcePath ...)` under SQLite's default BINARY
  (case-sensitive) collation, so `D:\...\ROADMAP.md` and `D:\...\roadmap.md` become
  **two** partitions, each returning its own "latest" row. The caller then loads
  these into a `Dictionary<string,FileVersionInfo>(OrdinalIgnoreCase)` which
  **collapses them nondeterministically** (last row inserted wins; the outer query
  has no ORDER BY).
- `GetFileRecordByPathAndVersionAsync` matches `f.SourcePath = $path` (case-sensitive).
- The versioning move in `DirectoryBackupService.ExecuteAsync` (~line 786) uses
  `File.Exists` (case-insensitive on Windows) to decide the `_prev` move, but repoints
  the old catalog row via the case-sensitive `GetFileRecordByPathAndVersionAsync`.

When a file's path casing changes between backups (e.g. an editor rewrites
`ROADMAP.md` as `roadmap.md`), `version = existingInfo.MaxVersion + 1` is computed
from whichever casing survived the dict collapse, the physical file **is** moved to
`_prev`, but the repoint lookup uses the current-scan casing and returns null → the
`_prev` file is orphaned, and the catalog accumulates duplicate/parallel version
chains. Confirmed in set-11: `forward raytracer\ROADMAP.md` has simultaneously-active
rows for `ROADMAP.md` v1, `roadmap.md` v2/v3, and **four** `ROADMAP.md` v4 rows, all
pointing at the same physical (case-insensitive) file, plus orphaned
`roadmap.md.v1` and `ROADMAP.md.v3` under `_prev`.
`COUNT(DISTINCT SourcePath COLLATE NOCASE)` = 1 where `COUNT(DISTINCT SourcePath)` = 2
for this file — proof the catalog is double-counting one file.

**Proper fix:** make `SourcePath` handling consistently case-insensitive across the
catalog on Windows. Apply `COLLATE NOCASE` to every `SourcePath` comparison and to the
`PARTITION BY` in `GetLatestVersionInfoAsync` (and the grouping in
`GetFileRecordsByPathAsync`, retention grouping, dedup lookups, etc.), OR canonicalize
`SourcePath` casing at write time so the catalog never stores two casings for one file.
NOCASE partition + NOCASE lookup fixes version numbering and the repoint together so
new backups stop orphaning and stop forking version chains. **Also needs a repair
story for existing data:** the 708 orphaned `_prev` files are cleanable via the
"Untracked Files (destination scan)" cleanup today; the duplicated/forked catalog
version rows need a reconcile (fold case-variant `SourcePath`s into one chain,
renumber versions, drop the redundant "current" rows).

**Not a retention or a save-versioning failure:** current backups DO catalog `_prev`
versions correctly (channels.conf v1–v7 are tracked under `_prev`), and retention
works per the configured "Custom 1" tier (`*.conf` etc. keep all versions ≤10 days).
The orphans are (a) legacy from a June catalog rollback and (b) fresh casualties of
this case-sensitivity bug — not a failure to write version rows.

## ADDED: Dedup-aware "actual backup size" estimate (2026-07-15)

**Problem (roadmap item 6):** the pre-backup coverage scan
(`BackupCoverageViewModel`) summed **raw** file sizes and bucketed files against the
catalog *by path* — it never hashed content, so two identical files at different
paths both counted in full. For a set with dedup enabled this over-stated the space
the backup would actually consume, sometimes badly.

**What shipped:** `DedupSizeEstimator` (`src/LithicBackup.Services/DedupSizeEstimator.cs`),
an opt-in estimator that mirrors `DirectoryBackupService` dedup accounting so its
reported `StoredBytes` matches what a real backup would write:
- **File-level dedup:** size gate first (a size colliding with nothing already
  stored is definitely new → counted full, no read), then item-5 progressive
  prefix-hash escalation for size-colliders (settled after 64 KiB unless a real
  prefix collision forces a full hash). Dedups against both already-stored plain
  content and files seen earlier in the same scan.
- **Block-level dedup:** reads and hashes every block of every file via
  `IDeduplicationEngine`, counting only blocks not already in the store and not
  already seen this run (`seenNewBlocks`) — partial hashing buys nothing here, so the
  pass is exact but expensive and gated behind an explicit user action with a "this
  reads all your data" warning (`RequiresFullRead`).
- Reuses the shared dedup primitives (`GetActivePlainContentSizesAsync`,
  `GetActivePlainContentPathsAsync`, whole-file hash, block store, and the
  path+size+mtime `_hashCache`) so the estimate can't drift from the real result.

Surfaced in `BackupCoverageView` as a "Compute actual size" button (visible only when
a dedup mode is enabled and a target is known); the fast raw-size scan stays the
default. Directory-backup only — disc backups don't dedup.

**Coverage:** `tools/dedup_estimate_test` runs BOTH the real `DirectoryBackupService`
and the estimator over identical inputs and asserts `StoredBytes` equals the bytes
physically written (plain copies + `_blocks/*.blk`, ignoring tiny `.fileref`/`.dedup`
manifests) across no-dedup (stored == raw), file-level (dup + unique + same-size-
different), block-level (files sharing a block, incl. partial last blocks), and an
all-redundant incremental re-run (0 new bytes). All checks pass.

## FIXED: Double-read on large size-colliding files during file dedup (2026-07-15)

**Problem (roadmap item 5):** in directory-backup file-level dedup, a file whose size
collided with other content was hashed in full up front and then, if it turned out
*not* to be a duplicate, read a second time to copy its bytes. The redundant read only
hit files larger than the in-memory buffer budget (small files are buffered on the
first read and copied from memory), but on multi-GB size-colliders it doubled the I/O.

**What shipped:** a progressive prefix pre-check in `DirectoryBackupService`. An
intra-run index `intraRunPlainPrefixes` (size → set of 64 KiB SHA-256 prefixes of
already-stored plain copies) plus `RuledOutByPrefixAsync`: for a large, non-buffered
file whose size collides *only* with other files in this run and **not** with any
already-stored plain content (`IsIntraRunOnlyCollision` = candidate size count ≥ 2 and
size not in `existingPlainSizes`), a cheap prefix hash proves it shares no prefix with
any same-size plain copy stored so far. When ruled out, the full up-front hash is
skipped and `deferHashToCopy` reads the file exactly once (hashing while copying via
`CopyFileWithHashAsync`). When the prefix collides, it escalates to the full hash to
confirm and, if identical, writes a `.fileref`. **Every** plain copy of a colliding
size registers its prefix after landing (`ComputePrefixHashOfBuffer` for buffered
writes, `ComputePrefixHashAsync` for streamed) — required for correctness, or a later
identical file could be stored as a second plain copy (a missed dedup). Existing-content
size collisions deliberately keep the full up-front hash: no schema change, no
destination reads. Test-stub plain writes are excluded from registration (`!stubbedPlain`).

**Coverage:** directory-backup dedup previously had **zero** automated tests. Added
`tools/dir_dedup_test`, which drives the real `DirectoryBackupService` and asserts:
identical files → 1 plain + 1 `.fileref`; different same-size files → 2 plain, 0 ref;
a mixed X / Y(diff, same size) / X sequence → 2 plain + 1 ref resolving to X. Each
scenario runs under both `MemoryBudget = Fixed 0 GiB` (forces the streaming/prefix
path) and the default Auto budget (buffered path) — 24/24 checks pass. The disc harness
still passes 25/25.

## TECH DEBT: `StartBurnForSavedSet` / `PlanCompleted` now have zero callers (2026-07-15)

Discovered while wiring roadmap item 4. `MainViewModel.StartBurnForSavedSet` and
`BackupJobViewModel.PlanCompleted` (raised at ~line 559) have **no remaining callers**
in `src\` — they were reached only through `ShowJobConfig`, which the item-2 cleanup
deleted. The item-2 FIXED note kept `StartBurnForSavedSet` believing it was live, but a
`grep` for both symbols now returns only their own definitions. Proper fix: delete
`StartBurnForSavedSet`, the `PlanCompleted` event, and its `Invoke` (and re-check
`BackupJobViewModel` for other now-dead members freed up by that). Left for a focused
follow-up rather than folded into the item-4 UX change.

## ADDED: Plan-time disc-format compatibility warning (suggest UDF) (2026-07-15)

**Feature (roadmap item 4):** the disc `FilesystemType` (ISO 9660 / Joliet / UDF) was
chosen in Settings and only *acted on* at burn time — `ZipMode.IncompatibleOnly` (the
default) silently auto-zips any file whose disc path violates the format's
name/path/depth limits. A set full of long Unicode paths burned as ISO 9660 got quietly
zipped wholesale with no chance to reconsider the format.

**What shipped:** `DiscCompatibilitySummary` (Core.Models) +
`IBackupOrchestrator.SummarizeCompatibility(plan, filesystemType)` tally how many
planned files (and bytes) would be zipped for a given format, using the **same**
per-file predicate the burn applies — both now route through a shared
`BackupOrchestrator.IsCompatibleForDisc`, which checks the **disc-relative** path
(`GetRelativeStagingPath`) rather than the raw source path, so the summary can't
disagree with what the burn zips. (The burn's own zip check at the ZipMode branch was
switched from `file.FullPath` to that helper — the correct input, since the disc
filesystem only ever sees the disc-relative path.) In the GUI,
`MainViewModel.WarnAndMaybeSwitchToUdf` runs the summary in the disc-burn path (after
planning, before the burn); when a significant fraction would be zipped
(≥5% of files, ≥5% of bytes, or ≥20 files) and the format isn't already UDF, it shows a
Yes/No/Cancel warning offering to switch this run to UDF. Switching just flips
`job.FilesystemType` (no re-scan — bin-packing is capacity-based and
format-independent). `ZipMode.IncompatibleOnly` remains the fallback for the handful of
files still incompatible under the chosen format. Directory backups are unaffected
(no filesystem-format limits). Harness test
`plan-time-compat-summary-counts-incompatible` verifies the counts; 25/25 pass.

## FIXED: Graceful re-plan when a disc over-reports its capacity (2026-07-15)

**Problem (fail-safe, not graceful):** when media physically holds less than
`GetMediaInfoAsync` reported, the planner bin-packs to the reported size and the burn
only discovers the shortfall mid-write. The burner threw a plain `IOException` once
committed bytes exceeded true capacity, so the whole backup **aborted** — correct
(no silent truncation) but needlessly destructive when the remaining files could
simply spill onto more discs.

**Fix:** introduced a typed `DiscCapacityExceededException` (in `IDiscBurner.cs`)
carrying `ObservedCapacityBytes` (the largest byte count known to fit).
`SimulatedDiscBurner` now throws it when `committedBytes + fileSize` exceeds its
`ActualCapacityBytes` knob, and clears the disc-shelf directory at burn start so a
failed attempt leaves no stale content for the retry. `BackupOrchestrator.ExecuteAsync`
catches it — guarded by `when (!hadIncomingCarry)` so a split spanning in from a prior
*recorded* disc still aborts safely (restarting would double-write committed chunks) —
caps every remaining disc to the observed capacity (`capacityCap`), re-packs all
not-yet-burned files (staged sources + carry + overflow + re-queued + later
allocations, deduped by path), splices the fresh allocations into the current slot,
resets carry/overflow/re-queued, and `continue`s without advancing `discIndex` (the
`finally` releases in-place locks and cleans staging). `ExecuteAsync` now works from a
mutable local `allocations` list rather than the immutable `plan.DiscAllocations`. The
`disc-over-reports-capacity` harness test was rewritten to assert graceful recovery
(succeeds across ≥2 discs, no disc exceeds the observed 100 KB, restore matches);
24/24 harness tests pass.

## FIXED: Removed dead schedule-wipe code path (`ShowJobConfig` cluster) (2026-07-15)

**Problem (dead code + latent footgun):** `MainViewModel.ShowJobConfig` had **zero
callers** but dragged along a null-returning schedule builder. Its `PlanCompleted`
handler called `SaveBackupSetAsync(..., jobConfig.BuildSchedule())`, and
`BackupJobViewModel.BuildSchedule()` returns `null` when `ScheduleEnabled` is false —
so if this path were ever re-wired it would wipe a stored schedule to null (reverting a
set to Off/Interval on reload) whenever the config checkbox happened to be off,
inconsistent with the live save path `SyncSettingsToJobOptions`, which preserves the
existing schedule object.

**Fix:** deleted the entire dead cluster reachable only from `ShowJobConfig` —
`ShowJobConfig`, `SaveBackupSetAsync`, `RestoreJobOptions`, `ApplySourceSettings`
(all in `MainViewModel`), and `BackupJobViewModel.BuildSchedule`. Verified no other
callers anywhere in `src\` before removing; `_pendingSettingsSave`, `StartEditFlow`,
`StartNewBackupFlow`, and `StartBurnForSavedSet` are used by live paths and were kept.
GUI builds clean, 0 warnings.

## FIXED: Disc-burn staging inherited source read-only → temp leak + burn-abort landmine (2026-07-15)

**Symptom (two failures from one cause):** `BackupOrchestrator` staged files into
`%TEMP%\LithicBackup\disc-*` via `File.Copy`, which **preserves the source's
read-only attribute**. A large fraction of backed-up content is read-only (git
object/pack files, anything copied from read-only media), so the staged copies were
read-only too. Consequences: (1) the post-burn cleanup `Directory.Delete(stagingDir,
true)` — wrapped in `catch {}` — silently failed on those files and leaked the staging
folder forever; (2) worse, the **unguarded** pre-clean `Directory.Delete` at the top
of the per-disc loop threw `UnauthorizedAccessException` on a leftover read-only file
from a prior run and **aborted the next disc burn** before it started.

**Fix:** added `BackupOrchestrator.ForceDeleteDirectory(path)`, which clears the
read-only attribute on every file (recursive enumerate, best-effort per file) before
`Directory.Delete(path, true)` — mirroring the existing `ForceDeleteFile`/`ClearReadOnly`
in `DirectoryBackupService`. Routed **all seven** staging-cleanup sites through it: the
main per-disc pre-clean and post-burn `finally`, the split-file spill cleanup, and the
consolidate and reburn staging paths (each had its own pre-clean + `finally`). This is
the disc-backup analog of the already-fixed directory-backup read-only deletion bug.

Scope: only `DiscStagingMode.TemporaryCopy` copies plain files to temp, but zipped/split
files and the split spill stage to temp even in `InPlace` mode, so the helper is needed
regardless of mode.

## FIXED: Source tree showed auto-included new folders as unchecked (display/coverage mismatch) (2026-07-14)

**Symptom:** With a *partially-selected* root (e.g. `D:\` selected with some
exclusions) whose `AutoIncludeNewSubdirectories` is ON, a newly-created
subdirectory showed **unchecked** in the Sources tree even though it *was* in
backup scope. The file scanner and the continuous-backup predicate both include
it via `SourceSelection.IncludesUnlistedDescendants` (returns true for a partial
parent with auto-include on), so any files placed in it while the flag is on get
backed up — but the checkbox never reflected that.

**Root cause:** `SourceSelectionNodeViewModel.CreateChildNode` gave a freshly
enumerated child `_isSelected = parent._isSelected ?? false`, so under a partial
(`null`) parent a new folder rendered unchecked regardless of the auto-include
flag.

**Fix (display/serialisation decoupling + pin-on-toggle-off):** `CreateChildNode`
now derives the unlisted-descendant checkbox from the rule the scanner uses —
`_isSelected = parent._isSelected switch { false => false, true => true, null => parent._autoIncludeNew }`
— so a new folder under a partial auto-include parent renders **checked**. Such a
node is flagged `_isAutoIncludeDerived`, and while auto-include stays ON `ToModel()`
**skips** it: the saved selection stays byte-for-byte identical (no bloat, no
reconcile churn — `MainViewModel.SelectionsEquivalent` sees no diff), and the
folder is re-derived from the parent's auto-include on reload. The flag clears the
instant the node gets a real state — a user click, a descendant edit rippling up
through `IsSelected`/`UpdateFromChildren`, or a saved-model restore in
`ApplySelectionAsync`.

**Semantics of turning auto-include OFF:** "Auto-include *new*" governs *future*
folders only — turning it off must NOT retroactively evict a folder it already
adopted. So the `AutoIncludeNew` setter, on a true→false transition, **pins** every
currently-covered derived descendant (clears its `_isAutoIncludeDerived` flag so
`ToModel` serialises it) as it propagates the flag down. A pinned directory that
has loaded children keeps them explicit, so `IncludesUnlistedDescendants` returns
the now-off flag and genuinely new folders stay excluded; a pinned directory with
no loaded children serialises as a fully-selected subtree, preserving its current
content. Net effect: display agrees with coverage, already-adopted folders survive
an auto-include-off toggle, and only future additions are excluded.

**Proper fix (worker-side materialisation — the durable half):** the editor-side
pinning above only reaches folders that are *materialised* in the tree (loaded/
expanded); an auto-include-covered folder the user never expanded would still drop
from scope if auto-include was turned off before it became an explicit entry. There
is a real difference between a folder's files being backed up via the *live rule*
and the folder being a *persisted checked entry* — only the latter survives the
rule changing. The continuous-backup worker now closes that gap: when it discovers
a **newly-created** directory (USN `USN_REASON_FILE_CREATE`, or a FileSystemWatcher
`Created`/`Renamed`) that a set covers *only* through a parent's auto-include rule,
it writes that folder into the set's `SourceSelections` as an explicit
`IsSelected = true` entry — exactly as if the user had ticked it — via
`SourceSelection.MaterializeDirectory` (creates the intermediate directory chain as
partial nodes, the target as selected, inheriting the governing ancestor's
auto-include flag). It re-reads the set fresh from the catalog before mutating so a
poll-interval-stale in-memory copy can't clobber a GUI edit; the reverse race (GUI
overwriting a just-made pin) is benign — while auto-include stays on the folder is
re-pinned on its next change. `MaterializeDirectory` no-ops when the folder is
already explicit or already covered by a fully-selected ancestor. Net effect: a new
folder created under an auto-include parent becomes permanent membership a few
seconds after it appears, so turning auto-include off later never silently drops it.
Detection is scoped to directory **creates** (not arbitrary metadata changes), so
pre-existing rule-covered folders are left as live-rule coverage until they actually
change. *Follow-up not yet done:* a directory that *enters* the covered area via an
intra-volume move (handled by `RunMovesAsync`, not the create path) is backed up but
not yet materialised — low priority; it re-pins on its next in-place change.


## FIXED: Changed file re-burned to the same multisession disc collided/shadowed (2026-07-12)

**Symptom (hardware edge case, follow-on from the multisession fix below):** once
multisession append works, a file that *changed* between two runs to the same set
is re-staged as a new version (v2). If that append lands on the **same physical
disc** as the earlier version, both versions map to the *same* on-disc path (the
disc path was just the drive-relative source path). After `ImportFileSystem()`
imports the earlier session, IMAPI's `AddFile` **rejects the duplicate path**
(burn fails); and even where it didn't, the newer entry would **shadow** the older
one so the earlier version could no longer be read back. This affects only the
`ExecuteAsync` disc pipeline — splits (unique chunk names), repair/re-burn discs
(fresh discs), and consolidation (latest-only) don't collide.

**Fix — version-unique disc paths.** The file's version is now resolved at *staging*
time (`fileVersion = versionInfo[path].MaxVersion + 1`, computable because
`versionInfo` is only mutated at record time) and carried on `StagedFileInfo.Version`.
A new helper `VersionedDiscPath(relativePath, version)` leaves v1 at its natural
path and inserts a `.v{N}` tag before the extension for later versions
(`docs\report.txt` → `docs\report.v2.txt`). Both the `BurnItem` written to disc and
the recorded `FileRecord.DiscPath` use this versioned path, so every version of a
source file occupies a **distinct** disc path — eliminating both the `AddFile`
collision and the restore shadowing. Restore is unaffected by design: it reads bytes
from `DiscPath` on the platter and writes to the destination derived from
`SourcePath`, so a `.v2`-tagged file still restores to the original location.

**Test:** `tools/disc_test_harness` case `changed-file-reburn-gets-distinct-disc-path`
backs up a file, changes it in place, backs up again to the same set, and asserts two
records (versions 1 and 2) with **distinct** disc paths (v2 carrying a `.v2` tag),
then restores the latest content byte-for-byte. Full matrix 24/24. (The simulator
models each session as its own disc surface, so it can't exhibit the single-volume
`AddFile` collision directly; the distinct-DiscPath invariant is what makes the
real-hardware union safe, and that invariant is what the test pins down.)

## FIXED: Multisession append lost earlier sessions' files on real hardware (2026-07-12)

**Symptom (hardware):** An incremental (multisession) backup that *appended* a new
session to a disc that already had data would, on real IMAPI2 hardware, mount
showing **only the newest session's files**. Files written in earlier sessions
became invisible on the volume and therefore unrestorable — a silent data-loss
bug for anyone relying on incremental disc backups. The decision logic
(`DiscSessionStrategy`) and the `SimulatedDiscBurner` handled multisession fine;
the gap was entirely in the real burner and in cross-run disc labelling.

**Two root causes, both fixed:**

1. **No file-system import in the IMAPI2 burn.** `Imapi2DiscBurner.BurnAsync` built
   a *fresh* standalone `MsftFileSystemImage` on every burn — it never set
   `fsi.MultisessionInterfaces` or called `fsi.ImportFileSystem()`, so an appended
   session's directory tree did not carry forward the earlier sessions' entries.
   **Fix:** `format2Data` is now created up front so its `MultisessionInterfaces`
   can be read before the image is built; when `options.Multisession` and the media
   is non-blank, the burner sets `fsi.MultisessionInterfaces` and calls
   `fsi.ImportFileSystem()` (the standard IMAPI2 append pattern) so the new session
   is the union of all prior sessions. **Hardware-untested** — validated by code
   review only, like the rest of the IMAPI2 path. The simulator can't exercise the
   IMAPI union directly (it models each session as its own disc surface), so the
   real-hardware `ImportFileSystem` call remains the one unverified link.

2. **Disc labels/sequence numbers reset every run → collisions.**
   `BackupOrchestrator.ExecuteAsync` computed `discSequence = discIndex + 1`, so
   *every* run's first disc was `Disc-001`. A second run to the same set produced a
   second `Disc-001` record; restore maps a disc *label* to a physical volume, so
   the collision made the second session's files unresolvable even in simulation.
   **Fix:** the run now seeds `sequenceBase` from the set's existing max
   `SequenceNumber` and numbers this run's discs from there, so labels are unique
   across runs (`Disc-001`, then `Disc-002`, …). This also fixes restore for any
   multi-run backup, not just multisession.

**Test:** `tools/disc_test_harness` case `multisession-append-restores-both-sessions`
runs two backups to the same set/burner, asserts the append produces a second disc
record with a unique label, and restores files from **both** sessions byte-for-byte.
Full matrix 24/24.

**Remaining nuance (not a bug, logged for awareness):** with unique labels, the two
sessions of one *physical* multisession disc are recorded as two logical disc
records (`Disc-001`, `Disc-002`). On real hardware both live on the same platter, so
restore may prompt "insert Disc-002" when the disc is already in the drive; the
files are still present (the imported union) so restore succeeds. A future
improvement could give restore a physical-disc identity so it doesn't re-prompt for
a disc that's already loaded.

## FEATURE: Burn-in-place disc staging mode (no full temp copy) (2026-07-12)

**What:** Disc backups can now burn plain files directly from their original
location instead of copying every file to a temp staging directory first. This
removes the biggest temp-space cost — previously a full disc's worth of data (up
to ~100 GB for a Blu-ray) had to be duplicated on the temp volume before the
burn. Selectable in **Settings → Disc staging**; default remains *copy to temp*
(`DiscStagingMode.TemporaryCopy`) so behaviour is unchanged unless the user opts
into *burn in place* (`DiscStagingMode.InPlace`).

**How it works:** The burn contract changed from "burn everything under this one
staging directory" to an explicit item list — `IDiscBurner.BurnAsync` now takes
`IReadOnlyList<BurnItem>` where each `BurnItem(DiscRelativePath, SourceAbsolutePath)`
maps a disc path to the bytes to read. In `TemporaryCopy` mode every item's source
is a temp copy (unchanged behaviour). In `InPlace` mode plain files' items point at
the *original* source path, and `BackupOrchestrator.ExecuteAsync` holds a
`FileShare.Read` lock (`StagedFileInfo.HeldLock`) on each such file from the moment
its size is validated until after the burn and catalog recording complete (released
in the per-disc `finally`). `FileShare.Read` blocks writers, so the file cannot grow,
change, or be deleted mid-burn, while still letting the burner read it concurrently.
Zipped and split files always stage to temp because their on-disc bytes differ from
the source; the software payload and exported catalog also live under temp.

- **Growth safety preserved:** the in-place path re-checks the file's length under
  the held lock (same as the copy path) and re-queues/bumps a grown file to a later
  disc rather than overflowing. Covered by harness test `burn-in-place-growth-safe`.
- **IMAPI path (hardware-untested):** `Imapi2DiscBurner.BurnAsync` was rewritten from
  `root.AddTree(stagingDir)` to per-item `root.AddFile(discRelativePath, stream)`,
  where `stream` is an `IStream` opened over the source via `SHCreateStreamOnFileEx`
  (read + deny-write). This mirrors the tested simulated path but, like all IMAPI2
  code here, has not been run against real hardware.
- **Config placement:** stored on global `UserSettings.DiscStagingMode` (machine-wide,
  like `MemoryBudget`) and stamped onto `BackupJob.StagingMode` when the job is built
  (MainViewModel + BackupWorker). Not persisted per backup set.
- **Tests:** `burn-in-place-basic` (multi-disc, byte-exact restore, no overflow) and
  `burn-in-place-growth-safe`. Full matrix 23/23.

The split-file spill snapshot (below) still copies each *oversized* file to temp even
in `InPlace` mode — that transient cost is unchanged and remains logged as tech debt.

## FIXED: Bin-packer crammed every file onto disc 1 (multi-disc spanning broken) (2026-07-12)

**Status:** Fixed 2026-07-12 in `BinPacker`.

**Symptom:** A disc backup whose data exceeds a single disc's capacity did not span
multiple discs — the planner allocated *every* file that individually fit onto the
first disc. On simulated media this went unnoticed (the `SimulatedDiscBurner` writes
to a shelf and doesn't enforce capacity), but a real IMAPI burn would overflow /
fail on disc 1. Found by the new headless disc-test harness
(`tools/disc_test_harness`): 5×80 KB files with a 200 KB disc produced 1 allocation
(400 KB) instead of 3.

**Root cause:** `DiscAllocation.FreeBytes` is `init`-only. `BinPacker`'s first-fit
loop checked `alloc.FreeBytes >= file.SizeBytes` but never decremented `FreeBytes`
as it added files (it couldn't — the property is immutable), so every existing bin
always reported its *full* capacity as free. Result: the first bin "fit" everything.

**Fix:** `BinPacker` now packs into a mutable private `Bin` working type that tracks
running `Used`/`Free`, and builds the immutable `DiscAllocation` list at the end.
First-fit-decreasing now sees each bin's true remaining space and opens new discs
when needed. Verified by the harness `happy-multi-disc-span` case (now 3 discs).

## FIXED: Oversized file split into chunks does not span physical discs (2026-07-12)

**Status:** Fixed 2026-07-12 in `BackupOrchestrator.ExecuteAsync` +
`RestoreService`. Verified by `tools/disc_test_harness` (`happy-file-splitting`,
which now asserts chunks land on ≥2 distinct discs with zero disc overflow and a
byte-exact restore).

**Symptom (before fix):** A single file larger than one disc's capacity was split
into disc-sized chunks, but *all* the chunks were staged to the **same** disc and
burned together — so the file never spanned multiple physical discs. In the harness a
300 KB file with a 150 KB disc produced two 150 KB `.discburn-split` chunks both on
`disc-1` (300 KB on a 150 KB disc). Restore still reassembled correctly in
simulation because both chunks were co-located, but on real media disc 1 would
overflow.

**Root cause:** the per-disc staging loop called
`_fileSplitter.SplitAsync(file, stagingDir, chunkSize, ct)`, writing *every* chunk
into that one disc's staging directory. Nothing distributed the chunks across the
plan's discs, and each chunk was recorded against a single disc.

**Fix:** `ExecuteAsync`'s staging loop was rewritten around a `SplitCarry`/overflow
model. When a file can't fit the remaining space on the current disc (and splitting
is allowed, or the file exceeds a whole disc), `BeginSplitAsync` snapshots it to a
spill file and `PlaceSplitChunksAsync` writes as many disc-sized chunks as fit onto
the current disc, carrying the remainder to subsequent discs across loop iterations
(the loop now continues while a carry or overflow list is non-empty, opening fresh
discs as needed). One shared `FileRecord` is created for the split file and each
`FileChunk` records its own `DiscId`/`Offset`/`Length`. Files that simply don't fit
the remaining space (but fit a whole disc) now go to an `overflowFiles` list for the
next disc instead of overflowing the current one — a latent overflow bug fixed in the
same pass. Spill directories are cleaned up in a `finally`.
`RestoreService.RestoreSplitFilesAsync` reassembles across discs: it pre-sizes each
destination, groups chunks by `DiscId`, mounts each disc once via
`DiscInsertCallback`, and writes each chunk at its `chunk.Offset`. The catalog layer
already returned all chunks for a file record regardless of disc
(`GetChunksForFileAsync`), so no schema change was needed. The old
`FileSplitter`/`IFileSplitter` (which chunked a whole file into one directory) was
now fully unused and was deleted along with the orchestrator's dead `_fileSplitter`
dependency.

**Tech debt (transient disk cost):** `BeginSplitAsync` snapshots the entire oversized
file to a spill file (`%TEMP%\LithicBackup\spill-*/data`) before carving chunks, so
the split is taken from a stable, self-consistent byte source and the SHA-256 is
computed once. The cost is up to one extra full copy of the file on the temp volume
for the duration of the burn (cleaned up in `finally`). For a genuinely huge file
(e.g. 100 GB across several BluRays) that transient doubling could be significant.
Proper fix if it ever bites: chunk directly from the source with offset seeks and a
one-shot streamed hash, guarded against mid-burn source mutation (size/mtime recheck),
avoiding the snapshot — the snapshot is currently the simplest way to guarantee
consistency across a multi-disc, possibly multi-hour burn.

## FIXED: File that grows between plan and burn could overflow a disc (2026-07-12)

**Status:** Fixed 2026-07-12 in `BackupOrchestrator.ExecuteAsync`. Verified by
`tools/disc_test_harness` (`file-grows-between-plan-and-burn`).

**Symptom:** The plan bin-packs to each file's scanned size, but staging happens
later. If a file grew between planning and staging, the plain-copy path recorded the
staged item at the *planned* size (`StagedSizeBytes = file.SizeBytes`) while copying
the file's larger current content. The per-disc capacity accounting then undercounted,
so a grown file could push a disc over its real capacity. Split files had a related
metadata inconsistency: the `FileRecord.SizeBytes` used the planned scan size while
the chunks were carved from a fresh snapshot of the (possibly larger) live file.

**Root cause (two parts):**
1. `StagedSizeBytes = file.SizeBytes` used the planned size, not the bytes actually
   copied. There was also a TOCTOU gap between the pre-copy metadata check and the
   copy: the file could grow in between and be copied larger than checked.
2. A latent bug in the same loop: `TryFillGapFromPending` appends a replacement file
   to `filesToProcess`, but the loop was a `foreach` over that same list — modifying a
   `List<T>` mid-`foreach` throws `InvalidOperationException`. This never fired only
   because no test previously triggered a mid-staging skip/re-queue during a normal
   plan+execute.

**Fix:**
- The plain-copy path now opens the source with `FileShare.Read` (a read lock that
  blocks writers) **first**, then re-checks the size *under the lock*. Once locked the
  file cannot grow, so the staged bytes and the capacity accounting are guaranteed
  consistent. If the locked size differs from the plan or no longer fits the space left
  on the disc, the file is re-queued at its true size for a later disc (and the gap it
  leaves is filled from the pending queue). `StagedSizeBytes` is set to the locked size.
- Split files carry the snapshot's actual byte length (`SplitContext.Size`, captured
  under the read lock in `BeginSplitAsync`) and the split `FileRecord.SizeBytes` uses
  it, so the record matches the sum of its chunk lengths.
- The staging loop is now an index-based `for` loop so gap-fill appends are both
  tolerated and processed.

## OPEN (minor): disc that over-reports capacity fails the burn instead of re-planning (2026-07-12)

**Status:** Behaves safely (fails loud, no corruption) but not gracefully. Covered by
`tools/disc_test_harness` (`disc-over-reports-capacity`).

**Symptom:** When media physically holds less than `GetMediaInfoAsync` reported (a
disc that over-states its size), the plan bin-packs to the reported capacity and the
burn only discovers the shortfall mid-write. `SimulatedDiscBurner` (with the
`ActualCapacityBytes` test knob) and a real burner both throw `IOException` once the
committed bytes exceed the true capacity — the backup aborts with an error rather than
silently truncating. That is the correct fail-safe.

**Not-yet-done (graceful path):** ideally the executor would catch the
capacity-exceeded failure, re-plan the remainder of that disc's files (plus anything
already staged for it) onto a fresh disc at the observed smaller capacity, and
continue — instead of aborting the whole run. This needs a re-plan/resume hook in
`ExecuteAsync` and a way to feed the observed actual capacity back into the packer.
Deferred; the safe-abort behavior is acceptable in the meantime.

## FIXED: Auto-include-new ignored on partially-selected directories (2026-07-12)

**Status:** Fixed 2026-07-12 in `SourceSelection`, `FileScanner`, and
`OrphanedDirectoriesViewModel`.

**Symptom:** With `D:\` set as a source (auto-include-new on) but *partially*
selected — i.e. a few subfolders deselected, which flips the node's tristate
`IsSelected` from `true` to `null` — new top-level entries under `D:\` were never
backed up. Reported via a continuous-mode rename: `D:\warez` → `D:\test_warez` was
not renamed at the destination. The USN journal produced the move, but the
continuous path judged `D:\test_warez` (a new, unlisted child of the partial `D:\`
node) as *not in the set*, so it classified the rename as "moved out": it
soft-deleted `warez`'s catalog records and ignored `test_warez` entirely.

**Root cause:** three separate copies of the inclusion rule
(`FileScanner.ScanNode`, `SourceSelection.IsPathIncluded`/`EvaluateNode`, and
`OrphanedDirectoriesViewModel.SelectionCoversPath`) all gated auto-include-new
behind `IsSelected == true`, so a partially-selected (`null`) directory never
picked up unlisted descendants even with `AutoIncludeNewSubdirectories = true`.
This was a silent data-loss risk on full scans too, not just continuous renames.

**Fix:** extracted one shared predicate,
`SourceSelection.IncludesUnlistedDescendants(node)`, used by all three sites. A
not-excluded directory covers unlisted descendants when it's either fully selected
with no child overrides (whole subtree) *or* has auto-include-new enabled —
regardless of whether it is fully or partially selected. The per-directory
auto-include toggle is now the sole control, as intended.

**Recovery for the already-broken state:** the earlier rename already soft-deleted
`warez` and skipped `test_warez`; the fix does not retroactively repair that. A full
/reconciling backup will now pick up `test_warez` as fresh files (the stale `warez`
destination copy lingers until the next cleanup/retention purge). Future renames
under a partial+auto-include directory will relocate in place correctly.

## ADDED: Continuous-mode fallback for non-NTFS source volumes (2026-07-12)

**Status:** Implemented 2026-07-12 in `BackupWorker`, `FileSystemMonitorImpl`, and
`IFileSystemMonitor`.

**What changed:** Continuous mode previously only worked on NTFS (it is driven by the
USN change journal; `UsnJournalReader.TryOpen` returns null for non-NTFS/inaccessible
volumes, which used to silently disable continuous detection for that volume). Non-NTFS
source volumes (exFAT, FAT32, network shares) now fall back to a `FileSystemWatcher`.

**Design:**
- `BackupWorker` owns a single `FileSystemMonitorImpl _fsMonitor` watching every non-NTFS
  watch root across all continuous sets (`MaintainFallbackWatchers`). Watcher events arrive
  on thread-pool threads and are buffered into `ConcurrentQueue`s, then drained on the poll
  thread (`DrainFallbackChanges`) into the same per-set `Pending` debounce map the USN path
  feeds — so `Pending` mutation stays single-threaded and the downstream targeted-backup
  pipeline is shared.
- **Restart reconciliation:** a watcher only sees live changes, so any (re)start of the
  watcher — including the first poll after a process restart (roots go empty→populated) —
  flags the affected sets `NeedsReconcile`, triggering a full timestamp/size scan
  (`RunFullBackupAsync` → `FileScanner.ComputeDiffAsync`, which compares size + LastWrite,
  no hashing) to catch anything changed while the watcher wasn't running.
- **Buffer overflow:** `FileSystemMonitorImpl` now sets `InternalBufferSize = 64 KB` (max)
  and raises a new `IFileSystemMonitor.Overflow` event on any watcher `Error`
  (InternalBufferOverflowException = too many changes at once, some dropped). The worker
  maps the overflowed root to affected sets and flags `NeedsReconcile` — a whole-tree
  rescan rather than trusting the now-incomplete per-file event list.
- Directory Created/Renamed events are expanded to their files (a bulk move-in may not fire
  per-file events); a plain directory Changed is ignored (the child's own event covers it).

**Known limitations of the fallback (inherent, acceptable):**
- Non-NTFS deletions are only reconciled on the next full scan (restart/overflow/schedule),
  same as the pre-existing NTFS "plain single-file deletes aren't reconciled in
  pure-continuous mode" open item further down — `ExecuteTargetedAsync` skips missing paths.
- A watcher restart (triggered when the set of non-NTFS watch roots changes, e.g. a set is
  edited) drops whatever was buffered; that is covered by the reconcile-on-(re)start flag,
  at the cost of an extra full timestamp/size scan on those sets.

## FIXED: Service panel could get stuck on "starting…/stopping…" with all buttons greyed (2026-07-12)

**Status:** Fixed 2026-07-12 in `MainViewModel.RefreshServiceStatus` /
`PollWhileServicePendingAsync`.

**Symptom (user report):** After uninstalling the Worker service, running the MSI
installer, and reopening the GUI, the Worker-service panel showed "Worker service
stopping…" indefinitely with Install/Start/Stop/Uninstall all disabled. The service was
actually fine (`sc query` showed RUNNING); the GUI just never left the stale reading.

**Root cause:** `START_PENDING`/`STOP_PENDING` satisfy none of the `CanInstall/
CanUninstall/CanStart/CanStop` gates, so all four buttons disable while pending. The SCM
is only re-queried on demand — at startup, on a button action (via
`WaitForServiceReadyAsync`, 5 s), or when navigating home — so a status read that landed
on a pending transition (a reinstall in progress, or a snapshot inherited by a long-lived
GUI instance) stuck there with no path back: pending disables the buttons, and disabled
buttons can't trigger the refresh that would clear pending.

**Fix:** `RefreshServiceStatus` now spawns a background watchdog
(`PollWhileServicePendingAsync`) whenever it observes a pending state and no poll is
already running; the watchdog re-queries every second (up to 60 s) until the state
settles, then lets the normal `Can*` notifications re-enable the buttons.
`WaitForServiceReadyAsync` shares the same `_servicePollActive` guard so an action's poll
and the watchdog never stack, and hands off to the watchdog if it times out while still
pending.

## FIXED: Modify dialog close stalled the UI with a ~10s wait cursor (2026-07-12)

**Status:** Fixed 2026-07-12 in `MainViewModel.ReconcileDestinationAfterEditAsync`.

**Symptom (user report):** After closing the Modify (backup-set editor) dialog, the
main window showed the busy/wait cursor for ~10 seconds, even when nothing was
changed in the dialog.

**Root cause:** Closing the dialog always auto-saves (`_pendingSettingsSave` →
`SaveAllAsync`), which sets `savedThisSession = true`, so the post-close
`ReconcileDestinationAfterEditAsync` ran on *every* close. That method
unconditionally called `GetAllFilesForBackupSetAsync`, loading the set's entire file
table (≈1M rows for large sets) on a background thread with `Mouse.OverrideCursor =
Wait`, just to diff which source folders were dropped/added — work that is pointless
when the selection didn't change.

**Fix (part 1 — unchanged selection):** Added a conservative fast-path at the top of
the reconcile: if the source selection is unchanged from when the dialog opened
(`SelectionsEquivalent`, comparing the same `JsonSerializer.Serialize` form the catalog
persists with), return immediately without loading any file records. Any genuine
selection change serializes differently and still runs the reconcile below, so
purge/backup prompts for dropped/added folders are unaffected. Gating on the JSON diff
(rather than `HasUnsavedChanges`) is correct for both the auto-save-on-close and
explicit-Save-then-close paths, since `HasUnsavedChanges` resets after an explicit save.

**Fix (part 2 — changed selection, targeted reconcile):** Even when the selection *did*
change, the reconcile no longer loads the whole file table. `SourceSelectionViewModel`
now records the path of every node the user toggles this session (in
`RequestSelectionSettle`, which fires only for user-clicked nodes — propagation to
children/ancestors is suppressed before it reaches the settle call) and exposes them as
`ChangedSelectionPaths`. `ComputeRemovedFilesTargeted` queries only those subtrees via
`GetFileRecordsUnderDirectoryAsync` (which matches a path and all its descendants,
working for both file and directory nodes), keeping records that were included before
the edit and excluded after. This is correct because a file's inclusion can only change
if the user toggled that file or one of its ancestor directories — so every removed
file sits at or under a recorded path. Two fallbacks preserve correctness: a recorded
empty path (the virtual "All Drives" root, e.g. "deselect all") reverts to the full
`GetAllFilesForBackupSetAsync` scan since a whole-tree change can't be localised; and an
empty recorded set (only cosmetic/expansion or auto-include-new edits, which never drop
an already-backed-up file) skips catalog reads entirely.

**Related concern (now fixed 2026-07-12):** auto-save on close calls
`sourceSelection.GetSelections()`. Selection restore is deferred to a post-show async
pass (Phase 3 in `StartEditFlow`), during which `GetSelections()` returns a
partial/empty tree — so closing the dialog (or clicking Save/Seed/Largest-Files) before
restore completed could save a truncated selection over the real saved sources.
**Fix:** `StartEditFlow` now holds a `TaskCompletionSource selectionRestored`, completed
in Phase 3's `finally` (so it also releases for new sets and error paths). Every path
that persists the selection — `SaveAllAsync` (used by both the Save button and the
auto-save-on-close), the Seed handler, and both Largest-Files save points — `await`s it
before reading `GetSelections()`, so a fast close simply waits for the (unchanged)
restore to finish and then writes the correct tree. The debounced `SelectionChanged`
auto-save was already gated on `IsApplyingSelections`, which flips in lockstep with the
new signal.

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

**Scope:** this is a **directory-backup-only** concern. Block-level dedup is
deliberately never applied to optical/disc backups (write-once media has no
persistent shared `_blocks` store to dedup against — see the note at
`BackupOrchestrator.cs` ~366), so the pre-pass and `preRecipes` only ever run when
the destination is a folder/drive.

**Proper fix if the hash maps ever bite (spill, don't recompute):** the recipes are
already computed once in the pre-pass, so the cheap fix is to **persist them** —
spill `preRecipes` to a temp on-disk structure (temp SQLite table, or a simple
per-file temp file keyed by path hash) during the pass and stream them back in the
main loop, keeping only `wholeFileCount` + `blockOccur` (both O(unique hashes)) in
RAM. Reading a stored recipe back is a few KB of I/O per file. The alternative —
dropping the recipes and *recomputing* each `.dedup` file's recipe in the main loop —
is worse: it re-reads the file's entire content (potentially GB) and re-hashes it,
trading a large amount of disk I/O + CPU to save a small amount of temp disk. Prefer
the spill.

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

## Test Disc feature: re-burn repairs untested on real optical hardware (2026-07-12)

**Status:** Implemented but only exercised against the simulated burner / directory
paths. The read-back test (`RestoreService.VerifyDiscAsync`) works over any mounted
volume, but the two re-burn repairs go through `Imapi2DiscBurner.BurnAsync` and need
validation with a real burner + media.

**What shipped:** A new per-set right-click action **Test Disc** (optical sets only —
gated on `JobOptions.TargetDirectory` being null/empty) integrity-tests one burned
disc against the catalog and, on failure, offers two repairs:
- `TestDiscViewModel` → `IRestoreService.VerifyDiscAsync(discId, discRoot,
  verifyContents)` reads every non-deleted catalog record on the disc, confirms
  presence + size, and (opt-in) re-hashes SHA-256; understands plain/zipped/split/
  `.dedup`/`.fileref` forms (mirrors the restore reader). `.fileref` whose backing
  plain copy is on another disc is reported as `UnresolvedReference`, not a failure.
- **Re-burn whole disc** → `IBackupOrchestrator.ReplaceDiscAsync(discId, recorderId,
  progress)` — re-stages every file the disc held from the live source and burns a
  fresh replacement disc.
- **Re-burn affected files** → `IBackupOrchestrator.ReplaceDiscFilesAsync(discId,
  failedFileRecordIds, recorderId, progress)` — re-burns only the failed files onto a
  new supplementary disc (Version+1 records, old records marked `IsDeleted` so restore
  resolves to the fresh copies).

**Caveats to validate on hardware:**
1. **Recorder ↔ drive mapping is best-effort.** The VM auto-detects the *reading*
   drive by volume label (`DriveInfo`), but the *burning* recorder is taken as
   `IDiscBurner.GetRecorderIds()[0]`. On a multi-burner machine the fresh disc could
   be burned in a different drive than the one tested. Proper fix: map the selected
   `DriveInfo` root to its IMAPI2 recorder id (e.g. via `MediaInfo.RecorderName` /
   device path) and pass the matching recorder.
2. **Both repairs read from the live source files.** A source file that no longer
   exists on disk is silently skipped (whole-disc) or reported as "0 re-burned"
   (affected-files) — recovery from another good disc is not automated. This is by
   design but should be surfaced more clearly once tested.
3. Post-burn read-back reuses the same IMAPI2 remount path flagged in the entry
   below, so its hardware caveats apply here too.

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

**Out-of-scope moves — now reconciled promptly (2026-07-12):** an item moved **out**
of a set's scope (to the Recycle Bin or any location outside the selection) is now
treated identically to a deletion, at once. `RunMovesAsync`'s `oldIn && !newIn`
branch (and the within-set recopy fallback that vacates the old path) calls
`MarkMovedOutAsync`, which soft-deletes the old path's catalog record(s)
(`MarkFilesDeletedByDirectoryAsync` for a directory, `MarkFilesDeletedBySourcePathsAsync`
for a file). This is safe to do immediately because a move is unambiguous in the USN
journal (an explicit old→new FRN pair), unlike a bare delete which can be atomic-save
churn. The moved item is **not** relocated on the destination — its destination copy
and version history are retained until the user's next Cleanup purges them, exactly
like a deleted file. Design decision (user, 2026-07-12): "moved-out files should be
treated identically with deleted files … they shouldn't be retained in the catalog
after the user does the next cleanup."

**Still open — plain single-file deletes aren't reconciled in pure-continuous mode:**
A bare source *delete* (not a move) in continuous mode is still deferred: `ExecuteTargetedAsync`
skips missing paths (`continue`), relying on a full scan that never runs in pure-continuous
mode. Cleanup's `DeletedFromDisk` only surfaces a record when its whole parent directory is
gone, so an individual deleted file whose parent survives lingers as an active row until a
scheduled/manual full run. This is intentionally NOT acted on promptly (a bare delete can be
transient atomic-save churn, unlike an explicit move). Proper fix if it ever bites: debounce
delete records and reconcile them after a quiet window, or add the periodic safety-net full
scan for continuous sets noted in the journal-wrap entry above.
