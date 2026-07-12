using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// Default <see cref="ISourceResolver"/>: follows each of a set's source drives
/// across drive-letter reassignments via <see cref="IVolumeResolver"/>, backfills
/// the stable source-volume identities for pre-feature sets, and reports which
/// configured source locations are currently missing.
/// </summary>
public sealed class SourceResolver : ISourceResolver
{
    private readonly IVolumeResolver _volumes;

    public SourceResolver(IVolumeResolver volumes)
    {
        _volumes = volumes;
    }

    public SourceResolution Resolve(BackupSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var opts = set.JobOptions ??= new JobOptions();
        opts.SourceVolumeMappings ??= [];

        bool metadataChanged = false;
        var letterChanges = new List<string>();

        // --- 1. Follow tracked source volumes that have moved. ---
        // Collect every intended letter move first, then apply them in a single
        // pass so no path is rewritten twice (e.g. if two drives swap letters).
        var moves = new Dictionary<char, char>();
        foreach (var mapping in opts.SourceVolumeMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.VolumeId)
                || string.IsNullOrWhiteSpace(mapping.DriveLetter))
                continue;

            string? mount = _volumes.GetCurrentMountPoint(mapping.VolumeId);
            if (mount is null || mount.Length < 2 || mount[1] != ':')
                continue; // Volume not connected — handled by the missing-source report.

            char currentLetter = char.ToUpperInvariant(mount[0]);
            char knownLetter = char.ToUpperInvariant(mapping.DriveLetter[0]);
            if (currentLetter != knownLetter)
            {
                moves[knownLetter] = currentLetter;
                letterChanges.Add($"{knownLetter}: \u2192 {currentLetter}:");
                mapping.DriveLetter = $"{currentLetter}:";
                metadataChanged = true;
            }
        }

        if (moves.Count > 0)
        {
            ApplyDriveMoves(set, moves);
            metadataChanged = true;
        }

        // --- 2. Backfill identities for source drives we don't track yet. ---
        var known = new HashSet<char>(
            opts.SourceVolumeMappings
                .Where(m => !string.IsNullOrWhiteSpace(m.DriveLetter))
                .Select(m => char.ToUpperInvariant(m.DriveLetter[0])));

        foreach (char letter in DistinctSourceDrives(set))
        {
            if (known.Contains(letter))
                continue;

            string? volumeId = _volumes.GetVolumeId($"{letter}:\\");
            if (volumeId is null)
                continue; // Drive not present right now; backfill on a later run.

            opts.SourceVolumeMappings.Add(new SourceVolumeMapping
            {
                DriveLetter = $"{letter}:",
                VolumeId = volumeId,
            });
            known.Add(letter);
            metadataChanged = true;
        }

        // --- 3. Report availability of the (possibly rewritten) source roots. ---
        var missing = new List<string>();
        bool anyAvailable = false;
        foreach (string rootPath in TopLevelSourcePaths(set))
        {
            if (Directory.Exists(rootPath) || File.Exists(rootPath))
                anyAvailable = true;
            else
                missing.Add(rootPath);
        }

        return new SourceResolution(metadataChanged, letterChanges, missing, anyAvailable);
    }

    /// <summary>Top-level source locations this set covers (respecting selection).</summary>
    private static IEnumerable<string> TopLevelSourcePaths(BackupSet set)
    {
        if (set.SourceSelections is { Count: > 0 })
        {
            return set.SourceSelections
                .Where(s => s.IsSelected != false && !string.IsNullOrWhiteSpace(s.Path))
                .Select(s => s.Path);
        }

        return set.SourceRoots.Where(r => !string.IsNullOrWhiteSpace(r));
    }

    /// <summary>Distinct drive-letter roots (upper-cased) across all source locations.</summary>
    private static IEnumerable<char> DistinctSourceDrives(BackupSet set)
    {
        var seen = new HashSet<char>();
        foreach (string path in TopLevelSourcePaths(set))
        {
            char? letter = DriveLetterOf(path);
            if (letter is char l && seen.Add(l))
                yield return l;
        }
    }

    /// <summary>Upper-cased drive letter of a rooted path (<c>"E:\foo"</c> → <c>'E'</c>), or null.</summary>
    private static char? DriveLetterOf(string path)
    {
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            return char.ToUpperInvariant(path[0]);
        return null;
    }

    /// <summary>
    /// Rewrite the drive letter of every source path (roots + selection tree)
    /// according to <paramref name="moves"/> (old letter → new letter), in a
    /// single pass keyed on each path's original letter.
    /// </summary>
    private static void ApplyDriveMoves(BackupSet set, IReadOnlyDictionary<char, char> moves)
    {
        for (int i = 0; i < set.SourceRoots.Count; i++)
            set.SourceRoots[i] = RemapPath(set.SourceRoots[i], moves);

        if (set.SourceSelections is { Count: > 0 })
            RemapSelections(set.SourceSelections, moves);
    }

    private static void RemapSelections(List<SourceSelection> nodes, IReadOnlyDictionary<char, char> moves)
    {
        foreach (var node in nodes)
        {
            node.Path = RemapPath(node.Path, moves);
            if (node.Children is { Count: > 0 })
                RemapSelections(node.Children, moves);
        }
    }

    private static string RemapPath(string path, IReadOnlyDictionary<char, char> moves)
    {
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])
            && moves.TryGetValue(char.ToUpperInvariant(path[0]), out char newLetter))
        {
            return newLetter + path[1..];
        }
        return path;
    }
}
