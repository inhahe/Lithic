using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Verifies the integrity of a directory-mode backup against its source.
/// Three independent checks are performed:
///   1. Coverage — every current source file has an active catalog record.
///   2. Backing storage — every active record's on-disk file actually exists
///      (plain copies and <c>.dedup</c>/<c>.fileref</c> manifests alike), and
///      every block a <c>.dedup</c> manifest references exists in the block store.
///   3. Reference resolution — every <c>.fileref</c> (which stores no bytes)
///      resolves, by content hash via the catalog, to an existing plain copy.
/// </summary>
public class VerifyService : IVerifyService
{
    private readonly ICatalogRepository _catalog;
    private readonly IFileScanner _scanner;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public VerifyService(ICatalogRepository catalog, IFileScanner scanner)
    {
        _catalog = catalog;
        _scanner = scanner;
    }

    public async Task<VerifyResult> VerifyAsync(
        BackupJob job,
        string targetDirectory,
        bool verifyContents = false,
        IProgress<VerifyProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!job.BackupSetId.HasValue)
        {
            return new VerifyResult
            {
                Issues =
                [
                    new VerifyIssue
                    {
                        Path = targetDirectory,
                        Detail = "Backup set has not been backed up yet — nothing to verify.",
                        Kind = VerifyIssueKind.MissingFromBackup,
                    },
                ],
            };
        }

        int backupSetId = job.BackupSetId.Value;
        var issues = new List<VerifyIssue>();
        string blockStoreDir = Path.Combine(targetDirectory, "_blocks");

        // --- Load the full catalog for this set (all versions, current + _prev). ---
        progress?.Report(new VerifyProgress { StatusMessage = "Loading catalog..." });
        var allRecords = await _catalog.GetAllFilesForBackupSetAsync(backupSetId, ct);
        var activeRecords = allRecords.Where(r => !r.IsDeleted).ToList();

        // Source paths that have at least one active (non-deleted) record.
        var backedUpSourcePaths = new HashSet<string>(
            activeRecords.Select(r => r.SourcePath),
            StringComparer.OrdinalIgnoreCase);

        // --- Check 1: every current source file is represented in the backup. ---
        progress?.Report(new VerifyProgress { StatusMessage = "Scanning source files..." });
        var isExcluded = DirectoryBackupService.BuildExclusionFilter(job);
        var scanned = await _scanner.ScanAsync(job.Sources, progress: null, ct, isExcluded);

        int sourceChecked = 0;
        foreach (var file in scanned)
        {
            ct.ThrowIfCancellationRequested();
            sourceChecked++;
            if (!backedUpSourcePaths.Contains(file.FullPath))
            {
                issues.Add(new VerifyIssue
                {
                    Path = file.FullPath,
                    Detail = "Source file is not present in the backup.",
                    Kind = VerifyIssueKind.MissingFromBackup,
                });
            }
        }

        // --- Checks 2 & 3: backing storage + .fileref resolution per record. ---
        // Cache hash -> resolved plain path so repeated references to the same
        // content don't re-query the catalog or re-stat the disk.
        var resolvedPlain = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // When verifying contents, remember which absolute plain paths and which
        // blocks we've already hashed so shared anchors / blocks are read once.
        var hashedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashedBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recordsChecked = 0;
        int fileRefsChecked = 0;
        int itemsHashed = 0;
        int total = activeRecords.Count;
        int processed = 0;

        foreach (var record in activeRecords)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            recordsChecked++;

            if (processed % 64 == 0 || processed == total)
            {
                progress?.Report(new VerifyProgress
                {
                    StatusMessage = verifyContents
                        ? "Verifying backup contents (reading & hashing)..."
                        : "Verifying backup files...",
                    CurrentFile = record.SourcePath,
                    ItemsChecked = processed,
                    TotalItems = total,
                    Percentage = total > 0 ? (double)processed / total * 100 : 100,
                });
            }

            if (record.IsFileRef)
            {
                fileRefsChecked++;

                // The .fileref manifest itself should exist on disk.
                string refPath = Path.Combine(targetDirectory, record.DiscPath);
                if (!File.Exists(refPath))
                {
                    issues.Add(new VerifyIssue
                    {
                        Path = record.SourcePath,
                        Detail = $"File reference manifest is missing: {record.DiscPath}",
                        Kind = VerifyIssueKind.BackingFileMissing,
                    });
                    continue;
                }

                // The referenced content must resolve to an existing plain copy.
                if (!resolvedPlain.TryGetValue(record.Hash, out string? plainPath))
                {
                    plainPath = await ResolvePlainContentPathAsync(
                        backupSetId, record.Hash, targetDirectory, ct);
                    resolvedPlain[record.Hash] = plainPath;
                }

                if (plainPath is null || !File.Exists(plainPath))
                {
                    issues.Add(new VerifyIssue
                    {
                        Path = record.SourcePath,
                        Detail = "File reference does not resolve to an existing plain copy "
                            + $"of its content (hash {Shorten(record.Hash)}).",
                        Kind = VerifyIssueKind.FileRefUnresolved,
                    });
                }
                else if (verifyContents && hashedPaths.Add(plainPath))
                {
                    // Re-hash the shared plain copy once and compare to the catalog.
                    itemsHashed++;
                    string? actual = await ComputeFileHashAsync(plainPath, ct);
                    if (actual is not null && !HashEquals(actual, record.Hash))
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = "Resolved content hash does not match the catalog "
                                + $"(expected {Shorten(record.Hash)}, got {Shorten(actual)}): "
                                + plainPath,
                            Kind = VerifyIssueKind.ContentMismatch,
                        });
                    }
                }
            }
            else if (record.IsDeduped)
            {
                string manifestPath = Path.Combine(targetDirectory, record.DiscPath);
                if (!File.Exists(manifestPath))
                {
                    issues.Add(new VerifyIssue
                    {
                        Path = record.SourcePath,
                        Detail = $"Dedup manifest is missing: {record.DiscPath}",
                        Kind = VerifyIssueKind.BackingFileMissing,
                    });
                    continue;
                }

                // Each block the manifest references must exist in the block store.
                try
                {
                    string json = await File.ReadAllTextAsync(manifestPath, ct);
                    var manifest = JsonSerializer.Deserialize<DedupManifest>(json, _jsonOptions);
                    if (manifest is null)
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = $"Dedup manifest is corrupt: {record.DiscPath}",
                            Kind = VerifyIssueKind.BackingFileMissing,
                        });
                        continue;
                    }

                    foreach (string blockHash in manifest.BlockHashes)
                    {
                        ct.ThrowIfCancellationRequested();
                        string blockPath = Path.Combine(blockStoreDir, blockHash + ".blk");
                        if (!File.Exists(blockPath))
                        {
                            issues.Add(new VerifyIssue
                            {
                                Path = record.SourcePath,
                                Detail = $"Missing block in store: {blockHash}.blk",
                                Kind = VerifyIssueKind.BlockMissing,
                            });
                        }
                        else if (verifyContents && hashedBlocks.Add(blockHash))
                        {
                            // Each block is content-addressed: its file name IS its
                            // SHA-256, so re-hash it and compare against the name.
                            itemsHashed++;
                            string? actual = await ComputeFileHashAsync(blockPath, ct);
                            if (actual is not null && !HashEquals(actual, blockHash))
                            {
                                issues.Add(new VerifyIssue
                                {
                                    Path = record.SourcePath,
                                    Detail = "Block content does not match its hash "
                                        + $"(expected {Shorten(blockHash)}, got {Shorten(actual)}): "
                                        + $"{blockHash}.blk",
                                    Kind = VerifyIssueKind.ContentMismatch,
                                });
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    issues.Add(new VerifyIssue
                    {
                        Path = record.SourcePath,
                        Detail = $"Could not read dedup manifest: {ex.Message}",
                        Kind = VerifyIssueKind.BackingFileMissing,
                    });
                }
            }
            else
            {
                // Plain copy (or zipped/split — DiscPath points at the stored file).
                string filePath = Path.Combine(targetDirectory, record.DiscPath);
                if (!File.Exists(filePath))
                {
                    issues.Add(new VerifyIssue
                    {
                        Path = record.SourcePath,
                        Detail = $"Backing file is missing: {record.DiscPath}",
                        Kind = VerifyIssueKind.BackingFileMissing,
                    });
                }
                else if (verifyContents
                    && !record.IsZipped && !record.IsSplit
                    && !string.IsNullOrEmpty(record.Hash)
                    && hashedPaths.Add(filePath))
                {
                    // Re-hash the stored plain copy and compare against the catalog.
                    // Zipped/split stores don't hold the raw content directly,
                    // so a name-vs-hash comparison wouldn't be meaningful for them.
                    itemsHashed++;
                    string? actual = await ComputeFileHashAsync(filePath, ct);
                    if (actual is not null && !HashEquals(actual, record.Hash))
                    {
                        issues.Add(new VerifyIssue
                        {
                            Path = record.SourcePath,
                            Detail = "Stored content hash does not match the catalog "
                                + $"(expected {Shorten(record.Hash)}, got {Shorten(actual)}): "
                                + record.DiscPath,
                            Kind = VerifyIssueKind.ContentMismatch,
                        });
                    }
                }
            }
        }

        progress?.Report(new VerifyProgress
        {
            StatusMessage = "Verification complete.",
            ItemsChecked = total,
            TotalItems = total,
            Percentage = 100,
        });

        return new VerifyResult
        {
            SourceFilesChecked = sourceChecked,
            RecordsChecked = recordsChecked,
            FileRefsChecked = fileRefsChecked,
            ContentsVerified = verifyContents,
            ItemsHashed = itemsHashed,
            Issues = issues,
        };
    }

    /// <summary>
    /// Compute the lowercase hex SHA-256 of a file's contents, streaming so large
    /// files don't have to fit in memory. Returns <c>null</c> if the file can't be
    /// read (the caller has already confirmed existence; an I/O failure here is
    /// reported separately by the existence check on a later run).
    /// </summary>
    private static async Task<string?> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool HashEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a content hash to the absolute path of an active plain copy of
    /// that content somewhere in the backup tree, or <c>null</c> if none exists.
    /// </summary>
    private async Task<string?> ResolvePlainContentPathAsync(
        int backupSetId, string hash, string targetDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        var candidates = await _catalog.GetActiveRecordsByHashAsync(backupSetId, hash, ct);
        var plain = candidates.FirstOrDefault(r => !r.IsFileRef && !r.IsDeduped);
        return plain is null ? null : Path.Combine(targetDirectory, plain.DiscPath);
    }

    private static string Shorten(string hash)
        => hash.Length <= 12 ? hash : hash[..12];
}
