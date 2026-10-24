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

**A harness that constructs `App` must set `App.SkipStartupForTests` first.**
WPF's `Application` constructor queues `OnStartup` for the first dispatcher pump,
`Run()` or not, and `App.OnStartup` starts the entire application: the
single-instance lock, the real catalog, a main window, a tray icon, background
monitors, an update check. `tools\editor_save_test` ran a second, full Lithic Backup
that way for a whole session before it came to light. With the real app already
open, the harness's copy woke that window and shut the harness down.

**A harness that runs a backup must give it a real set.** Since the catalog split
into per-set databases, the file-dedup index lives in the set's own database. A
`BackupJob` with `BackupSetId = null` backs up without deduplicating anything, and
reports no error. `dir_dedup_test` and `dedup_estimate_test` failed on every
dedup check for that reason alone while dedup itself worked. The app never runs a
backup without a set.

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
* **Settings come in two kinds, in two files.** `MachineSettings` govern backups
  (memory budget, disc staging, continuous timing), so the Worker must see them:
  one file, `C:\ProgramData\LithicBackup\settings.json`, beside the catalog for the
  same reason the catalog is there. A new setting the Worker uses goes here, and
  must be meaningful (or harmless) in a service with no UI. `UserSettings` are one
  user's app preferences (prompts, update checks, layout), in that account's
  `%LOCALAPPDATA%`; the Worker, as LocalSystem, would read the system profile's
  copy, so nothing it needs may live there.
  * **Only the app writes the machine file, and replaces it in one step**
    (`AtomicFile.TryReplace`: a rename with POSIX semantics), flushed to the disk
    first and from a temporary file of its own, so no reader sees part of one,
    even after a crash, and two saves at once cannot mix. An ordinary replacing
    move fails while any handle has the file open, and a virus scanner opens each
    new file: back-to-back saves under a reader failed after a second of retries.
    The POSIX rename goes through while handles that share delete access are
    open; the readers here share it and copy the bytes out before parsing.
  * **Read it where it is used, never from a copy kept since startup**: another
    account's app may have saved since, and a Worker pass can run backups for
    hours. The Worker and the GUI's job building read through a
    `MachineSettingsReader`, refreshed before each job: one file-time query when
    nothing changed. The settings dialog reads the file when it opens, saves this
    user's preferences whatever becomes of the shared file, saves the shared one
    off the UI thread, and gives Windows' reason if that fails.
  * **An unreadable file is not the defaults.** `TryLoad` tells a missing file
    (the defaults apply) from one that cannot be read; the reader then keeps the
    settings it last read, the Worker logs it and the GUI's status line shows it,
    once each, and the next look tries again.
  * **A running backup follows the file, not the defaults** (`LiveMemoryPolicy`):
    it keeps the job's own memory budget until the file is written after that
    budget was read. The job carries that write time
    (`BackupJob.MemoryBudgetWrittenUtc`), so a save made while it was being
    planned, which can take minutes, is followed from the run's first look.
  * **The app carries the machine settings over once**, on its first start without
    the machine file, from its own per-user file, where they used to be kept under
    the same names; with several accounts, the first to start wins. The Worker
    never seeds the file: its per-user file is the system profile's, which nothing
    writes. `UserSettings` writes back fields it does not know, so the old ones
    survive a carry-over that failed, to be tried at the next start; once the
    machine file exists the app drops them, so they cannot come back, however old,
    if it is ever deleted. A save also deletes temporary files a crash left more
    than a minute ago.
  * **Widening the shared folder's ACL changes nothing when it is already
    widened.** Setting it makes Windows re-apply the inherited part to every file
    beneath, and it runs at every start and every settings save.
* **Writes to a set take turns, and the Worker gives way.** Every write to a set's
  database first takes a cross-process lock file (`set-<id>.db.writelock`,
  `SqliteSetDatabase.AcquireCrossProcessWriteAsync`). A waiter polls every 25 ms.
  A backup holds the lock for a whole commit batch (500 files, a file of 100 MB
  or more, or 30 s: see "A copy is recorded only once it is on the disk") and
  takes it back within microseconds of releasing it. So the Worker's
  continuous run used to shut the GUI out for as long as it lasted: hours on a
  large one, with the GUI's backup showing its previous status throughout. A
  waiter now leaves `set-<id>.db.writelock.wait-<pid>`, refreshed each second. The
  Worker (`SqliteCatalogRepository.YieldWritesToWaitingProcesses`, set in its
  `Program.cs`) steps aside at its next acquisition while a fresh marker from
  another process exists. It ignores markers more than 5 s old and gives way for
  at most 2 minutes at a time. Nor does it make the waiter sit out the rest of
  its batch: a run asks `ShouldGiveWayToWaitingWriter` between files, at most
  every quarter second, and commits and steps aside when it is told to
  (`GiveWayIfWaitedForAsync`). A waiter gets in after the file being copied
  (`write_lock_fairness_test` §4). The GUI never gives way, so the backup the
  user is watching goes first. `BeginTransactionAsync`'s `onWaitingForOtherProcess`
  lets a backup say that it is waiting.
* **The Worker flushes volumes for the app.** Making a backup's copies durable
  before a commit takes flushing the destination's volume, which only an elevated
  process may do; the app asks the Worker over `\\.\pipe\LithicBackup.FlushVolume`,
  and falls back to flushing file by file when nobody answers. See "A copy is
  recorded only once it is on the disk".
* **Opening a set must not need the write lock.** Every process runs
  `SetSchema.sql` when it first opens a set, while the other may be mid-run. So
  the script is `CREATE ... IF NOT EXISTS` only, which is a no-op on an existing
  database and needs no write lock. The `SchemaVersion` row is inserted by the
  constructor only when missing. An `INSERT` takes the write lock even when it
  inserts nothing, and it made an open wait on the Worker, failing after the
  30 s busy timeout.

## Persistent stores

Three SQLite databases, with very different roles. Confusing them is the main
hazard when touching storage code.

| File | Location | Role | Losable? |
|---|---|---|---|
| `catalog.db` + `sets\set-<id>.db` | `C:\ProgramData\LithicBackup` | **Real data.** What has been backed up, where, in what form, at what version. | **No** — losing it means the backup can't be restored or extended incrementally |
| `sizecache.db` | `%LOCALAPPDATA%\LithicBackup` | Per-directory sizes, for the source tree | Yes — pure cache |
| `filehashcache.db` | same | Per-file SHA-256, keyed by path+size+mtime | Yes — pure cache |

**The catalog is shared, the caches are per-user.** The catalog sits under
`CommonApplicationData` (`CatalogLocation.RootDirectory`) because the GUI and the
Worker service must see the *same* one; when both used `LocalApplicationData` the
service, running as LocalSystem, quietly kept its own. `Resolve()` migrates a
pre-existing per-user catalog on first run, and `IsInsideAppDataDirectory` makes
the backup engine skip that whole directory unconditionally — the databases are
open for writing by the running process, so backing them up both wastes space and
fails on file locks.

### A trimmed projection must not reach a whole-row writer

`GetActiveFilesForClassificationAsync` loads five columns per row so Cleanup's
scan can hold millions of them in memory. `UpdateFileRecordAsync` writes
thirteen. Feed the first into the second and every column the reader skipped is
silently overwritten with a default: `Hash` blanked, the
IsZipped/IsSplit/IsDeduped/IsFileRef flags cleared — those are how restore knows
a file's storage form — and `Version` set to 1, because `FileRecord.Version`
defaults to 1 rather than 0 and so reads as plausible instead of empty.

Cleanup's purge did exactly this. It never corrupted anything only because
`DiscId` defaults to 0 and file writes route to a set database via the disc's
owner, so disc 0 threw before any SQL ran. That is luck, not design — the visible
symptom was a cleanup that always failed, and the invisible one would have been a
catalog quietly rewritten.

**Either return a type the whole-row writer cannot accept, or give the caller a
column-specific write.** The purge only ever meant "tombstone these rows", so it
now calls `MarkFileRecordsDeletedByIdsAsync`, which sets `IsDeleted` by id and
touches nothing else. Trimming a read is a local optimisation with a non-local
blast radius: the question is never "does this caller read the column", it is
"does anything downstream write it back".

### The catalog is a master plus one database per set

`catalog.db` is a small **master**: `BackupSets`, `DiscOwners` (which set owns
which disc), `UsnCursors` (per *volume*, not per set), and the schema version.
Each set's bulk records — its files — live in their own `sets\set-<id>.db`, so a
set's data can be dropped, copied, or repaired by the file, and one enormous set
does not slow queries for the others. `SqliteCatalogRepository` routes per-set
calls through `GetSet(id)` and holds `_masterGate` only for master work.

**The master still carries the pre-split tables**, and this is a live trap rather
than dead weight: `001_InitialSchema` creates `Discs`, `Files`, `FileChunks` and
`DeduplicationBlocks` in *every* catalog, new ones included, and `Discs` declares
`BackupSetId INTEGER NOT NULL REFERENCES BackupSets(Id)`. Any set old enough to
have rows there is anchored by a real foreign key.

Two rules follow, both learned from a set that could not be deleted at all:

* **Deleting a set must clear the legacy rows first**, children before parents
  (`FileChunks`/`DeduplicationBlocks` → `Files` → `Discs` → `DiscOwners` →
  `BackupSets`), all in one transaction. Miss them and the delete dies on
  `SQLite Error 19: FOREIGN KEY constraint failed`.
* **Drop `sets\set-<id>.db` only after that transaction commits.** The reverse
  order destroys data on failure: the file is already gone when the master delete
  throws, leaving a set that still appears in the list but has lost its entire
  catalog. Failing *after* the commit merely orphans a file, which is harmless —
  set ids come from `AUTOINCREMENT` and are never reused.

And a UI rule, because this stayed invisible for several attempts: **a delete
that fails must say so in a dialog.** The user confirmed a permanent, destructive
action in a modal dialog; answering Yes and watching the set reappear, with the
only explanation on a status line, reads as "delete is broken".

**Inside a set database, express "rows owned by this set" as a correlated
`EXISTS`, never as `DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = ?)`.**
The IN-form reads like a cheap filter and is not one: SQLite materialises it into
a LIST SUBQUERY plus a bloom filter, which is a full `SCAN Discs` on *every
execution of the statement*. A cleanup running one such statement per source path
spent two days at 58 MB/s of reads against 0.2 MB/s of writes without finishing.
`Discs.Id` is `INTEGER PRIMARY KEY`, so `EXISTS (SELECT 1 FROM Discs d WHERE
d.Id = Files.DiscId AND d.BackupSetId = $setId)` is one rowid seek per candidate
row — measured **772x** faster — and it leaves the planner free to drive off
`SourcePath` rather than `DiscId`. Ordinary joins (`INNER JOIN Discs d ON
f.DiscId = d.Id`) were always fine; they resolve through the same rowid. Only the
IN-form is the trap.

Every disc in `set-<id>.db` does belong to that set, so the predicate is in
practice a tautology — **keep it anyway.** The invariant is relied on for *speed*,
never for *correctness*: enforced per row, a stray foreign disc row cannot widen
an `UPDATE`. And note that `IX_Discs_BackupSetId` — which the per-set schema was
missing although the master always had it — buys only **1.25x** on its own,
because the IN-form still materialises every id. It is worth having for genuine
`WHERE BackupSetId = ?` lookups; it is not a substitute for the rewrite.

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

### A control belongs beside what it changes, and absent when there is nothing to change

Cleanup's "Categories per row" sat at the top of the page, above the scan
buttons. It was therefore the first thing visible on opening Cleanup — offering
to arrange results that did not exist yet — and once results did arrive it was
separated from them by the entire action grid. It now docks immediately above the
category cards and is hidden until there is at least one.

The trap in implementing that: `Categories` is populated by **two independent
paths** — the catalog scan builds its categories off-thread and assigns them
straight onto the collection, while the purge and edit paths go through
`RebuildCategories`. A `HasCategories` notification raised at one mutation site
would have left the control hidden after the *scan*, which is the case that
matters. Drive that kind of flag off `ObservableCollection.CollectionChanged`
rather than from each call site: a third path is easy to add and easy to forget.

### Text the app adds to a filename must not look like part of it

Cleanup disambiguates rows that share a filename by appending the backup time —
`config.yaml (backed up 2026-08-31 20:42)`. Concatenated into one string in one
colour, that reads as though the file were really called that. The annotation is
now a separate `OrphanedFileInfo.Annotation` / `OrphanedNodeViewModel.Annotation`
and is rendered in its own `Run` in `TextSecondaryBrush`.

**Keep it as data, not as a string the view pulls apart.** Sorting, searching and
copying a name should never need to know how to strip a suffix back off — and the
old form had already leaked into the sort: the tree ordered by `DisplayName` and
therefore by the appended timestamp, so the key is now `(DisplayName, Annotation)`
to reproduce it. `tools\cleanup_display_test` pins both halves: that name +
annotation still composes to the exact previous string, and that 500 shuffled row
sets order identically under the old and new keys.

The leading space belongs in `AnnotationDisplay`, not in whitespace between the
two `Run` elements. XAML collapses inter-element whitespace into a single space,
which works right up until someone reformats the file.

Volume displays (`E: (no label)`, `E: — MyDisc` in the Test Disc picker) are
deliberately *not* covered: a volume label is not a filename, and nothing there
risks being read as one.

### An inferred check must never promote a folder, and a stale copy must never be saved

Both halves were learned from the same set, twice, the second time costing a
1.2 TB copy of C:.

**Tristate promotion counts genuine selections only.** Under a partial folder with
auto-include-new on, the editor draws every unlisted child checked and flags it
`_isAutoIncludeDerived` — the scanner does cover them, so that display is right.
But a recompute that treats those inferred checks as selections promotes the
parent to *full*, and a full parent re-lists every child as an explicit selection
on the next open. Closing the editor used to save every time, so that was a
selection that widened itself with no clicks at all. (It no longer saves unasked
— see *The editor saves only when told to* — but the rule stands on its own: a
user who does click Save must not save a promotion they never made.)
`AllChildrenGenuinelySelected` is the rule:
a child counts toward promotion only if it is selected *and not derived*. This is
the view-model form of the model-level rule below; the 352ba73 revert removed the
model pass and left this one.

**A child's default follows coverage, not its parent's checkbox.** A folder saved
as *ticked, auto-include off, these children listed* backs up exactly the listed
children: for any folder with children, `IncludesUnlistedDescendants` looks only at
the auto-include flag, and the scanner treats ticked and partial alike. Turning
auto-include off on a ticked folder is the designed way to get this shape (its
current children are pinned so later ones stay out), and 1.3.1's promotion left
it behind on sets that never asked for it. `CreateChildNode` used to give every
unlisted child of a ticked folder the tick, unflagged, so `ToModel` wrote it out:
one open and close of the editor turned J:'s D:\ from 95 folders into all 309,
recycle bin included, and E:\ into every item on the drive down to `pagefile.sys`.
The restore now flags such a folder (`_unlistedChildrenExcluded`, from
`CoversListedChildrenOnly`), creates its unlisted children unticked, reads its
children at open even when it is collapsed, and reveals its checkbox only after
recomputing it from them. So it shows *partial* when anything unlisted is on disk,
and *ticked* with no flip on expand when nothing is. A click on the folder, or
on an ancestor whose push reaches it, is a whole-folder choice and clears the
flag. `CollectCoveredEntries` and `CollectSelectedRoots` descend into the same
shape instead of claiming the whole folder. Before this, adding a folder under
it never counted as "added", and the Worker watched the whole drive.

**A tick or an untick is about the whole folder, however deep it has been
opened.** `PropagateSelection` pushes either through every loaded descendant
(`IncludeSubtree` / `ExcludeSubtree`). Both release the narrower saved state
beneath. A collapsed folder restored from a saved entry is written back from that
entry verbatim (`ToModel`'s `_restoredModel` fallback, which keeps a never-opened
subtree lossless). Its deferred restore also re-applies that entry on first
expand. So a click that only changes the checkbox is lost. It shows, but the save
writes the old entry, and the next expand re-applies its exclusions. The tick used
to reach one level down and release
nothing, so a ticked folder could back up only part of itself. Orphan entries are
the exception to "release": they are turned into whole-folder ticks, not dropped,
because under auto-include off an unlisted entry is not covered. The clicked
folder's own release happens synchronously in the setter, because the push-down
runs later in a settle pass and an expand in between would re-apply the old
choices.

**Edit and back up from the catalog, not from memory.** A window that read a set
hours ago holds an old copy of it. `RefreshFromCatalogAsync` re-reads the set into
the *same* instance, because the row and every other holder share that
reference, and every GUI path that writes a set from memory calls it first: the
editor, a GUI backup (`RunIncrementalFlowAsync`), Change Destination and Remap
Source Drive. Without this, a selection corrected elsewhere is displayed stale and
then saved straight back — which happened within twelve minutes of a manual fix.
Standalone Largest Files goes one better and never writes its own copy at all: it
applies its change to a fresh read and writes that.

**Write only what you meant to change.** `UpdateBackupSetAsync` rewrites every
column from the object it is handed, selection included.
`UpdateBackupSetMetadataAsync` writes everything except `SourceSelectionJson`. A
caller that is recording a timestamp, a destination, a volume identity or an
exclusion pattern has no business carrying a selection with it — use the metadata
write. Reserve the full write for code whose purpose is to change the selection.
The same shape of bug was fixed for file rows earlier (a partial projection
reaching a whole-row writer); this is the backup-set version of it.

**Test the regression against the old code, not just the new.** The widening test
passes on the fix; what makes it worth keeping is that, with the fix reverted, it
reproduces the incident verbatim — `cycle 2 saves: FULL, 5 listed`. A test that
would also have passed on the bug proves nothing.

### The editor saves only when told to

Nothing done in the backup-set editor reaches the catalog until the user clicks
**Save**, or answers **Yes** when closing with unsaved changes. **Cancel**
discards the whole edit, confirming first only if something changed. Closing
with ✕ and no changes writes nothing; with changes it asks Save / Don't Save /
Cancel. A forced shutdown (installer, Windows ending the session) neither prompts
nor saves.

It used to auto-save every checkbox change 300 ms after it was made, and to save
on every close. No and Cancel therefore discarded nothing but settings, and any
stray click went straight to the catalog, where the Worker acts on it — one click
on the tick-box column's header while it is mixed selects every drive in full.

How it holds:

- **A private copy.** `StartEditFlow` edits `CloneBackupSet(shared)`, a deep copy
  made through the catalog's own JSON serialization, never the instance the
  row and `SelectedBackupSet` hold. A discarded edit therefore cannot linger in
  memory for some later metadata write to persist. After a save, `SaveAllAsync`
  copies what it wrote back into the shared instance.
- **One writer.** `SaveAllAsync` is the only place the editor writes the set. Its
  callers are the Save button, Yes at the close prompt, and "save first" before a
  seed. It drains the tree's deferred checkbox work (`FlushPendingEditsAsync`)
  before reading the tree, since only the Save button did that before.
  During File > Exit, `SaveBeforeExit` writes synchronously, because an exiting
  process does not wait for an async save, and nothing is auto-saved any more to
  cover for it.
- **One definition of "changed".** `HasRealUnsavedChanges` decides whether
  Cancel, ✕ and Seed need to ask. It is the event-based dirty flag, confirmed
  against the toggled-paths mark and the settings snapshot, so programmatic churn
  never raises a prompt.
- **Seeding is the exception, and says so.** It writes file records immediately
  (Cancel cannot undo that), against the destination shown on screen. With unsaved
  changes it asks to save first; it used to save silently.
- **Largest Files belongs to the edit it was opened from.** Opened from the editor
  (`deferPersistence`), each include/exclude toggle becomes an edit of the
  editor's exclusion list (`ExclusionToggled` → `ApplyExclusionToggle`): shown
  there, counted as unsaved, and kept or dropped with everything else. It used to
  write the catalog on every click without reaching that list, so the editor's
  next save put the old list back. On its own, its toggles wait for its Save
  button. Closing with unsaved toggles asks, through `IConfirmClose`, which
  `TaskWindow` consults on every close.
- **The reconcile follows what was saved.** The post-close purge/back-up offer
  compares against `savedState`, the copy frozen at the last save, not against
  the live copy, which may also hold edits made after that save and then
  discarded.

The cost: folder expand/collapse state, stored in the selection as `IsExpanded`,
is remembered only when a save happens. A no-change close used to persist it as
a side effect of saving everything.

`tools\editor_save_test` drives the real editor, message boxes included, against a
throwaway catalog, and counts every write (86 checks, the missing-entry cases
included). Built against the pre-change editor, 37 of the 60 checks that compiled
there at the time failed.

### A saved choice for something that isn't on disk is kept, shown, and never re-derived

A selection is kept by path, so a folder that is deleted, moved, or on a drive that
isn't plugged in keeps its entry, and a folder that reappears at that path is backed
up (or excluded) as before. That is deliberate: a folder that is only temporarily
gone must not fall out of the backup. What the editor must do about such entries:

- **Show them.** A saved entry whose path is gone is a real row
  (`CreateMissingNode`), dimmed and labelled "(not found)", or "(not connected)" for
  a top-level drive or share. That includes a whole source: dropping a top-level
  entry the editor couldn't find is how an unplugged drive used to be deleted from
  its set by the next save. Existence is checked off the UI thread, because an
  offline share can take seconds to answer. The row holds the saved entry exactly,
  never reads the disk, and never reports a size.
- **Never re-derive its state.** A missing folder's children are only its saved
  exceptions, not everything that was in it, so they cannot say what the folder
  itself should be: "all of it except tmp" has one child, tmp, unticked. The node
  keeps its saved state. It does count towards its parent's state; leaving it out
  is what used to untick a folder whose only ticked child had gone, dropping both.
- **Keep what the user chose when a row vanishes mid-session.** When a re-read
  (`ReconcileChildrenAsync`) finds a row gone, the row stays as "not found" if it
  carries a saved or user-made choice (`_hasExplicitState`). A row that only
  inherited its state from its parent goes. A missing row whose folder returns
  becomes a live row with its saved choice.
- **Keep the reconcile away from them.** Edits to missing rows go to
  `MissingFolderEditCount`, never `ChangedSelectionPaths`. The post-save reconcile
  offers to purge the backed-up copies of anything dropped from the selection, and
  for a folder that no longer exists those can be the only copies left. The editor's
  unsaved-changes check counts both.
- **Forget in one go, conservatively.** "Forget them" (`ForgetMissingAsync`) removes
  the missing rows and prunes missing entries from never-expanded folders' saved
  sub-selections (`PruneMissing`). It re-checks every path first, and leaves alone
  any source that isn't connected, together with everything under it: an unplugged
  drive has lost nothing. The count on the bar (`CountMissing`) uses the same rules,
  computed from what would be saved.

### Never widen a selection to "repair" a tristate

1.3.1 added a model-level pass that promoted a partial directory to
fully-selected when every child it *listed* was selected. It was withdrawn the
same day.

The saved model contains only nodes the user has touched, so a partial parent's
listed children are **not** the complete set. `C:\` was stored partial with six
listed children, all selected, against 64 actual subdirectories on the volume -
so the "repair" quietly added the whole drive to a backup set.

The reasoning behind it ("an unlisted child inherits its parent, so
every-listed-child-selected cannot conceal an exclusion") is exactly backwards
under a *partial* parent, where unlisted children are excluded by definition.

The rule this leaves behind: **a tristate correction may only ever narrow a
selection** - selected to partial when a listed child is excluded - never widen
it. Over-selecting a source is a data-volume and privacy problem, not a cosmetic
one, and it is strictly worse than the checkbox-flip it was trying to fix. Any
future attempt must be tested against a node whose recorded children are a
strict subset of what is on disk.

### Page Up/Down/Home/End follow the mouse, not the focus

`Behaviors\ListPagingKeys` installs one class handler on `Window` at startup so
those four keys scroll whichever list the cursor is over. WPF gives them only to
the keyboard-focused control, which suits editors and not this app: most of its
lists are read, not edited, and clicking into a results tree to gain focus also
changes the selection - not what someone who just wants to scroll is asking for.

**It adds behaviour and never replaces it**, which is the whole reason it is safe
to register app-wide. Two guards do that work:

* **Focus in a text editor - do nothing.** All four keys are caret keys there.
* **Focus inside an `ItemsControl` - do nothing**, and let WPF run. That moves
  the *selection*, which is right for a focused list and deliberately not what
  the hover path does (hovering scrolls, like the wheel).

Modified combinations (Ctrl+Home, Shift+PageDown) are left alone entirely.

Target resolution walks **outwards** from the cursor to the first ScrollViewer
that still has somewhere to scroll, so an inner list beats the page around it and
a list at its limit hands off to its container - the same rule `BubbleScrollWheel`
applies to the wheel. If the cursor is over no list, it falls back to the only
scrollable list in the window, and does nothing when there are two or more:
guessing is worse than no response.

A class handler, not a per-control attached property, because there are nineteen
views and the twentieth would have been forgotten.

### Rewriting a destructive path needs a frozen oracle, not a careful reading

`DestinationWalker` decides which files Cleanup offers to **delete**, so the three
optimisations applied to it — one directory sweep instead of two, parallel across
directories, no per-file string — are held to producing byte-identical verdicts.
`tools\dest_walk_test\OriginalWalk.cs` keeps the pre-optimisation algorithm
verbatim from git and both are run over the same generated trees, comparing the
result *sets* entry by entry rather than their counts. The trees deliberately
include the case that would destroy data if misjudged: a `.fileref`/`.dedup`
manifest materialised back into a plain file, whose bytes are real backup content
that no longer matches its catalog path exactly.

Keep that oracle frozen. Its only value is disagreeing when the real walker
changes meaning; "tidying" it to match the new code destroys the test.

**Enumerate a directory once.** `GetFiles()` followed by `GetDirectories()` is two
full FindFirstFile/FindNextFile sweeps of the same directory;
`GetFileSystemInfos()` returns both in one, partitioned on
`FileAttributes.Directory`.

**Compose hash keys from spans, not concatenations.** `DiscPathKey` was already
allocation-free inside while its caller built `dir + "\" + name` for every file
just to hash it — and two more strings for the `.fileref`/`.dedup` probes. A
tracked file needs no string at all; materialise the path only for entries that
land in a result list.

**And benchmark on the medium that matters.** The 13.77x measured here is on a
warm temp directory, which is precisely where halving the enumeration sweeps helps
least. A cold USB spinning destination should do better — so that figure is a
floor, and quoting it as a ceiling would be the wrong lesson.

### Never test one path against a list of roots

`orphanedDirPaths.Any(p => IsPathUnderRoot(path, p))` reads as a filter and is an
O(files x directories) nested loop. Four classification phases used it, and
`IsPathUnderRoot` allocates a string per comparison to append a separator.
Measured live: "Detecting excess versions" advanced at **21 files/sec** against
2,854,931 rows — about 36 hours for one phase, single-threaded, with the whole
thread pool parked and the catalog already in memory.

Use `DirectoryPrefixSet`: hash the roots and walk the *path's own ancestors*, so
cost tracks path depth rather than root count. Measured **3,718x** at 40,000
directories, widening as that count grows. It memoises the last directory it
answered for, which collapses per-file cost to per-directory because rows arrive
ordered by SourcePath — so it is deliberately **not thread-safe**; give each
consumer its own.

**Rewriting a hot predicate demands an equivalence test, not a reading of the
code.** A randomised pass over 60,000 cases caught a divergence every hand-picked
case had missed: for a root stored with a trailing separator, the original rule
reports the bare path as *outside* it. That is probably a bug in the original —
and a performance change is still the wrong place to fix it. The quirk is
preserved, documented, and asserted, and the one-root rule now lives in exactly
one place so the scalar and set forms cannot drift.

**Also: find out whether a slow phase is doing I/O before restructuring its
I/O.** The obvious theory here was to bulk-read the catalog and the directory
listings into memory and compare there. Both were already done exactly once; the
entire cost was in-memory string work. A ten-sample stack dump of the live
process named the method immediately — cheaper than any amount of reasoning about
the architecture.

**Report a rate, not just a percentage.** "2% complete" invites waiting. "21
files/sec against 2.8M rows" is 36 hours, and ends the discussion.

### Long phases must name what they are doing

The catalog scan's deleted-file check was reported as a hang. It was not hung:
it was running a phase that reported nothing, so the UI sat on a finished-looking
`(2,583,781 of 2,583,781)` for minutes while real work continued. Measured on the
live process at the time: 1,829 metadata ops/s sustained, i.e. plainly busy.

Three rules came out of it, and they generalise to any phase here:

* **Never leave a gap between the last `Bump` and the next `SetPhase`.** That gap
  is indistinguishable from a freeze, and the more expensive the gap the more
  convincing the illusion.
* **Count the thing the user is waiting on.** This phase now counts *files
  checked*, not directories probed, because files are the unit that takes the
  time.
* **Name the current path.** `ClassifyProgress.SetCurrent` is written by every
  worker without a lock (a reference store is atomic; which thread wins does not
  matter) and shown by the UI timer. A phase that genuinely does stall then names
  the path it stalled on, instead of leaving a number to be interpreted.
* **The full path, and all of it visible.** Progress names a file or folder by
  its full path, never `Path.GetFileName`, `DirectoryInfo.Name` or a
  destination-relative path. `index.html` says nothing about which one, or where.
  The displays that show it wrap (`TextWrapping="Wrap"`) instead of
  `TextTrimming="CharacterEllipsis"`, which cuts off the end of a path, and the end
  is the file name. The lines that change on every file (the progress panel, the
  main window's per-set line, both restore windows) keep a two-line `MinHeight`
  so the layout doesn't jump as paths change length. Tree rows, whose position
  already implies the path, still show just the name. Test:
  `tools\targeted_progress_test` §8.

**The same illusion came back in the purge**, which is why this is stated as an
invariant and not a one-off fix. `PurgeSelected` counted *work items*
(directories) while the cost is per *catalog row*; one directory carrying
hundreds of thousands of paths pinned the display at `97,059/97,187` for two days
with no way to show movement inside it. The denominator is now rows and the
counter advances inside the per-path work (chunked at `PurgePathChunkSize`), not
only between items. When adding a phase, ask what the **unit of cost** is and
count that — the unit of iteration usually is not it.

**And it came back a third time, in the backup itself** — the checks a directory
backup makes before copying its first file. See "A targeted run shows the same
progress as any other backup" below.

**A step with nothing to count still shows it is going.** Verify Integrity
(`VerifyService`) runs each step on a task of its own: loading the catalog,
scanning the sources, checking each record. `ReportingAsync` reports a snapshot
of the step as it starts and then at the `ProgressCadence` until it ends:
"Step n of 3", a count, a percentage where the total is known (the load counts
`GetFileRecordCountAsync` records, the scan against the catalog's live paths as
"about"), and the step's elapsed time. So the line moves even while the step
waits for the catalog, or hashes one large file. The load reads unsorted
(`inPathOrder: false`), since sorting every record before the first is most of
the read and Verify needs no order. Test: `tools\verify_progress_test`; the
window's own start, `tools\editor_save_test` §24.

### When creating a name is the whole cost, say so

A backup into the j: set copied 1.8 GB in nine hours. Its process ran one core
flat out with almost no I/O, and every stack sample sat in `File.Move` after a
copy's write. The destination folder held 1.4 million files whose names all began
"unknown artist - ", on a drive with 8.3 short-name creation on: Windows looks
for an unused short alias for every new long name, and in that folder it took
**779 ms per file** (7 ms in an empty folder on the same drive; **4.2 ms** in the
same folder once `fsutil 8dot3name set G: 1` had turned short names off). The
copy creates two names per file - its `.lbtmp` and the promotion over it - and
that was the entire run. Nothing on screen said so; the estimate read
"~1492:01:56", whose tilde the user took for a minus sign.

* **`SlowFileCreationMonitor`** times exactly that - the temp file's open plus
  the promotion (`OpenTemp`, `Promote`) - in every plain copy. An average of
  250 ms over 32 files warns once per run: on a line of its own in the panel
  (`BackupProgress.Advisory`, kept up until the next run, and appended to the
  row's result line), and through `DirectoryBackupService.Advisory`, which the
  Worker logs as a warning at once rather than when a days-long run ends. The odd
  slow file does not trigger it.
* **`ShortNameSetting.IsCreationEnabled`** asks the volume through a handle to the
  folder (`FSCTL_QUERY_PERSISTENT_VOLUME_STATE`), which needs no elevation where
  `fsutil` is refused. When short names are on, the warning gives the command;
  otherwise it names the drive, its connection, or an antivirus scanner.
* **Time left is said as people read it**: "about 05:30", "about 30:00:00", and
  past two days "about 62 days" (`FormatRemaining`), never "~", and never below
  zero - a phase that finds more files than it counted on clamps rather than
  going negative.

Pinned by `tools\targeted_progress_test` §18, which also checks
`ShortNameSetting` against the short name Windows actually gives a new file.

### One cadence for every progress display

`ProgressCadence` (Core) holds the one interval at which progress reaches the
screen, `IntervalMs` = 500 ms, two updates a second, and every display and every
throttled reporter uses it — the GUI's view models, the service layer, the polls
of the Coverage and Largest Files scans. It lived in `ViewModelBase`, out of the
services' reach, so they kept private copies, and two displays drifted (150 ms,
1 s). A loop that could report after every item asks `ProgressCadence.IsDue()`
after each one and reports when it says so: that holds the pace whether items
take microseconds or seconds, which a report every N items does not. The backup
engine's phases use it, as do the duplicate scans. About fifteen older loops
still keep their own stopwatch against `IntervalMs`: the catalog reconcile, the
purge, the main window's scans and deletes, Cleanup, the seed import and the size
scan. That is the same check by hand, worth converting when next touched.
`DestinationWalker`'s is shared between threads, which `ProgressCadence` is not.

### Probe and classify in one pass, one directory per work item

The deleted-file check probes a directory and classifies it in the same work
item, 16 at a time. Splitting those into "probe everything, then classify
everything" is the obvious refactor and is wrong three ways: it holds every
directory's filename set alive at once (the largest transient in the scan), it
puts all the reporting in the first half and all the cost in the second, and it
invites hoisting the per-file `File.Exists` fallback into a flat parallel pool.

That last one is the subtle one. The fallback must stay **inside** its
directory's work item. Each thread then walks one directory whose index it has
just read, so its metadata is already in cache; a flat pool would scatter 16
threads across the volume instead. Measured live: **1,829 metadata ops/s against
76 physical read ops/s**, so ~96% of checks never reach the platter — the cost is
per-call overhead (syscall, filter drivers, path parsing) on the calling thread,
not head movement. That is why this parallelises well even though `D:` is a
spinning disk, and for the few checks that do miss cache, SATA NCQ reorders a
16-deep queue into a shorter sweep than 16 serial requests would produce.

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

**A row has two halves, written by different passes, and they need separate
validity stamps.** `DirLastWriteUtc` validates the direct-file figures;
`FilteredDirLastWriteUtc` validates the filtered recursive totals. Conflating
them is not a tidiness question — it produced a cache that could never hit. The
filtered walk does not measure the direct-file figures, so when it created a row
it stamped `DirLastWriteUtc` with `DateTime.MinValue`, which no
`stamp >= currentLastWrite` test can ever satisfy, and every later write
preserved that MinValue. On the real cache **814,458 of 2,515,306 rows (32.4%)**
were inert, re-enumerated in full on every pass forever. Stamping the *real*
mtime there would have been just as wrong the other way: it would validate direct
sizes that pass never measured. If you add a third kind of total, give it its own
stamp.

Stamp from the mtime read **before** the walk, never after. An mtime read
afterwards is newer than the data it describes, so a directory modified during
the walk produces a row that validates and serves a stale total indefinitely.
Reading first is self-correcting: the stamp is merely too old, so the next pass
recomputes.

**A background job needs an owner and a way to stop.** `SizeComputeScheduler` is
per-editor and is disposed from `dialog.Closed`. It had no token, no stop
condition and no `IDisposable`, so closing the editor left a full-volume
enumeration running against a window that no longer existed — its worker's
closures rooting the entire node tree — and each reopen added another scheduler
over the same cache file. One was found still walking after two days. The token
is checked **inside the recursive walk**, not merely between queue items: one
walk of a large volume is hours on its own. And a cancelled scheduler must clear
its queues and refuse to restart, because `EnsureWorkerRunning` deliberately
restarts on the empty-queue race and will otherwise resurrect the worker forever.

## Cleanup's results are resizable in three ways, and the reason matters

Reading a path in the results tree is a width problem, and there are three levers,
all now user-controlled:

* **The Files and Size columns are draggable** (splitters on their left edges in
  the header; widths shared with every row through the `CleanupColumnLayout`
  resource object, persisted). Directory is a star column, so whatever they give
  up it takes.
* **The dividers between category cards are draggable** — the cards flow in a
  `WrapPanel` with a per-card `WidthOverride`, so widening one pushes its
  neighbour along. Double-click a divider to return that card to its share.
* **Categories per row (1-3)** as a quick preset, persisted.

**Measure the space that is actually left, not the column total.** The first
attempt at this dismissed column resizing on the grounds that reclaiming Files
and Size buys "only ~136px" of a ~160px name column — an 85% gain, seemingly not
worth building. That was the wrong quantity. The tree indents **19px per level**,
so at five levels deep the name column has ~65px of *usable* width, and handing
back 136px takes it to ~201px: **three times** the readable space. A gain that
looks marginal against the whole column is decisive against what remains after
the indent.

**Horizontal scrolling inside a card was rejected for a reason that only applies
to one implementation of it.** Content-sizing the name column and sharing it with
`SharedSizeGroup` would jitter under virtualization, because a shared group
measures only *realised* rows. But an explicitly-sized name column has nothing to
measure, so a row wider than the card would simply scroll. It is not implemented
because the two levers above cover the cases seen so far — not because it cannot
work. If deep trees still run out of room, that is the next thing to build.

## Modality is a concurrency decision, not a cosmetic one

`ProgressDialog.RunAsync` takes `modal` (default true). In WPF `ShowDialog`
disables **every other window in the application** — including the task windows
that exist precisely so work can run side by side — so a modal progress dialog
silently reverses that design for as long as it is up.

The rule: **read-only phases pass `modal: false`; anything that mutates the
catalog or the destination stays modal.** The post-edit diff scan and the
added-folders walk only read, so they no longer freeze the app. The purge that
follows ("Removing files from destination") stays modal, because letting the user
start a second operation on the same set while files are being deleted is exactly
the hazard the modality is preventing.

The modeless path closes its dialog after awaiting the work, marshalled through
the dispatcher — the continuation resumes on whatever context captured the await,
which is the UI thread in the app but is not guaranteed to be. Verified both
orderings, including the race where the work finishes before `Show()`: each
returns the right result and leaves zero windows open.

## After a selection edit, back up what was named — do not go looking for it again

The post-edit flow walks the folders you just ticked and knows every file in them.
It used to hand that list to the ordinary Backup entry point, which threw it away
and re-derived the same answer by scanning the whole selection and diffing against
the catalog. Measured across 41 full runs in one month, "Starting backup" ->
"Plan for":

| set | runs | median | min | max |
|---|---|---|---|---|
| I: | 21 | **699 s** | 195 s | 18,539 s |
| J: | 20 | **1,086 s** | 289 s | 16,810 s |

So adding a folder meant a median ~12-minute wait (worst case over five hours)
before the files started copying, to rediscover a list already in hand.

`RunIncrementalFlowAsync` now takes an optional `onlyThesePaths`. Everything
before the scan is unchanged — drive-letter following, source and destination
availability, job construction — and then it calls
`DirectoryBackupService.ExecuteTargetedAsync`, the same entry point continuous
backup uses for USN-reported changes. That still applies the set's exclusion rules
and still compares every candidate against the catalog; it simply does not hunt
for candidates.

**A targeted run is scoped, and the prompt must say so.** It copies the listed
files and nothing else; other pending changes wait for continuous backup or the
next scheduled run. Both review prompts now state their scope explicitly ("Back up
ONLY these files", "Delete ONLY these backed-up copies … your source files are not
touched"), because a button that does less than its name suggests is only safe if
it says which less.

The ordinary **Backup** button still means everything: it passes no path list and
takes the full scan.

**A targeted run shows the same progress as any other backup.** It fills the row's
inline progress panel (`BurnProgressViewModel`) exactly as a full directory backup
does. It hands a progress reporter and the panel's pause event through
`ExecuteTargetedAsync` to `ExecuteAsync`, which reports each file as it starts, the
byte totals, and 100% at the end. It used to run behind a `ProgressDialog` whose
reporter was never passed down: `ExecuteTargetedAsync` gave `ExecuteAsync`
`progress: null`, so the run said "Copying N file(s)…" and nothing more until it
finished. `tools\targeted_progress_test` pins the reports, Pause and Cancel against
the real engine. Anything new that copies files must take both, or the user is left
guessing whether it is working.

**So does everything it does before the first file is copied.** Adding a folder
of 2,671,103 files sat on "Checking 2,671,103 file(s) against the catalog..." at
0.0% for **15 min 55 s** (measured from the log and the run's disc record): a stat
of every candidate, a catalog lookup of every one, then — inside `ExecuteAsync`,
under the same line — the same lookup again and a lookup of deleted history, with
Pause ignored throughout. Each of these is now a **phase** that counts files:

| phase | where | when |
|---|---|---|
| Checking files against the catalog | `ExecuteTargetedAsync` | always |
| Analyzing files for deduplication | `ExecuteAsync` | block dedup on |
| Checking which files might be duplicates | `ExecuteAsync` | file dedup on, block dedup off |
| Verifying backup | `ExecuteAsync`, after the copy | verify after backup on |
| Applying retention rules | `ExecuteAsync`, after the copy | retention scoped to the run (targeted runs) |
| Removing expired versions | `ExecuteAsync`, after the copy | retention found versions to remove |

Verification used to send a status report per file, unthrottled: over millions
of files, millions of posts to the UI thread. A full backup's retention reads the
whole set in one query and still shows only "Applying retention rules...".

**A targeted run's retention reads only the files it gave a second or later
version** (`pathsWithHistory`: those the copy found an earlier version of). A
first version leaves no previous one to prune, so reading the other files' records
could never remove anything. It used to read every file the run wrote, a path at a
time: for 2.67M new files, 2.67M queries and as many `FileRecord` objects, silent,
for nothing.

`PhaseProgress` reports a phase as `BackupProgress.PhaseFilesDone` of
`PhaseFilesTotal`, with `StatusMessage` naming it and `CurrentFile` the file it is
on, at the `ProgressCadence`, starting with "0 of N" the moment the phase begins.
The panel shows "*phase* (*done* of *total*)...", the bar at the phase's own
percentage, the file, and under the bar the phase's files per second, the time
elapsed and the time left.

Catalog lookups go in batches (`LookupBatch`) sized from the rate of the calls so
far to take about 100 ms each. A call holds the set's connection, and the count and
Pause are serviced only between calls, so one must stay well inside a progress
interval; yet its fixed cost should vanish into its seeks. On the 5.7M-row set a
seek takes 15-60 µs when its pages are cached and 1.5-3.6 ms when they are not, a
hundredfold apart, and 9 ms has been seen with the Worker writing to the same set.
That is why the size is measured, not fixed: a fixed 1,000 would hold the count
and Pause for seconds on a cold cache.

**Each version is numbered as it is written, from the catalog under the write
lock.** The copy loop reads every file's last version (`GetLastVersionAsync`: the
newest live record, or failing that the newest deleted one, which a reappearing
file continues from) inside the transaction it writes in. That transaction holds
the set's cross-process write lock, so no other process can add a version of the
path between the read and the write. The run used to number every file from one
lookup made before its copy loop. The Worker's continuous backup commits between
a long run's transactions, and when it backed up a file the run was still to
reach, the run gave that file the same version number again, and moving the
current copy into `_prev` overwrote the older version's copy there
(`File.Move(..., overwrite: true)`). The check before copying still looks each
candidate up, but only to decide whether it is new or changed. One indexed seek
a file (19-36 µs warm on the 5.7M-row set) is noise beside the copy it numbers,
and it made a whole pre-copy pass ("Looking up earlier versions") unnecessary.
`targeted_progress_test` §14 has another writer add a version between the check
and the copy, and at a commit boundary, and checks every version's number and
content.

**Anything learned under the write lock is good only until the run lets it go.**
A run lets go at every commit (every 500 files or 30 s, and in the Worker whenever
another process waits for the set), and another writer can write between two of
them. So: version numbers are read per file under the lock (above).
The file-level dedup memo of "hash -> a plain copy holding it" is emptied whenever
the lock is taken back (`ReacquireLockAsync`), because the copy it names may have
been pruned. Pause commits and lets the lock go before waiting
(`WaitOutPauseAsync`, also called by the copy helpers between chunks): held, it
stalls every other writer of the set, the Worker's whole poll loop included.
Mid-file that hands the lock back after the file's number was read, so on
resuming the number is checked again: if another writer has recorded a version of
the file meanwhile, the partial copy is abandoned and the file starts over with a
fresh number and a fresh read (`VersionTakenException`). A new number follows the
highest any record of the path has, tombstoned ones included
(`GetLastVersionAsync`'s `HighestVersion`). A live chain can sit below a newer
tombstoned record, and that record still owns its number. The live record is still
the copy that moves to `_prev`.
Verification, which runs after the last commit, skips a file whose last version is
no longer the one this run wrote. A cancel throws `BackupCanceledException`,
carrying the files that had failed and whether the copy had finished.

**Progress is throttled by holding back, never by dropping.** The row panel
(`BurnProgressViewModel.OnBackupProgress`) shows at most one data report per half
second. It used to throw the rest away, and the report thrown away could be the one
naming a new file: a big file that started just after a small one left the small
file's name on screen for as long as the big one took. It now keeps the newest
held-back report and shows it when the interval is up. Equally, **a long phase of
work on one file must report**, not just the copy. Reading a file to hash it
before deciding whether to copy it (whole-file duplicate detection) is as slow as
the copy for a big file and used to say nothing. It now reports with
`BackupProgress.CurrentFileActivity` = "Checking for duplicates", which the panel
shows in place of "Copying files...".

## "What does this selection cover" has two answers, and only one is precise

`SourceSelection` offers two collectors, and picking the wrong one is silent:

| | selected FILE becomes | for |
|---|---|---|
| `CollectSelectedRoots` | its **containing directory** | file-system watchers — you can only watch a directory |
| `CollectCoveredEntries` | **the file itself** | anything asking what the selection actually covers |

The coarsening is lossy in a way that compounds, because both then reduce to
*minimal* roots. One ticked file at `D:\registrybackup.reg` yields the root
`D:\`, which absorbs every other `D:\…` entry as "already covered". Measured on
a real set: **299 entries instead of 2,546 — 88% of the selection swallowed by a
single stale file node**, for a file that no longer existed on disk.

The post-edit diff used the coarsening collector, so `newRoots` and `oldRoots`
were identical no matter which folders were ticked, `added` was always empty, and
the "back up what you just added" flow was never called. Adding a 12 GB folder
produced a scan and then nothing. It now uses `CollectCoveredEntries`; a file path
can swallow nothing, since nothing is prefixed by `"somefile.ext\"`.

**A separate, genuine difference worth knowing** (it was my first and wrong
explanation for the above, so it is recorded to stop it being reached for again):
`CollectCoveredEntries` only collects **fully**-selected nodes, while
`IsPathIncluded` additionally covers unlisted descendants of a
**partially**-selected directory whose auto-include-new is on. (`CollectSelectedRoots`,
which answers "where do I watch", returns such a directory as a root itself: see
"Auto-include must not pin what can never be backed up".) That can make a
newly-added root whose files were already considered included — a real case, just
not this one, since `D:\` here is partial with auto-include **off**. The flow now
reports that case instead of returning silently.

**If you add a third place that decides coverage, say which of the two questions
it is answering** — "where do I watch" or "what is in the set" — because they
give different answers and neither is wrong.

## A source scan must not walk what it will never back up

Exclusions were applied per FILE, and the scanner recursed into every
subdirectory regardless — so a set excluding `**/build/**`, `**/debug/**`,
`**/target*/*` still walked every directory of every build tree, rejecting the
files one at a time. On a spinning disk at ~3.4 ms per directory operation, that
was the bulk of the scan.

`GlobMatcher.CreateDirectorySubtreeFilter` answers the stricter question — is
EVERY file anywhere beneath this directory excluded — and `FileScanner` skips
those subtrees. Measured on a real tree with the real pattern set: **69.4 s ->
10.8 s, 6.4x**, with the resulting file list **identical** (0 lost, 0 gained).

**Being wrong here means a file is silently never backed up, so the predicate is
conservative by construction:**

* only patterns ending `/*` or `/**` qualify; the part before becomes an anchored
  regex matched against the directory. `**/debug/**` prunes a `debug` directory;
  `**/debug/*.obj` prunes nothing, because it excludes only some of the files.
* filename-only patterns (`*.raw`) never prune — a directory can hold any name.
* zero-tier tier sets prune only when they have no `FileExemptPatterns`, since an
  exemption can re-include a file deep inside.
* `/**` alone is refused rather than pruning everything.
* **Soundness depends on `*` crossing separators** in this GlobMatcher (`*` ->
  `.*`). That is what makes `dir/*` cover `dir/a/b/c`. If `*` is ever changed to
  stop at a separator, this must change with it or it will prune subtrees whose
  deeper files are not excluded.

**And the progress counter must not be able to look stopped.** `FilesFound`
counts only files that will be backed up, so crossing a large excluded tree pins
it at one number for hours; that is what "stuck at 657,774 files scanned" was —
a healthy scan with nothing to report. `ScanProgress` now carries
`DirectoriesScanned` and the UI shows it alongside. A progress indicator that can
legitimately freeze is not a progress indicator.

## A walk follows links, but never back into its own path

Every walk of a source tree follows directory links (junctions, symbolic links,
mounted folders): the scan (`FileScanner`), the editor's size walks
(`SourceSelectionNodeViewModel.ComputeDirectorySize*`), the walk of folders just
added to a selection (`MainViewModel.EnumerateFilesUnderRoots`) and the Worker's
walk of a folder that appeared (`LinkLoopGuard.EnumerateFiles`). They must make
the same choice, or the editor's sizes stop describing the backup.

A walk is a depth-first search of a directed graph whose nodes are directories
and whose edges are subdirectory entries, a link's edge going to its target.
Without links it is a tree. A link to the folder holding it, or to any folder
above that on the walk's path, is a back edge, and following it never ends:
`C:\a\b\loop` -> `C:\a` gives `C:\a\b\loop\b\loop\...` until the path is too
long for Windows, backing the same files up under each longer path (.NET's own
recursive enumeration does exactly this). `LinkLoopGuard`, one per walk, refuses
those links and only those. Every other link is followed, so its files are backed
up under the link's path, including a link to a folder that is also selected
(finite duplication, by choice: what is selected is backed up).

Directories are compared by final path (`GetFinalPathNameByHandle`), so `subst`
drives, mounted folders and links reached through links are seen for what they
are. The walk's path is split by the links it went through; within each stretch
the folders are one chain of real directories, so a target above any of them is
above the deepest, and the guard keeps only that one's final path per followed
link (the holding folder's) plus the current folder's. Plain folders cost
nothing; a link costs two handle opens. The guard works from the paths the walk
reached things by, not from visiting order, so it serves recursive walks and
.NET's queue-based enumeration alike, and `Seed` records the links among a
walk's start and its ancestors, so a selected folder inside a junction is
treated the same as when reached from above. A listed selection entry is walked
whatever it is (the selection says so), so a loop link ticked on its own backs
its target up once under its path, and nothing again through it.
`tools\link_loop_test` and editor_save_test 20c pin all of this.

## Build the answer, not a copy of the question

The destination scan needs, per disc path, one bit — is any record still active,
meaning the file on disk is properly tracked — and, only for paths where every
record is deleted, one source path to display. It used to get there by
materialising every row (`List<(DiscPath, IsDeleted, SourcePath)>`) and then
building a `Dictionary<string, List<DestPathRecord>>` on top, so the rows were
held twice over and a `List` object existed per path.

Measured on a real 2,810,190-row set:

| shape | peak |
|---|---|
| rows materialised + a record list per path | **1,751 MB** |
| aggregated as the rows arrive | 754 MB |
| …and keyed by a 128-bit path hash | **164 MB** |

with identical verdicts on every path at each step.
`ICatalogRepository.ForEachDiscPathEntryAsync` streams the rows to a callback and
`DestPathState` holds the aggregate, whose source-path field is nulled the moment
an active record appears — so most of the strings become garbage as they are read.
`Services/DiscPathKey` then removes the path strings themselves.

**Hashing a key you will make deletion decisions from needs an argument, and
"the odds are tiny" is the weakest one available.** The real argument here is
that the aggregation is *collision-safe by construction*: active wins, and
nothing ever clears it, so a file whose own path has an active record reads back
as active whatever collides with it. Every collision outcome is "miss an orphan",
never "offer a real backup for deletion". The birthday bound (≈1.2e-26 at 128
bits over 2.8M paths) is then a footnote, and measurement confirmed 0 collisions
and 0 changed verdicts across 2,803,602 real paths.

**What that argument depends on, so do not break it:** if a future change ever
lets a *deleted* record overwrite an active one for the same key, collisions stop
being safe and this becomes a way to delete live backups. Keep "active wins"
unconditional.

**The general shape: a lookup should hold what the question reduces to.** If you
find yourself keeping records so a later pass can reduce them, reduce them on the
way in — and if what remains is still mostly the keys, ask whether the keys
themselves need to be there.

## Anything that can move while nobody is watching must log

Only the Worker logged its backups, so a backup started from the GUI left no
trace on disk. When a task window vanished during one, there was no record that a
backup had even started, and the cause had to be inferred from reading the code
rather than read off a log line. `MainViewModel` now writes start, finish
(flagging errors) and nothing-to-do through `CrashLogger.Log`, into the same
daily log the Worker uses.

## Each task owns a window; nothing may navigate on the user's behalf

Task flows — Cleanup, Restore, Verify, Coverage, Test Disc, Largest Files, Find
File, Catalog-free Restore — each open in their **own non-modal top-level
window** (`Views/TaskWindow`, opened and tracked by
`Services/TaskWindowManager`). They set no `Owner` and do appear in the taskbar,
so a multi-hour scan can sit behind the main window and survives it being
minimised.

**Why, and what it replaced.** They used to be swapped into the main window's
`CurrentView`. That made the current task a piece of *global* state, so any code
that assigned `CurrentView` destroyed whatever the user was doing — and two
places did, as part of a backup's lifecycle rather than a navigation:
`StartBurn` reset the view so the row's progress panel would be visible, and
`ShowNoOpCompletion` did the same. Starting a backup therefore silently
discarded a running Cleanup destination scan; observed once, after several
hours of scanning.

**The rule: a backup starting or finishing must never change what the user is
looking at.** More generally, no background event may navigate. If a piece of UI
needs to be seen, it appears where it belongs and waits.

`CurrentView` and `GoHome` still exist but nothing assigns a flow to them. If you
add a task flow, give it a window — do not reintroduce an inline view, because
the moment one exists the global-state hazard is back.

**Concurrency between windows is policed, not assumed.** Several task windows may
be open at once, which means two of them can act on one backup set.
`TaskKind` records what each flow does (`WritesCatalog`, `WritesDestination`,
`ReadsDestination`) and `TaskWindowManager` uses it to:

* refuse a **second window of the same task on the same set** — it focuses the
  existing one instead, since two Cleanups purging one catalog is not something
  the catalog is built for;
* **warn and ask** before opening a task whose access conflicts with another
  window already open on that set, or with a **backup running** for it (a
  running backup is a writer too, even though it has no window).

Read-only pairs (Coverage alongside Cleanup) open silently. Adding a flow means
declaring its access honestly; understating it is how you get two writers on one
catalog with no warning.

**A task window asks before it loses unsaved edits.** A flow that holds edits
until its own Save implements `IConfirmClose`, and `TaskWindow.OnClosing` asks
it on every close — the flow's Close button and the window's ✕ alike, since both
end up in `Window.Close()`. With nothing unsaved it must return true without
asking. A forced shutdown never asks (unsaved edits are dropped, as in the set
editor). During File > Exit the close cannot be refused, so the flow is told to
offer only Save / Don't Save. Largest Files is the only such flow today.

## Exclusion rules are an invariant of the catalog, not a step in one code path

A file is excluded from a set by any of: a user glob in `ExcludedExtensions`, a
tier set with **zero** tiers, or a hard exclusion (the app's own data directory,
and `VolumeMetadataPaths`). All three live in one place —
`DirectoryBackupService.BuildExclusionFilter` — because a second copy of the
logic in the Cleanup view had already drifted from it once.

**`VolumeMetadataPaths` is everything in a volume root that is never user
data:** NTFS's metadata (`$Extend` and friends, which enumeration never shows),
and Windows' own per-volume items: `$Recycle.Bin`, `System Volume Information`,
and the paging, hibernation and swap files. The recycle bin is the same garbage as
`$Extend\$Deleted` by another route, namely what the user threw away. Before it was
listed, any set with a whole drive selected backed it up. One run was set to copy
216,208 files (21 GB) out of `D:\$Recycle.Bin`, most of them invisible even in the
Recycle Bin window because their `$I` index files were gone. It matches the first
component after the root only, so a user folder that merely shares a name deeper
down is ordinary data. Hard exclusions are also pruned from scans
(`BuildDirectoryPruneFilter`) and left out of the editor's tree, so the tree never
offers a checkbox for something no backup would include. Copies made before an
item joined the list are released the way everything is: through Cleanup's
excluded-files category.

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

## Cleanup's categories have to cover every way a backup becomes garbage

Nothing removes a backup automatically. The full backup computes
`diff.DeletedFiles` — catalogued source paths the scan didn't find — but
**only its count is used**: the number is logged and shown, and no row is
touched. That is deliberate (a deleted source keeps its backup and version
history until the user asks), and it makes **Cleanup the only route by which a
backup is ever released**. So a gap in Cleanup's categories is not cosmetic: it
is backup content that nothing in the product can ever find.

The categories divide the work between two scans, and the division is the trap:

| scan | sees | blind to |
|---|---|---|
| catalog scan | **active** rows, classified against the sources | anything whose row is already deleted |
| destination scan | files on the destination with **no active row** | anything whose row is still active |

A file with an **active row whose source is gone** must therefore be caught by
the catalog scan, because the destination scan skips it by design — an active
row means "properly tracked".

**A directory-level test does not establish a file-level fact.** "Deleted from
disk" used to ask only `Directory.Exists(parentDir)`, so moving a folder's
*contents* elsewhere and leaving the folder behind (even just one remaining
subfolder) left every one of those files with an active row, a live copy on the
destination, and no category willing to name it. Measured on a real set: 174
files / 4.8 GB in one folder. The check now reads the directory's file names
once and tests each catalogued file against them, confirming a miss with
`File.Exists` before offering anything for deletion.

**Unreachable is not deleted — and this rule now has three sites.** A drive that
is merely unplugged answers "missing" for every path on it, so a source-side
classification must probe each root once and skip unavailable ones wholesale
(reporting them), exactly as `CacheMaintenance` does for its sweep and
`CatalogReconcileService` does for the destination. Getting this wrong on the
source side would offer the whole volume for deletion.

**Probing is latency, so probe concurrently.** A cold directory open measured
58.6 ms on this machine's volumes; 139,470 of them serially is over two hours.
Reading the names as well costs 64.0 ms — **+9%** — which is what makes per-file
detection affordable. Sixteen at a time, the same figure `CacheMaintenance`
measured for the same kind of work.

## A purge acts on an old scan, beside writers that are not old

Clean Selected deletes what a scan found, and the scan can be hours old. Meanwhile
the set's row could back it up or seed it, and the Worker could back it up on its
own schedule. The purge's catalog half runs in one transaction under the set's
cross-process write lock, which keeps a writer out of *that* half only: the lock
is taken per batch, and a backup copies a file before it records it, so a copy
made during the disk half, at a path the purge then deletes, ended as a live
record with nothing behind it. Every guard below exists because of one of those
writers.

**The row.** The purge holds the set's row exactly as a seed does
(`BeginRowWork` with `BusyActivity` "being cleaned up", through `RowWorkHooks`,
because Cleanup is a task window and cannot reach `MainViewModel`), so nothing
can be started from the row until it ends, the row's Abort cancels it, and each
stage is shown there as well as in both of the window's status lines. It is
refused while the row is busy. Closing the window mid-purge asks first
(`IConfirmClose`); the purge carries on with the row as its only display.

**The Worker: two markers and a handshake.** `HoldSetForCleanup` writes
`set-N.db.cleaning`; each Worker run on the set holds `set-N.db.backingup`
(`StartSetRun`). Both are refreshed every 5 s and ignored once 30 s stale, so a
process that died cannot hold a set for good. Each side **announces before it
checks** - the Worker takes its marker and then looks for a cleanup, the purge
takes its marker and then looks for a run - so of two that start together at
least one sees the other, and the Worker always gives way: it drops its marker
and keeps its work queued. A full or targeted run that is already going watches
for the cleanup marker every second and cancels itself (`SetRun.StoppedForCleanup`
tells that apart from shutdown: the run returns DidNotRun, or requeues its paths,
rather than counting as a failure with backoff). The purge waits for the run's
marker to go before it touches anything, and that wait is cancellable and changes
nothing. Relocations are not stopped - they are quick renames over a queue that is
cleared as they start - so the purge just waits for them. A journal drain inside
a full scan backs up the scanned set under the scan's run (`SetState.ActiveRun`);
it must not announce itself again or end the marker. Markers are best effort: one
that cannot be written is the state of things before markers existed, and must
not stop the work.

**Re-checks under the lock.** Inside the transaction, before anything is retired:
every record written since the earlier of the two scans began
(`GetRecordsWrittenSinceAsync`; `BackedUpUtc` is round-trip UTC text, so a text
comparison is a time comparison) names a source path backed up again since; those
paths are not tombstoned, and neither their stored copies nor anything written
since is deleted. The one exception is the file of an excess version retired by
id: a newer version only makes it more excess, and version numbers are never
reused. That check cannot see a seed, which records what it adopts under the
files' own times, so after retiring everything, still under the lock, the purge
asks which of the files it is about to delete a live record still points at
(`FindDiscPathsInUseAsync`, one pass over the live rows' `DiscPath`, since that
column has no index) and deletes none of them. That rule needs no knowledge of
what ran since; it is the invariant itself.

**A row is what is purged.** The file rows under a directory have checkboxes, and
used to be display-only: a part-ticked directory was purged whole, unticked files
included, and a directory unticked itself but part-ticked by a ticked
subdirectory was purged too. `GetCheckedSelections` now takes an item's own
selection from its file rows, and `Restrict` narrows the item to what the ticked
rows stand for: a source path (with its stored files, attributed through the
scan's records), a record id, or a stored path. A stored file that cannot be
attributed is in no part and is never deleted on a row's behalf; a catalog-side
item without a path list, which would be purged by directory prefix, purges
nothing when part-ticked. The unticked rest stays listed, unticked, and
`SettleCheckState` makes a rebuilt tree's boxes agree with its items - they used
to show ticked over an unticked leftover, and Clean Selected then did nothing.

**One operation at a time in the window** (`IsBusy`). Scan catalog, scan for
exclusions, scan destination, Analyze, Apply and Clean Selected each wait for any
of the others. A catalog scan used to block the rest while a reconcile or a
destination scan blocked only itself, so Clean Selected could run during Apply
and a catalog scan during either. Nothing is gained by overlapping them: they
walk the same drive, and the two writers act on lists the others produce.

**Reconcile's Apply writes only what it re-checked.** Apply acts on records read
at Analyze, which can be long before, and wrote them back whole through
`UpdateFileRecordAsync` - undoing whatever had happened since: a row Cleanup had
retired came back to life (and its manifest was deleted), and a row a backup had
moved into the history was retired at, or flipped to, its old path. It now uses
`FlipFileRefToPlainIfUnchangedAsync` and `MarkFileRecordDeletedIfUnchangedAsync`,
which write two columns and one, and only where the row is still live at the
path Analyze read; anything else is skipped and counted. That holds whatever
changed the row - this window, the row, or the Worker - so it needs no
coordination of its own. The rule generalises: **a record read some time ago may
be used to find a row, never to overwrite it.**

Pinned by `tools\excess_versions_test` §12 and §13, against real backups and a
real destination scan; reverting the selection rule fails six of §12's checks,
and reverting Apply fails four of §13's.

## A configured destination is not a present one

A backup set's destination is a saved **path string** (`JobOptions.TargetDirectory`).
Having one configured says nothing about whether the removable drive is plugged
in, the share is up, or the letter still points at the same volume. Anything that
is about to change the catalog on the strength of what the destination contains
must ask `DestinationFilePurger.IsAvailable` first.

This matters because **absence is not an error** down at the filesystem: `File.Delete`
on a path under an unplugged drive raises nothing that a caller distinguishes
from "already gone", so a purge against a missing destination completes with zero
deletions **and zero failures** — the exact signature of a purge that had nothing
to do. The catalog half, meanwhile, succeeds unconditionally, because it is local.

The failure mode is therefore not "an error was swallowed" but "half a two-part
operation ran and reported success". And it is not symmetric: the surviving half
tombstones rows, and every catalog-side category classifies only ACTIVE files, so
those files leave the catalog scan permanently and remain visible only to the
destination scan's catalog-deleted category.

`CatalogReconcileService` had guarded this since it was written; the purge did
not, for months. **When adding an operation that pairs a catalog write with a
destination write, the guard goes at the top of the operation, not inside the
destination half.**

## An empty catalog is not an empty destination

The engine numbers a file's versions from the catalog (`GetLastVersionAsync`,
`HighestVersion + 1`) and files the superseded copy at `{drive}_prev\rel.v{N}`.
Every path to a catalog that knows less than the destination - a set imported
without its history, re-created by hand, cleared with Clear Backup History, or
seeded by a seed that skipped `_prev` - restarts that numbering at 1 over a
destination whose `.v1`, `.v2`, … already exist. The `_prev` move used
`File.Move(..., overwrite: true)`, so each later change silently destroyed one old
version. That is how the first two sets ever moved to a new computer behaved:
"Never backed up", then the Worker began a 1.6 TB re-copy a minute after the
import (see known-issues.md). Four defences, each sufficient for part of it:

* **The engine never overwrites a version file it does not own.** At the `_prev`
  move, a version file already at the record's number belongs to no record (the
  catalog's only record of that number says its copy is at the current path), so
  the catalog and the drive disagree, and **which file holds the record's
  content decides what happened**. If the current copy matches the record (hash,
  or size for a record seeded without one), the `.vN` is history the catalog
  never had: the copy is filed under the first number above both
  `HighestVersion` and every `.vN` on disk and the record renumbered to match.
  If instead the existing `.vN` matches, the catalog is *behind* the drive - the
  old computer backed up again after its history was exported, or a batch was
  uncommitted when the snapshot was taken - so the record is pointed at its `.vN`
  unchanged and the newer, unrecorded current copy becomes a version record of
  its own under the fresh number. (Renumbering in that case labelled the newer
  bytes with the old hash; the review of this change caught it.) Either way the
  new copy is numbered after the fresh number and the move is `overwrite:
  false`. Cost: three `File.Exists` probes per versioned write, plus one read of
  the current copy only when they find a collision.
* **History travels with an export.** `BackupSetTransfer` writes a zip of
  `backupset.json` plus `history.db`, a `VACUUM INTO` snapshot taken through its
  own read-only connection (the set's connection would hold its gate for the whole
  copy, and cannot `VACUUM` at all while a backup's transaction is open on it).
  Import `ATTACH`es the snapshot to the new set's connection and copies all four
  tables in one transaction. **Only disc ids are remapped**: they are global
  (allocated from the master's `DiscOwners`, in one transaction - a directory set
  gets a disc per run, continuous runs included, so tens of thousands is normal),
  while file, chunk and block ids are local and are kept, which only an empty
  target can guarantee - hence the emptiness check. A failed history import
  deletes the set it created: a set claiming a last-backup time with no records is
  the trap this exists to close. Settings-only exports stay the old plain JSON, so
  older versions can still read them; the two are told apart by the zip magic.
* **The catalog-empty check** (`DirectoryBackupService.FindUntrackedBackupAsync`):
  no file records at all (one `EXISTS` probe) **and** a file under the destination
  path of one of the set's own selected roots, current or `_prev`. Scoped to the
  set's roots on purpose: sets sharing a destination folder with different
  sources, or pointed at a folder of unrelated files, never trigger it. The GUI
  asks (seed / back up anyway / cancel) before its scan; the Worker refuses and
  logs once per destination, because "copy it all again" is not a choice to make
  on the user's behalf. Once the catalog has a single record the check is free and
  never fires, which is also why no "user confirmed" flag is needed.
* **The automated-backup hold** (`JobOptions.HoldAutomatedBackups`). Set by
  import, Clear Backup History and seeding, honoured by the Worker's
  `ReloadBackupSetsAsync` (`active` excludes held sets), cleared by the first full
  backup the GUI completes (including one that found nothing to copy) or the
  row's Resume link. Seeding needs it for a reason of its own: the seed commits
  in batches, so a Worker full scan that started mid-seed would treat every path
  not yet reached as new. It is written straight to the catalog from a fresh read;
  the editor's Save writes back whatever the catalog holds at that moment
  (`CarryStoredHoldAsync`) rather than its own copy's value, which re-paused a set
  released while the editor was open, and `SnapshotEditorSettings` blanks it so it
  cannot pose as an unsaved edit. On
  release the Worker moves its in-memory clocks up to `LastBackupUtc` (only on
  release: `LastBackupUtc` is bumped by targeted runs too, and timing the safety
  net from it is exactly what `LastFullScanUtc` exists to avoid).

**The seed** now walks each drive folder and its `{drive}_prev` twin together,
folder by folder, so all stored forms and versions of one source path are in hand
at once; it records each version under its own number (backup time = the version
file's write time, which is when it was copied - copies do not carry the source
timestamp - and what retention ages it by), numbers the current copy after them,
and commits whole paths in batches so a cancel can never leave a path
with history but no current copy (a re-run skips paths already present and would
never fill the gap). A file's source path comes from where it sits, never from a
`.fileref`'s own `SourcePath`: relocation moves manifests without rewriting it, so
after a rename it named the old file. After a seed it ticks the folders it found
only for a set with no sources yet; in an existing set that widened the selection.

**Two stored forms at one current location are two versions.** `index` and
`index.fileref` side by side were written by different backups. The newest (by
write time, which is when it was copied) is the current copy; each older one is
moved into `{drive}_prev` under the next free number and recorded, as the engine
would have filed it. All hashing is done before any move, so a cancel cannot
strand a moved file unrecorded; a file that cannot be moved stays where it is,
untracked, and is counted. The seed used to keep the plain form whatever its age
and leave the other untracked: on one real destination 23,190 `.fileref`s, every
one an older pointer to EMPTY content (git's index and `.claude.json` are briefly
zero bytes while being rewritten), which Cleanup's destination scan then listed as
untracked straight after the seed. Keeping "plain first" could also record a stale
plain copy as current, or leave out a plain copy that another file's `.fileref`
needs for its content. They came from continuous backup applying a rename OVER a
backed-up file (git's `index.lock` -> `index`) as a move: `MoveTargetedAsync`
relocated the renamed file's `.fileref` beside the target's plain copy and merged
the two chains. It now falls back when the target path already has a backup (any
record, live or not, so no two chains ever share a path), which backs the target up
as its next version; only a case-only rename, the same file, still relocates.
Two forms of one numbered HISTORY version (`x.v3` and
`x.v3.fileref`) cannot both be version 3, so there one is still kept (plain, then
`.dedup`, then `.fileref`) and the other left untracked.

**Catalog-writing work holds the set's row, wherever it was started.** Seeding
and Clear Backup History call `BeginRowWork`/`EndRowWork`: the row goes into
`IsRunning` with `BusyActivity` set ("seeding"), its scan-phase status line shows
the progress, and `ScanCts` is a token linked to the editor's own, so the row's
Abort stops it. Reusing `IsRunning` rather than adding a flag is the point: every
per-set command, `RunIncrementalFlowAsync`, the task-window conflict check and
`LoadBackupSetsAsync` already treat a running row as untouchable, so a backup
cannot start mid-seed from any path (the editor is modeless, and the post-edit
"back up added folders" offer reaches the backup flow without the row's button).
The reverse holds too: seeding or clearing is refused while the row is running a
backup. The work outlives the editor - Save and ✕ close it, the work carries on on
the row - so the seed must not touch the editor's tree once it has closed
(`editorClosed`), and the row is released before anything that could still throw.
The post-save reconcile prompts are skipped while the row is busy. A duplicate
analysis is read-only and blocks nothing; if the editor closes mid-run it moves to
the row's `BackgroundWorkText` line with Stop (`CarryDedupAnalysisToRow`).
Pinned by `tools\editor_save_test` §15, which freezes a seed by holding the set's
cross-process write lock. Cleanup's purge holds the row the same way, and also
holds the set against the Worker; see "A purge acts on an old scan, beside
writers that are not old".

**Restore follows the destination, not the disc label.** A directory backup's
disc record is labelled with the destination folder as it was when that run
happened, and restore found files only through that label. Nothing relabels
discs when the drive is followed to a new letter or the set is imported where the
drive has another letter (Change Destination relabels only discs matching the
path it changes from), so every version backed up before such a change failed to
restore with "Backup folder not found". `RestoreService.FindDiscRootAsync` keeps
the label when its folder exists and otherwise falls back to the set's current
destination, where the files sit at the same relative paths.

## "No tiers" means two opposite things, so it has one definition

A tier set with no tiers either **excludes** its files from backup (any set but
Default: the "None" set, or a custom set emptied) or **backs them up with no
history** (Default, the fallback for unmatched files: the engine's
`keepVersions` is false, so the current copy is overwritten in place). Every
check goes through `VersionTierSet.ExcludesFromBackup` / `KeepsNoHistory`; never
test `Tiers.Count == 0` alone. The per-file exclusion filters (engine
`BuildExclusionFilter`, editor `GetExcludeFilter`) did exactly that. They only
consult the resolver once some set excludes by pattern - but a "None" set with
patterns is the norm, and then an emptied Default excluded **every file no other
set matched**: on a real set, everything outside a list of text and code
extensions would have stopped being backed up, and Cleanup would have listed
their backups under "matches configured exclusion" for deletion. Pinned by
`tools\excess_versions_test` §7, which fails against the old check.

**Earlier versions of no-history files are excess, and only Cleanup removes
them.** Removing Default's tiers stops new versions, but the retention pass skips
a path whose tiers are empty, so the versions it already had would stay for good
(88 GB on one real set). Cleanup's Excess Versions now lists every earlier
version of a `KeepsNoHistory` file, newest included (the tiered rule "never
delete the newest earlier version" exists to keep a rollback, which a set that
keeps none has declined). Automatic retention deliberately still skips them: an
edit to one setting should not have the next backup delete tens of gigabytes
unreviewed. Excluded files' versions stay the exclusion category's.

**Cleanup must not delete a copy a `.fileref` needs.** A `.fileref` stores no
bytes; it resolves by hash to an active plain copy. The engine's retention pass
promotes a reference before deleting the last plain copy of its content; Cleanup's
purge copies nothing, so it holds such copies back instead, via
`ICatalogRepository.GetNeededContentCopiesAsync` (one per content, the newest;
none when the reference is being deleted too). It asks twice. At scan time,
against the Excess Versions list, so what is shown is what will go. And again in
the purge, **inside its catalog transaction** (which holds the set's write lock,
so no reference can appear in between), against everything being deleted in every
ticked category (`RecheckNeededCopiesAsync`): an excess copy can be listed because a
second plain copy survives, and that second copy can be in "deleted from disk",
ticked too; and a backup between scan and Clean can make a new `.fileref` to a
listed copy (a reverted file). Only excess versions can be held back. This
applied to tiered excess versions all along; making the newest earlier version
deletable made it likely, since that is the usual anchor.

**Overwriting in place retires the old record.** With no history the new copy
replaces the old one at the same path, and the old live record must be marked
deleted in the same batch (`replacedRecord` in `ExecuteCoreAsync`). It stayed
live, claiming the old content at a path that then held the new bytes: a
`.fileref` to the old content restored the new bytes (restore does not re-hash),
a later file with the old content became a reference to nothing, and the stale row
counted as a surviving copy in Cleanup's guard. Before the overwrite,
`KeepContentReachableAsync` gives the bytes to a reference that needs them
(promotes it, as retention does); if that fails the old copy is kept as an earlier
version rather than overwritten. A stored file left under another name when the
storage form changes (plain to `.fileref`, say) is deleted. This path used to run
only for an empty Default with no exclusion set, which is why nobody hit it.

**Directory pruning respects first-match order.** `BuildDirectoryPruneFilter`
skips a whole subtree for an excluding set's patterns, but tier sets are
first-match: a set that keeps history and comes earlier can claim files inside
(`x\tmp\a.docx` under `*.docx` ahead of `*\tmp\*`). Patterns of excluding sets are
collected only until the first non-Default set that keeps history; "None" is
always second, so the normal case prunes as before.

**Cleanup's later phases skip exactly the files other categories offer.** They
skipped every file under a removed or deleted-from-disk item's directory. A
deleted-from-disk item can be a folder that still exists with one file missing,
so its present files could show no excess version, catalog duplicate or manual
exclusion match (`AlreadyOfferedFilter`; every such item lists its paths).

**Tier edits are edits.** They live inside the tier sets - `TierSetViewModel.Tiers`
and each `RetentionTierViewModel` - where the editor's dirty flag never looked,
so adding or removing a tier or changing its numbers left Save disabled and the
close prompt silent (the edit was lost on closing). `TierSetViewModel.TiersEdited`
reports them, and the editor subscribes to every tier set it holds. A change in
the NUMBER of tiers also invalidates the cached exclusion filter, since it can
turn a set into an exclusion set. Pinned by `tools\editor_save_test` §16.

## Export and import show their progress, and stop when told

Both run in a `ProgressDialog` (modeless for export, which only reads; modal for
import, which adds a set) with the step, a bar and Cancel, and end in a message
saying where the file went (with an offer to show it in Explorer) or what was
imported. An export with history once ran for minutes with only a status-bar
line, and the file appears under its name only once complete, so a user could
not tell a running export from a failed one. The steps report at the
`ProgressCadence`:
- **copying the history**: `VACUUM INTO` is one statement, but it writes its
  file as it goes, to about the size of the pages it copies (96% of them in a
  measured run), so `BackupSetTransfer` polls the file's length against
  `GetSetHistorySizeBytes` — the pages in use, `(page_count - freelist_count) *
  page_size`, since free pages are not copied and the file length would
  overstate it. The copying connection carries an SQLite progress handler that
  returns non-zero once the token is cancelled, so a cancel stops the statement
  part-way with `SQLITE_INTERRUPT`, wherever in its run it lands (unlike
  `sqlite3_interrupt`, which is lost if it arrives before the statement
  starts); `history_transfer_test` §10 checks the stop is far quicker than the
  rest of the copy;
- **compressing / extracting**: a counted stream copy instead of
  `CreateEntryFromFile` / `ExtractToFile`;
- **importing the records**: until the first records go in (creating the set,
  waiting for the set's write lock, which a backup may hold, sizing the
  history) the step says "preparing" over a marquee; then the `INSERT ...
  SELECT` of files, chunks and dedup blocks goes 50,000 source row ids at a
  time, inside the one transaction, so the count moves and a cancel rolls the
  whole import back.

Each step ends with a report at 100%, so the line never shows a step
unfinished while the next is getting ready. Each export and import is logged
(start, end, size, duration, cancel).

While a set exports, its row is `IsExporting`, which disables only Delete and
a second Export of it: the export holds its database file open, so a delete
would leave the file behind. Everything else may go on, a backup included,
since `VACUUM INTO` reads a snapshot; the set is not held against the Worker
either. An export's temporary files are named per run (`<file>.<guid>.partial`
and `.history.partial`), so two exports to one file cannot delete each other's.

On a real exit, `MainWindow` calls `ProgressDialog.CancelRunningWork`, which
cancels whatever every progress dialog is running and waits up to 10 seconds
for it to unwind. A process that simply ends runs no `finally`: an export
would leave gigabytes of temporary files beside the export, and an import a
set that claims a history its rolled-back transaction never wrote.

## Opening a `.lithic` file reaches the one running window

The installer associates `.lithic` with `LithicBackup.exe "%1"` (component
`ExportFileAssociation`, non-advertised, so plain HKCR rows that uninstall
removes). The app is single-instance per session, so a double-click while it runs
starts a second process that must hand the file over and exit. It writes the
path to `%LOCALAPPDATA%\LithicBackup\open-requests\` (`OpenRequestQueue`: one file
per request, written under a temporary name and renamed so it is never read half
written) and then sets the existing "show window" event. The running app, on that
event or at the end of its own startup, takes every queued request and offers
each in turn (`MainViewModel.OfferOpenedFilesAsync`: what the file holds, then
Yes/No, then the ordinary import). A folder rather than a pipe because nothing
has to be listening at the moment of the hand-over - the running app may still be
on its splash screen - and requests opened together queue in order. Requests older
than ten minutes are dropped unread, so one left behind by a crash does not pop up
later. The GUI runs unelevated, so opening a file never prompts for elevation, and
the queue is per user, as the single-instance names are per session.

## A drive row's size is provisional until its children are sized

Drive rows take their size from `DriveInfo` (used space) so the tree shows
something at once. That figure comes with no file count. A drive is **never
walked on its own**, for its totals or for its filtered totals: the size
scheduler has a single worker, and a whole-drive walk - every drive in the
tree, the backup drive and network shares included - would hold it for minutes
at every open, ahead of the folders the user is looking at and of the plan
check. Instead, once every child the drive lists has a total, the drive takes
their sums (`TakeTotalsFromChildren`, run as each child's total lands and once
its children are listed), and the same for the filtered totals under the
scheduler's filter. While that is under way (the drive is expanded) its count
says "Working..."; while it is not, the count is blank and the size stays the
used space, with or without a filter. The sums describe what the tree lists.

**Calculate size** needs a fully selected drive's totals: an expanded one is
summed from its children, which it sizes; one that is not is walked
(`WalkInsteadOfSummingChildren`). A partly selected one is not needed itself,
only its children. Every size walk leaves out a volume root's own items
(`VolumeMetadataPaths`: the recycle bin, System Volume Information, the page,
hibernation and swap files), which the tree does not list and the backup never
copies, and re-reads a root's direct entries rather than trusting a cached
total. A walk that is cancelled (the editor closed mid-walk) returns and stores
nothing: a swallowed cancellation would leave partial sums cached as totals.

## Continuous backup and full scans interleave; they do not run concurrently

The Worker is **one sequential poll loop** — `CheckSchedules` → `CheckContinuous`
→ sleep — so `await RunFullBackupAsync(...)` parks every other kind of backup for
the scan's whole duration. Measured on the author's machine: full scans average
62–110 minutes and have reached **17.8 hours**, and across the twelve longest of
them exactly **zero** continuous backups fired.

Keep it sequential. Two runs on one set race on version numbering, disc
allocation and dedup refcounts, and the per-set `.writelock` prevents SQLite
corruption without preventing any of those. What a long run must not do is *stop
the world*, and the fix for that is a yield point, not a thread:

* **`ExecuteAsync` exposes a commit-boundary hook.** Its periodic commit is the
  one moment in a long run when the catalog is committed and no transaction,
  per-set gate or cross-process lock is held, so a caller can safely do its own
  catalog work there. The scan is *paused* at a safe point, never raced.
* **The hook must be told what the run owns.** `versionInfo` is resolved once,
  before the copy loop, so anything that writes one of the run's paths from
  inside the hook leaves it assigning a version computed from a stale snapshot.
  The handler receives `pathsThisRunWillWrite` and stays off them. A *different*
  set is fine — its own database, its own snapshot.
* **Hook the time-bounded commit only**, not the pre-large-file flush: a run that
  hits many large files in a row would otherwise call the handler in a tight loop.
* **Some work does not belong at a commit boundary at all.** Scheduling another
  scan, or rewriting the set's selection, is recorded and handed to the next poll.

**A long scan used to invalidate the journal it was running to repair.** Nothing
read the USN journal while a scan held the loop, and a 32 MB journal on a busy
volume does not survive hours — so the scan ended, the wrap was detected, and
another full scan was scheduled. The commit-boundary drain breaks that loop, and
the Worker now grows undersized journals to 512 MB (only ever growing: the journal
is shared with Windows Search and others, and shrinking destroys their history).

**"Did not run" and "ran but some files failed" are different answers.** Collapsing
them into a bool is what silently discarded the failed files' queued deltas, whose
journal records had already been consumed and whose cursor had already advanced.
Only a clean scan supersedes the queue. But the reconcile flag is cleared whenever
the scan *ran*, failures included — leaving it set starts another whole-tree scan
on the next poll, and one permanently unreadable file then loops forever. Failed
files are recovered by keeping their queued deltas, which costs one targeted retry.

**Continuous sets still need a periodic whole-tree scan.** They were given none,
on the reasoning that the journal reports everything. It does not: a plain
single-file delete is skipped by `ExecuteTargetedAsync`, a file that failed is
dropped after its retry, and a journal that silently stops advancing looks exactly
like "nothing changed". Time it from the last **full scan**, never from the last
run — every targeted continuous backup bumps the latter, so the busiest sets would
be the ones that never qualified.

## The Worker gives way to the app, set by set

The app never steps aside for the Worker on a set's write lock
(`YieldToWaitingWriters` is the Worker's alone: the work the user is watching
goes first). That is right for the lock, and it made a Worker run that wanted a
set the app was backing up wait for the whole of that backup. Because the loop is
sequential (above), so did everything else the Worker had to do. Measured: a
journal drain inside the i: set's full backup made a quick update of the j: set,
which waited for j:'s lock from 13:37 to 16:48 behind a backup in the app, and the
i: backup sat with it at zero CPU and zero I/O.

So the app says when it is working on a set, and the Worker does not ask for the
lock at all:

* **`HoldSetInApp`**, a marker beside the set's database like the cleanup one
  (refreshed every 5 s, ignored once 30 s stale), is held by `MainViewModel` while
  the set's row is *working* (`BackupSetRowViewModel.IsWorking`: running and not
  yet complete) and not paused. Not merely running - a finished backup's panel
  keeps the row running until it is dismissed, and with failed files on it nobody
  may dismiss it for days; the same predicate decides when Cleanup and the editor's
  post-save removal refuse a busy set. Not paused - a paused backup commits and
  gives the write lock up precisely so other writers can go on, and the Worker is
  one; resuming takes the set back and the Worker stops within a second. The
  review window before a backup is modal and inside the run, so the Worker waits
  while you review; you are about to back that set up anyway.
* **The Worker treats it as it treats a cleanup** (`AppWorkOn`): `StartSetRun`
  checks first without announcing anything, then announces the run and checks
  again, and returns null if the app has the set - the caller keeps the work
  queued, exactly as for a destination that is not connected, and the set's runs
  are not attempted again for two seconds (`WaitForApp`), so a held set costs
  the loop nothing: announcing and dropping a run at every pass made a marker
  each time, which woke the loop again.
  A run already going watches every second and cancels itself
  (`SetRun.StoppedForApp`); a full run returns DidNotRun, a targeted one requeues
  its paths, and neither counts as a failure with backoff. A journal drain inside
  another set's full scan therefore skips the held set and the scan carries on;
  and when the app takes the *scanning* set, the drain's targeted run of another
  set, linked to the scan's token, is cancelled with it and requeues its paths
  too (any cancellation does).
* Moves are announced but not stopped (quick renames over a queue cleared as they
  start); the app's backup waits on the lock for them, briefly.

Pinned by `tools\worker_idle_test` C8, on the real loop: held from the start,
nothing runs; taken mid-copy, the run stops (715 of 4,000 files) and copies nothing
more; let go, it finishes.

## The Worker's loop must cost nothing while it cannot act

The loop runs a pass at every poll and at every push wake-up, and the USN waiters
fire on any change to a watched volume, up to four times a second each. So
whatever a pass does, a busy machine does it several times a second. On
2026-10-18, with both backup drives unplugged, that came to 70 MB/s of reads and
35 MB/s of writes against the Worker's own catalog (known-issues.md). These rules
keep a pass cheap:

* **The sets are read only when they may have changed.**
  `ICatalogRepository.GetBackupSetsChangeStampAsync` moves after every write the
  repository makes to the sets, and whenever `PRAGMA data_version` moves, which
  reports every other connection's commits, the other process's included, but
  never the connection's own: hence the counter beside it. The Worker's reload
  compares the stamp before reading. It suits the Worker because the GUI writes
  the catalog rarely; it does not suit the GUI, because the Worker commits its
  journal cursors to the same database at nearly every pass, which moves every
  other connection's `data_version`. So the GUI's `DestinationSpaceMonitor`, which
  needs only each set's destination, reads `GetAllBackupSetOptionsAsync`: the
  options without the selection trees, as a type no whole-set writer takes. That
  saves parsing the trees, not reading them: the options column comes after the
  selection in the row, and SQLite reaches it only by paging through the
  selection's overflow pages. Keeping selections small is what keeps it cheap.
  SQLite does not rewrite a row whose bytes are unchanged, so writing a set back
  as it was moves no other connection's stamp, correctly.
* **SQLite never sorts the sets.** A row carries the set's selection tree, and
  `ORDER BY` sorts whole rows: past its cache, through a temp file. Measured: 9.2
  MB of rows cost 18.4 MB read and 9.2 MB written per read. `GetAllBackupSetsAsync`
  sorts in code.
* **A run that cannot start blocks the set until the next poll.** A destination
  that is not connected, no source available, a destination holding a backup the
  set does not track: each lasts until something outside the Worker changes, while
  the reconcile flag, an overdue schedule and queued changes all survive the
  attempt. `BlockRuns` holds the set's runs for one poll interval. A full backup
  that throws holds the set's *full* backups for 1, 2, 4 … minutes, up to an hour,
  but not its targeted runs, so continuous protection carries on while a
  safety-net scan keeps failing (a set whose *reconcile* is failing backs up
  nothing else meanwhile, as it never has before its reconcile runs). An edit to
  the set's options or selection ends that hold early: it may be the fix. A
  blocked set's only wake-up deadline is the end of its block (its overdue queue
  and its reconcile must not set the pace), and while every continuous set is
  blocked the loop ignores push wake-ups. Each condition is logged when it starts
  and when it ends, not at each attempt.
* **One full scan settles every reconcile pending when it starts.** The schedule's
  or the safety net's full backup runs first in a pass, and a set reconnected
  after a day usually has both that and a reconcile due: before, the second
  whole-tree scan began the moment the first ended (`SettleReconcile`). A clean
  scan clears only the changes and moves queued before it began. The drain queues
  more while it runs, and leaves some of them to the scan: its own paths, even when
  they change again after it copied them, and every move. Those stay queued.
* **An absent volume is not a volume without a journal.** "No usable journal" is
  cached only for a volume that is there, so a drive plugged in later is read
  through its journal. Treating an unplugged drive as a fallback volume flagged a
  reconcile at every service start and watched nothing.
* **Fallback watchers come and go one root at a time.**
  `FileSystemMonitorImpl.Watch` keeps the watchers of roots that stay, stops those
  of roots that are gone, and starts one for a root that exists and is not
  watched. Only the sets with a root that just started are flagged for a
  reconcile, since only there did no watcher see what changed; restarting every
  watcher whenever one root came or went cost every fallback set a full scan. It
  runs at every pass, so a watched root is looked at on disk only after its
  watcher reports an error, which is how a deleted root or a vanished volume shows.

### Auto-include must not pin what can never be backed up

`SourceSelection.MaterializeDirectory` pins each folder created under an
auto-include parent, building the folders between as *partial* nodes, and a
partial auto-include node pins every later folder created under it. So once a
build tree has been reached, every folder each build creates is pinned: cargo
makes new fingerprint, incremental and deps folders on every build. In the J: set
26,732 of 31,289 entries lay inside `debug` and `release` trees that the set's own
rules exclude; without them its selection JSON shrinks from 7.1 MB to 0.76 MB.

The Worker therefore pins nothing inside a tree its rules exclude entirely
(`IsPinCandidate`, through `SourceSelection.IsInsidePrunedDirectory`, the
bottom-up form of the prune filter, needed because the journal hands over deep
paths directly). It queues no changed file its rules exclude (`IsBackupCandidate`)
and no move with both ends inside such trees. And after each reload it removes the
entries already there, in the continuous sets it pins into, with
`SourceSelection.RemoveRedundantEntriesInExcludedFolders`.

**Removing entries must not lose a drive's watch root.** The Worker reads only
the drives its watch roots (`CollectSelectedRoots`) are on, and a partial folder
that auto-includes new ones used to give no root: a set whose only entries on a
drive were pins under such a folder would have stopped reading that drive once
they went. Such a folder is now a watch root of its own, which is also what a
fallback watcher needs to see the folders it would adopt. Checked on random
trees: every included path lies under a watch root, before and after removal.
Watch roots answer "where do I watch", not "where does this set write": the
untracked-destination probe (`FindUntrackedBackupAsync`, from the Worker and the
GUI) takes `CollectCoveredEntries`, since probing a whole partial folder would
find another set's files in a shared destination and refuse the set forever.

**The removal rule is what makes the removal safe, so keep it exact.** A node goes
only when its parent includes unlisted descendants and the node, and everything it
lists, include everything beneath them. Then no path's inclusion changes, even if
the exclusion rule is deleted later: what the node said, its parent still says.
Explicit exclusions, folders that back up only the children they list, the
children of parents that do not auto-include, and folders left expanded in the
editor are choices, and stay. Checked on
4,000 random trees and on both real sets (0 of 111,534 probes changed). The one
thing lost is what pinning exists for: if the parent's auto-include is turned off
later, those folders drop out with it, which costs nothing for folders whose files
are excluded anyway.

## A copy is recorded only once it is on the disk

A write is done once its bytes are in the Windows cache; the lazy writer puts them
on the disk seconds later. A drive lost in between takes them, and the rename that
promoted them, and a catalog that has recorded them says they are backed up when
they are not. Nothing re-copies them until the source changes. (The WD 6 TB USB
drive dropped off its bus 11 times in two days, each time mid-run; see
known-issues.md.) So whatever a commit records about a destination, it is on the
disk first:

* **The journal.** Each place that writes or renames something on a destination
  calls `DestinationWrites.Note(path)`: the promote of a copy, the move of the
  current copy into `_prev`, `.fileref`/`.dedup` manifests, dedup blocks, a
  materialised copy, the seed's moves into the history, a relocation's renames.
  The journal is an `AsyncLocal` that `DestinationWrites.Begin(destination)` sets
  for a run, a seed and a relocation, so concurrent runs keep their own and the
  write sites need no parameter. **A new place that writes to a destination must
  note it:** an unnoted write is committed without being flushed, and nothing
  says so. `durability_test` §7 checks that every record a run commits is of a
  file flushed before the commit.
* **Every commit makes the journal durable first** (`MakeDurableAsync`): the batch
  commits, the commit before a large file, the commit a pause makes, the final
  commit, and the commit a failing or cancelled run makes on its way out to save
  its progress. If the flush fails, the commit does not happen, the way-out one
  included (that is the case of the drive that has just gone), and what was noted
  stays noted, so a later attempt flushes it again. The next run copies the batch
  again.
* **A batch that rolls back leaves the disk ahead of the catalog,** and the next
  run must not be misled by what it left:
  * *Its moves into `_prev`.* The `_prev` move's "catalog is behind the drive"
    case files the record under its `.vN` when the current path holds another
    copy, as after a crash. When the current path holds nothing
    (`FollowMissingCurrentCopyAsync`), the record follows its copy to its `.vN`,
    or goes there all the same, so it never shares the current path with the new
    copy. (When another file has that `.vN` name, the record is retired.)
  * *Its new copies and blocks,* whose names may have survived without their
    data. The way-out path records exactly what it could not make durable, the
    journal's pending paths (`SuspectWrites`, beside the catalog, one file per
    destination, shared by the sets that back up there). A copy at a current
    path on that list is not recorded as a version of its own: it is deleted,
    and the source copied afresh. A block on it is written again when next
    needed, rather than taken as stored. Rewriting goes to a temporary file,
    flushed, then renamed over the block, since committed manifests may already
    resolve to it. A path leaves the list once a run has written it again and
    made that durable. Runs read the list when they start and write back their
    changes when they end, merged under an exclusive open.
  * *A reference given new bytes.* When a file that keeps no history is
    overwritten in place, a `.fileref` resolving to its old bytes is made a
    plain copy first. That change is committed before the overwrite, and only
    then is the reference's manifest deleted. A batch rolled back later cannot
    make it a reference again with nothing to resolve to.
* **Some writes are made durable at once, not at the commit:** a write that
  something irreversible follows. A `.fileref` given the bytes of the last
  plain copy of its content, before that copy is deleted or overwritten
  (`Promotion`), has its new copy flushed first
  (`DestinationWrites.MakeDurableNowAsync`, in `FinishPromotionsAsync`). If
  that fails, the old bytes are kept and the new copy is taken back. A new path
  that copies bytes and then deletes their source must do the same. Retention
  makes its promotions' copies as it goes, and holds back the expired copies
  they come from, along with any other expired copy of the same content. It
  finishes them in batches of up to 256, or 1 GB of copies: one flush, then
  the records, then the deletions. A no-history overwrite does its one
  promotion at once.
* **The seed and the moves flush before they take the set's write lock:** what
  they record was renamed before the transaction, so the lock is held for the
  inserts only. A run's batches flush under the lock, which they hold
  throughout anyway.
* **The flush** (`DestinationFlush`). The volume each written path is on is found
  from the folder it was written in, by handle (`DurableFlush.VolumeOf`,
  `GetFinalPathNameByHandle`, as `\\?\Volume{GUID}\`). A drive letter would
  mislead: it says nothing through a junction or a mount point, and Windows will
  not resolve a subst drive's at all. Each volume is flushed whole with one
  `FlushFileBuffers` on the volume, one flush of the device's cache, whatever the
  batch held. That needs an elevated process. The Worker is one (LocalSystem) and
  flushes for itself. For the app, which usually is not, the Worker flushes on
  request (`VolumeFlushPipe`, `\\.\pipe\LithicBackup.FlushVolume`, served by
  `VolumeFlushServer`). The request is the volume's name, never a path: the
  service's logon session does not see the user's subst and network drives, and
  the server refuses anything that is not a volume name. Its flushes run one at
  a time and are shared: requests that arrive while a volume is being flushed
  wait for the next flush, which covers them all. So a crowd of connections
  costs at most two flushes of a volume, and one thread. What no volume flush
  covers is flushed file by file, one device flush each: a network share, a
  file system without volume names, or no service answering (which is then not
  asked again for a minute). This is done after checking that the destination
  is still there, and a copy that is gone with its folder fails the flush. A
  folder that will not flush (a renamed one, on a file system that refuses)
  fails it too. So does a file that another process keeps open without sharing
  writes, such as a preview handler or a scanner. It is retried for most of a
  second first: until it is let go, it cannot be opened to be flushed.
* **A drive that comes back.** A USB drive that drops off and comes back under
  the same letter has lost what was in the cache, yet its paths work again, and a
  flush then succeeds, flushing none of what was lost. So a run holds the root
  of every volume it writes to (`VolumeWatch`; a root cannot be deleted): the
  destination's from `Begin` on, and another reached through a junction from
  the run's first write there. When the root may not be opened (an ACL that
  admits users to a folder only), the folder written in is held instead. So it
  is for a destination on a share, which has no volume name. A commit, after its flush, first checks that
  each handle still works: a handle from before the drop never works again. A
  watch whose volume went is renewed at the first write after a durable point,
  when the loss cost nothing. One that cannot be held then fails the commit,
  which cannot tell what the volume lost. Untested: it takes pulling the drive.
* **Batches are larger, because each commit costs a flush:** 500 files, a file of
  100 MB or more, or 30 s (they were 50, 10 MB and 30 s). The flush writes out
  synchronously whatever the lazy writer has not yet written. Over a longer batch
  the lazy writer has written most of it in the background, so the flush is a
  smaller share of the run. A longer batch holds the set's write lock longer, so
  the Worker now lets a waiting writer in between files (see "Writes to a set take
  turns, and the Worker gives way").

`durability_test` covers the pieces and the runs:
- subst drives, junctions and read-only files;
- the pipe against a server in its own process, and its shared flushes;
- a run, a seed, a folder move, retention's promotions and a no-history
  overwrite, each with a flush that records what it is given or fails;
- what the next run makes of a batch that rolled back: a missing current copy,
  a copy and blocks left behind.

`DirectoryBackupService.CommitBatchSize` is public so harnesses can reach a
commit boundary.

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
* **A cancel reaches a catalog read part-way.** `SqliteSetDatabase` reads rows
  through `Rows(cmd, ct)`, which checks the token before each row and, while
  the read runs, watches the connection (`SqliteCancellation`): an SQLite
  progress handler that aborts the statement once the token is cancelled. The
  watch matters as much as the check: a read ordered by source path sorts the
  whole set before its first row (`USE TEMP B-TREE FOR ORDER BY`), which no
  check between rows reaches. A new read with a token in hand uses it. **Reads
  only:** a write aborted inside a transaction rolls the whole transaction back
  and leaves the connection in autocommit; `Rows` asserts a SELECT. The export's
  `VACUUM INTO` is watched too, safely, on a connection of its own.
* **A task window's flow stops with the window.** `TaskWindowManager` disposes
  a flow that is `IDisposable` when its window closes, however it was closed;
  Cleanup, Verify, Coverage, Largest Files and Find File cancel their work
  there. A purge, a write the user confirmed, carries on, on its own token.
  Deleting a set closes its task windows first (`CloseForSet`), and does not
  go ahead if one is kept open.
* **Await catalog calls; don't block on them.** Work under a progress window
  awaits (`ProgressDialog.RunAsync` takes async work). The UI thread may await
  a set's catalog call directly: every way into a set's gate
  (`SqliteSetDatabase.LockAsync`, `WriteLockAsync`) first hops off a thread
  with a SynchronizationContext, since the catalog's async methods do their
  SQLite work before they first truly await, and a read of millions of rows
  ran on the UI thread. Awaiting inside a catalog transaction is safe: its
  owner is an `AsyncLocal` flow token, which survives an await or a hop, and
  its locks are not thread-bound. Only saving at exit blocks, because the
  process will not wait for an async save.
* **A deleted set stays deleted for the session.** `DeleteBackupSetAsync`
  marks the set deleted, and only after releasing the master gate closes its
  database. `SqliteSetDatabase.CloseAsync` waits for whatever holds the set's
  gate, so a read under way finishes first. A call that reaches the set
  afterwards gets `BackupSetDeletedException`; neither a late caller nor a
  reader holding an old reference re-creates the database. The search across
  sets skips a set deleted under it.
* **"Everything under a folder" is a range on the NOCASE path index, bounded
  exactly: `'P\' <= path < 'P]'`.** `]` is the character after `\` in NOCASE
  order (which folds only ASCII letters), so the range holds the paths under P
  whatever they contain; no sentinel "above every character" exists, since a
  file name can hold U+10FFFF. The restore browser's skip-scan moves its
  inclusive cursor to `P]` past a folder and just past a file, and throws if
  it ever stops advancing. These queries name their index (`INDEXED BY`): a
  planner that cannot use it fails loudly instead of reading every file of the
  set per folder, as a join, an OR or a stray `ORDER BY` once led it to do.
  `tools\path_range_test` holds them to one meaning and checks the cost.

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
