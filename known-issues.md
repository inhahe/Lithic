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
