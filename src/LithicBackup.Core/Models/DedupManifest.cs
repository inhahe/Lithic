namespace LithicBackup.Core.Models;

/// <summary>
/// JSON schema for .dedup manifest files. Lists the block hashes in order
/// so the file can be reassembled from the _blocks/ store.
/// </summary>
public class DedupManifest
{
    public string OriginalName { get; init; } = "";
    public long OriginalSize { get; init; }
    public string OriginalHash { get; init; } = "";
    public int BlockSize { get; init; }
    public List<string> BlockHashes { get; init; } = [];
}
