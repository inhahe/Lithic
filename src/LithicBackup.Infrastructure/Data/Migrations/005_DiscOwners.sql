-- Migration 005: per-set database split.
--
-- The catalog was historically a single SQLite database holding every set's
-- discs, files, chunks, and dedup blocks.  A single shared connection plus a
-- write transaction held open across a backup's whole copy loop meant two
-- backup sets could not run concurrently without serialising on (or corrupting)
-- that one database.
--
-- The split moves each set's disc/file/chunk/block records into its own
-- database file (sets/set-{id}.db), so concurrent backups of different sets
-- touch independent files and independent SQLite write locks.  The master
-- database (this file) keeps only the cross-cutting tables: BackupSets,
-- UsnCursors, and DiscOwners.
--
-- DiscOwners is both the allocator of globally-unique disc IDs (via its
-- AUTOINCREMENT rowid) and the routing index that maps a disc ID back to the
-- set that owns it.  Globally-unique disc IDs let the repository route every
-- disc-keyed call (GetDisc, GetFilesOnDisc, CreateFileRecord, ...) to the right
-- per-set database without changing those method signatures.
CREATE TABLE IF NOT EXISTS DiscOwners (
    DiscId  INTEGER PRIMARY KEY AUTOINCREMENT,
    SetId   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_DiscOwners_SetId ON DiscOwners(SetId);

UPDATE SchemaVersion SET Version = 5;
