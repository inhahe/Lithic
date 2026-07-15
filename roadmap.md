# LithicBackup — Roadmap

Planned work, pulled from `known-issues.md` triage and design discussions. Items
are ordered roughly by priority. When one ships, move its detail into
`known-issues.md` as a FIXED/ADDED entry and delete it here.

---

## 1. Disc-burn staging inherits source read-only → temp leak + burn-abort landmine ✅ DONE

**Status: SHIPPED.** Added `BackupOrchestrator.ForceDeleteDirectory` (clears the
read-only attribute on every file before `Directory.Delete`) and routed all seven
staging-cleanup sites through it (main per-disc pre-clean + post-burn finally, the
split-spill cleanup, and the consolidate/reburn staging paths). See the FIXED entry
in `known-issues.md`.

**Priority: high (real latent bug, small fix).**

`BackupOrchestrator` copies source files into a temp staging dir via `File.Copy`
(BackupOrchestrator.cs ~1116, ~1437, ~1567), and `File.Copy` **preserves the
source's read-only attribute**. A large fraction of backed-up content is read-only
(git object/pack files, anything copied from read-only media), so the staged copies
are read-only too. Consequences:

- **Temp leak:** the post-burn cleanup `Directory.Delete(stagingDir, true)`
  (BackupOrchestrator.cs ~986) is wrapped in `catch {}`, so it silently fails on the
  read-only copies and leaves them in `%TEMP%\LithicBackup\disc-*` forever.
- **Burn-abort landmine:** the pre-clean `Directory.Delete(stagingDir, true)`
  (BackupOrchestrator.cs ~261-262) is **not** guarded, so a leftover read-only file
  from a prior run throws `UnauthorizedAccessException` and **aborts the next disc
  burn** before it starts.

**Fix:** add a recursive `ForceDeleteDirectory` helper that clears the read-only
attribute on each file before deleting (mirroring the existing `ForceDeleteFile` /
`ClearReadOnly` in `DirectoryBackupService` and `CatalogReconcileService`), and use
it at every staging-cleanup site (~261, ~986, and the split-file spill cleanup
~1000). This is the disc-backup analog of the already-fixed directory-backup
read-only deletion bug (see the Cleanup "reappears" entry in known-issues.md).

Scope note: only affects `DiscStagingMode.TemporaryCopy`. In `InPlace` mode plain
files are never copied to temp, but zipped/split files and the split spill still are,
so the helper is still needed.

---

## 2. Remove the dead schedule-wipe landmine in `SaveBackupSetAsync` ✅ DONE

**Status: SHIPPED.** Deleted `MainViewModel.ShowJobConfig` (zero callers) and the whole
cluster that only it reached: `SaveBackupSetAsync`, `RestoreJobOptions`,
`ApplySourceSettings`, and `BackupJobViewModel.BuildSchedule`. The live save path
(`SyncSettingsToJobOptions`, which preserves the existing schedule object) is unaffected.
GUI builds clean, 0 warnings.

**Priority: medium (dead code, remove a footgun).**

`MainViewModel.ShowJobConfig` has **zero callers**, and it drags along
`SaveBackupSetAsync` and a null-returning `BackupJobViewModel.BuildSchedule()`.
`BuildSchedule()` returns `null` when `ScheduleEnabled` is false, so if this path
were ever re-wired it would wipe a stored schedule to null (reverting a set to
Off/Interval on reload) whenever the config checkbox happened to be off —
inconsistent with the live save path `SyncSettingsToJobOptions`, which preserves the
existing schedule object.

**Fix:** delete the dead trio (`ShowJobConfig`, `SaveBackupSetAsync`, and — if it
has no other callers after that — `BuildSchedule`). Verify `BackupJobViewModel` is
still otherwise used before removing anything shared.

---

## 3. Graceful re-plan when a disc over-reports its capacity ✅ DONE

**Status: SHIPPED.** Added a typed `DiscCapacityExceededException` (in
`IDiscBurner.cs`) that carries `ObservedCapacityBytes` — the largest byte count known
to fit. `SimulatedDiscBurner` throws it when `committedBytes + fileSize` exceeds the
`ActualCapacityBytes` knob (and now clears the disc-shelf dir at burn start so a failed
attempt leaves no stale content behind). `BackupOrchestrator.ExecuteAsync` catches it
(guarded by `when (!hadIncomingCarry)` so a split spanning in from a prior *recorded*
disc still aborts safely rather than double-writing committed chunks), caps all
remaining discs to the observed capacity, re-packs every not-yet-burned file
(staged sources + carry + overflow + re-queued + later allocations, deduped by path),
splices the fresh allocations into the current slot, and `continue`s without advancing
`discIndex`. The over-report harness test now asserts graceful recovery (24/24 pass).
See the FIXED entry in `known-issues.md`.

**Priority: medium (currently fails safe, not gracefully).**

When media physically holds less than `GetMediaInfoAsync` reported (a disc that
over-states its size), the planner bin-packs to the reported capacity and the burn
only discovers the shortfall mid-write. Both the simulated burner (with the
`ActualCapacityBytes` test knob) and real hardware throw `IOException` once committed
bytes exceed true capacity — the backup **aborts with an error rather than silently
truncating**, which is the correct fail-safe. Covered by
`tools/disc_test_harness` (`disc-over-reports-capacity`).

**Fix (graceful path):** have the executor catch the capacity-exceeded failure,
re-plan the remainder of that disc's files (plus anything already staged for it) onto
a fresh disc at the *observed* smaller capacity, and continue — instead of aborting
the whole run. Needs a re-plan/resume hook in `ExecuteAsync` and a way to feed the
observed actual capacity back into the packer.

---

## 4. Plan-time disc-filesystem compatibility warning (suggest UDF up front) ✅ DONE

**Status: SHIPPED.** Added `DiscCompatibilitySummary` (Core.Models) and
`IBackupOrchestrator.SummarizeCompatibility(plan, filesystemType)`, which walks every
planned file and applies the *same* per-file check the burn uses under
`ZipMode.IncompatibleOnly` — now routed through a shared `IsCompatibleForDisc` helper
that checks the **disc-relative** path (what actually lands on the disc), so the
plan-time count can't drift from what the burn zips. `MainViewModel.WarnAndMaybeSwitchToUdf`
runs the summary in the disc-burn path (after plan, before burn); when a significant
fraction (≥5% of files, ≥5% of bytes, or ≥20 files) would be zipped and the format
isn't already UDF, it shows a Yes/No/Cancel warning offering to switch this run to UDF
(no re-scan needed — bin-packing is capacity-based/format-independent, so it just flips
`job.FilesystemType`). Harness test `plan-time-compat-summary-counts-incompatible`
locks the summary counts (25/25 pass). See the ADDED entry in `known-issues.md`.

**Priority: medium (UX; prevents surprise mass-zipping).**

Today the disc `FilesystemType` (ISO 9660 / Joliet / UDF) is chosen in Settings and
only *acted on* during the burn: `ZipMode.IncompatibleOnly` (the default) silently
auto-zips any file whose name/path/depth violates the selected format's limits
(`PathCompatibility.CheckCompatibility`). That's a safe fallback, but the user never
sees it coming — a set full of long Unicode paths burned as ISO 9660 gets quietly
zipped wholesale, changing how the content lands on the disc, with no chance to
reconsider the format first.

This is the cleaner alternative to the rejected "switch filesystem format mid-burn"
idea: a physical disc authors as a single image, so the real fix is to pick the right
format *before* the burn, not partway through.

**Fix:** at plan time (after file enumeration, before staging/burn), run each planned
file's disc-relative path through `PathCompatibility.CheckCompatibility` for the
selected `FilesystemType` and produce a summary, e.g. "142 of 5,003 files (2.1 GB)
will be zipped for ISO 9660 compatibility." If a significant fraction is incompatible,
surface a warning that suggests switching to **UDF** (the most permissive format,
already the default) and offer to re-plan under UDF without zipping. Show this in the
plan/confirmation UI so the user chooses the format up front.

Notes:
- The compatibility check already exists per-format in `PathCompatibility`; this is
  wiring it into a pre-burn pass + summary, not new format logic.
- Keep `ZipMode.IncompatibleOnly` as the fallback for the handful of files that are
  still incompatible under the chosen format (e.g. a genuinely too-long path even for
  UDF), so the warning informs rather than blocks.
- Only relevant to disc backups; directory backups have no filesystem-format limits.

---

## 5. Avoid the double-read on large size-colliding files (progressive prefix hash) ✅ DONE

**Status: SHIPPED.** Added an intra-run prefix index to `DirectoryBackupService`
(`intraRunPlainPrefixes`: size → set of 64 KiB SHA-256 prefixes of already-stored
plain copies) plus a `RuledOutByPrefixAsync` pre-check. For a large (non-buffered)
file whose size collides *only* with other files in the same run — not with any
already-stored plain content (`IsIntraRunOnlyCollision`) — a cheap prefix hash
proves it shares no prefix with any same-size plain copy stored so far, so the full
up-front hash is skipped and `deferHashToCopy` reads the file exactly once (hashing
while copying). If the prefix *does* collide, it escalates to the full hash to
confirm and, if identical, writes a `.fileref`. Every plain copy of a colliding
size registers its prefix (`ComputePrefixHashOfBuffer` for buffered writes,
`ComputePrefixHashAsync` for streamed) so no later identical file can slip through
as a second plain copy (a missed-dedup bug). Existing-content size collisions keep
the full up-front hash (no schema change, no destination reads). New harness
`tools/dir_dedup_test` (directory-backup dedup had zero automated coverage) verifies
identical files still dedup to one plain + one `.fileref`, different same-size files
store two plain copies, and a mixed X/Y/X sequence resolves the ref to X — on both
the streaming (Fixed 0 GiB) and buffered budgets (24/24 checks pass). See the FIXED
entry in `known-issues.md`.

**Priority: low (optional; narrow win).**

In directory-backup file-level dedup, files whose size collides with existing plain
content are read once to compute a full hash and, if they turn out *not* to be a
duplicate, read a second time to copy the bytes — but only when the file is over the
in-memory buffer budget (small files are buffered on the first read and copied from
memory, so they're already read once). The redundant second read only hits large
size-colliding files.

WinDirStat's progressive-tier hashing (`FileDupeControl.cpp`: SMALL 4 KiB → MEDIUM
1 MiB → whole file, gated by a size bucket with ≥2 members) is the right shape for
cutting this: hash a cheap prefix first and only escalate to the full hash when the
prefix collides too. Storing/streaming a prefix hash would let most size-collisions be
ruled out after reading a few KiB instead of the whole file, shrinking the double-read
to only the files that genuinely share both size *and* prefix.

This is the **only** part of the WinDirStat progressive-hashing design that transfers
to Lithic's backup path — non-duplicates still have to be read in full to *copy* them,
and confirmed duplicates still need a full hash to be written safely as a `.fileref`,
so progressive tiers buy nothing there. It's a modest optimization on a narrow file
class, hence low priority.

---

## 6. "Actual backup size" estimate that accounts for dedup ✅ DONE

**Status: SHIPPED.** Added `DedupSizeEstimator` (`src/LithicBackup.Services/DedupSizeEstimator.cs`),
a standalone estimator that faithfully mirrors `DirectoryBackupService` dedup
accounting to report the bytes a backup would actually write. File-level path uses
the size gate + item-5 progressive prefix-hash escalation (most candidates settled
after 64 KiB, full read deferred only on a real prefix collision); block-level path
reads/hashes every file via `IDeduplicationEngine` and counts only new blocks
(existing store + intra-run `seenNewBlocks`), gated behind an explicit user action
with a "this reads all your data" warning. Both dedup against already-stored content
*and* files seen earlier in the same scan, and reuse the shared dedup primitives
(`GetActivePlainContentSizesAsync`, whole-file hash, block store, `_hashCache`) so the
number can't drift from the real backup. Surfaced in `BackupCoverageViewModel` /
`BackupCoverageView` as an opt-in "Compute actual size" button; the fast raw-size scan
remains the default. A no-drift test (`tools/dedup_estimate_test`) runs BOTH the real
`DirectoryBackupService` and the estimator over identical inputs and asserts
`StoredBytes` equals the bytes physically written across no-dedup, file-level,
block-level (incl. partial blocks), and all-redundant incremental scenarios. See the
ADDED entry in `known-issues.md`.

**Priority: medium (UX; the current estimate over-states deduped backups).**

Today's pre-backup scan (`BackupCoverageViewModel`) sums **raw** file sizes and
buckets each file against the catalog *by path* — it never hashes content, so two
identical files at different paths both count in full. For a set with dedup enabled
that over-states the space the backup will actually consume, sometimes badly.

Add an opt-in "compute actual size" pass that mirrors what the real
`DirectoryBackupService` dedup path would store, so the number matches reality:

- **File-level dedup enabled, block-level off:** a file only dedupes if its *whole*
  content matches existing plain content (or an earlier file in this run). Rule out
  the vast majority by the **size gate** first (a size that collides with nothing
  already stored is definitely new → count full, no read). For the size-colliders,
  use the **progressive prefix-hash** algorithm (item 5 / WinDirStat's tiers: cheap
  prefix → escalate only on collision) so most candidates are settled after a few KiB
  instead of a full read. This is the case where partial hashing genuinely pays off.
- **Block-level dedup enabled:** you must read and hash *every block of every file* to
  know which blocks are already stored, so the whole content is read regardless —
  **partial/prefix hashing buys nothing here** (as noted, correctly, in the request).
  The pass is exact but expensive; gate it behind an explicit user action and a
  clear "this reads all your data" warning.
- **Both enabled:** block-level dominates the cost (full read either way), so treat it
  like the block-level case.

Implementation notes:
- Reuse the existing dedup machinery so the estimate can't drift from the real result:
  the size pre-check (`GetActivePlainContentSizesAsync`), the whole-file hash, the
  block-hash store, and the `_hashCache` (path+size+mtime) so unchanged files aren't
  re-read on repeat estimates.
- Dedup against **both** already-stored content *and* files seen earlier in the same
  scan (intra-run dedup), matching backup behaviour.
- Directory-backup only — disc backups don't dedup.
- Keep the existing fast raw-size scan as the default; this exact pass is a separate,
  clearly-slower option because in the block-dedup case it reads everything.

Confirmed while scoping this: the default fast scan already **honours exclusions** —
`FileScanner` skips deselected subtrees (`IsSelected == false`) and applies the
`isExcluded` glob filter, which covers both filename patterns (`*.log`) and path
patterns (`*/bin/*`). So the raw estimate is already exclusion-correct; this item only
adds dedup-awareness on top.

---

## Related ideas — already implemented, pending hardware validation

These two came up as new ideas but turn out to already exist. The remaining work is
validation on real optical hardware, not new design.

### Burn plain files in place instead of a full staging copy (the "ISO with links" idea)

Already shipped as **`DiscStagingMode.InPlace`** (Settings → Disc staging; default is
still *copy to temp*). In this mode the burn works from an explicit item list —
`BurnItem(DiscRelativePath, SourceAbsolutePath)` — and plain files' items point at the
*original* source path rather than a temp copy, so no full disc's worth of data is
duplicated on the temp volume. `BackupOrchestrator` holds a `FileShare.Read` lock on
each in-place file from size-validation until after the burn + catalog recording
complete, so the file can't grow, change, or be deleted mid-burn while the burner
still reads it — exactly the "lock the files until the disc is finished" behaviour.

Remaining gaps:
- **Zipped and split files always stage to temp**, because their on-disc bytes differ
  from the source (compression / chunk headers) — an in-place link can't represent a
  transformed file. The oversized-file split "spill" snapshot also still copies to
  temp even in InPlace mode. These transient costs are inherent, not fixable by
  linking.
- **IMAPI2 hardware path is untested.** `Imapi2DiscBurner.BurnAsync` was rewritten
  from `root.AddTree(stagingDir)` to per-item `root.AddFile(discRelativePath, stream)`
  over an `IStream` opened read/deny-write on the source, but like all IMAPI2 code
  here it has only been exercised against the simulated burner. **Needs a real
  burner + media test.**

### Optical filesystem formats

The app already supports the three formats that Windows/IMAPI2 can author, selectable
per set (Settings, `FilesystemType`):
- **ISO 9660** — most compatible; 8.3 names, shallow directory depth.
- **Joliet** — ISO 9660 + long Unicode filenames (burned together, `Joliet | ISO9660`).
- **UDF** — long paths, large files; required for Blu-ray. Current default.

`PathCompatibility` already enforces each format's name/path/depth limits, and
`Imapi2DiscBurner` maps them to IMAPI2's `FsiFileSystems`. There is no additional
mainstream optical filesystem to add — IMAPI2 authors exactly these three (HFS+/Mac
hybrid discs are not supported by the Windows burn engine). The pending work here is
again **hardware validation** of the real IMAPI2 burn for each format, not new format
support.
