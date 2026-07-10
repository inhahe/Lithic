using System.Collections.Concurrent;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

namespace LithicBackup.Worker;

/// <summary>
/// Background service that monitors backup set schedules and file-system
/// changes, executing directory backups automatically.
/// </summary>
/// <remarks>
/// Continuous-mode sets are driven by the NTFS USN change journal rather than
/// <see cref="FileSystemWatcher"/>: the journal never drops events, records
/// changes that happened while the service was offline, and lets us version
/// exactly the files that changed instead of rescanning whole drives. Each poll
/// reads new journal records per volume, routes the changed paths to the sets
/// that include them, debounces each file individually, then batches the
/// "ready" files into a single targeted backup per set.
/// </remarks>
public sealed class BackupWorker : BackgroundService
{
    private readonly ILogger<BackupWorker> _logger;
    private readonly ICatalogRepository _catalog;
    private readonly DirectoryBackupService _directoryBackup;
    private readonly IDestinationResolver _destinationResolver;

    /// <summary>How often we reload backup sets, check schedules, and read journals.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on how long a continuous-mode change may stay pending before
    /// it is backed up regardless of ongoing activity. Prevents a constantly-
    /// written file (e.g. an active log) from starving its own debounce window.
    /// </summary>
    private static readonly TimeSpan MaxContinuousWait = TimeSpan.FromMinutes(5);

    /// <summary>Per-set tracking state.</summary>
    private readonly ConcurrentDictionary<int, SetState> _sets = new();

    /// <summary>
    /// Per-volume USN journal readers, opened lazily. A null value means the
    /// volume has no usable journal (non-NTFS or inaccessible) — cached so we
    /// don't retry it on every poll.
    /// </summary>
    private readonly Dictionary<char, UsnJournalReader?> _journalReaders = new();

    /// <summary>Only one backup runs at a time.</summary>
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    public BackupWorker(
        ILogger<BackupWorker> logger,
        ICatalogRepository catalog,
        DirectoryBackupService directoryBackup,
        IDestinationResolver destinationResolver)
    {
        _logger = logger;
        _catalog = catalog;
        _directoryBackup = directoryBackup;
        _destinationResolver = destinationResolver;
    }

    /// <summary>
    /// Resolve a set's destination to a live path, following any drive-letter
    /// reassignment, backfilling/persisting the volume identity when it
    /// changes, and logging a letter move.  Returns the live target directory,
    /// or <c>null</c> when the destination volume is not currently connected
    /// (the caller should skip the run).
    /// </summary>
    private async Task<string?> ResolveDestinationAsync(BackupSet set, JobOptions opts, CancellationToken ct)
    {
        var resolution = _destinationResolver.Resolve(opts);

        if (resolution.MetadataChanged)
            await _catalog.UpdateBackupSetAsync(set, ct);

        if (!resolution.IsConnected)
        {
            _logger.LogWarning(
                "Skipping backup for \"{Name}\": destination drive not connected ({Path}).",
                set.Name, resolution.PreviousPath ?? "unknown path");
            return null;
        }

        if (resolution.LetterChanged)
        {
            _logger.LogInformation(
                "Destination drive for \"{Name}\" moved: {Old} -> {New}. Updated automatically.",
                set.Name, resolution.PreviousPath, resolution.LivePath);
        }

        return resolution.LivePath;
    }

    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LithicBackup Worker started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReloadBackupSetsAsync(stoppingToken);
                    await CheckSchedulesAsync(stoppingToken);
                    await CheckContinuousAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in worker loop.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        finally
        {
            foreach (var reader in _journalReaders.Values)
                reader?.Dispose();
            _journalReaders.Clear();
            _logger.LogInformation("LithicBackup Worker stopped.");
        }
    }

    // ------------------------------------------------------------------
    // Set management
    // ------------------------------------------------------------------

    /// <summary>
    /// Reload backup sets from the catalog and update per-set state.
    /// </summary>
    private async Task ReloadBackupSetsAsync(CancellationToken ct)
    {
        var allSets = await _catalog.GetAllBackupSetsAsync(ct);
        var seen = new HashSet<int>();

        foreach (var set in allSets)
        {
            seen.Add(set.Id);
            var schedule = set.JobOptions?.Schedule;

            // Only directory-mode sets with an enabled schedule are relevant.
            bool active = schedule is { Enabled: true }
                          && set.JobOptions?.TargetDirectory is not null;

            if (!_sets.TryGetValue(set.Id, out var state))
            {
                state = new SetState
                {
                    BackupSet = set,
                    LastRunUtc = set.LastBackupUtc ?? DateTime.MinValue,
                };
                _sets[set.Id] = state;
            }
            else
            {
                state.BackupSet = set;
            }

            state.IsActive = active;

            // The directories the user actually selected (not the whole drive
            // roots). Used to decide which volumes to read and which changes
            // are relevant.
            state.WatchRoots = ResolveWatchRoots(set);
        }

        // Remove sets that no longer exist in the catalog.
        foreach (var id in _sets.Keys)
        {
            if (!seen.Contains(id))
                _sets.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Resolve the set of directories that are change-relevant for a backup set.
    /// Prefers the actually-selected directories from the selection tree; falls
    /// back to <see cref="BackupSet.SourceRoots"/> for legacy sets.
    /// </summary>
    private static List<string> ResolveWatchRoots(BackupSet set)
    {
        if (set.SourceSelections is { Count: > 0 })
        {
            var roots = SourceSelection.CollectSelectedRoots(set.SourceSelections);
            if (roots.Count > 0)
                return roots;
        }

        return set.SourceRoots.ToList();
    }

    // ------------------------------------------------------------------
    // Scheduled & interval checks
    // ------------------------------------------------------------------

    private async Task CheckSchedulesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var (_, state) in _sets)
        {
            if (!state.IsActive || state.BackupSet.JobOptions?.Schedule is not { } schedule)
                continue;

            bool shouldRun = schedule.Mode switch
            {
                ScheduleMode.Interval => ShouldRunInterval(state, schedule, now),
                ScheduleMode.Daily => ShouldRunDaily(state, schedule, now),
                _ => false, // Continuous handled separately.
            };

            if (shouldRun)
                await RunFullBackupAsync(state, ct);
        }
    }

    private static bool ShouldRunInterval(SetState state, BackupSchedule schedule, DateTime now)
    {
        if (schedule.IntervalHours <= 0) return false;
        var elapsed = now - state.LastRunUtc;
        return elapsed.TotalHours >= schedule.IntervalHours;
    }

    private static bool ShouldRunDaily(SetState state, BackupSchedule schedule, DateTime now)
    {
        var localNow = now.ToLocalTime();

        // Has it already run today (in local time)?
        var localLastRun = state.LastRunUtc.ToLocalTime();
        if (localLastRun.Date == localNow.Date)
            return false;

        // Is it past the scheduled time?
        return localNow.Hour > schedule.DailyHour
               || (localNow.Hour == schedule.DailyHour && localNow.Minute >= schedule.DailyMinute);
    }

    // ------------------------------------------------------------------
    // Continuous / USN-journal-driven
    // ------------------------------------------------------------------

    private async Task CheckContinuousAsync(CancellationToken ct)
    {
        var continuousSets = _sets.Values
            .Where(s => s.IsActive
                        && s.BackupSet.JobOptions?.Schedule is { Mode: ScheduleMode.Continuous })
            .ToList();

        if (continuousSets.Count == 0)
            return;

        var now = DateTime.UtcNow;

        // 1. Read changes from every NTFS volume these sets care about, and
        //    route each changed path to the sets that include it.
        var driveLetters = continuousSets
            .SelectMany(s => s.WatchRoots)
            .Select(GetDriveLetter)
            .Where(c => c != '\0')
            .Distinct()
            .ToList();

        foreach (var drive in driveLetters)
        {
            ct.ThrowIfCancellationRequested();

            var changes = await ReadVolumeChangesAsync(drive, ct);
            foreach (var change in changes)
            {
                if (change.IsDirectory)
                    continue;

                foreach (var state in continuousSets)
                {
                    if (PathBelongsToSet(state.BackupSet, change.FullPath))
                    {
                        state.Pending[change.FullPath] =
                            state.Pending.TryGetValue(change.FullPath, out var t)
                                ? (t.First, now)
                                : (now, now);
                    }
                }
            }
        }

        // 2. Per-file debounce: back up files that have been quiet for the
        //    debounce window, or that have been pending past the max-wait cap.
        foreach (var state in continuousSets)
        {
            if (state.Pending.Count == 0)
                continue;

            var schedule = state.BackupSet.JobOptions!.Schedule!;
            var ready = new List<string>();

            foreach (var (path, t) in state.Pending.ToList())
            {
                var quiet = now - t.Last;
                var waited = now - t.First;
                if (quiet.TotalSeconds >= schedule.DebounceSeconds || waited >= MaxContinuousWait)
                {
                    ready.Add(path);
                    state.Pending.Remove(path);
                }
            }

            if (ready.Count > 0)
                await RunTargetedBackupAsync(state, ready, ct);
        }
    }

    /// <summary>
    /// Read new change records for a volume since the saved cursor, advancing
    /// and persisting the cursor. Opens (and, if necessary, creates) the
    /// journal on first use; returns empty when the volume has no usable journal.
    /// </summary>
    private async Task<IReadOnlyList<UsnChange>> ReadVolumeChangesAsync(char drive, CancellationToken ct)
    {
        if (!_journalReaders.TryGetValue(drive, out var reader))
        {
            reader = UsnJournalReader.TryOpen(drive);
            _journalReaders[drive] = reader;
            if (reader is null)
            {
                _logger.LogInformation(
                    "USN journal unavailable on {Drive}: — continuous detection disabled for this volume.",
                    drive);
            }
        }

        if (reader is null)
            return [];

        var volumeId = reader.VolumeId;
        var cursor = await _catalog.GetUsnCursorAsync(volumeId, ct);

        // First run for this volume, or the journal was re-created: start from
        // the current end so we don't replay the entire journal as "changes".
        // Initial file state is captured by the scheduled/manual full backup.
        if (cursor is null || cursor.Value.JournalId != reader.JournalId)
        {
            await _catalog.SaveUsnCursorAsync(
                new UsnCursor(volumeId, reader.JournalId, reader.CurrentNextUsn, DateTime.UtcNow), ct);
            return [];
        }

        long startUsn = cursor.Value.NextUsn;
        IReadOnlyList<UsnChange> changes;
        long nextUsn;
        try
        {
            changes = reader.ReadChanges(startUsn, out nextUsn, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reading USN journal on {Drive}: — will reopen next poll.", drive);
            reader.Dispose();
            _journalReaders.Remove(drive);
            return [];
        }

        if (nextUsn != startUsn)
        {
            await _catalog.SaveUsnCursorAsync(
                new UsnCursor(volumeId, reader.JournalId, nextUsn, DateTime.UtcNow), ct);
        }

        return changes;
    }

    /// <summary>
    /// Whether a changed path belongs to a backup set, honoring the selection
    /// tree's inclusion rules (or the raw source roots for legacy sets).
    /// </summary>
    private static bool PathBelongsToSet(BackupSet set, string path)
    {
        if (set.SourceSelections is { Count: > 0 })
            return SourceSelection.IsPathIncluded(set.SourceSelections, path);

        return set.SourceRoots.Any(root => IsUnderRoot(path, root));
    }

    private static char GetDriveLetter(string path)
    {
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            return char.ToUpperInvariant(path[0]);
        return '\0';
    }

    /// <summary>
    /// Whether <paramref name="path"/> lies within <paramref name="root"/>,
    /// respecting directory boundaries (so <c>C:\py</c> does not match
    /// <c>C:\python\…</c>).
    /// </summary>
    private static bool IsUnderRoot(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Execute backups
    // ------------------------------------------------------------------

    /// <summary>Run a targeted backup of an explicit set of changed paths.</summary>
    private async Task RunTargetedBackupAsync(SetState state, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (!await _backupLock.WaitAsync(0, ct))
        {
            // Another backup is in progress — requeue these paths for next poll.
            var now = DateTime.UtcNow;
            foreach (var p in paths)
                state.Pending.TryAdd(p, (now, now));
            return;
        }

        try
        {
            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(set, opts, ct);
            if (targetDir is null)
            {
                // Destination not connected — requeue these paths for next poll.
                var requeueNow = DateTime.UtcNow;
                foreach (var p in paths)
                    state.Pending.TryAdd(p, (requeueNow, requeueNow));
                return;
            }
            var job = BuildJob(set, opts, targetDir);

            var retentionTiers = opts.RetentionTiers.Count > 0
                ? opts.RetentionTiers
                : VersionRetentionService.DefaultTiers;

            _logger.LogInformation(
                "Continuous backup for \"{Name}\": {Count} changed file(s).", set.Name, paths.Count);

            var result = await _directoryBackup.ExecuteTargetedAsync(
                job, targetDir, paths, retentionTiers, ct);

            state.LastRunUtc = DateTime.UtcNow;

            if (result.Success)
            {
                if (result.BytesWritten > 0)
                    _logger.LogInformation(
                        "Continuous backup for \"{Name}\": {Bytes} bytes written.",
                        set.Name, result.BytesWritten);
            }
            else
            {
                _logger.LogWarning(
                    "Continuous backup for \"{Name}\" had {Count} failed file(s).",
                    set.Name, result.FailedFiles.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Continuous backup for \"{Name}\" cancelled.", state.BackupSet.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Continuous backup failed for \"{Name}\".", state.BackupSet.Name);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>Run a full scan-and-backup (scheduled interval/daily runs).</summary>
    private async Task RunFullBackupAsync(SetState state, CancellationToken ct)
    {
        if (!await _backupLock.WaitAsync(0, ct))
        {
            _logger.LogInformation(
                "Skipping backup for \"{Name}\" — another backup is in progress.", state.BackupSet.Name);
            return;
        }

        try
        {
            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(set, opts, ct);
            if (targetDir is null)
                return; // Destination not connected; retry on the next scheduled run.

            _logger.LogInformation("Starting backup for \"{Name}\" → {Target}", set.Name, targetDir);

            var job = BuildJob(set, opts, targetDir);

            var (diff, totalBytes, totalFiles) = await _directoryBackup.PlanAsync(job, ct);

            if (totalFiles == 0)
            {
                _logger.LogInformation("Nothing to back up for \"{Name}\".", set.Name);
                state.LastRunUtc = DateTime.UtcNow;
                return;
            }

            _logger.LogInformation(
                "Plan for \"{Name}\": {Files} files, {Bytes} bytes ({New} new, {Changed} changed, {Deleted} deleted).",
                set.Name, totalFiles, totalBytes,
                diff.NewFiles.Count, diff.ChangedFiles.Count, diff.DeletedFiles.Count);

            var retentionTiers = opts.RetentionTiers.Count > 0
                ? opts.RetentionTiers
                : VersionRetentionService.DefaultTiers;

            var result = await _directoryBackup.ExecuteAsync(
                job, targetDir, retentionTiers, progress: null, ct);

            state.LastRunUtc = DateTime.UtcNow;

            if (result.Success)
            {
                _logger.LogInformation(
                    "Backup completed for \"{Name}\": {Bytes} bytes written.",
                    set.Name, result.BytesWritten);
            }
            else
            {
                _logger.LogWarning(
                    "Backup for \"{Name}\" completed with {Count} failed file(s).",
                    set.Name, result.FailedFiles.Count);

                foreach (var f in result.FailedFiles.Take(10))
                    _logger.LogWarning("  Failed: {Path} — {Error}", f.Path, f.Error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Backup for \"{Name}\" cancelled.", state.BackupSet.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup failed for \"{Name}\".", state.BackupSet.Name);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>Build a <see cref="BackupJob"/> from a set's saved options.</summary>
    private static BackupJob BuildJob(BackupSet set, JobOptions opts, string targetDir)
    {
        return new BackupJob
        {
            BackupSetId = set.Id,
            Sources = set.SourceSelections ?? set.SourceRoots
                .Select(root => new SourceSelection
                {
                    Path = root,
                    IsDirectory = true,
                    IsSelected = true,
                    AutoIncludeNewSubdirectories = true,
                })
                .ToList(),
            ZipMode = opts.ZipMode,
            FilesystemType = opts.FilesystemType,
            CapacityOverrideBytes = opts.CapacityOverrideBytes,
            VerifyAfterBurn = opts.VerifyAfterBurn,
            IncludeCatalogOnDisc = opts.IncludeCatalogOnDisc,
            AllowFileSplitting = opts.AllowFileSplitting,
            TargetDirectory = targetDir,
            EnableFileDeduplication = opts.EnableFileDeduplication,
            EnableDeduplication = opts.EnableDeduplication,
            DeduplicationBlockSize = opts.DeduplicationBlockSize,
            ExcludedExtensions = opts.ExcludedExtensions,
            RetentionTiers = opts.RetentionTiers,
            TierSets = opts.TierSets,
            // Machine-global memory budget (shared with the interactive app via
            // settings.json) so scheduled backups honor the same limit.
            MemoryBudget = UserSettings.Load().MemoryBudget,
        };
    }

    // ------------------------------------------------------------------

    /// <summary>Per-backup-set tracking state.</summary>
    private sealed class SetState
    {
        public required BackupSet BackupSet { get; set; }
        public DateTime LastRunUtc { get; set; }
        public bool IsActive { get; set; }

        /// <summary>
        /// Directories actually selected for this set (not the drive roots).
        /// Used to pick which volumes to read and which changes are relevant.
        /// </summary>
        public List<string> WatchRoots { get; set; } = [];

        /// <summary>
        /// Pending continuous-mode changes for this set: path → (first seen,
        /// last seen). Drives per-file debounce and the max-wait cap.
        /// </summary>
        public Dictionary<string, (DateTime First, DateTime Last)> Pending { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
