using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Burning;

/// <summary>
/// Mock <see cref="IDiscBurner"/> that simulates optical disc burns without
/// real hardware.  Burned content is persisted to a "disc shelf" directory
/// so that verify and restore work against real files.
///
/// <para>
/// Each burn copies the staging directory to
/// <c>{ShelfDirectory}/{recorderId}/disc-{N}/</c> and writes a
/// <c>_manifest.json</c> with per-file metadata (path, size, SHA-256).
/// </para>
///
/// Usage: pass <c>--simulate-burner</c> on the command line, or swap
/// <c>Imapi2DiscBurner</c> for <c>SimulatedDiscBurner</c> in App.xaml.cs.
/// </summary>
public class SimulatedDiscBurner : IDiscBurner
{
    /// <summary>How fast simulated burns run relative to real time.
    /// 1.0 = DVD 1x (~1.35 MB/s), 8.0 = DVD 8x (~10.8 MB/s).</summary>
    public double SpeedMultiplier { get; set; } = 8.0;

    /// <summary>Simulated disc capacity in bytes.  Default = DVD-R (4.7 GB).</summary>
    public long DiscCapacityBytes { get; set; } = 4_700_000_000L;

    /// <summary>Simulated media type reported by <see cref="GetMediaInfoAsync"/>.</summary>
    public MediaType SimulatedMediaType { get; set; } = MediaType.DVD;

    /// <summary>Probability (0.0–1.0) that any given file fails during burn.
    /// Set to 0 for a clean burn, e.g. 0.05 for ~5% failure rate.</summary>
    public double FileFailureProbability { get; set; }

    /// <summary>If non-null, the burn fails catastrophically at this percentage
    /// (0–100) with an <see cref="IOException"/>.  Simulates disc ejection or
    /// laser failure mid-burn.</summary>
    public double? CatastrophicFailureAtPercent { get; set; }

    /// <summary>If true, <see cref="EraseAsync"/> simulates failure.</summary>
    public bool SimulateEraseFail { get; set; }

    /// <summary>Number of simulated recorder drives.</summary>
    public int RecorderCount { get; set; } = 1;

    /// <summary>Root directory where virtual discs are stored.
    /// Defaults to <c>%LOCALAPPDATA%/LithicBackup/simulated-discs</c>.</summary>
    public string ShelfDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LithicBackup", "simulated-discs");

    private readonly Random _rng = new();

    // Per-recorder state: tracks session count and which disc is "loaded".
    private readonly Dictionary<string, RecorderState> _recorders = new();

    /// <summary>
    /// Get the disc root path for a given recorder and disc number.
    /// Useful for the restore <c>DiscInsertCallback</c> to locate simulated discs.
    /// </summary>
    public string GetDiscPath(string recorderId, int discNumber) =>
        Path.Combine(ShelfDirectory, recorderId, $"disc-{discNumber}");

    /// <summary>All disc directories currently on the shelf for any recorder.</summary>
    public IReadOnlyList<string> GetAllDiscPaths()
    {
        var paths = new List<string>();
        if (!Directory.Exists(ShelfDirectory)) return paths;
        foreach (var recorderDir in Directory.GetDirectories(ShelfDirectory))
        {
            foreach (var discDir in Directory.GetDirectories(recorderDir, "disc-*"))
                paths.Add(discDir);
        }
        return paths;
    }

    // -------------------------------------------------------------------
    // IDiscBurner
    // -------------------------------------------------------------------

    public IReadOnlyList<string> GetRecorderIds()
    {
        return Enumerable.Range(0, RecorderCount)
            .Select(i => $"SIM_RECORDER_{i}")
            .ToList();
    }

    public Task<MediaInfo> GetMediaInfoAsync(string recorderId, CancellationToken ct = default)
    {
        var state = GetRecorderState(recorderId);
        long used = state.BytesUsed;

        return Task.FromResult(new MediaInfo
        {
            MediaType = SimulatedMediaType,
            IsBlank = state.SessionCount == 0,
            IsRewritable = true,
            TotalCapacityBytes = DiscCapacityBytes,
            FreeSpaceBytes = Math.Max(0, DiscCapacityBytes - used),
            SessionCount = state.SessionCount,
            RecorderName = $"Simulated {SimulatedMediaType} Drive ({recorderId})",
        });
    }

    public async Task BurnAsync(
        string recorderId,
        string sourceDirectory,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        var state = GetRecorderState(recorderId);

        // Enumerate the staging directory to get real file names and sizes.
        var files = Directory.Exists(sourceDirectory)
            ? Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            : [];

        long totalBytes = 0;
        var fileInfos = new List<(string FullPath, string RelativePath, long Size)>();
        foreach (var f in files)
        {
            var fi = new FileInfo(f);
            string rel = Path.GetRelativePath(sourceDirectory, f);
            fileInfos.Add((f, rel, fi.Length));
            totalBytes += fi.Length;
        }

        if (totalBytes == 0)
            totalBytes = 1; // avoid division by zero

        // Prepare the shelf directory for this disc.
        int discNumber = state.SessionCount + 1;
        string discDir = GetDiscPath(recorderId, discNumber);
        Directory.CreateDirectory(discDir);

        // Simulate burn speed: DVD 1x ~ 1.35 MB/s.
        double bytesPerSecond = 1_350_000.0 * SpeedMultiplier;
        var sw = Stopwatch.StartNew();
        long bytesWritten = 0;
        var manifest = new List<DiscFileEntry>();

        foreach (var (fullPath, relativePath, fileSize) in fileInfos)
        {
            ct.ThrowIfCancellationRequested();

            // Catastrophic failure simulation.
            double pct = (double)bytesWritten / totalBytes * 100;
            if (CatastrophicFailureAtPercent.HasValue && pct >= CatastrophicFailureAtPercent.Value)
            {
                throw new IOException(
                    $"Simulated disc error at {pct:F1}%: laser calibration failure.");
            }

            // Per-file failure simulation.
            if (FileFailureProbability > 0 && _rng.NextDouble() < FileFailureProbability)
            {
                throw new IOException(
                    $"Simulated write error on file: {Path.GetFileName(fullPath)}");
            }

            // Copy the file to the shelf (the simulated "disc surface").
            string destPath = Path.Combine(discDir, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null)
                Directory.CreateDirectory(destDir);

            string hash;
            using (var stream = File.OpenRead(fullPath))
            {
                var hashBytes = await SHA256.HashDataAsync(stream, ct);
                hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            File.Copy(fullPath, destPath, overwrite: true);

            manifest.Add(new DiscFileEntry
            {
                RelativePath = relativePath,
                SizeBytes = fileSize,
                Sha256 = hash,
            });

            // Simulate the time it takes to write this file.
            double writeSeconds = fileSize / bytesPerSecond;
            if (writeSeconds > 0.01)
            {
                int chunks = Math.Max(1, (int)(writeSeconds / 0.1));
                long chunkSize = fileSize / chunks;
                int delayMs = Math.Max(1, (int)(writeSeconds * 1000 / chunks));

                for (int i = 0; i < chunks; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(delayMs, ct);

                    bytesWritten += chunkSize;
                    progress?.Report(new BurnProgress
                    {
                        CurrentFile = Path.GetFileName(fullPath),
                        BytesWritten = bytesWritten,
                        TotalBytes = totalBytes,
                        Percentage = (double)bytesWritten / totalBytes * 100,
                        Elapsed = sw.Elapsed,
                        EstimatedRemaining = EstimateRemaining(sw.Elapsed, bytesWritten, totalBytes),
                    });
                }

                // Account for integer-division remainder.
                long written = chunkSize * chunks;
                if (written < fileSize)
                    bytesWritten += fileSize - written;
            }
            else
            {
                bytesWritten += fileSize;
            }

            progress?.Report(new BurnProgress
            {
                CurrentFile = Path.GetFileName(fullPath),
                BytesWritten = Math.Min(bytesWritten, totalBytes),
                TotalBytes = totalBytes,
                Percentage = Math.Min((double)bytesWritten / totalBytes * 100, 100),
                Elapsed = sw.Elapsed,
                EstimatedRemaining = EstimateRemaining(sw.Elapsed, bytesWritten, totalBytes),
            });
        }

        // Write the manifest.
        var manifestObj = new DiscManifest
        {
            RecorderId = recorderId,
            DiscNumber = discNumber,
            BurnedAtUtc = DateTime.UtcNow,
            MediaType = SimulatedMediaType.ToString(),
            TotalBytes = totalBytes,
            FileCount = manifest.Count,
            Files = manifest,
        };
        string manifestJson = JsonSerializer.Serialize(manifestObj, _jsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(discDir, "_manifest.json"), manifestJson, ct);

        // Simulated finalization.
        progress?.Report(new BurnProgress
        {
            CurrentFile = "Finalizing disc...",
            BytesWritten = totalBytes,
            TotalBytes = totalBytes,
            Percentage = 100,
            Elapsed = sw.Elapsed,
        });
        await Task.Delay(500, ct);

        state.SessionCount++;
        state.BytesUsed += totalBytes;
    }

    public async Task EraseAsync(string recorderId, bool fullErase = false,
        CancellationToken ct = default)
    {
        if (SimulateEraseFail)
            throw new IOException("Simulated erase failure: disc may be write-protected.");

        // Delete the shelf content for this recorder.
        string recorderDir = Path.Combine(ShelfDirectory, recorderId);
        if (Directory.Exists(recorderDir))
            Directory.Delete(recorderDir, true);

        await Task.Delay(fullErase ? 3000 : 1000, ct);
        var state = GetRecorderState(recorderId);
        state.SessionCount = 0;
        state.BytesUsed = 0;
    }

    public Task<bool> CanMultisessionAsync(string recorderId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private RecorderState GetRecorderState(string recorderId)
    {
        if (!_recorders.TryGetValue(recorderId, out var state))
        {
            state = new RecorderState();
            _recorders[recorderId] = state;
        }
        return state;
    }

    private static TimeSpan? EstimateRemaining(TimeSpan elapsed, long written, long total)
    {
        if (written <= 0) return null;
        double rate = written / elapsed.TotalSeconds;
        double remaining = (total - written) / rate;
        return TimeSpan.FromSeconds(remaining);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    // -------------------------------------------------------------------
    // Internal types
    // -------------------------------------------------------------------

    private class RecorderState
    {
        public int SessionCount { get; set; }
        public long BytesUsed { get; set; }
    }

    /// <summary>Manifest written to each virtual disc for inspection.</summary>
    public class DiscManifest
    {
        public string RecorderId { get; set; } = "";
        public int DiscNumber { get; set; }
        public DateTime BurnedAtUtc { get; set; }
        public string MediaType { get; set; } = "";
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }
        public List<DiscFileEntry> Files { get; set; } = [];
    }

    /// <summary>One file entry in the disc manifest.</summary>
    public class DiscFileEntry
    {
        public string RelativePath { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
