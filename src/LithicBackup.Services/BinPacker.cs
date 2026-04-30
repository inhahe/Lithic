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
        var allocations = new List<DiscAllocation>();

        foreach (var file in sorted)
        {
            // Find first disc with enough room
            DiscAllocation? target = null;
            foreach (var alloc in allocations)
            {
                if (alloc.FreeBytes >= file.SizeBytes)
                {
                    target = alloc;
                    break;
                }
            }

            if (target is null)
            {
                // Need a new disc — use the per-disc capacity or repeat the last one.
                int discIndex = allocations.Count;
                long capacity = discIndex < discCapacities.Count
                    ? discCapacities[discIndex]
                    : discCapacities[^1];

                target = new DiscAllocation
                {
                    DiscSequence = allocations.Count + 1,
                    Files = [],
                    TotalBytes = 0,
                    FreeBytes = capacity,
                };
                allocations.Add(target);
            }

            target.Files.Add(file);
        }

        // Rebuild with correct totals
        return allocations.Select((a, i) =>
        {
            long total = a.Files.Sum(f => f.SizeBytes);
            int capIndex = i < discCapacities.Count ? i : discCapacities.Count - 1;
            long capacity = discCapacities[capIndex];
            return new DiscAllocation
            {
                DiscSequence = i + 1,
                Files = a.Files,
                TotalBytes = total,
                FreeBytes = capacity - total,
            };
        }).ToList();
    }
}
