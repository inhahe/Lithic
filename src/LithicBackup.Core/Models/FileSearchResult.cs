namespace LithicBackup.Core.Models;

/// <summary>
/// Summary of files matching a search query within a single backup set.
/// </summary>
public class FileSearchResult
{
    public int BackupSetId { get; init; }
    public string BackupSetName { get; init; } = string.Empty;
    public int MatchingFileCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public int LatestVersion { get; init; }
    public DateTime? LastBackedUpUtc { get; init; }
}
