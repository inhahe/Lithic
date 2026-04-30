using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Allocates files to discs, maximizing disc space usage.
/// </summary>
public interface IBinPacker
{
    /// <summary>
    /// Pack files into disc allocations. Each allocation represents
    /// one disc's worth of files.
    /// </summary>
    /// <param name="files">Files to allocate.</param>
    /// <param name="discCapacity">Available bytes per disc.</param>
    /// <returns>One <see cref="DiscAllocation"/> per disc needed.</returns>
    IReadOnlyList<DiscAllocation> Pack(IReadOnlyList<ScannedFile> files, long discCapacity);

    /// <summary>
    /// Pack files into disc allocations using per-disc capacities.
    /// When more discs are needed than capacities supplied, the last capacity is repeated.
    /// </summary>
    /// <param name="files">Files to allocate.</param>
    /// <param name="discCapacities">Available bytes for each disc (at least one required).</param>
    /// <returns>One <see cref="DiscAllocation"/> per disc needed.</returns>
    IReadOnlyList<DiscAllocation> Pack(IReadOnlyList<ScannedFile> files, IReadOnlyList<long> discCapacities);
}

/// <summary>
/// One disc's worth of files as allocated by the bin packer.
/// </summary>
public class DiscAllocation
{
    /// <summary>Sequence number of this disc (1-based).</summary>
    public int DiscSequence { get; init; }

    /// <summary>Files allocated to this disc.</summary>
    public List<ScannedFile> Files { get; init; } = [];

    /// <summary>Total bytes of files on this disc.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Remaining free bytes on this disc.</summary>
    public long FreeBytes { get; init; }
}
