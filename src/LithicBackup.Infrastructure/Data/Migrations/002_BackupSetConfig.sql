-- Migration 002: Store full source selections and job options on BackupSets.

ALTER TABLE BackupSets ADD COLUMN SourceSelectionJson TEXT;
ALTER TABLE BackupSets ADD COLUMN JobOptionsJson TEXT;

UPDATE SchemaVersion SET Version = 2 WHERE Version = 1;
