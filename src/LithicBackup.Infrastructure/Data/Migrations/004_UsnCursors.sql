-- LithicBackup catalog schema v4: NTFS USN change-journal cursors.
-- Persists, per volume, the journal identity and the next USN to read from,
-- so continuous backup resumes exactly where it left off across restarts and
-- captures changes that occurred while the worker was offline.

CREATE TABLE IF NOT EXISTS UsnCursors (
    VolumeId    TEXT    NOT NULL PRIMARY KEY,  -- volume root, e.g. "C:\"
    JournalId   INTEGER NOT NULL,              -- USN journal identity (detects re-creation)
    NextUsn     INTEGER NOT NULL,              -- next USN to read from
    UpdatedUtc  TEXT    NOT NULL
);

UPDATE SchemaVersion SET Version = 4;
