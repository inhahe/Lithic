using System.Text.Json;
using Microsoft.Data.Sqlite;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Data;

/// <summary>
/// SQLite implementation of <see cref="ICatalogRepository"/>.
/// </summary>
public class SqliteCatalogRepository : ICatalogRepository
{
    private readonly SqliteConnection _connection;

    public SqliteCatalogRepository(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        // Enable WAL mode for better concurrent read performance and
        // foreign keys for referential integrity.
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA foreign_keys=ON");

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        var assembly = typeof(SqliteCatalogRepository).Assembly;

        // Apply initial schema (idempotent — uses IF NOT EXISTS).
        ApplyMigration(assembly, "LithicBackup.Infrastructure.Data.Migrations.001_InitialSchema.sql");

        // Check current schema version.
        int currentVersion = 0;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1";
            try { currentVersion = Convert.ToInt32(cmd.ExecuteScalar()); }
            catch { /* Table may not exist on first run before 001 applies. */ }
        }

        // Apply numbered migrations in order.
        var migrations = new (int Version, string ResourceName)[]
        {
            (2, "LithicBackup.Infrastructure.Data.Migrations.002_BackupSetConfig.sql"),
        };

        foreach (var (version, resourceName) in migrations)
        {
            if (currentVersion >= version)
                continue;
            ApplyMigration(assembly, resourceName);
        }
    }

    private void ApplyMigration(System.Reflection.Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return;
        using var reader = new StreamReader(stream);
        Execute(reader.ReadToEnd());
    }

    // ---------------------------------------------------------------
    // Backup Sets
    // ---------------------------------------------------------------

    public Task<BackupSet> CreateBackupSetAsync(BackupSet set, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BackupSets
                (Name, SourceRoots, MaxIncrementalDiscs, DefaultMediaType,
                 DefaultFilesystemType, CapacityOverrideBytes, CreatedUtc, LastBackupUtc,
                 SourceSelectionJson, JobOptionsJson)
            VALUES
                ($name, $roots, $maxDiscs, $mediaType,
                 $fsType, $capOverride, $created, $lastBackup,
                 $selJson, $jobJson);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", set.Name);
        cmd.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(set.SourceRoots));
        cmd.Parameters.AddWithValue("$maxDiscs", set.MaxIncrementalDiscs);
        cmd.Parameters.AddWithValue("$mediaType", (int)set.DefaultMediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)set.DefaultFilesystemType);
        cmd.Parameters.AddWithValue("$capOverride", (object?)set.CapacityOverrideBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", set.CreatedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$lastBackup",
            set.LastBackupUtc.HasValue ? set.LastBackupUtc.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$selJson",
            set.SourceSelections is not null
                ? JsonSerializer.Serialize(set.SourceSelections) : DBNull.Value);
        cmd.Parameters.AddWithValue("$jobJson",
            set.JobOptions is not null
                ? JsonSerializer.Serialize(set.JobOptions) : DBNull.Value);

        set.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return Task.FromResult(set);
    }

    public Task<BackupSet?> GetBackupSetAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM BackupSets WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return Task.FromResult<BackupSet?>(null);

        return Task.FromResult<BackupSet?>(ReadBackupSet(r));
    }

    public Task<IReadOnlyList<BackupSet>> GetAllBackupSetsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM BackupSets ORDER BY Name";

        var list = new List<BackupSet>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadBackupSet(r));

        return Task.FromResult<IReadOnlyList<BackupSet>>(list);
    }

    public Task UpdateBackupSetAsync(BackupSet set, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE BackupSets SET
                Name = $name,
                SourceRoots = $roots,
                MaxIncrementalDiscs = $maxDiscs,
                DefaultMediaType = $mediaType,
                DefaultFilesystemType = $fsType,
                CapacityOverrideBytes = $capOverride,
                LastBackupUtc = $lastBackup,
                SourceSelectionJson = $selJson,
                JobOptionsJson = $jobJson
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", set.Id);
        cmd.Parameters.AddWithValue("$name", set.Name);
        cmd.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(set.SourceRoots));
        cmd.Parameters.AddWithValue("$maxDiscs", set.MaxIncrementalDiscs);
        cmd.Parameters.AddWithValue("$mediaType", (int)set.DefaultMediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)set.DefaultFilesystemType);
        cmd.Parameters.AddWithValue("$capOverride", (object?)set.CapacityOverrideBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastBackup",
            set.LastBackupUtc.HasValue ? set.LastBackupUtc.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$selJson",
            set.SourceSelections is not null
                ? JsonSerializer.Serialize(set.SourceSelections) : DBNull.Value);
        cmd.Parameters.AddWithValue("$jobJson",
            set.JobOptions is not null
                ? JsonSerializer.Serialize(set.JobOptions) : DBNull.Value);

        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static BackupSet ReadBackupSet(SqliteDataReader r)
    {
        var set = new BackupSet
        {
            Id = r.GetInt32(r.GetOrdinal("Id")),
            Name = r.GetString(r.GetOrdinal("Name")),
            SourceRoots = JsonSerializer.Deserialize<List<string>>(
                r.GetString(r.GetOrdinal("SourceRoots"))) ?? [],
            MaxIncrementalDiscs = r.GetInt32(r.GetOrdinal("MaxIncrementalDiscs")),
            DefaultMediaType = (MediaType)r.GetInt32(r.GetOrdinal("DefaultMediaType")),
            DefaultFilesystemType = (FilesystemType)r.GetInt32(r.GetOrdinal("DefaultFilesystemType")),
            CapacityOverrideBytes = r.IsDBNull(r.GetOrdinal("CapacityOverrideBytes"))
                ? null : r.GetInt64(r.GetOrdinal("CapacityOverrideBytes")),
            CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc"))),
            LastBackupUtc = r.IsDBNull(r.GetOrdinal("LastBackupUtc"))
                ? null : DateTime.Parse(r.GetString(r.GetOrdinal("LastBackupUtc"))),
        };

        // New columns — may not exist in legacy databases before migration 002.
        try
        {
            int selOrd = r.GetOrdinal("SourceSelectionJson");
            if (!r.IsDBNull(selOrd))
                set.SourceSelections = JsonSerializer.Deserialize<List<SourceSelection>>(
                    r.GetString(selOrd));
        }
        catch (IndexOutOfRangeException) { }

        try
        {
            int jobOrd = r.GetOrdinal("JobOptionsJson");
            if (!r.IsDBNull(jobOrd))
                set.JobOptions = JsonSerializer.Deserialize<JobOptions>(
                    r.GetString(jobOrd));
        }
        catch (IndexOutOfRangeException) { }

        return set;
    }

    // ---------------------------------------------------------------
    // Discs
    // ---------------------------------------------------------------

    public Task<DiscRecord> CreateDiscAsync(DiscRecord disc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Discs
                (BackupSetId, Label, SequenceNumber, MediaType, FilesystemType,
                 Capacity, BytesUsed, RewriteCount, IsMultisession, IsBad, Status,
                 CreatedUtc, LastWrittenUtc)
            VALUES
                ($setId, $label, $seq, $mediaType, $fsType,
                 $capacity, $used, $rewrites, $multi, $isBad, $status,
                 $created, $lastWritten);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$setId", disc.BackupSetId);
        cmd.Parameters.AddWithValue("$label", disc.Label);
        cmd.Parameters.AddWithValue("$seq", disc.SequenceNumber);
        cmd.Parameters.AddWithValue("$mediaType", (int)disc.MediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)disc.FilesystemType);
        cmd.Parameters.AddWithValue("$capacity", disc.Capacity);
        cmd.Parameters.AddWithValue("$used", disc.BytesUsed);
        cmd.Parameters.AddWithValue("$rewrites", disc.RewriteCount);
        cmd.Parameters.AddWithValue("$multi", disc.IsMultisession ? 1 : 0);
        cmd.Parameters.AddWithValue("$isBad", disc.IsBad ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (int)disc.Status);
        cmd.Parameters.AddWithValue("$created", disc.CreatedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$lastWritten",
            disc.LastWrittenUtc.HasValue ? disc.LastWrittenUtc.Value.ToString("o") : DBNull.Value);

        disc.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return Task.FromResult(disc);
    }

    public Task<DiscRecord?> GetDiscAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Discs WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return Task.FromResult<DiscRecord?>(null);

        return Task.FromResult<DiscRecord?>(ReadDisc(r));
    }

    public Task<IReadOnlyList<DiscRecord>> GetDiscsForBackupSetAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Discs WHERE BackupSetId = $setId ORDER BY SequenceNumber";
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var list = new List<DiscRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadDisc(r));

        return Task.FromResult<IReadOnlyList<DiscRecord>>(list);
    }

    public Task UpdateDiscAsync(DiscRecord disc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Discs SET
                Label = $label,
                SequenceNumber = $seq,
                MediaType = $mediaType,
                FilesystemType = $fsType,
                Capacity = $capacity,
                BytesUsed = $used,
                RewriteCount = $rewrites,
                IsMultisession = $multi,
                IsBad = $isBad,
                Status = $status,
                LastWrittenUtc = $lastWritten
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", disc.Id);
        cmd.Parameters.AddWithValue("$label", disc.Label);
        cmd.Parameters.AddWithValue("$seq", disc.SequenceNumber);
        cmd.Parameters.AddWithValue("$mediaType", (int)disc.MediaType);
        cmd.Parameters.AddWithValue("$fsType", (int)disc.FilesystemType);
        cmd.Parameters.AddWithValue("$capacity", disc.Capacity);
        cmd.Parameters.AddWithValue("$used", disc.BytesUsed);
        cmd.Parameters.AddWithValue("$rewrites", disc.RewriteCount);
        cmd.Parameters.AddWithValue("$multi", disc.IsMultisession ? 1 : 0);
        cmd.Parameters.AddWithValue("$isBad", disc.IsBad ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (int)disc.Status);
        cmd.Parameters.AddWithValue("$lastWritten",
            disc.LastWrittenUtc.HasValue ? disc.LastWrittenUtc.Value.ToString("o") : DBNull.Value);

        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<int> GetIncrementalDiscCountAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Discs WHERE BackupSetId = $setId";
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
    }

    private static DiscRecord ReadDisc(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("Id")),
        BackupSetId = r.GetInt32(r.GetOrdinal("BackupSetId")),
        Label = r.GetString(r.GetOrdinal("Label")),
        SequenceNumber = r.GetInt32(r.GetOrdinal("SequenceNumber")),
        MediaType = (MediaType)r.GetInt32(r.GetOrdinal("MediaType")),
        FilesystemType = (FilesystemType)r.GetInt32(r.GetOrdinal("FilesystemType")),
        Capacity = r.GetInt64(r.GetOrdinal("Capacity")),
        BytesUsed = r.GetInt64(r.GetOrdinal("BytesUsed")),
        RewriteCount = r.GetInt32(r.GetOrdinal("RewriteCount")),
        IsMultisession = r.GetInt32(r.GetOrdinal("IsMultisession")) != 0,
        IsBad = r.GetInt32(r.GetOrdinal("IsBad")) != 0,
        Status = (BurnSessionStatus)r.GetInt32(r.GetOrdinal("Status")),
        CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedUtc"))),
        LastWrittenUtc = r.IsDBNull(r.GetOrdinal("LastWrittenUtc"))
            ? null : DateTime.Parse(r.GetString(r.GetOrdinal("LastWrittenUtc"))),
    };

    // ---------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------

    public Task<FileRecord> CreateFileRecordAsync(FileRecord file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Files
                (DiscId, SourcePath, DiscPath, SizeBytes, Hash,
                 IsZipped, IsSplit, IsDeduped, IsFileRef, Version, IsDeleted,
                 SourceLastWriteUtc, BackedUpUtc)
            VALUES
                ($discId, $srcPath, $discPath, $size, $hash,
                 $zipped, $split, $deduped, $fileRef, $version, $deleted,
                 $srcWrite, $backedUp);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$discId", file.DiscId);
        cmd.Parameters.AddWithValue("$srcPath", file.SourcePath);
        cmd.Parameters.AddWithValue("$discPath", file.DiscPath);
        cmd.Parameters.AddWithValue("$size", file.SizeBytes);
        cmd.Parameters.AddWithValue("$hash", file.Hash);
        cmd.Parameters.AddWithValue("$zipped", file.IsZipped ? 1 : 0);
        cmd.Parameters.AddWithValue("$split", file.IsSplit ? 1 : 0);
        cmd.Parameters.AddWithValue("$deduped", file.IsDeduped ? 1 : 0);
        cmd.Parameters.AddWithValue("$fileRef", file.IsFileRef ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", file.Version);
        cmd.Parameters.AddWithValue("$deleted", file.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$srcWrite", file.SourceLastWriteUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$backedUp", file.BackedUpUtc.ToString("o"));

        file.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(file);
    }

    public Task<IReadOnlyList<FileRecord>> GetFilesOnDiscAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files WHERE DiscId = $discId ORDER BY SourcePath";
        cmd.Parameters.AddWithValue("$discId", discId);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));

        return Task.FromResult<IReadOnlyList<FileRecord>>(list);
    }

    public Task UpdateFileRecordAsync(FileRecord file, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET
                DiscId = $discId,
                SourcePath = $srcPath,
                DiscPath = $discPath,
                SizeBytes = $size,
                Hash = $hash,
                IsZipped = $zipped,
                IsSplit = $split,
                IsDeduped = $deduped,
                IsFileRef = $fileRef,
                Version = $version,
                IsDeleted = $deleted,
                SourceLastWriteUtc = $srcWrite,
                BackedUpUtc = $backedUp
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", file.Id);
        cmd.Parameters.AddWithValue("$discId", file.DiscId);
        cmd.Parameters.AddWithValue("$srcPath", file.SourcePath);
        cmd.Parameters.AddWithValue("$discPath", file.DiscPath);
        cmd.Parameters.AddWithValue("$size", file.SizeBytes);
        cmd.Parameters.AddWithValue("$hash", file.Hash);
        cmd.Parameters.AddWithValue("$zipped", file.IsZipped ? 1 : 0);
        cmd.Parameters.AddWithValue("$split", file.IsSplit ? 1 : 0);
        cmd.Parameters.AddWithValue("$deduped", file.IsDeduped ? 1 : 0);
        cmd.Parameters.AddWithValue("$fileRef", file.IsFileRef ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", file.Version);
        cmd.Parameters.AddWithValue("$deleted", file.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$srcWrite", file.SourceLastWriteUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$backedUp", file.BackedUpUtc.ToString("o"));

        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileRecord>> GetAllFilesForBackupSetAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
            ORDER BY f.SourcePath, f.Version DESC
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));

        return Task.FromResult<IReadOnlyList<FileRecord>>(list);
    }

    public Task<Dictionary<string, FileVersionInfo>> GetLatestVersionInfoAsync(int backupSetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.SourcePath,
                   MAX(f.Version)          AS MaxVersion,
                   f.SizeBytes,
                   f.SourceLastWriteUtc,
                   f.IsDeduped,
                   f.IsFileRef
            FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId AND f.IsDeleted = 0
            GROUP BY f.SourcePath
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);

        var dict = new Dictionary<string, FileVersionInfo>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string path = r.GetString(0);
            dict[path] = new FileVersionInfo(
                MaxVersion: r.GetInt32(1),
                SizeBytes: r.GetInt64(2),
                SourceLastWriteUtc: DateTime.Parse(r.GetString(3)),
                IsDeduped: r.GetInt32(4) != 0,
                IsFileRef: r.GetInt32(5) != 0);
        }

        return Task.FromResult(dict);
    }

    public Task<FileRecord?> GetFileRecordByPathAndVersionAsync(int backupSetId, string sourcePath, int version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.* FROM Files f
            INNER JOIN Discs d ON f.DiscId = d.Id
            WHERE d.BackupSetId = $setId
              AND f.SourcePath = $path
              AND f.Version = $ver
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        cmd.Parameters.AddWithValue("$path", sourcePath);
        cmd.Parameters.AddWithValue("$ver", version);

        using var r = cmd.ExecuteReader();
        FileRecord? result = r.Read() ? ReadFile(r) : null;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<FileRecord>> GetFilesBySourcePathAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files WHERE SourcePath = $path ORDER BY Version DESC";
        cmd.Parameters.AddWithValue("$path", sourcePath);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));

        return Task.FromResult<IReadOnlyList<FileRecord>>(list);
    }

    private static FileRecord ReadFile(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
        SourcePath = r.GetString(r.GetOrdinal("SourcePath")),
        DiscPath = r.GetString(r.GetOrdinal("DiscPath")),
        SizeBytes = r.GetInt64(r.GetOrdinal("SizeBytes")),
        Hash = r.GetString(r.GetOrdinal("Hash")),
        IsZipped = r.GetInt32(r.GetOrdinal("IsZipped")) != 0,
        IsSplit = r.GetInt32(r.GetOrdinal("IsSplit")) != 0,
        IsDeduped = r.GetInt32(r.GetOrdinal("IsDeduped")) != 0,
        IsFileRef = r.GetInt32(r.GetOrdinal("IsFileRef")) != 0,
        Version = r.GetInt32(r.GetOrdinal("Version")),
        IsDeleted = r.GetInt32(r.GetOrdinal("IsDeleted")) != 0,
        SourceLastWriteUtc = DateTime.Parse(r.GetString(r.GetOrdinal("SourceLastWriteUtc"))),
        BackedUpUtc = DateTime.Parse(r.GetString(r.GetOrdinal("BackedUpUtc"))),
    };

    // ---------------------------------------------------------------
    // Bad Disc Management
    // ---------------------------------------------------------------

    public Task MarkDiscAsBadAsync(int discId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Discs SET IsBad = 1 WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", discId);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileRecord>> GetFilesForReplacementAsync(int badDiscId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Files WHERE DiscId = $discId ORDER BY SourcePath";
        cmd.Parameters.AddWithValue("$discId", badDiscId);

        var list = new List<FileRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));

        return Task.FromResult<IReadOnlyList<FileRecord>>(list);
    }

    // ---------------------------------------------------------------
    // File Chunks
    // ---------------------------------------------------------------

    public Task CreateFileChunkAsync(FileChunk chunk, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO FileChunks
                (FileRecordId, DiscId, Sequence, Offset, Length, DiscFilename)
            VALUES
                ($fileId, $discId, $seq, $offset, $length, $filename);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$fileId", chunk.FileRecordId);
        cmd.Parameters.AddWithValue("$discId", chunk.DiscId);
        cmd.Parameters.AddWithValue("$seq", chunk.Sequence);
        cmd.Parameters.AddWithValue("$offset", chunk.Offset);
        cmd.Parameters.AddWithValue("$length", chunk.Length);
        cmd.Parameters.AddWithValue("$filename", chunk.DiscFilename);

        chunk.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FileChunk>> GetChunksForFileAsync(long fileRecordId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM FileChunks WHERE FileRecordId = $fileId ORDER BY Sequence";
        cmd.Parameters.AddWithValue("$fileId", fileRecordId);

        var list = new List<FileChunk>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FileChunk
            {
                Id = r.GetInt64(r.GetOrdinal("Id")),
                FileRecordId = r.GetInt64(r.GetOrdinal("FileRecordId")),
                DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
                Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
                Offset = r.GetInt64(r.GetOrdinal("Offset")),
                Length = r.GetInt64(r.GetOrdinal("Length")),
                DiscFilename = r.GetString(r.GetOrdinal("DiscFilename")),
            });
        }

        return Task.FromResult<IReadOnlyList<FileChunk>>(list);
    }

    // ---------------------------------------------------------------
    // Deduplication Blocks
    // ---------------------------------------------------------------

    public Task<DeduplicationBlock?> FindBlockByHashAsync(string hash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM DeduplicationBlocks WHERE Hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);

        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return Task.FromResult<DeduplicationBlock?>(null);

        return Task.FromResult<DeduplicationBlock?>(new DeduplicationBlock
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            Hash = r.GetString(r.GetOrdinal("Hash")),
            SizeBytes = r.GetInt32(r.GetOrdinal("SizeBytes")),
            ReferenceCount = r.GetInt32(r.GetOrdinal("ReferenceCount")),
            DiscId = r.GetInt32(r.GetOrdinal("DiscId")),
        });
    }

    public Task CreateBlockAsync(DeduplicationBlock block, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DeduplicationBlocks (Hash, SizeBytes, ReferenceCount, DiscId)
            VALUES ($hash, $size, $refs, $discId);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$hash", block.Hash);
        cmd.Parameters.AddWithValue("$size", block.SizeBytes);
        cmd.Parameters.AddWithValue("$refs", block.ReferenceCount);
        cmd.Parameters.AddWithValue("$discId", block.DiscId);

        block.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.CompletedTask;
    }

    public Task IncrementBlockReferenceAsync(long blockId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE DeduplicationBlocks SET ReferenceCount = ReferenceCount + 1 WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", blockId);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Orphaned Directory Cleanup
    // ---------------------------------------------------------------

    public Task<int> MarkFilesDeletedByDirectoryAsync(int backupSetId, string directoryPrefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Normalise the prefix so LIKE matches correctly.
        var prefix = directoryPrefix.TrimEnd('\\') + "\\";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Files SET IsDeleted = 1
            WHERE IsDeleted = 0
              AND DiscId IN (SELECT Id FROM Discs WHERE BackupSetId = $setId)
              AND (SourcePath LIKE $prefix ESCAPE '\' OR SourcePath = $exact)
            """;
        cmd.Parameters.AddWithValue("$setId", backupSetId);
        // Escape any existing LIKE wildcards in the path, then add the trailing %
        var escaped = prefix.Replace("[", "\\[").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$prefix", escaped + "%");
        cmd.Parameters.AddWithValue("$exact", directoryPrefix);
        int rows = cmd.ExecuteNonQuery();

        return Task.FromResult(rows);
    }

    // ---------------------------------------------------------------
    // Database Export
    // ---------------------------------------------------------------

    public Task ExportDatabaseAsync(string destinationPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // SQLite's VACUUM INTO creates a clean, compacted copy of the database.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", destinationPath);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Transactions
    // ---------------------------------------------------------------

    public Task<IDisposable> BeginTransactionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tx = _connection.BeginTransaction();
        return Task.FromResult<IDisposable>(new TransactionScope(tx));
    }

    /// <summary>
    /// Wrapper that commits on Dispose unless an exception has occurred,
    /// in which case it rolls back.
    /// </summary>
    private sealed class TransactionScope : IDisposable
    {
        private SqliteTransaction? _transaction;
        private bool _completed;

        public TransactionScope(SqliteTransaction transaction)
        {
            _transaction = transaction;
        }

        /// <summary>Mark the transaction as successfully completed (will commit on Dispose).</summary>
        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (_transaction is null)
                return;

            try
            {
                if (_completed)
                    _transaction.Commit();
                else
                    _transaction.Rollback();
            }
            finally
            {
                _transaction.Dispose();
                _transaction = null;
            }
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
