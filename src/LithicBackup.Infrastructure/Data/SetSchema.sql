-- Per-set catalog database schema (sets/set-{id}.db).
--
-- Holds the disc/file/chunk/dedup-block records for ONE backup set.  See
-- migration 005 for the rationale behind the master + per-set split.
--
-- Disc IDs are allocated globally by the master database (DiscOwners) and then
-- inserted here with an explicit value, so Discs.Id is a plain
-- INTEGER PRIMARY KEY (NOT AUTOINCREMENT).  File, chunk, and block IDs are
-- local to this database and use AUTOINCREMENT as before.
--
-- BackupSetId is a denormalised plain column (no foreign key) because the
-- BackupSets table lives in the separate master database file.
--
-- DeduplicationBlocks.DiscId is a plain column (no foreign key).  Directory
-- backups register blocks with DiscId = 0 (there is no disc), and the block
-- table is only a dedup-decision index — the authoritative block store is the
-- on-disk _blocks/ directory, and restore reads blocks from there, never from
-- this table.

-- Everything in this script must be a no-op on an existing database that does
-- not need SQLite's write lock: it runs every time a process opens the set, and
-- the other process may be holding that lock for a long run of commits. Every
-- CREATE ... IF NOT EXISTS qualifies. The SchemaVersion row is inserted by the
-- SqliteSetDatabase constructor, and only when it is missing - an INSERT needs
-- the write lock even when it inserts nothing, and used to make opening a set
-- wait on the Worker (and fail after the 30 s busy timeout).
CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version INTEGER NOT NULL PRIMARY KEY
);

CREATE TABLE IF NOT EXISTS Discs (
    Id                  INTEGER PRIMARY KEY,
    BackupSetId         INTEGER NOT NULL,
    Label               TEXT    NOT NULL,
    SequenceNumber      INTEGER NOT NULL,
    MediaType           INTEGER NOT NULL,
    FilesystemType      INTEGER NOT NULL,
    Capacity            INTEGER NOT NULL,
    BytesUsed           INTEGER NOT NULL DEFAULT 0,
    RewriteCount        INTEGER NOT NULL DEFAULT 0,
    IsMultisession      INTEGER NOT NULL DEFAULT 0,
    IsBad               INTEGER NOT NULL DEFAULT 0,
    Status              INTEGER NOT NULL DEFAULT 0,
    CreatedUtc          TEXT    NOT NULL,
    LastWrittenUtc      TEXT
);

CREATE TABLE IF NOT EXISTS Files (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    DiscId              INTEGER NOT NULL REFERENCES Discs(Id),
    SourcePath          TEXT    NOT NULL,
    DiscPath            TEXT    NOT NULL,
    SizeBytes           INTEGER NOT NULL,
    Hash                TEXT    NOT NULL,
    IsZipped            INTEGER NOT NULL DEFAULT 0,
    IsSplit             INTEGER NOT NULL DEFAULT 0,
    IsDeduped           INTEGER NOT NULL DEFAULT 0,
    IsFileRef           INTEGER NOT NULL DEFAULT 0,
    Version             INTEGER NOT NULL DEFAULT 1,
    IsDeleted           INTEGER NOT NULL DEFAULT 0,
    SourceLastWriteUtc  TEXT    NOT NULL,
    BackedUpUtc         TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS FileChunks (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    FileRecordId        INTEGER NOT NULL REFERENCES Files(Id),
    DiscId              INTEGER NOT NULL REFERENCES Discs(Id),
    Sequence            INTEGER NOT NULL,
    Offset              INTEGER NOT NULL,
    Length              INTEGER NOT NULL,
    DiscFilename        TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS DeduplicationBlocks (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    Hash                TEXT    NOT NULL UNIQUE,
    SizeBytes           INTEGER NOT NULL,
    ReferenceCount      INTEGER NOT NULL DEFAULT 1,
    DiscId              INTEGER NOT NULL DEFAULT 0
);

-- Discs lookups by owning set.  The master schema (001_InitialSchema) has always
-- indexed this column; the index was simply never carried across when the catalog
-- was split into master + per-set databases, so every `WHERE BackupSetId = ?`
-- against a set database fell back to a full table scan.  On a real 66,470-disc
-- set that is ~10 MB re-read per query, and the purge issued one such query PER
-- SOURCE PATH — see the "Cleanup purge scanned the Discs table once per path"
-- entry in known-issues.md.
CREATE INDEX IF NOT EXISTS IX_Discs_BackupSetId ON Discs(BackupSetId);

CREATE INDEX IF NOT EXISTS IX_Files_DiscId ON Files(DiscId);
CREATE INDEX IF NOT EXISTS IX_Files_SourcePath ON Files(SourcePath);
CREATE INDEX IF NOT EXISTS IX_FileChunks_FileRecordId ON FileChunks(FileRecordId);
CREATE INDEX IF NOT EXISTS IX_DeduplicationBlocks_Hash ON DeduplicationBlocks(Hash);
CREATE INDEX IF NOT EXISTS IX_Files_Active_Disc_Path
    ON Files(DiscId, SourcePath)
    WHERE IsDeleted = 0;

-- Resolve file-level dedup references by content hash: a .fileref's content is
-- found by locating an active plain copy with the same Hash. Indexed so restore
-- and retention's last-plain-copy guard don't full-scan the file table.
CREATE INDEX IF NOT EXISTS IX_Files_Active_Hash
    ON Files(Hash)
    WHERE IsDeleted = 0;

-- Case-insensitive active-path index for the lazy restore browser.  The restore
-- tree lists a directory's *direct* children on expand via a loose-index
-- skip-scan (seek to the first path under the prefix, emit the child, then jump
-- the cursor past that child's whole subtree, repeat) so expanding a node costs
-- O(direct children) index seeks, not O(subtree).  All path matching here is
-- COLLATE NOCASE (the Windows filesystem is case-insensitive), so the seek/range
-- (SourcePath > cursor / < upper) can only use an index whose key collation is
-- NOCASE — the plain IX_Files_SourcePath is BINARY and would force a full scan.
-- Partial (IsDeleted = 0) because the browser only ever shows live files, so the
-- index stays small and its range scans skip tombstoned history for free.
CREATE INDEX IF NOT EXISTS IX_Files_Active_SourcePath_NoCase
    ON Files(SourcePath COLLATE NOCASE)
    WHERE IsDeleted = 0;

-- Case-insensitive path index over ALL rows, including tombstones.
--
-- The partial index above cannot serve version-history lookups, because those
-- must see deleted rows: GetFileRecordByPathAndVersionAsync (used to revive a
-- tombstoned record and to fetch the previous version when writing a new one),
-- GetFileRecordsByPathAsync and GetFileRecordsUnderDirectoryAsync all match on
-- `SourcePath = ? COLLATE NOCASE` WITHOUT an `IsDeleted = 0` predicate, so
-- SQLite may not use a `WHERE IsDeleted = 0` partial index, and the plain
-- IX_Files_SourcePath is BINARY collation so it can't satisfy a NOCASE compare
-- either. The result was a FULL TABLE SCAN per lookup — and the continuous
-- backup path runs up to three of them PER CHANGED FILE, PER SET.
--
-- Measured on a real 2.35M-row / 2 GB set database: the three lookups cost
-- ~3,100 ms combined before this index and ~0.3 ms after (~9,600x), which is
-- what pinned a core and drove ~128 MB/s of pure reads with zero writes in the
-- Worker service. The index costs ~275 MB on that database and ~11 s to build
-- once. See the "Worker pegged a CPU core" entry in known-issues.md.
CREATE INDEX IF NOT EXISTS IX_Files_SourcePath_NoCase
    ON Files(SourcePath COLLATE NOCASE);

-- Whole-file-duplicate size pre-check (DirectoryBackupService step 5c): "does any
-- active plain copy in this set already have byte size N?".  Only files whose size
-- collides with stored content can be whole-file duplicates, so this answers, per
-- candidate file, whether the file must be hashed up front or can take the
-- single-pass hash-while-copy path.
--
-- The predicate is exactly the "active plain content" definition, so the index is
-- partial on all five flags.  That keeps it small (only live plain copies, not
-- tombstones / .fileref / .dedup rows) and lets a size probe seek straight to the
-- matching rows with all five flags already satisfied.  DiscId rides along as a
-- second column so the Files->Discs join reads no table row; only the residual
-- `Hash <> ''` check touches the table, and only for the first candidate row
-- (each probe is LIMIT 1).
--
-- Measured on the real 2.35M-row / 2.3 GB set database: the previous
-- GetActivePlainContentSizesAsync materialised a DISTINCT of every active plain
-- size (252,289 of them) via a full index SCAN plus a temp B-tree, costing
-- ~6.6 s — on EVERY backup pass, to answer a question about the 1-3 sizes
-- actually being backed up.  Probing just those sizes against this index is an
-- indexed seek each.  See the "Worker pegged a CPU core" entry in known-issues.md.
CREATE INDEX IF NOT EXISTS IX_Files_ActivePlain_Size
    ON Files(SizeBytes, DiscId)
    WHERE IsDeleted = 0
      AND IsFileRef = 0
      AND IsDeduped = 0
      AND IsSplit = 0
      AND IsZipped = 0;
