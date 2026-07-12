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
    /// Burn a set of files to disc. Each <see cref="BurnItem"/> maps a path as it
    /// should appear on the disc to the absolute path of the source bytes to read.
    /// The source may be a temporary staging copy or, in burn-in-place mode, the
    /// original file held under a read lock for the duration of the burn.
    /// </summary>
    Task BurnAsync(
        string recorderId,
        IReadOnlyList<BurnItem> items,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>Erase a rewritable disc.</summary>
    Task EraseAsync(string recorderId, bool fullErase = false, CancellationToken ct = default);

    /// <summary>Check whether the recorder supports multisession with the current media.</summary>
    Task<bool> CanMultisessionAsync(string recorderId, CancellationToken ct = default);
}

/// <summary>
/// One file to be written to a disc.
/// </summary>
/// <param name="DiscRelativePath">
/// The file's path as it should appear on the burned disc (relative to the disc
/// root, e.g. <c>"C\Users\me\photo.jpg"</c>).
/// </param>
/// <param name="SourceAbsolutePath">
/// Absolute path of the bytes to read for this file. Either a temporary staging
/// copy or, in burn-in-place mode, the original source file (held under a read
/// lock by the caller so it cannot change before the burn completes).
/// </param>
public sealed record BurnItem(string DiscRelativePath, string SourceAbsolutePath);

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
