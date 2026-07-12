using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LithicBackup.Core.Exceptions;
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
/// Usage: pass <c>--test-mode</c> on the command line (which wires up a
/// <c>SwitchableDiscBurner</c>), or swap <c>Imapi2DiscBurner</c> for
/// <c>SimulatedDiscBurner</c> in App.xaml.cs.
/// </summary>
public class SimulatedDiscBurner : IDiscBurner
{
    /// <summary>How fast simulated burns run relative to real time.
    /// 1.0 = DVD 1x (~1.35 MB/s), 8.0 = DVD 8x (~10.8 MB/s).</summary>
    public double SpeedMultiplier { get; set; } = 8.0;

    /// <summary>Simulated disc capacity in bytes.  Default = DVD-R (4.7 GB).</summary>
    public long DiscCapacityBytes { get; set; } = 4_700_000_000L;

    /// <summary>
    /// If set, the disc's <em>real</em> writable capacity — smaller than the
    /// <see cref="DiscCapacityBytes"/> it reports via <see cref="GetMediaInfoAsync"/>.
    /// Simulates media that over-reports its size: planning trusts the reported
    /// capacity and fits the data, but the burn runs out of room and fails with an
    /// <see cref="IOException"/> once cumulative written bytes exceed this value.
    /// Null means the disc holds exactly what it reports.
    /// </summary>
    public long? ActualCapacityBytes { get; set; }

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

    /// <summary>If true, <see cref="GetRecorderIds"/> reports no drives, so a
    /// backup fails at start with "No disc recorder detected." (pre-burn).</summary>
    public bool SimulateNoRecorder { get; set; }

    /// <summary>If true, <see cref="GetMediaInfoAsync"/> throws as though the
    /// drive were empty — surfaces during planning / at burn start (pre-burn).</summary>
    public bool SimulateNoMedia { get; set; }

    /// <summary>If true, <see cref="BurnAsync"/> writes the disc normally but then
    /// fails the post-burn verification step (an end-of-burn error).</summary>
    public bool SimulateVerifyFailure { get; set; }

    /// <summary>Number of simulated recorder drives.</summary>
    public int RecorderCount { get; set; } = 1;

    /// <summary>
    /// When <c>true</c> (default), the actual file bytes are copied to the disc
    /// shelf so that verify and restore work against real data.  When
    /// <c>false</c>, the full directory tree and real filenames are still
    /// recreated on the shelf, but each file holds a tiny stub recording its
    /// hash and size instead of the real content — so the shelf mirrors the true
    /// structure for easy inspection while staying tiny (no 100&#160;GB of
    /// duplicated data on the system drive).  This is sufficient for exercising
    /// burn timing, capacity, bin-packing, spanning, and failure-injection
    /// behaviour.  The trade-off: <em>restore</em> from a metadata-only disc
    /// cannot reconstruct file content, so restore (and block-dedup round-trips
    /// that read the stored bytes back) require the default full-content mode.
    /// </summary>
    public bool StoreFileContents { get; set; } = true;

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
        if (SimulateNoRecorder)
            return [];

        return Enumerable.Range(0, RecorderCount)
            .Select(i => $"SIM_RECORDER_{i}")
            .ToList();
    }

    public Task<MediaInfo> GetMediaInfoAsync(string recorderId, CancellationToken ct = default)
    {
        if (SimulateNoMedia)
            throw new IOException("Simulated: no media present in the drive.");

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
        IReadOnlyList<BurnItem> items,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        var state = GetRecorderState(recorderId);

        // Read each item's real name and size from its source path (a temp
        // staging copy or an in-place original held under a read lock).
        long totalBytes = 0;
        var fileInfos = new List<(string FullPath, string RelativePath, long Size)>();
        foreach (var item in items)
        {
            var fi = new FileInfo(item.SourceAbsolutePath);
            fileInfos.Add((item.SourceAbsolutePath, item.DiscRelativePath, fi.Length));
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
        long committedBytes = 0; // bytes accounted against ActualCapacityBytes
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

            // Over-reported capacity: the disc claimed more room than it actually
            // has, so we run out of space partway through the burn.
            if (ActualCapacityBytes.HasValue
                && committedBytes + fileSize > ActualCapacityBytes.Value)
            {
                throw new IOException(
                    $"Simulated out of disc space: the media over-reported its capacity " +
                    $"({committedBytes + fileSize} bytes needed, only {ActualCapacityBytes.Value} available).");
            }
            committedBytes += fileSize;

            // Always hash the source (reads from the user's drive, costs no
            // shelf space) so the manifest records accurate per-file hashes.
            string hash;
            using (var stream = File.OpenRead(fullPath))
            {
                var hashBytes = await SHA256.HashDataAsync(stream, ct);
                hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // Recreate the file on the shelf (the simulated "disc surface") at
            // its real relative path. In full mode we copy the actual bytes; in
            // metadata-only mode we still create the same directory tree and a
            // file with the same name, but its content is a tiny stub recording
            // the hash and size instead of the real bytes — so the shelf mirrors
            // the true structure for easy inspection without the disk cost.
            string destPath = Path.Combine(discDir, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null)
                Directory.CreateDirectory(destDir);

            if (StoreFileContents)
            {
                File.Copy(fullPath, destPath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(
                    destPath,
                    $"[LithicBackup simulated metadata-only stub]\nsha256: {hash}\nsize: {fileSize}\n",
                    ct);
            }

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

        // Post-burn read-back verification — mirrors the real burner. The data
        // was written to the shelf above; now read it back and confirm it
        // matches the source.
        if (options.VerifyAfterBurn)
        {
            // Failure injection: pretend the read-back found a mismatch.
            if (SimulateVerifyFailure)
            {
                progress?.Report(new BurnProgress
                {
                    CurrentFile = "Verifying disc...",
                    BytesWritten = totalBytes,
                    TotalBytes = totalBytes,
                    Percentage = 100,
                    Elapsed = sw.Elapsed,
                });
                await Task.Delay(300, ct);
                throw new BurnException(
                    "Simulated post-burn verification failed: data mismatch on disc.");
            }

            // Real read-back is only meaningful when the shelf holds actual
            // bytes. In metadata-only mode (StoreFileContents = false) the shelf
            // files are tiny stubs, so a byte-for-byte check would always fail —
            // skip it there.
            if (StoreFileContents)
            {
                await BurnVerifier.VerifyAsync(
                    items, discDir, totalBytes, sw, progress, ct);
            }
        }

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
