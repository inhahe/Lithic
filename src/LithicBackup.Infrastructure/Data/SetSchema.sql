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

CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version INTEGER NOT NULL PRIMARY KEY
);
INSERT INTO SchemaVersion (Version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);

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
