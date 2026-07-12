using LithicBackup.Core.Interfaces;

namespace LithicBackup.Services;

/// <summary>
/// First-fit-decreasing bin packer: sorts files largest-first, places
/// each in the first disc that has room. Typically achieves 90%+ utilization.
/// </summary>
public class BinPacker : IBinPacker
{
    public IReadOnlyList<DiscAllocation> Pack(IReadOnlyList<ScannedFile> files, long discCapacity)
    {
        return Pack(files, new[] { discCapacity });
    }

    public IReadOnlyList<DiscAllocation> Pack(IReadOnlyList<ScannedFile> files, IReadOnlyList<long> discCapacities)
    {
        if (discCapacities.Count == 0)
            throw new ArgumentException("At least one disc capacity is required.", nameof(discCapacities));

        foreach (var cap in discCapacities)
        {
            if (cap <= 0)
                throw new ArgumentOutOfRangeException(nameof(discCapacities), "All disc capacities must be positive.");
        }

        // Sort largest first
        var sorted = files.OrderByDescending(f => f.SizeBytes).ToList();

        // Working bins track their running usage as files are added, so first-fit
        // sees each bin's REMAINING space. (DiscAllocation.FreeBytes is init-only
        // and cannot be decremented in place, so packing state is kept here and the
        // immutable allocations are built at the end.)
        var bins = new List<Bin>();

        foreach (var file in sorted)
        {
            // Find the first disc that still has room for this file.
            Bin? target = null;
            foreach (var bin in bins)
            {
                if (bin.Free >= file.SizeBytes)
                {
                    target = bin;
                    break;
                }
            }

            if (target is null)
            {
                // Need a new disc — use the per-disc capacity or repeat the last one.
                int discIndex = bins.Count;
                long capacity = discIndex < discCapacities.Count
                    ? discCapacities[discIndex]
                    : discCapacities[^1];

                target = new Bin { Capacity = capacity };
                bins.Add(target);
            }

            target.Files.Add(file);
            target.Used += file.SizeBytes;
        }

        return bins.Select((b, i) => new DiscAllocation
        {
            DiscSequence = i + 1,
            Files = b.Files,
            TotalBytes = b.Used,
            FreeBytes = b.Capacity - b.Used,
        }).ToList();
    }

    /// <summary>Mutable packing state for one disc while allocating files.</summary>
    private sealed class Bin
    {
        public List<ScannedFile> Files { get; } = [];
        public long Capacity { get; init; }
        public long Used { get; set; }
        public long Free => Capacity - Used;
    }
}
