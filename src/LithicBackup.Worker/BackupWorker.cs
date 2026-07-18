using System.Collections.Concurrent;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

namespace LithicBackup.Worker;

/// <summary>
/// Background service that monitors backup set schedules and file-system
/// changes, executing directory backups automatically.
/// </summary>
/// <remarks>
/// Continuous-mode sets on NTFS volumes are driven by the USN change journal: it
/// never drops events, records changes that happened while the service was
/// offline, and lets us version exactly the files that changed instead of
/// rescanning whole drives. Each poll reads new journal records per volume, routes
/// the changed paths to the sets that include them, debounces each file
/// individually, then batches the "ready" files into a single targeted backup per
/// set.
///
/// Volumes without a usable journal (non-NTFS or inaccessible) fall back to a
/// <see cref="FileSystemWatcher"/>. Unlike the journal that fallback is not
/// persistent — it only observes changes while the service runs — so its sets are
/// reconciled with a full timestamp/size scan whenever watching (re)starts
/// (covering process restart) and whenever the watcher's buffer overflows (its
/// change list is then incomplete). Live watcher events otherwise feed the same
/// per-file debounce → targeted-backup pipeline as the journal.
/// </remarks>
public sealed class BackupWorker : BackgroundService
{
    private readonly ILogger<BackupWorker> _logger;
    private readonly ICatalogRepository _catalog;
    private readonly DirectoryBackupService _directoryBackup;
    private readonly IDestinationResolver _destinationResolver;
    private readonly ISourceResolver _sourceResolver;

    /// <summary>
    /// Default cadence for reloading backup sets, checking schedules, and reading
    /// journals. Continuous sets can request a shorter interval via
    /// <see cref="BackupSchedule.PollIntervalSeconds"/>; see
    /// <see cref="ComputeEffectivePollInterval"/>.
    /// </summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Floor on the continuous poll interval so a tiny configured value
    /// can't spin the worker loop into a busy-poll.</summary>
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default upper bound on how long a continuous-mode change may stay pending
    /// before it is backed up regardless of ongoing activity. Prevents a
    /// constantly-written file (e.g. an active log) from starving its own
    /// debounce window. Overridable per set via
    /// <see cref="BackupSchedule.MaxWaitSeconds"/>; this value is the fallback
    /// when a set leaves it at 0 (or an out-of-range value).
    /// </summary>
    private static readonly TimeSpan DefaultMaxContinuousWait = TimeSpan.FromMinutes(5);

    /// <summary>Per-set tracking state.</summary>
    private readonly ConcurrentDictionary<int, SetState> _sets = new();

    /// <summary>
    /// Per-volume USN journal readers, opened lazily. A null value means the
    /// volume has no usable journal (non-NTFS or inaccessible) — cached so we
    /// don't retry it on every poll. Volumes with a null reader use the
    /// <see cref="_fsMonitor"/> fallback instead.
    /// </summary>
    private readonly Dictionary<char, UsnJournalReader?> _journalReaders = new();

    /// <summary>
    /// FileSystemWatcher-based fallback for volumes without a usable USN journal
    /// (non-NTFS or inaccessible). Unlike the journal it is not persistent — it
    /// only sees changes while the service runs, so its sets are reconciled with a
    /// full timestamp/size scan whenever watching (re)starts (covering process
    /// restart) and whenever its buffer overflows. Watches every non-NTFS watch
    /// root across all continuous sets at once.
    /// </summary>
    private readonly FileSystemMonitorImpl _fsMonitor = new();

    /// <summary>
    /// Changes pushed by <see cref="_fsMonitor"/> from watcher threads, drained on
    /// the poll thread into each set's <c>Pending</c> map (mirroring the USN path).
    /// A queue keeps all <c>Pending</c> mutation single-threaded. The change type
    /// is kept so a newly created/renamed directory (a possible bulk move-in) can
    /// be expanded to its files, while a mere directory-timestamp change is ignored.
    /// </summary>
    private readonly ConcurrentQueue<(string Path, FileChangeType Type)> _fswChanges = new();

    /// <summary>
    /// Watch roots whose <see cref="_fsMonitor"/> buffer overflowed (change list
    /// incomplete). Drained on the poll thread to flag affected sets for a full
    /// reconciling scan.
    /// </summary>
    private readonly ConcurrentQueue<string> _fswOverflows = new();

    /// <summary>
    /// Roots the <see cref="_fsMonitor"/> is currently watching, kept sorted so we
    /// only restart the watcher (and reconcile) when the set actually changes.
    /// </summary>
    private List<string> _fswRoots = new();

    /// <summary>Only one backup runs at a time.</summary>
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    public BackupWorker(
        ILogger<BackupWorker> logger,
        ICatalogRepository catalog,
        DirectoryBackupService directoryBackup,
        IDestinationResolver destinationResolver,
        ISourceResolver sourceResolver)
    {
        _logger = logger;
        _catalog = catalog;
        _directoryBackup = directoryBackup;
        _destinationResolver = destinationResolver;
        _sourceResolver = sourceResolver;

        // Watcher events arrive on thread-pool threads; buffer them and let the
        // poll loop route them, so per-set state stays single-threaded.
        _fsMonitor.FileChanged += (_, e) => _fswChanges.Enqueue((e.FullPath, e.ChangeType));
        _fsMonitor.Overflow += (_, e) => _fswOverflows.Enqueue(e.Root);
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

    /// <summary>
    /// Follow the set's source drives across any drive-letter reassignment
    /// (rewriting source paths and persisting the change), and report source
    /// availability.  Returns <c>false</c> when no configured source location is
    /// currently reachable (the caller should skip the run rather than back up
    /// nothing).  Partially-missing sources are logged and the run proceeds.
    /// </summary>
    private async Task<bool> ResolveSourcesAsync(BackupSet set, CancellationToken ct)
    {
        var resolution = _sourceResolver.Resolve(set);

        if (resolution.MetadataChanged)
            await _catalog.UpdateBackupSetAsync(set, ct);

        if (resolution.LetterChanges.Count > 0)
        {
            _logger.LogInformation(
                "Source drive(s) for \"{Name}\" moved: {Changes}. Updated automatically.",
                set.Name, string.Join(", ", resolution.LetterChanges));
        }

        if (!resolution.AnyAvailable)
        {
            _logger.LogWarning(
                "Skipping backup for \"{Name}\": no source location is currently available ({Missing}).",
                set.Name, string.Join(", ", resolution.MissingSources));
            return false;
        }

        if (resolution.MissingSources.Count > 0)
        {
            _logger.LogWarning(
                "Some source locations for \"{Name}\" are not available and will be skipped: {Missing}.",
                set.Name, string.Join(", ", resolution.MissingSources));
        }

        return true;
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

                await Task.Delay(ComputeEffectivePollInterval(), stoppingToken);
            }
        }
        finally
        {
            foreach (var reader in _journalReaders.Values)
                reader?.Dispose();
            _journalReaders.Clear();
            _fsMonitor.Dispose();
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

    /// <summary>
    /// The loop delay for the next poll: the smallest
    /// <see cref="BackupSchedule.PollIntervalSeconds"/> requested by any active
    /// continuous set (clamped to <see cref="MinPollInterval"/>), or
    /// <see cref="DefaultPollInterval"/> when no continuous set is active. A single
    /// shared loop serves every set, so the most demanding continuous set sets the
    /// pace; interval/daily sets are unaffected by a faster cadence.
    /// </summary>
    private TimeSpan ComputeEffectivePollInterval()
    {
        var effective = DefaultPollInterval;

        foreach (var state in _sets.Values)
        {
            if (!state.IsActive
                || state.BackupSet.JobOptions?.Schedule is not { Mode: ScheduleMode.Continuous } schedule)
                continue;

            if (schedule.PollIntervalSeconds <= 0)
                continue; // treat unset/invalid as "use default"

            var requested = TimeSpan.FromSeconds(schedule.PollIntervalSeconds);
            if (requested < effective)
                effective = requested;
        }

        return effective < MinPollInterval ? MinPollInterval : effective;
    }

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

        var truncatedDrives = new HashSet<char>();

        // Newly-created directories that are included via a parent's auto-include
        // rule but aren't yet explicit selections. Collected during change routing
        // and pinned into their sets afterwards (see MaterializeDiscoveredDirectoriesAsync)
        // so their membership persists past the user turning auto-include off.
        var discoveredDirs = new List<(SetState State, string Dir)>();

        // [rename-trace] Running totals for the once-per-poll summary below.
        int traceTotalChanges = 0, traceTotalMoves = 0;

        foreach (var drive in driveLetters)
        {
            ct.ThrowIfCancellationRequested();

            var (changes, moves, truncated) = await ReadVolumeChangesAsync(drive, ct);
            if (truncated)
                truncatedDrives.Add(drive);

            traceTotalChanges += changes.Count;
            traceTotalMoves += moves.Count;

            // [rename-trace] step 1: what the USN journal actually reported for
            // this volume. If a directory rename doesn't show up here, detection
            // (not propagation) is the problem.
            if (moves.Count > 0)
                _logger.LogDebug(
                    "[rename-trace] step 1: USN journal on {Drive}: reported {Count} move(s): {Moves}",
                    drive, moves.Count,
                    string.Join(" | ", moves.Select(m =>
                        $"{(m.IsDirectory ? "DIR" : "file")} '{m.OldPath}' -> '{m.NewPath}'")));

            foreach (var change in changes)
            {
                if (change.IsDirectory)
                {
                    // A brand-new directory that the set covers only through a
                    // parent's auto-include rule should become a permanent explicit
                    // selection. Skip plain metadata changes on existing directories
                    // (their child files carry their own change records) and dirs
                    // already gone by poll time.
                    bool created = (change.Reason & UsnJournalReader.UsnReasonFileCreate) != 0;
                    if (created && Directory.Exists(change.FullPath))
                    {
                        foreach (var state in continuousSets)
                        {
                            if (PathBelongsToSet(state.BackupSet, change.FullPath))
                                discoveredDirs.Add((state, change.FullPath));
                        }
                    }
                    continue;
                }

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

            // Route same-volume relocations to every set either endpoint touches.
            // The apply step (RunMovesAsync) decides, with catalog access, whether
            // to relocate (both endpoints in the set), delete (item left the set),
            // or back up fresh (item entered the set). Preserve journal order.
            foreach (var move in moves)
            {
                foreach (var state in continuousSets)
                {
                    bool oldIn = PathBelongsToSet(state.BackupSet, move.OldPath);
                    bool newIn = PathBelongsToSet(state.BackupSet, move.NewPath);
                    if (oldIn || newIn)
                    {
                        state.PendingMoves.Add(move);
                        // [rename-trace] step 2: this move was matched to a set and
                        // queued for the apply step.
                        _logger.LogDebug(
                            "[rename-trace] step 2: queued move '{Old}' -> '{New}' (isDir={IsDir}) " +
                            "for set \"{Name}\" (oldIn={OldIn}, newIn={NewIn})",
                            move.OldPath, move.NewPath, move.IsDirectory,
                            state.BackupSet.Name, oldIn, newIn);
                    }
                }
            }
        }

        // [rename-trace] Once-per-poll heartbeat so you can confirm the continuous
        // loop is actually running (~every 30s) and see whether anything was
        // detected. If you rename a directory and never see a move counted here,
        // the change never reached the worker.
        _logger.LogDebug(
            "[rename-trace] continuous poll: {Sets} set(s), {Drives} drive(s) read, " +
            "{Changes} file change(s), {Moves} move(s) detected",
            continuousSets.Count, driveLetters.Count, traceTotalChanges, traceTotalMoves);

        // 1b. Fallback for volumes without a usable USN journal (non-NTFS): drive
        //     them with a FileSystemWatcher instead. A watcher only sees live
        //     changes, not offline ones, so watch every non-NTFS watch root and
        //     reconcile with a full timestamp/size scan whenever watching
        //     (re)starts or its buffer overflows.
        MaintainFallbackWatchers(continuousSets);
        DrainFallbackChanges(continuousSets, now, discoveredDirs);

        // Pin any newly-discovered auto-include folders into their sets as explicit
        // selections so their membership survives auto-include being turned off.
        await MaterializeDiscoveredDirectoriesAsync(discoveredDirs, ct);

        // 2. If a watched volume's journal lost continuity (it wrapped or was
        //    recreated while the service was down), the incremental USN stream
        //    skipped everything that changed in the gap — those records are gone
        //    from the journal and can't be read here. Flag each affected set for
        //    a full reconciling scan so nothing is silently missed.
        if (truncatedDrives.Count > 0)
        {
            foreach (var state in continuousSets)
            {
                if (state.NeedsReconcile)
                    continue;

                bool affected = state.WatchRoots
                    .Select(GetDriveLetter)
                    .Any(truncatedDrives.Contains);

                if (affected)
                {
                    state.NeedsReconcile = true;
                    _logger.LogWarning(
                        "USN journal continuity lost on a source volume for \"{Name}\" " +
                        "(journal wrapped or was recreated during downtime). Running a full " +
                        "reconciling backup to capture changes missed while the service was off.",
                        state.BackupSet.Name);
                }
            }
        }

        // 3. Reconcile flagged sets with a full scan. A full scan supersedes any
        //    queued per-file deltas for that set. The flag is cleared only once
        //    the scan actually runs, so it retries on later polls if a backup is
        //    already in progress or the destination is offline.
        foreach (var state in continuousSets)
        {
            if (!state.NeedsReconcile)
                continue;

            if (await RunFullBackupAsync(state, ct))
            {
                state.NeedsReconcile = false;
                state.Pending.Clear();
                // A full scan reconciles the destination against the live source
                // tree, so any queued relocations are already accounted for.
                state.PendingMoves.Clear();
            }
        }

        // 3b. Apply pending relocations. Moves are discrete, atomic events (no
        //     debounce): relocate the destination copy in place rather than
        //     re-copying. Applied before the per-file debounce so a moved-and-
        //     edited file relocates first, then its content update copies to the
        //     new path.
        foreach (var state in continuousSets)
        {
            if (state.NeedsReconcile || state.PendingMoves.Count == 0)
                continue;

            await RunMovesAsync(state, ct);
        }

        // 4. Per-file debounce: back up files that have been quiet for the
        //    debounce window, or that have been pending past the max-wait cap.
        foreach (var state in continuousSets)
        {
            if (state.NeedsReconcile || state.Pending.Count == 0)
                continue;

            var schedule = state.BackupSet.JobOptions!.Schedule!;
            var ready = new List<string>();

            // Per-set cap on how long an actively-changing file may stay pending
            // before it is versioned anyway; falls back to the default when the
            // set leaves it unset (0) or supplies a nonsensical negative value.
            var maxWait = schedule.MaxWaitSeconds > 0
                ? TimeSpan.FromSeconds(schedule.MaxWaitSeconds)
                : DefaultMaxContinuousWait;

            foreach (var (path, t) in state.Pending.ToList())
            {
                var quiet = now - t.Last;
                var waited = now - t.First;
                if (quiet.TotalSeconds >= schedule.DebounceSeconds || waited >= maxWait)
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
    /// Get the USN journal reader for a drive, opening it once and caching the
    /// result (including a null result, meaning the volume has no usable journal —
    /// non-NTFS or inaccessible — and must use the FileSystemWatcher fallback).
    /// Callable only from the poll thread.
    /// </summary>
    private UsnJournalReader? GetJournalReader(char drive)
    {
        if (!_journalReaders.TryGetValue(drive, out var reader))
        {
            reader = UsnJournalReader.TryOpen(drive);
            _journalReaders[drive] = reader;
            if (reader is null)
            {
                _logger.LogInformation(
                    "USN journal unavailable on {Drive}: — using file-system watcher fallback " +
                    "for this volume (live changes only; reconciled by full scan on restart).",
                    drive);
            }
        }

        return reader;
    }

    /// <summary>
    /// Read new change records for a volume since the saved cursor, advancing
    /// and persisting the cursor. Opens (and, if necessary, creates) the
    /// journal on first use; returns empty when the volume has no usable journal.
    /// </summary>
    private async Task<(IReadOnlyList<UsnChange> Changes, IReadOnlyList<UsnMove> Moves, bool Truncated)> ReadVolumeChangesAsync(
        char drive, CancellationToken ct)
    {
        var reader = GetJournalReader(drive);
        if (reader is null)
            return ([], [], false);

        var volumeId = reader.VolumeId;
        var cursor = await _catalog.GetUsnCursorAsync(volumeId, ct);

        // First time we've tracked this volume: seed the cursor to the current
        // end and watch forward. The set's initial full backup captures the
        // baseline, so there is no prior gap to reconcile here.
        if (cursor is null)
        {
            await _catalog.SaveUsnCursorAsync(
                new UsnCursor(volumeId, reader.JournalId, reader.CurrentNextUsn, DateTime.UtcNow), ct);
            return ([], [], false);
        }

        // The journal was deleted and recreated since we last read it (e.g. a
        // very long outage, or `fsutil usn deletejournal`). Our saved position is
        // meaningless against the new journal and the intervening changes are gone
        // from it — re-seed to the current end and reconcile with a full scan.
        if (cursor.Value.JournalId != reader.JournalId)
        {
            await _catalog.SaveUsnCursorAsync(
                new UsnCursor(volumeId, reader.JournalId, reader.CurrentNextUsn, DateTime.UtcNow), ct);
            return ([], [], true);
        }

        long startUsn = cursor.Value.NextUsn;
        IReadOnlyList<UsnChange> changes;
        IReadOnlyList<UsnMove> moves;
        long nextUsn;
        bool truncated;
        try
        {
            changes = reader.ReadChanges(startUsn, out nextUsn, out truncated, out moves, ct);
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
            return ([], [], false);
        }

        if (truncated)
        {
            // Our saved cursor points before the journal's retained window: the
            // records that changed while we were down have been purged (the
            // journal wrapped). Re-seed the cursor to the journal's current end so
            // live detection resumes instead of failing forever, and tell the
            // caller to reconcile the missed gap with a full scan.
            long resumeUsn = reader.TryRefreshPosition(out _, out long liveNext)
                ? liveNext
                : reader.CurrentNextUsn;
            await _catalog.SaveUsnCursorAsync(
                new UsnCursor(volumeId, reader.JournalId, resumeUsn, DateTime.UtcNow), ct);
            return ([], [], true);
        }

        if (nextUsn != startUsn)
        {
            await _catalog.SaveUsnCursorAsync(
                new UsnCursor(volumeId, reader.JournalId, nextUsn, DateTime.UtcNow), ct);
        }

        return (changes, moves, false);
    }

    /// <summary>
    /// Whether a watch root lives on a volume that has no usable USN journal and
    /// therefore relies on the FileSystemWatcher fallback. Callable only from the
    /// poll thread (touches the journal-reader cache).
    /// </summary>
    private bool UsesFallbackVolume(string root)
    {
        var drive = GetDriveLetter(root);
        return drive != '\0' && GetJournalReader(drive) is null;
    }

    /// <summary>
    /// Point the fallback FileSystemWatcher at the current set of non-NTFS watch
    /// roots across all continuous sets, restarting it only when that set actually
    /// changes. Any (re)start — including the first one after a process restart,
    /// when the watcher wasn't running at all — flags every affected set for a full
    /// reconciling scan, since a watcher cannot see changes made while it wasn't
    /// watching and a restart drops whatever was buffered.
    /// </summary>
    private void MaintainFallbackWatchers(IReadOnlyList<SetState> continuousSets)
    {
        var fallbackRoots = continuousSets
            .SelectMany(s => s.WatchRoots)
            .Where(UsesFallbackVolume)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fallbackRoots.SequenceEqual(_fswRoots, StringComparer.OrdinalIgnoreCase))
            return;

        _fsMonitor.Start(fallbackRoots);
        _fswRoots = fallbackRoots;

        foreach (var state in continuousSets)
        {
            if (!state.NeedsReconcile && state.WatchRoots.Any(UsesFallbackVolume))
            {
                state.NeedsReconcile = true;
                _logger.LogInformation(
                    "Watching \"{Name}\" via file-system watcher fallback; running a full " +
                    "reconciling backup to capture any changes made while it wasn't watching.",
                    state.BackupSet.Name);
            }
        }
    }

    /// <summary>
    /// Drain buffered FileSystemWatcher events into per-set pending state, mirroring
    /// how the USN path routes journal changes. Per-file changes go into the debounce
    /// map; a newly created/renamed directory is expanded to its files (a bulk
    /// move-in may not fire per-file events); an overflow flags the affected sets for
    /// a full reconciling scan because their change list is incomplete.
    /// </summary>
    private void DrainFallbackChanges(
        IReadOnlyList<SetState> continuousSets, DateTime now,
        List<(SetState State, string Dir)> discoveredDirs)
    {
        while (_fswChanges.TryDequeue(out var change))
        {
            var (changedPath, changeType) = change;

            foreach (var state in continuousSets)
            {
                if (!PathBelongsToSet(state.BackupSet, changedPath))
                    continue;

                if (Directory.Exists(changedPath))
                {
                    // Only a new/renamed directory needs whole-subtree enumeration.
                    // A plain Changed on a directory is just its child list or
                    // timestamp updating — the child's own event covers the real
                    // change — so skip it to avoid re-walking the tree on every write.
                    if (changeType is FileChangeType.Created or FileChangeType.Renamed)
                    {
                        EnqueueForBackup(state, changedPath, isDirectory: true, now);
                        // A new folder covered only by auto-include should also become
                        // a permanent explicit selection (pinned after routing).
                        discoveredDirs.Add((state, changedPath));
                    }
                }
                else
                {
                    state.Pending[changedPath] =
                        state.Pending.TryGetValue(changedPath, out var t) ? (t.First, now) : (now, now);
                }
            }
        }

        if (_fswOverflows.IsEmpty)
            return;

        var overflowRoots = new List<string>();
        while (_fswOverflows.TryDequeue(out var root))
            overflowRoots.Add(root);

        foreach (var state in continuousSets)
        {
            if (state.NeedsReconcile)
                continue;

            bool affected = state.WatchRoots.Any(w =>
                overflowRoots.Any(r => IsUnderRoot(w, r) || IsUnderRoot(r, w)));

            if (affected)
            {
                state.NeedsReconcile = true;
                _logger.LogWarning(
                    "File-system watcher buffer overflowed for a source volume of \"{Name}\" " +
                    "(too many changes at once to enumerate). Running a full reconciling backup " +
                    "to capture the changes that were dropped.",
                    state.BackupSet.Name);
            }
        }
    }

    /// <summary>
    /// Pin newly-discovered directories that are currently included only via a
    /// parent's auto-include-new rule into their sets as explicit, checked
    /// selections — so their membership is persisted and survives the user later
    /// turning auto-include off (a live-rule-only folder would otherwise drop out
    /// of scope at that point without ever having been backed up as a real entry).
    /// </summary>
    /// <remarks>
    /// Each affected set is re-read fresh from the catalog before mutation rather
    /// than persisting the worker's in-memory copy: that copy can be up to a poll
    /// interval stale, so writing it back would clobber any edit the GUI saved in
    /// the meantime. Reading fresh confines the read-modify-write to a sub-second
    /// window and preserves everything the user changed. (The reverse race — the
    /// GUI overwriting a pin made while its editor was open — is benign: while
    /// auto-include stays on the folder is simply re-pinned on its next change.)
    /// Legacy root-only sets have no selection tree to pin into and are skipped.
    /// </remarks>
    private async Task MaterializeDiscoveredDirectoriesAsync(
        IReadOnlyList<(SetState State, string Dir)> discovered, CancellationToken ct)
    {
        if (discovered.Count == 0)
            return;

        foreach (var group in discovered.GroupBy(d => d.State))
        {
            var state = group.Key;
            if (state.BackupSet.SourceSelections is not { Count: > 0 })
                continue; // legacy root-only set — nothing to pin into.

            BackupSet? fresh;
            try
            {
                fresh = await _catalog.GetBackupSetAsync(state.BackupSet.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed reloading set {Id} to pin auto-included folders — will retry next poll.",
                    state.BackupSet.Id);
                continue;
            }

            if (fresh?.SourceSelections is not { Count: > 0 })
                continue;

            bool changed = false;
            foreach (var dir in group
                         .Select(d => d.Dir)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (SourceSelection.MaterializeDirectory(fresh.SourceSelections, dir))
                {
                    changed = true;
                    _logger.LogInformation(
                        "Auto-included new folder \"{Dir}\" into set \"{Name}\" as a permanent selection.",
                        dir, fresh.Name);
                }
            }

            if (!changed)
                continue;

            try
            {
                await _catalog.UpdateBackupSetAsync(fresh, ct);
                // Adopt the freshly-persisted copy so this poll's later stages and
                // subsequent polls route against the pinned tree.
                state.BackupSet = fresh;
                state.WatchRoots = ResolveWatchRoots(fresh);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed persisting auto-included folders for set \"{Name}\" — will retry next poll.",
                    fresh.Name);
            }
        }
    }

    /// <summary>
    /// Whether a changed path belongs to a backup set, honoring the selection
    /// tree's inclusion rules (or the raw source roots for legacy sets).
    /// </summary>
    private static bool PathBelongsToSet(BackupSet set, string path)
    {
        // Hard exclusion: the app's own data directory (catalog DBs, logs, dumps)
        // is never part of any set, regardless of an auto-include-new parent. This
        // stops the worker from queuing its own live database writes for backup and
        // from materializing C:\ProgramData\LithicBackup into the selection tree.
        if (CatalogLocation.IsInsideAppDataDirectory(path))
            return false;

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

                foreach (var f in result.FailedFiles.Take(10))
                    _logger.LogWarning("  Failed: {Path} — {Error}", f.Path, f.Error);
                if (result.FailedFiles.Count > 10)
                    _logger.LogWarning(
                        "  … and {More} more failed file(s).", result.FailedFiles.Count - 10);
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

    /// <summary>
    /// Apply this set's queued same-volume relocations: rename the destination
    /// copy in place instead of re-copying. Requeues (leaves the moves pending)
    /// if another backup holds the lock or the destination is offline. Items that
    /// cannot be cleanly relocated (special storage formats, version history, or
    /// items that entered the set) fall back to a normal backup of the new path.
    /// </summary>
    private async Task RunMovesAsync(SetState state, CancellationToken ct)
    {
        if (!await _backupLock.WaitAsync(0, ct))
        {
            // [rename-trace] step 3: another backup holds the lock — moves wait.
            _logger.LogDebug(
                "[rename-trace] step 3: RunMovesAsync for \"{Name}\" skipped — another backup " +
                "in progress; {Count} move(s) stay queued for next poll.",
                state.BackupSet.Name, state.PendingMoves.Count);
            return; // another backup in progress — moves stay queued for next poll
        }

        try
        {
            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(set, opts, ct);
            if (targetDir is null)
            {
                // [rename-trace] step 3: destination offline — moves wait.
                _logger.LogDebug(
                    "[rename-trace] step 3: RunMovesAsync for \"{Name}\" skipped — destination " +
                    "not connected; {Count} move(s) stay queued.",
                    set.Name, state.PendingMoves.Count);
                return; // destination not connected — moves stay queued
            }

            var job = BuildJob(set, opts, targetDir);

            // Snapshot and clear now that we hold the lock and a live destination;
            // anything we can't relocate is re-enqueued for the copy path below.
            var moves = state.PendingMoves.ToList();
            state.PendingMoves.Clear();

            // [rename-trace] step 3: we hold the lock and have a live destination —
            // about to apply the queued moves.
            _logger.LogDebug(
                "[rename-trace] step 3: RunMovesAsync for \"{Name}\": applying {Count} move(s); " +
                "destination='{Dest}'.",
                set.Name, moves.Count, targetDir);

            var now = DateTime.UtcNow;
            int relocated = 0, recopied = 0, freshNames = 0;

            foreach (var move in moves)
            {
                ct.ThrowIfCancellationRequested();

                bool oldIn = PathBelongsToSet(set, move.OldPath);
                bool newIn = PathBelongsToSet(set, move.NewPath);

                if (oldIn && newIn)
                {
                    // [rename-trace] step 4: both endpoints are in the set — this
                    // is an in-place relocation. The trace callback surfaces the
                    // physical destination paths computed inside the service.
                    _logger.LogDebug(
                        "[rename-trace] step 4: relocating in place '{Old}' -> '{New}' " +
                        "(isDir={IsDir}) for \"{Name}\".",
                        move.OldPath, move.NewPath, move.IsDirectory, set.Name);

                    var outcome = await _directoryBackup.MoveTargetedAsync(
                        job, targetDir, move.OldPath, move.NewPath, move.IsDirectory, ct,
                        trace: msg => _logger.LogDebug("[rename-trace] step 5: {Msg}", msg));

                    // [rename-trace] step 6: the service's verdict for this move.
                    _logger.LogDebug(
                        "[rename-trace] step 6: '{Old}' -> '{New}' => {Outcome}.",
                        move.OldPath, move.NewPath, outcome);

                    switch (outcome)
                    {
                        case TargetedMoveOutcome.Relocated:
                            relocated++;
                            break;

                        case TargetedMoveOutcome.NothingToRelocate:
                            // The old name was never backed up (the ubiquitous
                            // atomic-save pattern: write foo.tmp, rename over foo).
                            // There is no destination copy to move and no stale
                            // record to reconcile — just back up the new name.
                            freshNames++;
                            EnqueueForBackup(state, move.NewPath, move.IsDirectory, now);
                            break;

                        default: // FellBack — tracked, but couldn't relocate cleanly.
                            // Back up the new side as fresh files, then reconcile the
                            // now-vacated old path so its stale record doesn't linger
                            // (a move's source departure is unambiguous in the USN
                            // journal, so we act on it at once rather than waiting for
                            // a full scan that never runs in pure-continuous mode).
                            recopied++;
                            EnqueueForBackup(state, move.NewPath, move.IsDirectory, now);
                            if (!await MarkMovedOutAsync(set, move.OldPath, move.IsDirectory, ct))
                                // A file was re-created at the old path (atomic-save
                                // replace): it wasn't tombstoned, so back it up in place.
                                EnqueueForBackup(state, move.OldPath, move.IsDirectory, now);
                            break;
                    }
                }
                else if (newIn)
                {
                    // Entered the set from outside — back up the new side as fresh.
                    _logger.LogDebug(
                        "[rename-trace] step 4: '{New}' entered the set (old path not covered) — " +
                        "backing up as fresh, no in-place relocation.",
                        move.NewPath);
                    EnqueueForBackup(state, move.NewPath, move.IsDirectory, now);
                }
                else
                {
                    // oldIn-only: the item moved out of the set's selection. Treat it
                    // identically to a deletion — soft-delete its catalog record(s) so
                    // version history is retained until the user's next cleanup purges
                    // them. The moved item is NOT relocated on the destination.
                    _logger.LogDebug(
                        "[rename-trace] step 4: '{Old}' moved OUT of the set (new path not covered) — " +
                        "marking removed, destination copy left as-is.",
                        move.OldPath);
                    if (!await MarkMovedOutAsync(set, move.OldPath, move.IsDirectory, ct))
                        // A file was re-created at the old path (atomic-save replace):
                        // it wasn't tombstoned, so back it up in place instead.
                        EnqueueForBackup(state, move.OldPath, move.IsDirectory, now);
                }
            }

            if (relocated > 0 || recopied > 0)
            {
                state.LastRunUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "Continuous backup for \"{Name}\": relocated {Relocated} moved item(s) in place; " +
                    "{Recopied} tracked item(s) could not be relocated and will be re-copied.",
                    set.Name, relocated, recopied);
            }

            if (freshNames > 0)
                _logger.LogDebug(
                    "Continuous backup for \"{Name}\": {Fresh} renamed item(s) had no prior backup " +
                    "(new name backed up normally; typically atomic saves).",
                    set.Name, freshNames);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Continuous relocation for \"{Name}\" cancelled.", state.BackupSet.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Continuous relocation failed for \"{Name}\".", state.BackupSet.Name);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>
    /// Queue a path for a normal continuous backup. For a directory, enqueues
    /// every file beneath it; for a file, enqueues the file itself.
    /// </summary>
    private static void EnqueueForBackup(SetState state, string path, bool isDirectory, DateTime now)
    {
        if (isDirectory)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return;
            }

            foreach (var f in files)
                state.Pending[f] = state.Pending.TryGetValue(f, out var t) ? (t.First, now) : (now, now);
        }
        else
        {
            state.Pending[path] = state.Pending.TryGetValue(path, out var t) ? (t.First, now) : (now, now);
        }
    }

    /// <summary>
    /// Reconcile a source path that has left the set — either moved to a location
    /// outside the selection, or vacated by a within-set move that had to be
    /// re-copied rather than relocated. The catalog record(s) are soft-deleted,
    /// exactly as a full-scan deletion would mark them: version history (and the
    /// destination copy) is retained until the user's next cleanup purges it. The
    /// destination is never touched here.
    ///
    /// Returns <c>true</c> when the record(s) were actually tombstoned, or
    /// <c>false</c> when the tombstone was SKIPPED because a file/directory still
    /// occupies the "vacated" path — see the atomic-save guard below. A caller that
    /// gets <c>false</c> should back the path up instead (it still holds content).
    /// </summary>
    private async Task<bool> MarkMovedOutAsync(BackupSet set, string oldPath, bool isDirectory, CancellationToken ct)
    {
        // Atomic-save / same-name-recreate guard. Applications that save atomically
        // (KeyNote's .knt files, and many editors) rename the original out to a
        // temp/backup name — which the journal reports as a move whose new name is
        // outside the set — and IMMEDIATELY write a replacement at the original
        // path. By the time we apply the move (next poll), a file already exists
        // again at oldPath. Tombstoning it here would soft-delete the live record,
        // so the next backup can't find the prior version: it restarts at v1 and
        // never moves the old copy into _prev (the version chain is discarded on
        // every save). If something still occupies the path, it was replaced in
        // place, not removed — leave the record live and let the caller back it up
        // so it versions normally.
        bool stillOccupied = isDirectory ? Directory.Exists(oldPath) : File.Exists(oldPath);
        if (stillOccupied)
        {
            _logger.LogDebug(
                "Continuous backup for \"{Name}\": path \"{Path}\" reported moved-out but still " +
                "exists on disk (atomic-save replace) — keeping its record live and backing it up.",
                set.Name, oldPath);
            return false;
        }

        try
        {
            if (isDirectory)
                await _catalog.MarkFilesDeletedByDirectoryAsync(set.Id, oldPath, ct);
            else
                await _catalog.MarkFilesDeletedBySourcePathsAsync(set.Id, new[] { oldPath }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Continuous backup for \"{Name}\": failed to reconcile moved-out path \"{Path}\".",
                set.Name, oldPath);
            return false;
        }
    }

    /// <summary>
    /// Run a full scan-and-backup (scheduled interval/daily runs, and continuous
    /// reconciliation after a journal gap). Returns <c>true</c> when the scan
    /// actually ran to completion, or <c>false</c> when it was skipped because a
    /// backup was already in progress or the destination was not connected (so a
    /// caller that needs the run to happen can retry later).
    /// </summary>
    private async Task<bool> RunFullBackupAsync(SetState state, CancellationToken ct)
    {
        if (!await _backupLock.WaitAsync(0, ct))
        {
            _logger.LogInformation(
                "Skipping backup for \"{Name}\" — another backup is in progress.", state.BackupSet.Name);
            return false;
        }

        try
        {
            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(set, opts, ct);
            if (targetDir is null)
                return false; // Destination not connected; retry on a later run.

            if (!await ResolveSourcesAsync(set, ct))
                return false; // No source available; retry on a later run.

            _logger.LogInformation("Starting backup for \"{Name}\" → {Target}", set.Name, targetDir);

            var job = BuildJob(set, opts, targetDir);

            var (diff, totalBytes, totalFiles) = await _directoryBackup.PlanAsync(job, ct);

            if (totalFiles == 0)
            {
                _logger.LogInformation("Nothing to back up for \"{Name}\".", set.Name);
                state.LastRunUtc = DateTime.UtcNow;
                return true;
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
                if (result.FailedFiles.Count > 10)
                    _logger.LogWarning(
                        "  … and {More} more failed file(s).", result.FailedFiles.Count - 10);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Backup for \"{Name}\" cancelled.", state.BackupSet.Name);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup failed for \"{Name}\".", state.BackupSet.Name);
            return false;
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>Build a <see cref="BackupJob"/> from a set's saved options.</summary>
    private static BackupJob BuildJob(BackupSet set, JobOptions opts, string targetDir)
    {
        // Machine-global settings shared with the interactive app via settings.json.
        var machineSettings = UserSettings.Load();
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
            // Machine-global memory budget + disc staging mode so scheduled
            // backups honor the same limits and burn-in-place preference.
            MemoryBudget = machineSettings.MemoryBudget,
            StagingMode = machineSettings.DiscStagingMode,
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

        /// <summary>
        /// Pending same-volume relocations (renames/moves) detected from the USN
        /// journal, in journal order. Each entry relocates the destination copy
        /// instead of re-copying the source. Applied (and cleared) once the
        /// destination is reachable and no other backup holds the lock; requeued
        /// otherwise. Order matters — a later change may depend on an earlier
        /// relocation's new path.
        /// </summary>
        public List<UsnMove> PendingMoves { get; } = new();

        /// <summary>
        /// True when a watched source volume's USN journal lost continuity (it
        /// wrapped or was recreated during downtime), so this set needs a full
        /// reconciling scan to catch changes the incremental journal stream
        /// missed. Cleared once that scan runs.
        /// </summary>
        public bool NeedsReconcile { get; set; }
    }
}
