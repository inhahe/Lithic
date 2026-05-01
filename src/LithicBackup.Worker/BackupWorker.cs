using System.Collections.Concurrent;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Services;

namespace LithicBackup.Worker;

/// <summary>
/// Background service that monitors backup set schedules and file-system
/// changes, executing directory backups automatically.
/// </summary>
public sealed class BackupWorker : BackgroundService
{
    private readonly ILogger<BackupWorker> _logger;
    private readonly ICatalogRepository _catalog;
    private readonly DirectoryBackupService _directoryBackup;
    private readonly IFileSystemMonitor _monitor;

    /// <summary>How often we reload backup sets and check schedules.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Per-set tracking state.</summary>
    private readonly ConcurrentDictionary<int, SetState> _sets = new();

    /// <summary>Only one backup runs at a time.</summary>
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    /// <summary>Fired by the file-system monitor; accumulated here for debounce.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);

    public BackupWorker(
        ILogger<BackupWorker> logger,
        ICatalogRepository catalog,
        DirectoryBackupService directoryBackup,
        IFileSystemMonitor monitor)
    {
        _logger = logger;
        _catalog = catalog;
        _directoryBackup = directoryBackup;
        _monitor = monitor;
    }

    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LithicBackup Worker started.");

        _monitor.FileChanged += OnFileChanged;

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
            _monitor.FileChanged -= OnFileChanged;
            _monitor.Stop();
            _logger.LogInformation("LithicBackup Worker stopped.");
        }
    }

    // ------------------------------------------------------------------
    // Set management
    // ------------------------------------------------------------------

    /// <summary>
    /// Reload backup sets from the catalog and update per-set state.
    /// Starts/stops file-system watchers as needed.
    /// </summary>
    private async Task ReloadBackupSetsAsync(CancellationToken ct)
    {
        var allSets = await _catalog.GetAllBackupSetsAsync(ct);
        var seen = new HashSet<int>();

        var watchDirs = new List<string>();

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

            // Collect directories for continuous-mode watchers.
            if (active && schedule!.Mode == ScheduleMode.Continuous)
            {
                watchDirs.AddRange(set.SourceRoots.Where(Directory.Exists));
            }
        }

        // Remove sets that no longer exist in the catalog.
        foreach (var id in _sets.Keys)
        {
            if (!seen.Contains(id))
                _sets.TryRemove(id, out _);
        }

        // Restart the file-system monitor with the current watch list.
        // (Start is idempotent — it stops existing watchers first.)
        var distinctDirs = watchDirs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctDirs.Count > 0)
        {
            _monitor.Stop();
            _monitor.Start(distinctDirs);
        }
    }

    // ------------------------------------------------------------------
    // Scheduled & interval checks
    // ------------------------------------------------------------------

    private async Task CheckSchedulesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var (id, state) in _sets)
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
                await RunBackupAsync(state, ct);
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
    // Continuous / change-triggered
    // ------------------------------------------------------------------

    private void OnFileChanged(object? sender, FileChangeEventArgs e)
    {
        _pendingChanges[e.FullPath] = DateTime.UtcNow;
    }

    private async Task CheckContinuousAsync(CancellationToken ct)
    {
        if (_pendingChanges.IsEmpty)
            return;

        // Find continuous-mode sets whose source roots overlap with pending changes.
        var now = DateTime.UtcNow;

        foreach (var (id, state) in _sets)
        {
            if (!state.IsActive
                || state.BackupSet.JobOptions?.Schedule is not { Mode: ScheduleMode.Continuous } schedule)
                continue;

            // Are there pending changes under this set's source roots?
            bool hasChanges = false;
            DateTime latestChange = DateTime.MinValue;

            foreach (var (path, changeTime) in _pendingChanges)
            {
                if (state.BackupSet.SourceRoots.Any(root =>
                    path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    hasChanges = true;
                    if (changeTime > latestChange)
                        latestChange = changeTime;
                }
            }

            if (!hasChanges)
                continue;

            // Debounce: wait until no changes for DebounceSeconds.
            var quiet = now - latestChange;
            if (quiet.TotalSeconds < schedule.DebounceSeconds)
                continue;

            // Debounce satisfied — run the backup.
            // Clear pending changes for this set's roots before running so
            // new changes that arrive during the backup aren't lost.
            foreach (var path in _pendingChanges.Keys.ToList())
            {
                if (state.BackupSet.SourceRoots.Any(root =>
                    path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingChanges.TryRemove(path, out _);
                }
            }

            await RunBackupAsync(state, ct);
        }
    }

    // ------------------------------------------------------------------
    // Execute backup
    // ------------------------------------------------------------------

    private async Task RunBackupAsync(SetState state, CancellationToken ct)
    {
        // Only one backup at a time.
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
            var targetDir = opts.TargetDirectory!;

            _logger.LogInformation("Starting backup for \"{Name}\" → {Target}", set.Name, targetDir);

            // Build the BackupJob from saved options.
            var job = new BackupJob
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
            };

            // Plan.
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

            // Execute.
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

    // ------------------------------------------------------------------

    /// <summary>Per-backup-set tracking state.</summary>
    private sealed class SetState
    {
        public required BackupSet BackupSet { get; set; }
        public DateTime LastRunUtc { get; set; }
        public bool IsActive { get; set; }
    }
}
