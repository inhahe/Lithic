using System.IO.Enumeration;

namespace LithicBackup.Core.Models;

/// <summary>
/// Machine-global rules that govern how continuous-mode backup decides when a
/// changed file is "ready" to copy. Two independent tier systems:
///
/// <para><b>Debounce (size tiers).</b> A file is copied once it has been quiet
/// (no further changes) for its debounce window. Bigger files get longer
/// windows because the <i>cost of a false trigger</i> — copying a file that is
/// still mid-write — grows with size: a stray sub-second lull in a 60&#160;GB
/// download shouldn't kick off a 60&#160;GB copy. Size predicts that cost, so we
/// demand more quiet before committing to an expensive copy. Tiers are matched
/// by <see cref="DebounceSizeTier.MaxSizeBytes"/> (first tier whose ceiling the
/// file fits under, in ascending order).</para>
///
/// <para><b>Max-wait (mask tiers).</b> Debounce alone never fires for a file
/// that keeps changing forever (a busy log, an always-appending session file),
/// because it never goes quiet. Max-wait is the escape hatch: after a file has
/// been pending for its max-wait window it is copied regardless of ongoing
/// changes. But a mid-write capture is only <i>useful</i> for files whose
/// partial content is meaningful — append-only logs (a valid prefix) — and
/// actively harmful for files written in place or finalized at the end
/// (downloads, databases): you'd copy a torn/corrupt snapshot. So max-wait is
/// opt-in by filename/path mask. Tiers are evaluated in order, first match wins;
/// a file matching no tier gets an <b>infinite</b> max-wait (only ever copied
/// when it finally settles via debounce). Within a matched tier,
/// <see cref="MaxWaitMaskTier.MaxWaitSeconds"/> &lt;= 0 also means infinite.</para>
/// </summary>
public class ContinuousRules
{
    /// <summary>
    /// Size tiers for the debounce window, ascending by
    /// <see cref="DebounceSizeTier.MaxSizeBytes"/>. The first tier whose ceiling
    /// the file's current size is at or below supplies the debounce seconds; a
    /// file larger than every tier uses the last tier.
    /// </summary>
    public List<DebounceSizeTier> DebounceTiers { get; set; } = DefaultDebounceTiers();

    /// <summary>
    /// Ordered mask tiers for the max-wait cap. Evaluated top to bottom;
    /// first tier that matches (by include masks, minus exclude masks) wins. A
    /// file matching no tier has infinite max-wait.
    /// </summary>
    public List<MaxWaitMaskTier> MaxWaitTiers { get; set; } = DefaultMaxWaitTiers();

    /// <summary>
    /// Default debounce size tiers: files under 1&#160;MB settle in half a
    /// second; up to 100&#160;MB in 3&#160;s; up to 1&#160;GB in 10&#160;s;
    /// anything larger in 30&#160;s.
    /// </summary>
    public static List<DebounceSizeTier> DefaultDebounceTiers() => new()
    {
        new DebounceSizeTier { MaxSizeBytes = 1L * 1024 * 1024,        DebounceSeconds = 0.5 },
        new DebounceSizeTier { MaxSizeBytes = 100L * 1024 * 1024,      DebounceSeconds = 3 },
        new DebounceSizeTier { MaxSizeBytes = 1024L * 1024 * 1024,     DebounceSeconds = 10 },
        new DebounceSizeTier { MaxSizeBytes = long.MaxValue,          DebounceSeconds = 30 },
    };

    /// <summary>
    /// Default max-wait tier: append-only text formats that stay meaningful as a
    /// partial prefix (logs, JSON/JSONL/NDJSON session files, plain output and
    /// trace dumps) are captured at most every 5&#160;minutes even while they keep
    /// growing. Deliberately excludes databases and downloads, whose mid-write
    /// snapshots would be torn/corrupt — those fall through to infinite max-wait
    /// and are only backed up once they settle.
    /// </summary>
    public static List<MaxWaitMaskTier> DefaultMaxWaitTiers() => new()
    {
        new MaxWaitMaskTier
        {
            Name = "Append-only logs",
            IncludeMasks = new() { "*.log", "*.jsonl", "*.ndjson", "*.json", "*.out", "*.trace" },
            ExcludeMasks = new(),
            MaxWaitSeconds = 300,
        },
    };

    /// <summary>
    /// Resolve the debounce window (seconds) for a file of the given size using
    /// <see cref="DebounceTiers"/>. Falls back to a sensible default if no tiers
    /// are configured.
    /// </summary>
    public double ResolveDebounceSeconds(long sizeBytes)
    {
        if (DebounceTiers is { Count: > 0 })
        {
            foreach (var tier in DebounceTiers)
            {
                if (sizeBytes <= tier.MaxSizeBytes)
                    return tier.DebounceSeconds > 0 ? tier.DebounceSeconds : 0;
            }
            // Larger than every tier's ceiling: use the last (biggest) tier.
            var last = DebounceTiers[^1];
            return last.DebounceSeconds > 0 ? last.DebounceSeconds : 0;
        }
        return 0.5;
    }

    /// <summary>
    /// Resolve the max-wait cap (seconds) for a file using <see cref="MaxWaitTiers"/>.
    /// Returns <c>null</c> to mean "infinite" — the file is only copied when it
    /// settles via debounce, never forced mid-change. The first tier whose
    /// include masks match (and whose exclude masks don't) wins; a
    /// non-positive <see cref="MaxWaitMaskTier.MaxWaitSeconds"/> in that tier
    /// also yields infinite.
    /// </summary>
    public int? ResolveMaxWaitSeconds(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (MaxWaitTiers is { Count: > 0 })
        {
            foreach (var tier in MaxWaitTiers)
            {
                if (TierMatches(tier, name, fullPath))
                    return tier.MaxWaitSeconds > 0 ? tier.MaxWaitSeconds : (int?)null;
            }
        }
        return null; // no tier matched → infinite max-wait
    }

    private static bool TierMatches(MaxWaitMaskTier tier, string name, string fullPath)
    {
        bool included = false;
        if (tier.IncludeMasks is { Count: > 0 })
        {
            foreach (var mask in tier.IncludeMasks)
            {
                if (MaskMatches(mask, name, fullPath)) { included = true; break; }
            }
        }
        if (!included) return false;

        if (tier.ExcludeMasks is { Count: > 0 })
        {
            foreach (var mask in tier.ExcludeMasks)
            {
                if (MaskMatches(mask, name, fullPath)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Match a glob mask against either the bare filename or, when the mask looks
    /// like a path pattern (contains a directory separator), the full path.
    /// Case-insensitive, supports <c>*</c> and <c>?</c>.
    /// </summary>
    private static bool MaskMatches(string mask, string name, string fullPath)
    {
        if (string.IsNullOrEmpty(mask)) return false;
        var target = (mask.Contains('\\') || mask.Contains('/')) ? fullPath : name;
        return FileSystemName.MatchesSimpleExpression(mask, target, ignoreCase: true);
    }
}

/// <summary>
/// One debounce size tier: files whose size is at or below
/// <see cref="MaxSizeBytes"/> use <see cref="DebounceSeconds"/> as their quiet
/// window (unless an earlier, smaller tier already matched).
/// </summary>
public class DebounceSizeTier
{
    /// <summary>Inclusive upper size bound for this tier, in bytes.</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>Debounce window (seconds) applied to files in this tier.</summary>
    public double DebounceSeconds { get; set; }
}

/// <summary>
/// One max-wait mask tier: files matching <see cref="IncludeMasks"/> (and not
/// <see cref="ExcludeMasks"/>) are force-copied after <see cref="MaxWaitSeconds"/>
/// even while still changing.
/// </summary>
public class MaxWaitMaskTier
{
    /// <summary>Friendly label shown in the settings UI.</summary>
    public string Name { get; set; } = "";

    /// <summary>Glob masks (e.g. <c>*.log</c>) that opt a file into this tier.</summary>
    public List<string> IncludeMasks { get; set; } = new();

    /// <summary>Glob masks that veto a file even if an include mask matched.</summary>
    public List<string> ExcludeMasks { get; set; } = new();

    /// <summary>
    /// Force-copy a matching file after it has been pending this many seconds.
    /// Values &lt;= 0 mean infinite (only copied when it settles via debounce).
    /// </summary>
    public int MaxWaitSeconds { get; set; }
}
