# Lithic Backup — design

Companion to `README.md` (which describes features from the user's side) and
`known-issues.md` (open bugs and tech debt, plus write-ups of fixed ones worth
remembering). This file records **how the pieces fit together and which
invariants a change has to respect** — read it before making a change whose
effects might not stay inside the file you're editing.

## Projects

```
LithicBackup.sln
├── LithicBackup.Core            Models, interfaces, exceptions. No I/O.
├── LithicBackup.Infrastructure  File scanning, dedup engines, IMAPI2 disc
│                                burning, the SQLite catalog
├── LithicBackup.Services        Backup orchestration, restore, retention,
│                                directory backup, planning, consolidation
├── LithicBackup                 WPF desktop application (GUI)
└── LithicBackup.Worker          Windows Service for scheduled/continuous runs
```

`src\Directory.Build.props` holds the single `<Version>` every project under
`src\` inherits; `installer\build-installer.ps1` reads the same value, so the MSI
`ProductVersion` cannot drift from the assembly version. See `CLAUDE.md`.

`tools\` holds stand-alone console harnesses (this project has no unit-test
project — measurement and verification live there). Each sets
`ImportDirectoryBuildProps=false` so it never stamps the shipping version onto
itself.

## Two processes, one dataset

The GUI and the Worker service are separate processes that share the catalog and
the machine-global settings. Anything that changes shared on-disk state must work
when the other process is running.

* **Shutdown handshake.** An MSI upgrade cannot kill either process (integrity
  levels), so it *asks*: `SignalLithicShutdown` sets a named event each listens
  on. GUI: `Global\LithicBackup.Shutdown`, falling back to the session-local
  `LithicBackup.Shutdown` (`App.xaml.cs`). Worker:
  `Global\LithicBackup.Worker.Shutdown` (`ShutdownSignalListener.cs`). The GUI is
  tried on `Global\`, then `Session\<n>\` for each running GUI's session, then the
  bare name, so the handshake also reaches GUIs older than the fix. The action
  itself runs impersonated as the invoking user in that user's session.
  **The action cannot load at all without
  `installer\CustomActions\CustomAction.config`** — that file was missing from
  1.0.11 through 1.0.55, so this whole mechanism was silently dead. Full reasoning
  in `CLAUDE.md`.
* **Settings** are machine-global and read by both, so a new setting has to be
  meaningful (or harmless) in a service with no UI.

## Persistent stores

Three SQLite databases, with very different roles. Confusing them is the main
hazard when touching storage code.

| File | Location | Role | Losable? |
|---|---|---|---|
| `catalog.db` | `%LOCALAPPDATA%\LithicBackup` | **Real data.** What has been backed up, where, in what form, at what version. | **No** — losing it means the backup can't be restored or extended incrementally |
| `sizecache.db` | same | Per-directory sizes, for the source tree | Yes — pure cache |
| `filehashcache.db` | same | Per-file SHA-256, keyed by path+size+mtime | Yes — pure cache |

The two caches are **derived state and must always be treated as such**: any
entry may be missing, stale, or wrong-but-validatable, and the code must recompute
rather than trust. That is what makes Settings ▸ Caches safe to offer.

### Cache design (`ViewModels\DirectorySizeCache`, `FileHashCache`, `CacheMaintenance`)

Both caches follow the same shape, and the shape matters:

* **On-demand point lookups, never a bulk load.** The key is the table's primary
  key, so a lookup is one index seek. Both classes previously read their whole
  table into a `Dictionary` up front; on a 538 MB database that cost ~6 s before
  the first directory could expand. Any future change that reintroduces
  "load everything first" reintroduces that stall — see the write-up in
  `known-issues.md`.
* **A bounded in-memory memo in front**, which caches *misses* as well as hits
  (a cold scan over never-hashed files would otherwise re-query for every one),
  dropped wholesale on overflow rather than evicted one at a time.
* **Writes buffered and flushed in batches**, held separately from the memo so
  dropping the memo can never lose a write.
* **Failure is not fatal.** If the database cannot be opened the cache degrades
  to a session-only memo. A cache must never fail a backup.
* **Keys are compared `Ordinal`**, matching SQLite's binary-collation primary
  key. A case-insensitive memo in front of a case-sensitive table would serve a
  remembered miss under the wrong casing.
* **Tables are `WITHOUT ROWID`.** A text primary key on a rowid table stores every
  path twice (table b-tree + automatic index); measured 34% larger on real data.
  Existing databases stay in the old layout until compacted.

**No single owner.** `SizeComputeScheduler` constructs its own
`DirectorySizeCache`, and one scheduler exists per open backup-set editor, so
several instances of the same cache can be live at once over one file. This is
why `CacheMaintenance` cannot notify owners directly and instead bumps a static
generation counter that every instance checks under its own lock
(`InvalidateLiveInstances`). **If you add another cache, it needs the same
counter**, or maintenance will leave it serving rows that no longer exist and
holding statements prepared against a dropped table.

**Automatic pruning is a bound, not a policy.** The background prune only fires
above 1 GiB of *used* bytes and evicts at random. Random is deliberate: there is
no recency column, and ordering by the primary key would evict the same
alphabetical prefix forever. Targeted cleanup is the user-driven compaction.

**Size gates must measure used bytes, not file bytes.** SQLite never shrinks a
file on delete (freed pages go to the freelist), so `page_count` alone as a loop
condition never terminates. Use `page_count - freelist_count`.

### Cache maintenance (Settings ▸ Caches)

User-driven compaction: drop entries whose path is gone, rebuild the table
`WITHOUT ROWID` if it is still the legacy shape, then reclaim. Four rules hold it
together, each of which cost a measurement to learn:

* **Unreachable ≠ gone.** A drive that is merely unplugged answers "missing" for
  every path on it. Sweeping it would evict that whole drive in one pass — and a
  disconnected backup drive's *hashes* are terabytes of re-reading. Every key's
  root is checked once (`RootAvailability`); an unreachable root is skipped
  entirely and reported back to the user, never swept.
* **Sorted keys, with subtree skipping.** A directory found missing makes every
  key beneath it missing too, so a deleted tree costs one probe instead of
  thousands. This is what makes the sweep finish at all — a first attempt that
  probed *random* paths extrapolated to about a day.
* **Probes run concurrently.** The sweep is pure disk latency, not work: a cold
  metadata lookup measured 47 ms with essentially no CPU. Sixteen at a time
  measured 235 probes/s against 21 on one thread. `ProbeParallelism` is capped
  there deliberately — 32 buys only 20% more and makes the drive unresponsive to
  everything else.
* **`VACUUM` is only half of reclaiming.** In WAL mode it rewrites the database
  *through the log*, so it finishes with a shrunken file and a log just as large
  beside it, and `Microsoft.Data.Sqlite` pools connections so disposing one does
  not fold the log back. Always follow it with
  `PRAGMA wal_checkpoint(TRUNCATE)`. Measured before that was added: the file
  went 514 MB → 233 MB and left a 269 MB `-wal`, so a run that freed 281 MB
  reported 23.5 MB.

Everything runs off the UI thread with `IProgress<string>` and a
`CancellationToken`; deletes are committed in batches, so stopping part-way keeps
whatever finished. Verified by `tools\cache_maint_test`, which compacts a copy of
a real cache and compares surviving rows against the untouched original field by
field.

Measured end-to-end on the real 1.5M-row cache: **1,035 s**, 517 MB → 234 MB,
388,745 rows dropped. (The same run before the probes were parallelised: 1,852 s.)
**A sweep is inherently a long operation** — any change here should keep it
cancellable, resumable-by-repetition, and off the UI thread, rather than trying
to make it fast enough to block on.

## Source selection and the size column

`SourceSelectionNodeViewModel` builds the tree. Expansion enumerates **one level
only**; recursive totals come from the cache or are computed in the background by
`SizeComputeScheduler`.

`ComputeDirectorySizeCached` walks a whole subtree and writes a row for every
directory it passes — so asking for one drive's size populates the cache with
every directory beneath it. Two separate payoffs, worth keeping straight:

* a row carrying `RecursiveSize` lets a total display with **no walk at all**;
* the deep rows make a *recompute* cheap, because an unchanged directory (own
  `LastWriteTimeUtc`) skips its file enumeration. The traversal still happens.

**Known limitation:** a cached recursive total is validated only against the
directory's *own* mtime, which does not change when a file deep inside a
descendant grows. Totals can therefore lag until the scheduler recomputes.

## Exclusion rules are an invariant of the catalog, not a step in one code path

A file is excluded from a set by any of: a user glob in `ExcludedExtensions`, a
tier set with **zero** tiers, or a hard exclusion (the app's own data directory,
NTFS volume metadata). All three live in one place —
`DirectoryBackupService.BuildExclusionFilter` — because a second copy of the
logic in the Cleanup view had already drifted from it once.

**The invariant: no path may enter or stay in the catalog without passing that
filter.** Every route by which a path can become tracked must apply it:

| Route | Where the filter is applied |
|---|---|
| Full scan (manual, scheduled, verify) | `FileScanner.ScanAsync(…, isExcluded)` |
| Continuous backup of changed paths | `ExecuteTargetedAsync`, per candidate |
| **Rename/move fast path** | `MoveTargetedAsync`, per landing path |

The third one is easy to miss and was missed: a relocation *copies nothing*, so
it never reaches the copy path's filter, and the worker's own endpoint test
(`PathBelongsToSet`) answers a different question — is this inside the
**selection** — which has nothing to say about exclusion globs. A file renamed
into an excluded directory was therefore relocated on the destination and its
catalog row repointed at the excluded path, leaving it backed up in defiance of
the rule. It now returns `TargetedMoveOutcome.ExcludedAtDestination` and is
released like a move out of the selection. See the exclusion-vs-rename entry in
`known-issues.md`.

**So: if you add a fourth route by which a path can be tracked, it applies the
filter too** — including routes that only rewrite a `SourcePath` without copying
bytes, which are exactly the ones where the omission is invisible until a
Cleanup scan contradicts the backup.

## Threading rules that bite

* **Nothing on the UI thread may do I/O at startup.** `App.OnStartup` constructs
  the caches; their constructors must not touch the disk (connections open on a
  background task). A 1.65 s hash-cache load used to sit between launch and the
  first window.
* **Moving a stall off a constructor and onto the first lookup does not remove
  it** if every lookup takes the same lock — the first lookup is what the user
  waits for. Measure the first *use*, not construction.
* Long-running user-invoked work (cache compaction, scans, verification) belongs
  on a background task with `IProgress<T>` and a `CancellationToken`, reported in
  the view, and must survive being stopped part-way.

## Verifying changes

There is no unit-test project. The conventions are:

* `manual-test-checklist.md` for UI flows.
* `tools\<name>` console harnesses for anything measurable or mechanically
  checkable — e.g. `expand_probe` (times the real view-model expansion with no
  window attached), `treeview_bench` (times the real item template with a visual
  tree), `cache_maint_test` (runs the real `CacheMaintenance` against a copy of a
  real cache and verifies the rebuild is lossless).
* **Measure before attributing a performance problem.** The tree-expansion
  slowness was blamed twice on the XAML; a harness showed the XAML was fine and
  the cache was the whole cost. Both wrong theories are recorded in
  `known-issues.md` so they don't get re-proposed.
