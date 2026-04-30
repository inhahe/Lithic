using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Abstraction over optical disc burning (IMAPI2).
/// </summary>
public interface IDiscBurner
{
    /// <summary>Enumerate available disc recorder device IDs.</summary>
    IReadOnlyList<string> GetRecorderIds();

    /// <summary>Query the inserted media in the specified recorder.</summary>
    Task<MediaInfo> GetMediaInfoAsync(string recorderId, CancellationToken ct = default);

    /// <summary>
    /// Burn files to disc. <paramref name="sourceDirectory"/> is a staging directory
    /// whose contents are written to the disc image.
    /// </summary>
    Task BurnAsync(
        string recorderId,
        string sourceDirectory,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Erase a rewritable disc.</summary>
    Task EraseAsync(string recorderId, bool fullErase = false, CancellationToken ct = default);

    /// <summary>Check whether the recorder supports multisession with the current media.</summary>
    Task<bool> CanMultisessionAsync(string recorderId, CancellationToken ct = default);
}

/// <summary>Information about the currently inserted media.</summary>
public class MediaInfo
{
    public MediaType MediaType { get; init; }
    public bool IsBlank { get; init; }
    public bool IsRewritable { get; init; }
    public long TotalCapacityBytes { get; init; }
    public long FreeSpaceBytes { get; init; }
    public int SessionCount { get; init; }
    public string RecorderName { get; init; } = string.Empty;
}

/// <summary>Options for a burn operation.</summary>
public class BurnOptions
{
    public FilesystemType FilesystemType { get; init; } = FilesystemType.UDF;
    public bool Multisession { get; init; }
    public bool VerifyAfterBurn { get; init; } = true;

    /// <summary>Burn speed multiplier (0 = auto/max).</summary>
    public int Speed { get; init; }
}

/// <summary>Progress update during a burn.</summary>
public class BurnProgress
{
    public string CurrentFile { get; init; } = string.Empty;
    public long BytesWritten { get; init; }
    public long TotalBytes { get; init; }
    public double Percentage { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
}
