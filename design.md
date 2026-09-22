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

**The same illusion came back in the purge**, which is why this is stated as an
invariant and not a one-off fix. `PurgeSelected` counted *work items*
(directories) while the cost is per *catalog row*; one directory carrying
hundreds of thousands of paths pinned the display at `97,059/97,187` for two days
with no way to show movement inside it. The denominator is now rows and the
counter advances inside the per-path work (chunked at `PurgePathChunkSize`), not
only between items. When adding a phase, ask what the **unit of cost** is and
count that — the unit of iteration usually is not it.

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
`CollectSelectedRoots`/`CollectCoveredEntries` only collect **fully**-selected
nodes, while `IsPathIncluded` additionally covers unlisted descendants of a
**partially**-selected directory whose auto-include-new is on. That can make a
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
