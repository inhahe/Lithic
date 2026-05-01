-- LithicBackup catalog schema v1

CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version     INTEGER NOT NULL PRIMARY KEY
);
INSERT INTO SchemaVersion (Version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);

CREATE TABLE IF NOT EXISTS BackupSets (
    Id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name                    TEXT    NOT NULL,
    SourceRoots             TEXT    NOT NULL,   -- JSON array of paths
    MaxIncrementalDiscs     INTEGER NOT NULL DEFAULT 5,
    DefaultMediaType        INTEGER NOT NULL DEFAULT 0,
    DefaultFilesystemType   INTEGER NOT NULL DEFAULT 2,  -- UDF
    CapacityOverrideBytes   INTEGER,
    CreatedUtc              TEXT    NOT NULL,
    LastBackupUtc           TEXT
);

CREATE TABLE IF NOT EXISTS Discs (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    BackupSetId         INTEGER NOT NULL REFERENCES BackupSets(Id),
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
    DiscId              INTEGER NOT NULL REFERENCES Discs(Id)
);

-- Indexes for common queries
CREATE INDEX IF NOT EXISTS IX_Discs_BackupSetId ON Discs(BackupSetId);
CREATE INDEX IF NOT EXISTS IX_Files_DiscId ON Files(DiscId);
CREATE INDEX IF NOT EXISTS IX_Files_SourcePath ON Files(SourcePath);
CREATE INDEX IF NOT EXISTS IX_FileChunks_FileRecordId ON FileChunks(FileRecordId);
CREATE INDEX IF NOT EXISTS IX_DeduplicationBlocks_Hash ON DeduplicationBlocks(Hash);
