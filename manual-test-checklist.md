# Manual test checklist

Things worth testing **by hand** after the recent roadmap work. The automated
suites (`tools/disc_test_harness`, `tools/dir_dedup_test`, `tools/dedup_estimate_test`,
`tools/case_rename_test`) already cover the simulated pipeline and the catalog logic.
This list is the stuff a simulator *can't* prove: real optical hardware, the WPF
dialogs, read-only media, and "does the number the UI shows match what actually got
written."

Each item maps to a completed roadmap entry. Do the ones that touch hardware/UI —
skip anything you don't have media for.

> **Now automated (2026-07-15):** most of items 1, 3, 4, 5, and 6 that *can* run
> headless now have dedicated harness tests, so the manual list below has shrunk to
> the parts a simulator genuinely can't prove — real optical hardware and the actual
> on-screen WPF dialog. Automated coverage per item:
> - **1** — `disc_test_harness`: `readonly-source-main-path-no-staging-leak` (main
>   burn path) + `reburn-staging-cleanup-handles-readonly-source` (File.Copy reburn path).
> - **3** — `disc_test_harness`: `disc-over-reports-capacity` (simulates a smaller
>   *actual* capacity than reported → graceful re-plan). **Answer to "is it reported
>   or silent?":** it's **reported, not silent** — both the simulator and real IMAPI2
>   hardware throw once committed bytes exceed the true capacity, and the orchestrator
>   now catches that, caps remaining discs to the observed capacity, and re-packs the
>   rest onto more discs automatically. The user never has to manually override the size.
> - **4** — `disc_test_harness`: `udf-warning-decision-yes-no-cancel` +
>   `udf-warning-below-threshold-stays-silent` drive the Yes/No/Cancel decision
>   programmatically through the shared `DiscCompatibilityAdvisor` (the same logic the
>   dialog uses). Only the dialog's *visual appearance* is left to eyeball.
> - **5** — `dir_dedup_test`: large (>64 KiB) same-size files, both streaming and
>   buffered paths.
> - **6** — `dedup_estimate_test`: runs the real backup **and** the estimator over
>   identical inputs and asserts the estimate equals the bytes actually written.

---

## 1. Read-only source files → disc burn (temp-leak + burn-abort fix)

*Automated: yes (see note above). Manual value: confirm on a real burner.*

The fix clears the read-only attribute before deleting staging dirs, so read-only
content no longer leaks into `%TEMP%\LithicBackup` or aborts the *next* burn.

- [ ] Back up a set that contains genuinely **read-only** files (a `.git` object/pack
      dir is perfect — those are all read-only — or `attrib +R` a folder of files).
      Use the default **copy-to-temp** disc staging mode.
- [ ] After the burn completes, check `%TEMP%\LithicBackup\` — there should be **no
      leftover `disc-*` folders**. (Before the fix, read-only staged copies survived
      the cleanup and piled up here.)
- [ ] Immediately start a **second** burn without rebooting. It should start cleanly.
      (Before the fix, a leftover read-only file from run 1 would throw
      `UnauthorizedAccessException` and abort run 2 before it began.)
- [ ] Repeat with **InPlace** staging mode selected (Settings → Disc staging). Plain
      files burn from source, but zipped/split files still stage to temp — confirm no
      temp leak there either.
- [ ] Restore the disc and confirm the read-only files come back intact.

## 2. Schedules survive save/reload (dead schedule-wipe code removed)

Pure dead-code removal — the live save path was never touched — but worth a quick
confirmation that nothing regressed.

- [ ] Create a set with a **schedule** (Interval or specific time). Save it.
- [ ] Close and reopen the app. Confirm the schedule is **still set** (not reverted to
      Off/Interval-default).
- [ ] Edit the set's sources/options and save again with the schedule **enabled**, then
      with it **disabled**; reopen each time and confirm the stored schedule matches
      what you left it as.

## 3. Disc that over-reports its capacity → graceful re-plan

The simulator proves the re-pack logic, but only real media over-reports for real.

- [ ] Burn a set sized **close to a disc's true capacity** onto media that's known (or
      suspected) to over-state its size — e.g. a cheap DVD-R that advertises 4.7 GB but
      holds slightly less. Fill it near the brim.
- [ ] Confirm the burn **doesn't abort** when it hits the shortfall: it should cap the
      remaining discs to the real observed capacity, re-pack the not-yet-burned files
      onto a fresh disc, and continue.
- [ ] Confirm the finished set **restores completely** — nothing silently truncated,
      no file left half-written across the capacity boundary.
- [ ] Bonus: a set whose overflow includes a **split (oversized) file** spanning the
      disc that over-reported — confirm the split still reassembles correctly on restore.

## 4. Plan-time UDF compatibility warning dialog

This is a **WPF Yes/No/Cancel dialog** that only appears in the real GUI disc-burn flow.

- [ ] Make a set with lots of files that are **incompatible with ISO 9660** — long
      Unicode filenames, deep directory nesting, or names longer than 8.3. Set the
      set's `FilesystemType` to **ISO 9660** (not UDF).
- [ ] Start a disc backup. After planning, before the burn, a warning should pop up
      saying roughly "N of M files (X GB) will be zipped for ISO 9660 compatibility"
      and offering to **switch this run to UDF**.
- [ ] Test all three buttons:
      - **Yes** → burns as UDF, and the incompatible files land **unzipped** (verify on
        restore that they're plain files, not zip archives).
      - **No** → burns as ISO 9660, incompatible files get **zipped** (the old fallback).
      - **Cancel** → the whole backup aborts, nothing burned.
- [ ] Confirm the warning **does NOT appear** when the set is already UDF, or when only
      a trivial fraction of files are incompatible (threshold is ≥5% of files, ≥5% of
      bytes, or ≥20 files).
- [ ] Sanity-check the count in the dialog roughly matches how many files actually get
      zipped if you choose **No**.

## 5. Large-file directory-backup dedup (progressive prefix hash)

Automated coverage exists (`tools/dir_dedup_test`), but real large files are worth a
timing + correctness spot-check since this changed the read pattern.

- [ ] Directory-backup a folder with **large (multi-hundred-MB) files that share a size
      but differ in content** (e.g. two big video files trimmed to identical byte
      counts). With file-level dedup on, both should be stored as **separate plain
      copies** — no false dedup.
- [ ] Include **genuine duplicates** at different paths (copy a big file to two
      locations). Confirm the second is stored as a `.fileref`, not a second full copy.
- [ ] A mixed sequence — file X, a same-size-but-different file Y, then X again — should
      resolve the third file's ref back to the **first X**, not to Y.
- [ ] Informally note the timing on a large set vs. before — the win is that non-dup
      large files whose size collides only within this run are now read **once**, not
      twice. Nothing should be slower.

## 6. "Compute actual size" button matches real backup output

The estimator is pinned to the real writer by `tools/dedup_estimate_test`, but confirm
the **button in the Backup Coverage view** behaves and that the number is believable.

- [ ] Open **Backup Coverage** for a dedup-enabled set. The default fast scan shows raw
      size. Click **"Compute actual size"**.
- [ ] For a set with lots of duplicate content, the actual size should be **noticeably
      smaller** than the raw size.
- [ ] Then actually run the backup and compare the bytes written on disk (or the
      catalog's stored size) to what the button predicted — they should match closely.
- [ ] For a **block-level dedup** set, confirm the "this reads all your data" warning
      appears before the pass runs (block-level has to read everything).
- [ ] Confirm **exclusions** are honored in the estimate — deselected subtrees and glob
      excludes (`*.log`, `*/bin/*`) shouldn't be counted.

---

## Pending hardware validation (from the roadmap's "Related ideas" section)

All of this is IMAPI2 real-burner work that has **only ever run against the simulator**.
If you have a real optical drive + blank media, these are the highest-value manual tests.

- [ ] **InPlace staging on real hardware.** `Imapi2DiscBurner.BurnAsync` was rewritten
      to add files per-item from an `IStream` opened read/deny-write on the source
      (instead of `AddTree` on a staging dir). This has never touched a real burner.
      Burn a set in InPlace mode to a real disc and confirm it authors + restores.
- [ ] **Each filesystem format on real media:** ISO 9660, Joliet, and UDF. Burn one set
      per format to real discs and confirm each authors correctly and restores. Verify
      Joliet long-Unicode names and UDF long/deep paths survive the round trip.
- [ ] **Blu-ray** specifically (requires UDF) if you have a BD burner.
- [ ] **Multi-disc spanning** on real media — a set large enough to need several physical
      discs, confirming the split/carry logic and per-disc catalog recording work with
      real eject/insert cycles.
