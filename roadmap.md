# LithicBackup — Roadmap

Planned work, pulled from `known-issues.md` triage and design discussions. Items
are ordered roughly by priority. When one ships, move its detail into
`known-issues.md` as a FIXED/ADDED entry and delete it here.

---

## 1. Disc-burn staging inherits source read-only → temp leak + burn-abort landmine

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

## 2. Remove the dead schedule-wipe landmine in `SaveBackupSetAsync`

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

## 3. Graceful re-plan when a disc over-reports its capacity

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

## 4. Plan-time disc-filesystem compatibility warning (suggest UDF up front)

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

## 5. Avoid the double-read on large size-colliding files (progressive prefix hash)

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
