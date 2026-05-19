-- Migration 003: Covering index for the catalog version-info query.
-- The GROUP BY SourcePath in GetLatestVersionInfoAsync is the most
-- expensive query at modify-window load time.  This partial index
-- covers the JOIN filter (DiscId), the WHERE filter (IsDeleted = 0),
-- and the GROUP BY column (SourcePath) in a single B-tree scan.

CREATE INDEX IF NOT EXISTS IX_Files_Active_Disc_Path
    ON Files(DiscId, SourcePath)
    WHERE IsDeleted = 0;

UPDATE SchemaVersion SET Version = 3;
