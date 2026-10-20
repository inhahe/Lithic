using System.Collections.Concurrent;
using System.IO.Enumeration;
using LithicBackup.Core;
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
    /// Per-volume USN journal <em>push</em> waiters, keyed by drive letter. Each
    /// blocks on the volume's journal and calls <see cref="SignalWake"/> the instant
    /// the volume changes, so the loop reacts immediately instead of waiting out the
    /// poll interval. Only created for drives whose journal a reader has already
    /// opened (see <see cref="SyncJournalWaiters"/>); the periodic poll remains the
    /// correctness backstop, so a volume without a waiter is still covered, just with
    /// higher latency. Mutated only on the poll thread.
    /// </summary>
    private readonly Dictionary<char, UsnJournalWaiter> _journalWaiters = new();

    /// <summary>
    /// One-permit gate the loop waits on between cycles. A USN push waiter (or any
    /// other early-wake source) releases it to cut the current sleep short; extra
    /// releases collapse to a single pending wake (auto-reset semantics), so a burst
    /// of changes wakes the loop exactly once.
    /// </summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

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

    /// <summary>Only one backup runs at a time.</summary>
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    /// <summary>
    /// The settings the app saves for backups (<see cref="MachineSettings"/>), as
    /// last read: refreshed at the top of a poll cycle and before each job, so
    /// edits in the GUI take effect without a restart, even during a pass that
    /// runs hours of backups. A file that cannot be read leaves them as they were.
    /// </summary>
    private readonly MachineSettingsReader _machine = new();

    /// <summary>
    /// The continuous-backup timing rules (size-tiered debounce + mask-tiered
    /// max-wait) of <see cref="_machine"/>. These replace the old per-set
    /// <c>DebounceSeconds</c>/<c>MaxWaitSeconds</c> fields (kept on
    /// <see cref="BackupSchedule"/> only for back-compat).
    /// </summary>
    private ContinuousRules _rules = new();

    /// <summary>
    /// The catalog's <see cref="ICatalogRepository.GetBackupSetsChangeStampAsync"/>
    /// when the sets were last read, so a pass reads them again only after they
    /// may have changed. -1 before the first read.
    /// </summary>
    private long _loadedSetsStamp = -1;

    /// <summary>
    /// Whether each drive letter had a volume behind it, memoised for one pass:
    /// every watch root asks, and a set can have hundreds. Cleared at the start of
    /// each pass. Poll thread only.
    /// </summary>
    private readonly Dictionary<char, bool> _volumePresence = new();

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

        // Said as the run finds it: a run this is about can last days.
        _directoryBackup.Advisory += (setId, message) =>
        {
            string name = setId is { } id && _sets.TryGetValue(id, out var s) ? s.BackupSet.Name : $"set {setId}";
            _logger.LogWarning("Backup of \"{Name}\": {Advisory}", name, message);
        };

        // Watcher events arrive on thread-pool threads; buffer them and let the
        // poll loop route them, so per-set state stays single-threaded. Waking the
        // loop makes the non-NTFS fallback push-driven too, matching the USN path.
        _fsMonitor.FileChanged += (_, e) =>
        {
            _fswChanges.Enqueue((e.FullPath, e.ChangeType));
            SignalWake();
        };
        _fsMonitor.Overflow += (_, e) =>
        {
            _fswOverflows.Enqueue(e.Root);
            SignalWake();
        };
    }

    /// <summary>
    /// Resolve a set's destination to a live path, following any drive-letter
    /// reassignment, backfilling/persisting the volume identity when it
    /// changes, and logging a letter move.  Returns the live target directory,
    /// or <c>null</c> when the destination volume is not currently connected
    /// (the caller should skip the run).
    /// </summary>
    /// <remarks>
    /// A missing destination blocks the set's runs until the next poll
    /// (<see cref="BlockRuns"/>), and is logged when it goes and when it comes
    /// back, not at each attempt.
    /// </remarks>
    private async Task<string?> ResolveDestinationAsync(SetState state, CancellationToken ct)
    {
        var set = state.BackupSet;
        var resolution = _destinationResolver.Resolve(set.JobOptions!);

        // Volume identity only - never the selection.
        if (resolution.MetadataChanged)
            await PersistResolvedAsync(set, fresh => _destinationResolver.Resolve(fresh.JobOptions!),
                metadataOnly: true, ct);

        if (!resolution.IsConnected)
        {
            if (state.DestinationMissingSinceUtc is null)
            {
                state.DestinationMissingSinceUtc = DateTime.UtcNow;
                _logger.LogWarning(
                    "Skipping backup for \"{Name}\": destination drive not connected ({Path}). Its backups "
                    + "wait until it is, and this is not logged again until then.",
                    set.Name, resolution.PreviousPath ?? "unknown path");
            }
            BlockRuns(state);
            return null;
        }

        if (state.DestinationMissingSinceUtc is { } missingSince)
        {
            state.DestinationMissingSinceUtc = null;
            _logger.LogInformation(
                "Destination drive for \"{Name}\" is connected again ({Path}), after {Minutes:F0} minute(s).",
                set.Name, resolution.LivePath, (DateTime.UtcNow - missingSince).TotalMinutes);
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
    /// Save a drive-letter move or volume identity the resolver applied to the
    /// Worker's copy of a set, by applying it again to the catalog's current copy
    /// and writing that. The Worker's copy is from the last reload, which during
    /// another set's long scan can be hours old; written back whole, it would undo
    /// whatever was saved since - an edit to the selection, a hold on automated
    /// backups placed by a seed.
    /// </summary>
    /// <param name="stale">The Worker's copy, already resolved; it names the set.</param>
    /// <param name="resolve">The same resolution, made on the fresh copy.</param>
    /// <param name="metadataOnly">Write the set's metadata only (options, volume ids), not its selection.</param>
    private async Task PersistResolvedAsync(
        BackupSet stale, Action<BackupSet> resolve, bool metadataOnly, CancellationToken ct)
    {
        var fresh = await _catalog.GetBackupSetAsync(stale.Id, ct);
        if (fresh?.JobOptions is null)
            return;
        resolve(fresh);
        if (metadataOnly)
            await _catalog.UpdateBackupSetMetadataAsync(fresh, ct);
        else
            await _catalog.UpdateBackupSetAsync(fresh, ct);
    }

    /// <summary>
    /// Follow the set's source drives across any drive-letter reassignment
    /// (rewriting source paths and persisting the change), and report source
    /// availability.  Returns <c>false</c> when no configured source location is
    /// currently reachable (the caller should skip the run rather than back up
    /// nothing).  Partially-missing sources are logged when they change, and the
    /// run proceeds.
    /// </summary>
    /// <remarks>
    /// Like a missing destination, having no source blocks the set's runs until
    /// the next poll and is logged only when it starts and when it ends.
    /// </remarks>
    private async Task<bool> ResolveSourcesAsync(SetState state, CancellationToken ct)
    {
        var set = state.BackupSet;
        var resolution = _sourceResolver.Resolve(set);

        if (resolution.MetadataChanged)
            await PersistResolvedAsync(set, fresh => _sourceResolver.Resolve(fresh), metadataOnly: false, ct);

        if (resolution.LetterChanges.Count > 0)
        {
            _logger.LogInformation(
                "Source drive(s) for \"{Name}\" moved: {Changes}. Updated automatically.",
                set.Name, string.Join(", ", resolution.LetterChanges));
        }

        if (!resolution.AnyAvailable)
        {
            if (!state.SourcesMissing)
            {
                state.SourcesMissing = true;
                _logger.LogWarning(
                    "Skipping backup for \"{Name}\": no source location is currently available ({Missing}). "
                    + "Its full backups wait until one is, and this is not logged again until then.",
                    set.Name, string.Join(", ", resolution.MissingSources));
            }
            BlockRuns(state);
            return false;
        }

        if (state.SourcesMissing)
        {
            state.SourcesMissing = false;
            _logger.LogInformation("A source location for \"{Name}\" is available again.", set.Name);
        }

        string? missing = resolution.MissingSources.Count > 0
            ? string.Join(", ", resolution.MissingSources)
            : null;
        if (missing is not null && missing != state.PartlyMissingSources)
        {
            _logger.LogWarning(
                "Some source locations for \"{Name}\" are not available and will be skipped: {Missing}.",
                set.Name, missing);
        }
        state.PartlyMissingSources = missing;

        return true;
    }

    /// <summary>
    /// Attempt no run of this set before the next poll, because the last one could
    /// not start for a reason only something outside the Worker can end: the
    /// destination is not connected, no source is, or the destination holds a
    /// backup this set does not track. A pending reconcile, an overdue schedule
    /// and queued changes all survive the failed attempt, so without this the
    /// attempt was repeated at every pass, several a second while changes arrive.
    /// Never shortens a longer block already in place.
    /// </summary>
    private void BlockRuns(SetState state)
    {
        var until = DateTime.UtcNow + ComputeEffectivePollInterval();
        if (until > state.RunsBlockedUntilUtc)
            state.RunsBlockedUntilUtc = until;
    }

    /// <summary>Whether <see cref="BlockRuns"/> holds this set's runs now.</summary>
    private static bool RunsBlocked(SetState state) => DateTime.UtcNow < state.RunsBlockedUntilUtc;

    /// <summary>
    /// Whether no full backup of this set may start now: its runs are blocked, or
    /// its full backups are backing off after failures (<see cref="FailureBackoff"/>).
    /// </summary>
    private static bool FullBackupsBlocked(SetState state)
        => RunsBlocked(state) || DateTime.UtcNow < state.FullBackupsBlockedUntilUtc;

    /// <summary>
    /// How long to hold a set's full backups after they have thrown
    /// <paramref name="consecutiveFailures"/> times in a row: 1 minute, doubling
    /// to at most an hour. A failed full backup leaves its reconcile flag or its
    /// overdue schedule in place, and each attempt can be a whole-tree scan before
    /// it fails. Targeted runs are not held, so continuous protection carries on
    /// while a safety-net scan keeps failing.
    /// </summary>
    private static TimeSpan FailureBackoff(int consecutiveFailures)
        => TimeSpan.FromMinutes(Math.Min(60, 1 << Math.Clamp(consecutiveFailures - 1, 0, 6)));

    /// <summary>
    /// When the hold on everything this set has waiting ends, or null when some of
    /// it can run now. Its runs may be blocked, or its reconcile, which its queued
    /// changes wait for, may be backing off after failures. The one definition the
    /// wake-up delay and the choice to heed push wake-ups both use.
    /// </summary>
    private static DateTime? HeldUntil(SetState state, DateTime nowUtc)
    {
        if (nowUtc < state.RunsBlockedUntilUtc)
            return state.RunsBlockedUntilUtc;
        if (state.NeedsReconcile && nowUtc < state.FullBackupsBlockedUntilUtc)
            return state.FullBackupsBlockedUntilUtc;
        return null;
    }

    /// <summary>
    /// Whether some active continuous set can run now, so that a change the push
    /// waiters report can lead to a backup before the next poll.
    /// </summary>
    private bool AnyContinuousSetCanRun()
    {
        var now = DateTime.UtcNow;
        foreach (var state in _sets.Values)
        {
            if (state.IsActive
                && state.BackupSet.JobOptions?.Schedule is { Mode: ScheduleMode.Continuous }
                && HeldUntil(state, now) is null)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand control back to the host BEFORE doing any work. Not a nicety — a
        // correctness requirement.
        //
        // BackgroundService.StartAsync calls ExecuteAsync and only stores the
        // returned Task; it does not block on it. But "returns a Task" is not the
        // same as "returns promptly": if the body runs to completion synchronously
        // — which it does here, because the loop below is dominated by synchronous
        // file I/O and awaits that complete synchronously — then the CALL itself
        // does not return until the first genuinely-incomplete await. Host.StartAsync
        // starts hosted services sequentially, so for as long as that takes, NO
        // LATER HOSTED SERVICE IS STARTED.
        //
        // That is exactly what happened: a backup kept this method on the caller's
        // stack for thirteen minutes, so ShutdownSignalListener.StartAsync was never
        // reached and the Global\LithicBackup.Worker.Shutdown event did not exist
        // while the service was running. The upgrade custom action duly logged
        // "no listener on 'Global\LithicBackup.Worker.Shutdown'", and the log showed
        // the listener registering only as the service shut down, immediately
        // followed by "Application started" — startup completing at the moment of
        // shutdown, the signature of this bug.
        //
        // Task.Yield forces an asynchronous continuation, so ExecuteAsync returns to
        // the host at once and the loop resumes on a thread-pool thread.
        await Task.Yield();

        _logger.LogInformation("LithicBackup Worker started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _volumePresence.Clear();
                    await ReloadBackupSetsAsync(stoppingToken);
                    await CheckSchedulesAsync(stoppingToken);
                    await CheckContinuousAsync(stoppingToken);

                    // Keep the set of USN push waiters in sync with the active
                    // continuous volumes so a change wakes the loop immediately.
                    SyncJournalWaiters();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in worker loop.");
                }

                // Wait until the next deadline, or until a push waiter reports a
                // change (whichever comes first). Draining the gate here also
                // clears any signal that fired during processing above.
                try
                {
                    var delay = ComputeNextWakeDelay(DateTime.UtcNow);
                    if (AnyContinuousSetCanRun())
                    {
                        await _wake.WaitAsync(delay, stoppingToken);
                    }
                    else
                    {
                        // With every continuous set blocked, a reported change can
                        // only be queued, which the next pass does as well. The
                        // waiters fire on any change to a watched volume, up to
                        // four times a second each, so heeding them here would run
                        // passes that cannot back anything up.
                        await Task.Delay(delay, stoppingToken);
                        _wake.Wait(0);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var waiter in _journalWaiters.Values)
                waiter.Dispose();
            _journalWaiters.Clear();
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
    /// Reload backup sets from the catalog and update per-set state, if the
    /// catalog reports that they may have changed since the last reload.
    /// </summary>
    private async Task ReloadBackupSetsAsync(CancellationToken ct)
    {
        ReloadMachineSettings();

        // This runs at every pass, and push wake-ups can make that several times
        // a second; reading the sets parses every selection tree, megabytes for a
        // large one. Read before the sets, so a write that lands in between
        // leaves the stamp different and is picked up by the next pass.
        long stamp = await _catalog.GetBackupSetsChangeStampAsync(ct);
        if (stamp == _loadedSetsStamp)
            return;

        var allSets = await _catalog.GetAllBackupSetsAsync(ct);
        var seen = new HashSet<int>();

        foreach (var set in allSets)
        {
            seen.Add(set.Id);
            var schedule = set.JobOptions?.Schedule;

            // Only directory-mode sets with an enabled schedule are relevant -
            // and not while automated backups are held until the user runs a
            // backup in the app (an imported, cleared or seeded set).
            bool scheduled = schedule is { Enabled: true }
                             && set.JobOptions?.TargetDirectory is not null;
            bool held = set.JobOptions?.HoldAutomatedBackups == true;
            bool active = scheduled && !held;

            if (!_sets.TryGetValue(set.Id, out var state))
            {
                state = new SetState
                {
                    BackupSet = set,
                    LastRunUtc = set.LastBackupUtc ?? DateTime.MinValue,
                    // Persisted across restarts. Falling back to LastBackupUtc
                    // (rather than MinValue) keeps a fresh install from running a
                    // whole-tree scan the moment the service first starts.
                    LastFullScanUtc = FullScanStamp.Read(set.Id)
                                      ?? set.LastBackupUtc ?? DateTime.MinValue,
                };
                _sets[set.Id] = state;
            }
            else
            {
                // An edit to the set may be what fixes a failing full backup, so it
                // ends the backoff. Not a change of LastBackupUtc: every targeted
                // run moves that, and would make the backoff pointless.
                if (state.ConsecutiveFailures > 0 && SettingsDiffer(state.BackupSet, set))
                {
                    state.ConsecutiveFailures = 0;
                    state.FullBackupsBlockedUntilUtc = DateTime.MinValue;
                    _logger.LogInformation(
                        "\"{Name}\" was edited, so its full backups, held after failing, may start again.",
                        set.Name);
                }
                state.BackupSet = set;
            }

            if (scheduled && held != state.WasHeld)
            {
                if (held)
                    _logger.LogInformation(
                        "Automated backups for \"{Name}\" are paused until a backup is run from Lithic Backup " +
                        "(the set was imported, or its history was cleared or seeded).", set.Name);
                else
                    _logger.LogInformation("Automated backups for \"{Name}\" resumed.", set.Name);
            }

            // Released from a hold: its clocks were read when this service first
            // saw the set, before the full backup the user has just run in the app
            // to release it. Without this, the release was followed at once by a
            // redundant whole-tree scan. Only on release, never at first sight:
            // LastBackupUtc is also bumped by every targeted continuous run, and
            // timing the safety net from it is exactly what LastFullScanUtc exists
            // to avoid.
            bool released = state.WasHeld && !held;
            if (released && set.LastBackupUtc is { } lastBackup)
            {
                if (lastBackup > state.LastRunUtc)
                    state.LastRunUtc = lastBackup;
                if (lastBackup > state.LastFullScanUtc)
                    state.LastFullScanUtc = lastBackup;
            }
            state.WasHeld = held;

            state.IsActive = active;

            // The directories the user actually selected (not the whole drive
            // roots). Used to decide which volumes to read and which changes
            // are relevant.
            state.WatchRoots = ResolveWatchRoots(set);
            state.ApplyExclusionRules(set.JobOptions);
        }

        // Remove sets that no longer exist in the catalog.
        foreach (var id in _sets.Keys)
        {
            if (!seen.Contains(id))
                _sets.TryRemove(id, out _);
        }

        _loadedSetsStamp = stamp;

        await RemoveRedundantSelectionEntriesAsync(ct);
    }

    /// <summary>
    /// Read the settings the app saves for backups again if the file has been
    /// written since the last read, so GUI edits apply without a restart. A file
    /// that cannot be read keeps the settings already in force (falling back to
    /// the defaults then would be the very failure the file exists to prevent),
    /// is logged once, and is tried again at the next look.
    /// </summary>
    private void ReloadMachineSettings()
    {
        if (_machine.Refresh())
        {
            _logger.LogWarning(
                "Could not read the backup settings in {Path}; going on with the ones last read "
                + "until it can be read.", MachineSettings.DefaultPath);
        }
        _rules = _machine.Current.ContinuousRules ?? new();
    }

    /// <summary>
    /// Remove from each continuous set's saved selection the entries that lie
    /// inside folders its own rules exclude entirely and record nothing but
    /// "included" (<see cref="SourceSelection.RemoveRedundantEntriesInExcludedFolders"/>).
    /// They cannot change any backup, and every read of the set parses them. Runs
    /// after each reload, so a rule the user adds later clears what it covers too.
    /// </summary>
    /// <remarks>
    /// Only active continuous sets, the ones the Worker pins folders into: the
    /// entries in any other set were put there by the user.
    /// </remarks>
    private async Task RemoveRedundantSelectionEntriesAsync(CancellationToken ct)
    {
        foreach (var state in _sets.Values)
        {
            if (!state.IsActive
                || state.BackupSet.JobOptions?.Schedule is not { Mode: ScheduleMode.Continuous }
                || state.BackupSet.SourceSelections is not { Count: > 0 } loaded)
                continue;

            // Counted on the loaded copy, which stays as the catalog has it unless
            // the save succeeds: usually nothing is removable and nothing is read.
            if (SourceSelection.CountRedundantEntriesInExcludedFolders(loaded, state.PrunesDirectory) == 0)
                continue;

            await ChangeSavedSelectionAsync(state, "remove redundant entries from", (set, selections) =>
            {
                int removed = SourceSelection.RemoveRedundantEntriesInExcludedFolders(
                    selections, state.PrunesDirectory);
                if (removed > 0)
                {
                    _logger.LogInformation(
                        "Removed {Count} entries from the selection of \"{Name}\": they lie inside folders "
                        + "its exclusion rules exclude entirely, so they could not affect any backup.",
                        removed, set.Name);
                }
                return removed;
            }, ct);
        }
    }

    /// <summary>
    /// Change a set's saved selection: read the set fresh from the catalog, apply
    /// <paramref name="change"/> to its selection, save it if the change reports
    /// doing anything, and adopt the saved copy for this pass and later ones.
    /// </summary>
    /// <remarks>
    /// Read fresh rather than taken from the Worker's copy, which can be a pass
    /// old: writing that back would undo an edit the GUI saved in the meantime.
    /// Reading fresh confines the read-modify-write to milliseconds. (The reverse
    /// race, the GUI saving over a change made while its editor was open, is
    /// benign: the change is made again the next time it applies.)
    /// Failures are logged and leave the set as it was.
    /// </remarks>
    /// <param name="purpose">Completes "Could not … the selection of set N", for the log.</param>
    /// <param name="change">Edits the fresh selection in place and returns how many
    /// things it changed; 0 means nothing to save.</param>
    private async Task ChangeSavedSelectionAsync(
        SetState state, string purpose,
        Func<BackupSet, List<SourceSelection>, int> change, CancellationToken ct)
    {
        try
        {
            var fresh = await _catalog.GetBackupSetAsync(state.BackupSet.Id, ct);
            if (fresh?.SourceSelections is not { Count: > 0 } selections)
                return;

            if (change(fresh, selections) == 0)
                return;

            await _catalog.UpdateBackupSetAsync(fresh, ct);
            state.BackupSet = fresh;
            state.WatchRoots = ResolveWatchRoots(fresh);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not {Purpose} the selection of set {Id}.", purpose, state.BackupSet.Id);
        }
    }

    /// <summary>
    /// Whether two copies of a set differ in what the user edits: its options or
    /// its selection. Serialises both, so call it only when it matters.
    /// </summary>
    private static bool SettingsDiffer(BackupSet before, BackupSet after)
        => System.Text.Json.JsonSerializer.Serialize(before.JobOptions)
               != System.Text.Json.JsonSerializer.Serialize(after.JobOptions)
           || System.Text.Json.JsonSerializer.Serialize(before.SourceSelections)
               != System.Text.Json.JsonSerializer.Serialize(after.SourceSelections);

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
                // Continuous sets get their per-file work from the journal in
                // CheckContinuousAsync; this is only the safety net below.
                ScheduleMode.Continuous => ShouldRunContinuousSafetyNet(state, now),
                _ => false,
            };

            if (shouldRun && !FullBackupsBlocked(state))
            {
                // A reconcile pending when the scan starts is settled by it.
                bool reconcilePending = state.NeedsReconcile;
                var scanStartedUtc = DateTime.UtcNow;
                int movesQueuedBefore = state.PendingMoves.Count;
                var outcome = await RunFullBackupAsync(state, ct);
                if (reconcilePending)
                    SettleReconcile(state, outcome, scanStartedUtc, movesQueuedBefore);
            }
        }
    }

    /// <summary>
    /// How long a continuous set may go without a whole-tree scan before one is
    /// run as a safety net.
    /// </summary>
    private const double ContinuousSafetyNetHours = 24;

    /// <summary>
    /// Continuous sets were given no periodic full scan at all, on the reasoning
    /// that the USN journal reports everything. It does not, and three gaps had
    /// no other closer:
    ///
    /// <list type="bullet">
    /// <item>A plain single-file delete is skipped by <c>ExecuteTargetedAsync</c>
    /// (it <c>continue</c>s on a missing path) and Cleanup only surfaces a record
    /// when the whole parent directory is gone — so a deleted file whose parent
    /// survives stayed an active catalog row indefinitely.</item>
    /// <item>A file that failed to copy is dropped from the pending queue after
    /// one retry; nothing revisits it.</item>
    /// <item>A journal that silently stops advancing (a stale reader handle, say)
    /// looks exactly like "nothing changed", forever.</item>
    /// </list>
    ///
    /// <para>Timed from <see cref="SetState.LastFullScanUtc"/>, not
    /// <c>LastRunUtc</c>: every targeted continuous backup bumps the latter, so a
    /// busy set would never qualify.</para>
    /// </summary>
    private static bool ShouldRunContinuousSafetyNet(SetState state, DateTime now)
        => (now - state.LastFullScanUtc).TotalHours >= ContinuousSafetyNetHours;

    /// <summary>
    /// Record that a whole-tree scan just finished, in memory and on disk.
    /// Persistence failure is logged and ignored: the only cost is that a service
    /// restart forgets the timer and schedules the next safety-net scan early.
    /// </summary>
    private void MarkFullScanCompleted(SetState state)
    {
        var now = DateTime.UtcNow;
        state.LastFullScanUtc = now;

        if (!FullScanStamp.Write(state.BackupSet.Id, now))
        {
            _logger.LogDebug(
                "Could not persist the full-scan stamp for set {Id}; the safety-net "
                + "scan will be scheduled from this process's start instead.",
                state.BackupSet.Id);
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

    /// <summary>
    /// Release the loop's wake gate so the current between-cycle wait ends early.
    /// Called from USN push-waiter threads, so it must stay cheap and thread-safe;
    /// extra releases while a permit is already pending are swallowed (auto-reset).
    /// </summary>
    private void SignalWake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending — coalesce */ }
    }

    /// <summary>
    /// The delay before the next loop cycle: the periodic backstop cadence
    /// (<see cref="ComputeEffectivePollInterval"/>) shortened to the nearest pending
    /// debounce / max-wait deadline so queued continuous changes are backed up on
    /// time rather than at the next poll. Push waiters can cut this short further by
    /// releasing the wake gate. Floored so a burst of same-tick changes can't spin
    /// the loop.
    /// </summary>
    private TimeSpan ComputeNextWakeDelay(DateTime nowUtc)
    {
        var delay = ComputeEffectivePollInterval();

        foreach (var state in _sets.Values)
        {
            if (!state.IsActive
                || state.BackupSet.JobOptions?.Schedule is not { Mode: ScheduleMode.Continuous })
                continue;

            // Nothing a held set has waiting can happen before the hold ends, so
            // that is its only deadline. Its overdue queue and its reconcile would
            // otherwise set the pace, and drive the loop flat out against a
            // destination that is not there.
            if (HeldUntil(state, nowUtc) is { } heldUntil)
            {
                if (heldUntil - nowUtc < delay)
                    delay = heldUntil - nowUtc;
                continue;
            }

            // Moves and pending reconciles are applied each cycle but gated on the
            // backup lock, so retry them soon.
            if (state.NeedsReconcile || state.PendingMoves.Count > 0)
            {
                if (delay > MinPollInterval)
                    delay = MinPollInterval;
            }

            // Queued changes are not backed up while a reconcile is pending (it
            // goes first, and a clean one supersedes them), so until then their
            // debounce deadlines say nothing about when work can happen.
            if (state.NeedsReconcile || state.Pending.Count == 0)
                continue;

            foreach (var (path, t) in state.Pending)
            {
                // Debounce window depends on the file's current size (bigger files
                // demand a longer quiet period); max-wait depends on its name/path
                // mask, and may be infinite (no cap) — in which case only the
                // debounce deadline bounds the wake.
                var debounce = TimeSpan.FromSeconds(_rules.ResolveDebounceSeconds(GetFileSizeSafe(path)));
                var deadline = (t.Last + debounce) - nowUtc;

                if (_rules.ResolveMaxWaitSeconds(path) is int maxWaitSec)
                {
                    var byMaxWait = (t.First + TimeSpan.FromSeconds(maxWaitSec)) - nowUtc;
                    if (byMaxWait < deadline)
                        deadline = byMaxWait;
                }

                if (deadline < delay)
                    delay = deadline;
            }
        }

        // Floor: avoid a busy loop when many items land in the same tick (their
        // deadlines can already be in the past). A quarter-second is imperceptible
        // for backup latency yet bounds journal re-reads under sustained churn.
        var floor = TimeSpan.FromMilliseconds(250);
        return delay < floor ? floor : delay;
    }

    /// <summary>
    /// Start/stop per-volume USN push waiters so they cover exactly the drive
    /// letters used by currently-active continuous sets whose journal is usable.
    /// Poll-thread only (touches the journal-reader and waiter caches). A waiter
    /// wakes the loop the instant its volume changes; the periodic poll still runs
    /// as the correctness backstop, so a volume without a waiter (non-NTFS, or one
    /// whose waiter failed to open) is simply served at poll latency instead.
    /// </summary>
    private void SyncJournalWaiters()
    {
        var desired = new HashSet<char>();

        foreach (var state in _sets.Values)
        {
            if (!state.IsActive
                || state.BackupSet.JobOptions?.Schedule is not { Mode: ScheduleMode.Continuous })
                continue;

            foreach (var root in state.WatchRoots)
            {
                var drive = GetDriveLetter(root);
                if (drive == '\0')
                    continue;

                // Only NTFS volumes with a usable journal get a push waiter; the
                // rest fall back to the FileSystemWatcher path (already event-driven).
                if (GetJournalReader(drive) is not null)
                    desired.Add(drive);
            }
        }

        // Tear down waiters whose volume is no longer active.
        foreach (var drive in _journalWaiters.Keys.ToList())
        {
            if (!desired.Contains(drive))
            {
                _journalWaiters[drive].Dispose();
                _journalWaiters.Remove(drive);
            }
        }

        // Bring up waiters for newly-active volumes.
        foreach (var drive in desired)
        {
            if (_journalWaiters.ContainsKey(drive))
                continue;

            var waiter = UsnJournalWaiter.TryOpen(drive, SignalWake);
            if (waiter is not null)
            {
                _journalWaiters[drive] = waiter;
                _logger.LogInformation(
                    "USN push notifications enabled on {Drive}: — continuous changes now wake " +
                    "the worker immediately instead of waiting for the next poll.", drive);
            }
        }
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

        // Anything a commit-boundary drain noticed while a full scan was running.
        // It could not act on these itself (scheduling a scan or rewriting the
        // selection from inside a backup), so it parked them here.
        var truncatedDrives = new HashSet<char>(_drainTruncatedDrives);
        _drainTruncatedDrives.Clear();

        // Newly-created directories that are included via a parent's auto-include
        // rule but aren't yet explicit selections. Collected during change routing
        // and pinned into their sets afterwards (see MaterializeDiscoveredDirectoriesAsync)
        // so their membership persists past the user turning auto-include off.
        var discoveredDirs = new List<(SetState State, string Dir)>(_drainDiscoveredDirs);
        _drainDiscoveredDirs.Clear();

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

            RouteJournalRecords(changes, moves, continuousSets, now, discoveredDirs);
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
            if (!state.NeedsReconcile || FullBackupsBlocked(state))
                continue;

            var scanStartedUtc = DateTime.UtcNow;
            int movesQueuedBefore = state.PendingMoves.Count;
            var outcome = await RunFullBackupAsync(state, ct);
            SettleReconcile(state, outcome, scanStartedUtc, movesQueuedBefore);
        }

        // 3b. Apply pending relocations. Moves are discrete, atomic events (no
        //     debounce): relocate the destination copy in place rather than
        //     re-copying. Applied before the per-file debounce so a moved-and-
        //     edited file relocates first, then its content update copies to the
        //     new path.
        foreach (var state in continuousSets)
        {
            if (state.NeedsReconcile || state.PendingMoves.Count == 0 || RunsBlocked(state))
                continue;

            await RunMovesAsync(state, ct);
        }

        // 4. Per-file debounce: back up files that have been quiet for the
        //    debounce window, or that have been pending past the max-wait cap.
        foreach (var state in continuousSets)
        {
            if (state.NeedsReconcile || state.Pending.Count == 0 || RunsBlocked(state))
                continue;

            var ready = SelectReadyPaths(state, now);

            if (ready.Count > 0)
                await RunTargetedBackupAsync(state, ready, ct);
        }
    }

    /// <summary>
    /// Take the pending paths whose debounce window has elapsed (or whose
    /// max-wait cap has expired), removing them from the queue.
    /// </summary>
    /// <param name="excluded">
    /// Paths to leave queued regardless of readiness. Used while a full scan is
    /// running: that scan resolved its version numbers from a snapshot taken
    /// before its copy loop, so writing one of its paths from anywhere else
    /// would make it assign a stale version.
    /// </param>
    private List<string> SelectReadyPaths(
        SetState state, DateTime now, IReadOnlySet<string>? excluded = null)
    {
        var ready = new List<string>();

        foreach (var (path, t) in state.Pending.ToList())
        {
            if (excluded is not null && excluded.Contains(path))
                continue;

            // Size-tiered debounce: a file is ready once it has been quiet for
            // its size's window (bigger files demand more quiet, since a false
            // trigger mid-write costs a larger wasted copy). Mask-tiered
            // max-wait: an ever-changing file whose name/path opts in (logs,
            // append-only session files) is versioned anyway after its cap;
            // a file matching no max-wait tier has an infinite cap and is only
            // copied once it settles via debounce.
            long size = GetFileSizeSafe(path);
            var debounceSeconds = _rules.ResolveDebounceSeconds(size);
            int? maxWaitSeconds = _rules.ResolveMaxWaitSeconds(path);

            var quiet = now - t.Last;
            var waited = now - t.First;

            bool isReady = quiet.TotalSeconds >= debounceSeconds
                           || (maxWaitSeconds is int mw && waited.TotalSeconds >= mw);

            if (isReady)
            {
                ready.Add(path);
                state.Pending.Remove(path);
            }
        }

        return ready;
    }

    /// <summary>
    /// Record what a full scan did to a set's pending reconcile. A scan that ran
    /// settles it, whichever path started the scan: the reconcile step, or the
    /// schedule's or the safety net's full backup, which would otherwise be
    /// followed at once by a second whole-tree scan of the same set. A scan that
    /// did not run (lock busy, or the set blocked) leaves it pending.
    /// </summary>
    /// <param name="scanStartedUtc">When the scan began, before it planned anything.</param>
    /// <param name="movesQueuedBefore">How many moves were queued when it began.</param>
    private void SettleReconcile(
        SetState state, ReconcileOutcome outcome, DateTime scanStartedUtc, int movesQueuedBefore)
    {
        if (outcome == ReconcileOutcome.DidNotRun)
            return;

        // Cleared as soon as the scan RAN, even if files failed. Leaving it
        // set would start another whole-tree scan on the very next poll, and
        // for a set with a permanently unreadable file (a locked venv binary,
        // say — this machine has ~78 of them) that is an endless full-scan
        // loop. The failed files are covered by keeping their queued deltas
        // below, which costs one cheap targeted retry instead.
        state.NeedsReconcile = false;

        if (outcome == ReconcileOutcome.RanClean)
        {
            // Only a CLEAN scan supersedes the queued per-file deltas, and only
            // those queued before it began: it walked the whole tree and compared
            // every file, so they are accounted for. What the commit-boundary
            // drain queued while it ran stays. The drain leaves the scan's own
            // paths to the scan even when they change again after it copied them,
            // and it never applies moves.
            foreach (var path in state.Pending
                         .Where(p => p.Value.Last < scanStartedUtc)
                         .Select(p => p.Key)
                         .ToList())
                state.Pending.Remove(path);
            // A full scan reconciles the destination against the live source
            // tree, so the relocations queued before it are accounted for.
            state.PendingMoves.RemoveRange(0, Math.Min(movesQueuedBefore, state.PendingMoves.Count));
        }
        else
        {
            // Partial scan: the failed files' catalog rows still hold their
            // OLD size/mtime, so the next debounce pass re-stats them, sees
            // the difference and retries just those. Clearing here dropped
            // them for good — the journal records that produced them were
            // consumed and the cursor advanced past them before the scan ran,
            // so nothing else would ever have reported them again.
            _logger.LogInformation(
                "Keeping {Count} queued change(s) for \"{Name}\": the reconciling scan "
                + "did not complete cleanly.", state.Pending.Count, state.BackupSet.Name);
        }
    }

    /// <summary>
    /// Route one volume's journal records to the sets they concern: changed files
    /// into each set's queue, new folders into <paramref name="discoveredDirs"/>
    /// for pinning, and same-volume moves into each set's move list. The poll and
    /// the drain inside a full scan both route through here, so they cannot route
    /// differently.
    /// </summary>
    /// <returns>How many (file, set) pairs were queued.</returns>
    private int RouteJournalRecords(
        IReadOnlyList<UsnChange> changes, IReadOnlyList<UsnMove> moves,
        IReadOnlyList<SetState> continuousSets, DateTime now,
        List<(SetState State, string Dir)> discoveredDirs)
    {
        int routed = 0;

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
                        if (IsPinCandidate(state, change.FullPath))
                            discoveredDirs.Add((state, change.FullPath));
                    }
                }
                continue;
            }

            foreach (var state in continuousSets)
            {
                if (IsBackupCandidate(state, change.FullPath))
                {
                    Touch(state, change.FullPath, now);
                    routed++;
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
                if (!(oldIn || newIn) || MovesWithinExcludedTrees(state, move))
                    continue;

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

        return routed;
    }

    /// <summary>
    /// Whether both ends of a move lie inside trees the set's rules exclude
    /// entirely. Nothing there can be backed up, so there is nothing to relocate;
    /// builds rename temporary files and folders there by the thousand, and
    /// queued, they would wait in memory for as long as the destination is away.
    /// </summary>
    private static bool MovesWithinExcludedTrees(SetState state, UsnMove move)
    {
        string Folder(string path) => move.IsDirectory ? path : Path.GetDirectoryName(path) ?? path;
        return state.IsInsideExcludedSubtree(Folder(move.OldPath))
               && state.IsInsideExcludedSubtree(Folder(move.NewPath));
    }

    /// <summary>How often the journal may be drained from inside a full scan.</summary>
    private const double FullScanDrainIntervalSeconds = 60;

    /// <summary>
    /// Upper bound on files backed up per drain, so one boundary cannot stall the
    /// scan for long. Anything over the cap stays queued for the next boundary.
    /// </summary>
    private const int MaxDrainFilesPerBoundary = 200;

    private DateTime _lastFullScanDrainUtc = DateTime.MinValue;

    /// <summary>
    /// Truncation and directory discoveries noticed by a drain, handed to the next
    /// <see cref="CheckContinuousAsync"/>. A drain runs from inside a backup and
    /// must not start another one or rewrite the selection, so it records these
    /// and lets the normal poll act on them.
    /// </summary>
    private readonly HashSet<char> _drainTruncatedDrives = [];
    private readonly List<(SetState State, string Dir)> _drainDiscoveredDirs = [];

    /// <summary>
    /// Called at the running backup's commit boundaries. Two jobs:
    ///
    /// <list type="number">
    /// <item><b>Drain the journal.</b> A full scan parks the poll loop for its
    /// whole duration - measured up to 17.8 hours on the author's machine - during
    /// which nothing reads the USN journal. These volumes have the Windows-default
    /// 32 MB journal, which a busy disk wraps in well under that, and a wrap
    /// destroys the records: the next poll reports "continuity lost" and schedules
    /// ANOTHER full scan. Reading here keeps the cursor moving so a long scan
    /// cannot invalidate the very stream it exists to repair.</item>
    /// <item><b>Back up what is ready</b>, so changes made during a multi-hour scan
    /// are protected while it runs instead of waiting for it to finish.</item>
    /// </list>
    ///
    /// <para>Safety rests on two things. It runs at a commit boundary, where the
    /// catalog is committed and no transaction, gate or cross-process lock is held,
    /// so it is sequential with the scan rather than concurrent. And it leaves the
    /// paths the scan intends to write to the scan, which would otherwise copy
    /// them a second time. (The scan numbers each version under the write lock as
    /// it writes, so a version written here would be followed, not duplicated.)</para>
    /// </summary>
    private async Task DrainJournalDuringFullScanAsync(
        SetState scanningSet, IReadOnlySet<string> pathsTheScanWillWrite, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFullScanDrainUtc).TotalSeconds < FullScanDrainIntervalSeconds)
            return;
        _lastFullScanDrainUtc = now;

        // A full scan holds one pass for hours; volumes come and go meanwhile.
        _volumePresence.Clear();

        var continuousSets = _sets.Values
            .Where(s => s.IsActive
                        && s.BackupSet.JobOptions?.Schedule is { Mode: ScheduleMode.Continuous })
            .ToList();

        if (continuousSets.Count == 0)
            return;

        // (a) Read and route. Same routing as the poll loop, minus the actions a
        //     drain must not take: directory materialisation rewrites the set's
        //     selection, and a truncation must schedule a scan - neither belongs
        //     inside a running backup, so both are deferred to the next poll.
        var drives = continuousSets
            .SelectMany(s => s.WatchRoots)
            .Select(GetDriveLetter)
            .Where(c => c != NoDrive)
            .Distinct()
            .ToList();

        int routed = 0;

        foreach (var drive in drives)
        {
            ct.ThrowIfCancellationRequested();

            var (changes, moves, truncated) = await ReadVolumeChangesAsync(drive, ct);
            if (truncated)
                _drainTruncatedDrives.Add(drive);

            routed += RouteJournalRecords(changes, moves, continuousSets, now, _drainDiscoveredDirs);
        }

        // (b) Back up what has settled. Moves are left for the poll loop:
        //     relocating a destination copy while a scan is writing into the same
        //     tree is the one thing here that could collide with it.
        foreach (var state in continuousSets)
        {
            ct.ThrowIfCancellationRequested();

            if (state.Pending.Count == 0)
                continue;

            // The set being scanned is running, so it is not blocked; any other
            // set may be.
            if (!ReferenceEquals(state, scanningSet) && RunsBlocked(state))
                continue;

            // Only the set being scanned has paths to leave alone; a different set
            // is not being written by the scan.
            var excluded = ReferenceEquals(state, scanningSet) ? pathsTheScanWillWrite : null;
            var ready = SelectReadyPaths(state, now, excluded);
            if (ready.Count == 0)
                continue;

            if (ready.Count > MaxDrainFilesPerBoundary)
            {
                foreach (var extra in ready.Skip(MaxDrainFilesPerBoundary))
                    state.Pending.TryAdd(extra, (now, now));
                ready = ready.Take(MaxDrainFilesPerBoundary).ToList();
            }

            _logger.LogInformation(
                "Interleaved continuous backup for \"{Name}\" during a full scan: "
                + "{Count} file(s); {Routed} new journal record(s) this drain.",
                state.BackupSet.Name, ready.Count, routed);

            // Calls the backup service directly: _backupLock is already held by the
            // full scan we are running inside, and it is not reentrant.
            await ExecuteTargetedPathsAsync(state, ready, ct);
        }
    }

    /// <summary>
    /// Get the USN journal reader for a drive, opening it once and caching the
    /// result (including a null result, meaning the volume has no usable journal —
    /// non-NTFS or inaccessible — and must use the FileSystemWatcher fallback).
    /// Callable only from the poll thread.
    /// </summary>
    /// <summary>
    /// Minimum USN journal size the worker wants on a watched volume, and the
    /// allocation delta to grow it by.
    ///
    /// <para>Windows' default is 32 MB, which on a busy volume holds only a few
    /// hundred thousand records. Any stretch where the journal goes unread for
    /// longer than that destroys the records in the gap and forces a whole-tree
    /// rescan: this machine logged 214 "continuity lost" events in two months,
    /// all three volumes still at the default. 512 MB is ~16x the history for an
    /// amount of disk no backup target would notice.</para>
    /// </summary>
    private const long DesiredJournalBytes = 512L * 1024 * 1024;
    private const long DesiredJournalDeltaBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Grow a volume's journal to <see cref="DesiredJournalBytes"/> if it is
    /// smaller. Only ever enlarges, and logs the journal id either side: enlarging
    /// is documented to preserve the existing journal, and the id is how we would
    /// notice if it ever did not.
    /// </summary>
    private void EnsureJournalLargeEnough(char drive, UsnJournalReader reader)
    {
        long before = reader.MaximumSize;
        long idBefore = reader.JournalId;

        var outcome = reader.TryEnsureMinimumSize(
            DesiredJournalBytes, DesiredJournalDeltaBytes);

        switch (outcome)
        {
            case JournalResizeResult.Enlarged:
                reader.TryRefreshPosition(out long idAfter, out _);
                _logger.LogInformation(
                    "Enlarged the USN journal on {Drive}: from {Before} MB to {After} MB so a long "
                    + "backup or outage can no longer wrap it (journal id {IdBefore} -> {IdAfter}"
                    + "{Note}).",
                    drive,
                    before / (1024 * 1024),
                    reader.MaximumSize / (1024 * 1024),
                    idBefore, idAfter,
                    idBefore == idAfter ? ", unchanged as expected" : ", CHANGED — expect one reconciling scan");
                break;

            case JournalResizeResult.NotPermitted:
                _logger.LogInformation(
                    "USN journal on {Drive}: is {Before} MB and could not be enlarged (needs "
                    + "administrator or LocalSystem). Continuous backup still works; a long "
                    + "outage is just more likely to wrap it and force a full rescan.",
                    drive, before / (1024 * 1024));
                break;

            case JournalResizeResult.Failed:
                _logger.LogDebug(
                    "Could not enlarge the USN journal on {Drive}: (currently {Before} MB).",
                    drive, before / (1024 * 1024));
                break;
        }
    }

    private UsnJournalReader? GetJournalReader(char drive)
    {
        // An absent volume has no journal, but that answer must not be cached as
        // "no usable journal": the volume may be plugged in later, and is read
        // from then on. A reader whose volume has gone is dropped, so a volume
        // that turns up under the same letter is opened afresh.
        if (!VolumeIsPresent(drive))
        {
            if (_journalReaders.Remove(drive, out var gone))
                gone?.Dispose();
            return null;
        }

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
            else
            {
                EnsureJournalLargeEnough(drive, reader);
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
        return drive != NoDrive && VolumeIsPresent(drive) && GetJournalReader(drive) is null;
    }

    /// <summary>
    /// Whether a volume is mounted at this drive letter, memoised for the pass
    /// (<see cref="_volumePresence"/>). Poll thread only.
    /// </summary>
    private bool VolumeIsPresent(char drive)
    {
        if (!_volumePresence.TryGetValue(drive, out bool present))
        {
            present = Directory.Exists($"{drive}:\\");
            _volumePresence[drive] = present;
        }
        return present;
    }

    /// <summary>
    /// Point the fallback FileSystemWatcher at the current non-NTFS watch roots
    /// across all continuous sets. A root starts being watched when it is listed
    /// and exists: at the first pass after a process start, and whenever a root
    /// appears later (a drive plugged in, a folder re-created). Each set with a
    /// root that just started is flagged for a full reconciling scan, since no
    /// watcher saw what changed there before.
    /// </summary>
    /// <remarks>
    /// Roots that stay keep their watchers (<see cref="FileSystemMonitorImpl.Watch"/>),
    /// so a root coming or going costs no other set a reconcile. A root that goes
    /// simply stops being watched, and reconciles when it returns. A volume that
    /// is not there at all is not a fallback volume (<see cref="UsesFallbackVolume"/>):
    /// once it is plugged in it is read through its journal if it has one.
    /// </remarks>
    private void MaintainFallbackWatchers(IReadOnlyList<SetState> continuousSets)
    {
        var fallbackRoots = continuousSets
            .SelectMany(s => s.WatchRoots)
            .Where(UsesFallbackVolume)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var started = _fsMonitor.Watch(fallbackRoots);
        if (started.Count == 0)
            return;

        var justStarted = new HashSet<string>(started, StringComparer.OrdinalIgnoreCase);
        foreach (var state in continuousSets)
        {
            if (!state.NeedsReconcile && state.WatchRoots.Any(justStarted.Contains))
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
                    // Nothing in a wholly excluded tree is queued or pinned.
                    if ((changeType is FileChangeType.Created or FileChangeType.Renamed)
                        && !state.IsInsideExcludedSubtree(changedPath))
                    {
                        EnqueueForBackup(state, changedPath, isDirectory: true, now);
                        // A new folder covered only by auto-include should also become
                        // a permanent explicit selection (pinned after routing).
                        discoveredDirs.Add((state, changedPath));
                    }
                }
                else if (!state.IsExcludedFile(changedPath))
                {
                    Touch(state, changedPath, now);
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
    /// Goes through <see cref="ChangeSavedSelectionAsync"/>, which re-reads the set
    /// so that a GUI edit is not overwritten, and adopts the pinned tree so this
    /// pass's later stages and later passes route against it. Callers pass only
    /// folders outside wholly excluded trees (<see cref="IsPinCandidate"/>).
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

            var dirs = group
                .Select(d => d.Dir)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await ChangeSavedSelectionAsync(state, "pin auto-included folders into", (set, selections) =>
            {
                int pinned = 0;
                foreach (var dir in dirs)
                {
                    if (SourceSelection.MaterializeDirectory(selections, dir))
                    {
                        pinned++;
                        _logger.LogInformation(
                            "Auto-included new folder \"{Dir}\" into set \"{Name}\" as a permanent selection.",
                            dir, set.Name);
                    }
                }
                return pinned;
            }, ct);
        }
    }

    /// <summary>
    /// Whether a changed file should be queued for this set's continuous backup:
    /// it is in the selection, and the set's rules do not exclude it. The backup
    /// would skip an excluded file anyway; dropping it here keeps it out of the
    /// queue, which a build can otherwise fill with thousands of them, held in
    /// memory for as long as the destination stays unplugged. Moves are
    /// routed by <see cref="PathBelongsToSet"/> alone, since a move into an
    /// excluded folder is something <see cref="RunMovesAsync"/> must act on.
    /// </summary>
    private static bool IsBackupCandidate(SetState state, string path)
        => PathBelongsToSet(state.BackupSet, path) && !state.IsExcludedFile(path);

    /// <summary>
    /// Whether a new folder should be considered for pinning into this set: it is
    /// in the selection and not inside a tree the set's rules exclude entirely.
    /// A pin there could never affect a backup, and builds create such folders by
    /// the thousand.
    /// </summary>
    private static bool IsPinCandidate(SetState state, string directory)
        => PathBelongsToSet(state.BackupSet, directory) && !state.IsInsideExcludedSubtree(directory);

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

        // Hard exclusion: NTFS volume metadata ($Extend and friends). The USN
        // journal names these, so they reach us here as ordinary-looking paths,
        // but no directory enumeration will ever produce them — so they can't be
        // shown in the selection treeview, can't be deselected, and must not be
        // swept in by an auto-include-new parent such as all of C:\. Rejecting
        // them here also keeps them out of auto-include-new's folder discovery.
        // See VolumeMetadataPaths for the full rationale.
        if (VolumeMetadataPaths.IsVolumeMetadata(path))
            return false;

        if (set.SourceSelections is { Count: > 0 })
            return SourceSelection.IsPathIncluded(set.SourceSelections, path);

        return set.SourceRoots.Any(root => IsUnderRoot(path, root));
    }

    /// <summary>
    /// Current size of a file in bytes, or 0 if it can't be stat'd (missing,
    /// locked, or a transient error). Used to pick the debounce size tier; 0
    /// simply selects the smallest tier, which is the safe default for a file we
    /// can't measure.
    /// </summary>
    private static long GetFileSizeSafe(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>"no drive letter" sentinel returned by <see cref="GetDriveLetter"/>.</summary>
    private const char NoDrive = '\0';

    private static char GetDriveLetter(string path)
    {
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            return char.ToUpperInvariant(path[0]);
        return NoDrive;
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
            await ExecuteTargetedPathsAsync(state, paths, ct);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    /// <summary>
    /// Back up exactly <paramref name="paths"/> for a set, <b>without</b> taking
    /// <see cref="_backupLock"/>.
    ///
    /// <para>Split out from <see cref="RunTargetedBackupAsync"/> so the
    /// commit-boundary drain can reuse it: that runs from inside a full scan which
    /// already holds the lock, and <see cref="SemaphoreSlim"/> is not reentrant, so
    /// going through the locking wrapper would silently requeue everything and do
    /// nothing at all. Every other caller must use the wrapper.</para>
    /// </summary>
    private async Task ExecuteTargetedPathsAsync(
        SetState state, IReadOnlyList<string> paths, CancellationToken ct)
    {
        void Requeue()
        {
            // Exactly as for a destination that is not connected.
            var now = DateTime.UtcNow;
            foreach (var p in paths)
                state.Pending.TryAdd(p, (now, now));
        }

        // Inside the full scan of this same set (its journal drain), the scan's
        // run is already announced and watching; this is part of it.
        var outer = state.ActiveRun;
        SetRun? run = null;
        try
        {
            if (outer is null)
            {
                run = StartSetRun(state, stopForApp: true, ct);
                if (run is null)
                {
                    Requeue();
                    return;
                }
                ct = run.Token;
            }

            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(state, ct);
            if (targetDir is null)
            {
                // Destination not connected — requeue these paths for next poll.
                var requeueNow = DateTime.UtcNow;
                foreach (var p in paths)
                    state.Pending.TryAdd(p, (requeueNow, requeueNow));
                return;
            }

            // Dropped rather than requeued: nothing can run until the user acts,
            // and the full backup that follows picks these files up anyway.
            if (await RefuseUntrackedDestinationAsync(state, targetDir, ct))
                return;

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
        catch (OperationCanceledException) when ((run ?? outer)?.StoppedForApp == true)
        {
            // Before the general case: the run's token is `ct` now, so that one
            // would match too, and drop the paths.
            Requeue();
            _logger.LogInformation(
                "Continuous backup for \"{Name}\" paused: Lithic Backup is working on it. "
                + "Its changes are backed up when that is done.", state.BackupSet.Name);
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
            run?.Dispose();
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

        SetRun? run = null;
        try
        {
            // Announced, so a cleanup waits for the moves, but not stopped by
            // one: they are quick renames, and the queue is cleared below.
            run = StartSetRun(state, stopForApp: false, ct);
            if (run is null)
                return; // moves stay queued until the app is done with the set

            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(state, ct);
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
            int relocated = 0, recopied = 0, freshNames = 0, excludedDest = 0;

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

                        case TargetedMoveOutcome.ExcludedAtDestination:
                            // The move landed the item on a path this set excludes.
                            // Both endpoints are in the SELECTION, so PathBelongsToSet
                            // saw an ordinary in-set move — only the service, which
                            // holds the job's exclusion rules, can tell the difference.
                            // Release it exactly as for a move out of the selection:
                            // tombstone the old record so the destination copy stops
                            // being tracked at a path the set does not back up.
                            //
                            // The new path is still enqueued, and deliberately: for a
                            // DIRECTORY move only some of the moved files may match an
                            // exclusion rule, and the copy path re-applies the same
                            // filter per file — so this drops exactly the excluded ones
                            // and still backs up their siblings. When everything under
                            // the move is excluded the enqueue costs one filtered pass
                            // and copies nothing.
                            excludedDest++;
                            EnqueueForBackup(state, move.NewPath, move.IsDirectory, now);
                            if (!await MarkMovedOutAsync(set, move.OldPath, move.IsDirectory, ct))
                                // A file was re-created at the old path (atomic-save
                                // replace): it wasn't tombstoned, so back it up in place.
                                EnqueueForBackup(state, move.OldPath, move.IsDirectory, now);
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

            if (relocated > 0 || recopied > 0 || excludedDest > 0)
            {
                state.LastRunUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "Continuous backup for \"{Name}\": relocated {Relocated} moved item(s) in place; " +
                    "{Recopied} tracked item(s) could not be relocated and will be re-copied.",
                    set.Name, relocated, recopied);
            }

            if (excludedDest > 0)
                _logger.LogInformation(
                    "Continuous backup for \"{Name}\": {Count} moved item(s) landed on excluded " +
                    "path(s); their backups were released rather than relocated.",
                    set.Name, excludedDest);

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
            run?.Dispose();
            _backupLock.Release();
        }
    }

    /// <summary>
    /// Queue a path for a normal continuous backup. For a directory, enqueues
    /// every file beneath it that the set's rules do not exclude; for a file,
    /// enqueues the file itself.
    /// </summary>
    private static void EnqueueForBackup(SetState state, string path, bool isDirectory, DateTime now)
    {
        if (!isDirectory)
        {
            Touch(state, path, now);
            return;
        }

        // Wholly excluded subtrees are not entered, as in the scanner, and an
        // unreadable folder part-way is skipped instead of ending the walk.
        var prunes = state.PrunesDirectory;
        var files = new FileSystemEnumerable<string>(
            path, (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !prunes(entry.ToFullPath()),
        };

        try
        {
            foreach (var f in files)
            {
                if (!state.IsExcludedFile(f))
                    Touch(state, f, now);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went while it was being read; what was queued stays.
        }
    }

    /// <summary>
    /// Queue a changed path for the set's per-file debounce: now is its latest
    /// change, and the first is kept if it was queued already.
    /// </summary>
    private static void Touch(SetState state, string path, DateTime now)
        => state.Pending[path] = state.Pending.TryGetValue(path, out var t) ? (t.First, now) : (now, now);

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
            // The old name's records go, but not one that is the last copy of
            // content another file's .fileref resolves to: its stored file stays
            // where it is, and the reference needs a live record to find it by.
            var records = isDirectory
                ? await _catalog.GetFileRecordsUnderDirectoryAsync(set.Id, oldPath, ct)
                : await _catalog.GetFileRecordsByPathAsync(set.Id, oldPath, ct);
            var live = records.Where(r => !r.IsDeleted).Select(r => r.Id).ToList();
            await _catalog.RetireRecordsKeepingNeededCopiesAsync(set.Id, live, ct: ct);
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
    /// What Lithic Backup is doing with the set, if anything that the Worker must
    /// give way to: cleaning it up (<see cref="ICatalogRepository.HoldSetForCleanup"/>),
    /// or working on it some other way - a backup, a seed - with its row busy
    /// (<see cref="ICatalogRepository.HoldSetInApp"/>). Null when nothing is.
    /// </summary>
    private string? AppWorkOn(int setId)
        => _catalog.IsSetBeingCleaned(setId) ? "it is being cleaned up in Lithic Backup"
            : _catalog.IsSetHeldInApp(setId) ? "Lithic Backup is working on it"
            : null;

    /// <summary>
    /// True while the app is working on this set (<see cref="AppWorkOn"/>): its
    /// backups wait, keeping their queued work, until it is done. A purge deletes
    /// backed-up files after its catalog transaction commits, and a copy written
    /// at one of those paths meanwhile would be deleted with them; a backup in the
    /// app does not give the set's write lock up, so a run that waited for it held
    /// the Worker - every set - for as long as that backup ran. Logged once per
    /// wait. Asked by <see cref="StartSetRun"/>, after the run has announced itself.
    /// </summary>
    private bool AppHoldsSet(SetState state)
    {
        if (AppWorkOn(state.BackupSet.Id) is not { } why)
        {
            if (state.LoggedAppWait)
                _logger.LogInformation("Lithic Backup is done with \"{Name}\"; its backups carry on.", state.BackupSet.Name);
            state.LoggedAppWait = false;
            return false;
        }
        if (!state.LoggedAppWait)
        {
            _logger.LogInformation(
                "Backups of \"{Name}\" wait: {Why}.", state.BackupSet.Name, why);
            state.LoggedAppWait = true;
        }
        return true;
    }

    /// <summary>
    /// Start a run on a set, or null if the app is working on it
    /// (<see cref="AppWorkOn"/>; the caller keeps the work for later, as for a
    /// destination that is not connected). The run announces itself before it
    /// checks (<see cref="ICatalogRepository.HoldSetForBackgroundBackup"/>), and
    /// with <paramref name="stopForApp"/> watches for app work that begins while
    /// it runs and cancels <see cref="SetRun.Token"/> within a second: a purge
    /// waits for the run's marker to go before it deletes anything, a backup in
    /// the app waits for the set's lock, and a full backup can run for hours.
    /// Checking only at the start let a run that was already going copy files at
    /// the very paths the purge then deleted.
    /// </summary>
    private SetRun? StartSetRun(SetState state, bool stopForApp, CancellationToken ct)
    {
        int setId = state.BackupSet.Id;
        var marker = _catalog.HoldSetForBackgroundBackup(setId);
        if (AppHoldsSet(state))
        {
            marker.Dispose();
            return null;
        }
        var run = new SetRun(state, marker, stopForApp ? () => AppWorkOn(setId) is not null : null, ct);
        state.ActiveRun = run;
        return run;
    }

    /// <summary>One Worker run on one set; see <see cref="StartSetRun"/>.</summary>
    private sealed class SetRun : IDisposable
    {
        private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

        private readonly SetState _state;
        private readonly IDisposable _marker;
        private readonly CancellationTokenSource _cts;
        private readonly Timer? _watch;
        private int _stopped;
        private int _disposed;

        public SetRun(SetState state, IDisposable marker, Func<bool>? appWorking, CancellationToken ct)
        {
            _state = state;
            _marker = marker;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Token = _cts.Token;
            if (appWorking is not null)
                _watch = new Timer(_ =>
                {
                    if (Volatile.Read(ref _disposed) != 0 || !appWorking())
                        return;
                    Interlocked.Exchange(ref _stopped, 1);
                    try { _cts.Cancel(); }
                    catch (ObjectDisposedException) { }
                }, null, WatchInterval, WatchInterval);
        }

        public CancellationToken Token { get; }

        /// <summary>Whether the app's work on the set, not shutdown, stopped the run.</summary>
        public bool StoppedForApp => Volatile.Read(ref _stopped) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _watch?.Dispose();
            if (ReferenceEquals(_state.ActiveRun, this))
                _state.ActiveRun = null;
            // Last: a cleanup waiting on the marker must not start while this
            // run can still write.
            _marker.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Run a full scan-and-backup (scheduled interval/daily runs, and continuous
    /// reconciliation after a journal gap). Returns <c>true</c> when the scan
    /// actually ran, and whether it reconciled cleanly.
    ///
    /// <para>The three-way result matters: "did not run" and "ran but some files
    /// failed" used to be collapsed into the same <c>true</c>, which silently
    /// discarded the failed files' queued deltas. See
    /// <see cref="ReconcileOutcome"/>.</para>
    /// </summary>
    private async Task<ReconcileOutcome> RunFullBackupAsync(SetState state, CancellationToken ct)
    {
        if (!await _backupLock.WaitAsync(0, ct))
        {
            _logger.LogInformation(
                "Skipping backup for \"{Name}\" — another backup is in progress.", state.BackupSet.Name);
            return ReconcileOutcome.DidNotRun;
        }

        SetRun? run = null;
        try
        {
            run = StartSetRun(state, stopForApp: true, ct);
            if (run is null)
                return ReconcileOutcome.DidNotRun; // retried once the app is done with the set
            // The run's own token from here: a cleanup of the set stops it.
            var runCt = run.Token;

            var set = state.BackupSet;
            var opts = set.JobOptions!;
            var targetDir = await ResolveDestinationAsync(state, runCt);
            if (targetDir is null)
                return ReconcileOutcome.DidNotRun; // Destination not connected; retry later.

            if (!await ResolveSourcesAsync(state, runCt))
                return ReconcileOutcome.DidNotRun; // No source available; retry later.

            if (await RefuseUntrackedDestinationAsync(state, targetDir, runCt))
                return ReconcileOutcome.DidNotRun; // Until the user seeds or backs up in the app.

            _logger.LogInformation("Starting backup for \"{Name}\" → {Target}", set.Name, targetDir);

            var job = BuildJob(set, opts, targetDir);

            var (diff, totalBytes, totalFiles) = await _directoryBackup.PlanAsync(job, runCt);

            if (totalFiles == 0)
            {
                _logger.LogInformation("Nothing to back up for \"{Name}\".", set.Name);
                state.LastRunUtc = DateTime.UtcNow;
                state.ConsecutiveFailures = 0;
                MarkFullScanCompleted(state);
                return ReconcileOutcome.RanClean;
            }

            _logger.LogInformation(
                "Plan for \"{Name}\": {Files} files, {Bytes} bytes ({New} new, {Changed} changed, {Deleted} deleted).",
                set.Name, totalFiles, totalBytes,
                diff.NewFiles.Count, diff.ChangedFiles.Count, diff.DeletedFiles.Count);

            var retentionTiers = opts.RetentionTiers.Count > 0
                ? opts.RetentionTiers
                : VersionRetentionService.DefaultTiers;

            // Hand over the plan's diff: without it ExecuteAsync scans the whole
            // tree and diffs it against the catalog all over again.
            var result = await _directoryBackup.ExecuteAsync(
                job, targetDir, retentionTiers, progress: null, runCt,
                precomputedDiff: diff,
                onCommitBoundary: (excluded, innerCt) =>
                    DrainJournalDuringFullScanAsync(state, excluded, innerCt));

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

            state.ConsecutiveFailures = 0;
            MarkFullScanCompleted(state);
            return result.Success
                ? ReconcileOutcome.RanClean
                : ReconcileOutcome.RanWithFailures;
        }
        catch (BackupCanceledException ex) when (ex.CopyFinished
                                                 && (ct.IsCancellationRequested || run?.StoppedForApp == true))
        {
            // Stopped during verification or retention: every file was copied and
            // committed, so the scan did its job, and repeating hours of it after
            // the restart would gain nothing.
            // Its whole-set retention - the only pruning of versions the targeted
            // runs never touch - did not finish; the next full scan does it.
            _logger.LogInformation(
                "Backup for \"{Name}\" stopped after its copy finished; retention did not complete.",
                state.BackupSet.Name);
            state.LastRunUtc = DateTime.UtcNow;
            state.ConsecutiveFailures = 0;
            MarkFullScanCompleted(state);
            return ex.FailedFiles.Count == 0
                ? ReconcileOutcome.RanClean
                : ReconcileOutcome.RanWithFailures;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Backup for \"{Name}\" cancelled.", state.BackupSet.Name);
            return ReconcileOutcome.DidNotRun;
        }
        catch (OperationCanceledException) when (run?.StoppedForApp == true)
        {
            // Whole paths were committed as it went; the next run carries on.
            _logger.LogInformation(
                "Backup for \"{Name}\" paused: Lithic Backup is working on it. It runs again when that is done.",
                state.BackupSet.Name);
            return ReconcileOutcome.DidNotRun;
        }
        catch (Exception ex)
        {
            state.ConsecutiveFailures++;
            var wait = FailureBackoff(state.ConsecutiveFailures);
            state.FullBackupsBlockedUntilUtc = DateTime.UtcNow + wait;
            _logger.LogError(ex,
                "Backup failed for \"{Name}\" ({Count} in a row); no full backup of it starts for {Minutes:F0} minute(s).",
                state.BackupSet.Name, state.ConsecutiveFailures, wait.TotalMinutes);
            return ReconcileOutcome.DidNotRun;
        }
        finally
        {
            run?.Dispose();
            _backupLock.Release();
        }
    }

    /// <summary>
    /// True when the set must not be backed up because its catalog is empty while
    /// its destination already holds backed-up files where this set writes (see
    /// <see cref="DirectoryBackupService.FindUntrackedBackupAsync"/>). A run then
    /// copies every file again over the copies already there, and the version
    /// history beside them is invisible to the set. That is a decision for the
    /// user, in the app: seed from the destination, or back up anyway. Logged
    /// once per destination, not at every poll.
    /// </summary>
    private async Task<bool> RefuseUntrackedDestinationAsync(SetState state, string targetDir, CancellationToken ct)
    {
        var set = state.BackupSet;
        DirectoryBackupService.UntrackedBackup? found;
        try
        {
            // What the selection covers, not the watch roots: those include whole
            // partial folders that auto-include new ones, and probing all of such
            // a folder would find another set's files in a shared destination.
            var written = set.SourceSelections is { Count: > 0 } selections
                ? SourceSelection.CollectCoveredEntries(selections)
                : set.SourceRoots.ToList();
            found = await _directoryBackup.FindUntrackedBackupAsync(
                set.Id, written, targetDir, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Cannot tell: err on the side of not writing over a backup.
            _logger.LogWarning(ex,
                "Could not check whether the destination of \"{Name}\" already holds a backup; skipping this run.",
                set.Name);
            BlockRuns(state);
            return true;
        }

        if (found is null)
        {
            state.RefusedUntrackedAt = null;
            return false;
        }

        // Only the user, in the app, can end this, so asking again at every pass
        // would only re-read the destination.
        BlockRuns(state);

        if (!string.Equals(state.RefusedUntrackedAt, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            state.RefusedUntrackedAt = targetDir;
            _logger.LogWarning(
                "Not backing up \"{Name}\": it has no backup history, but its destination already holds " +
                "backed-up files ({Example}{More}). A backup now would copy everything again over them{History}. " +
                "Open the set in Lithic Backup and use Seed from Existing Backup in its editor, or run a backup " +
                "there to go ahead anyway. Its automated backups are paused until then, which its row in Lithic " +
                "Backup shows.",
                set.Name, found.OccupiedPaths[0],
                found.OccupiedPaths.Count > 1 ? $" and {found.OccupiedPaths.Count - 1} more" : "",
                found.HasVersionHistory ? ", and the version history kept there would not be part of the set" : "");
        }

        // A log line alone left a scheduled set unbacked-up with nothing in the
        // app to say why. Held, its row says its automated backups are paused, and
        // the next backup run there asks whether to seed or to back up anyway.
        await HoldAutomatedBackupsAsync(set, ct);
        return true;
    }

    /// <summary>
    /// Hold the set's automated backups until the user runs one in the app,
    /// written on a fresh copy of the set (see <see cref="PersistResolvedAsync"/>).
    /// </summary>
    private async Task HoldAutomatedBackupsAsync(BackupSet set, CancellationToken ct)
    {
        try
        {
            var fresh = await _catalog.GetBackupSetAsync(set.Id, ct);
            if (fresh?.JobOptions is { HoldAutomatedBackups: false } opts)
            {
                opts.HoldAutomatedBackups = true;
                await _catalog.UpdateBackupSetMetadataAsync(fresh, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not pause the automated backups of \"{Name}\".", set.Name);
        }
    }

    /// <summary>Build a <see cref="BackupJob"/> from a set's saved options.</summary>
    /// <remarks>
    /// Refreshes the machine settings first: a pass can run backups for hours,
    /// and a save made meanwhile must reach the next job.
    /// </remarks>
    private BackupJob BuildJob(BackupSet set, JobOptions opts, string targetDir)
    {
        ReloadMachineSettings();
        var machineSettings = _machine.Current;
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
            MemoryBudgetWrittenUtc = _machine.CurrentWrittenUtc,
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

        /// <summary>Whether the set was held at the last reload (to log the change once).</summary>
        public bool WasHeld { get; set; }

        /// <summary>Whether "waiting for the app" has been logged for the current wait.</summary>
        public bool LoggedAppWait { get; set; }

        /// <summary>
        /// The Worker's run on this set, while there is one (see StartSetRun). A
        /// targeted backup the full scan's journal drain makes of this set runs
        /// inside it, and must neither announce itself again nor end the marker.
        /// </summary>
        public SetRun? ActiveRun { get; set; }

        /// <summary>
        /// The destination this set was last refused at because its catalog is
        /// empty and the destination already holds a backup (see
        /// <c>RefuseUntrackedDestinationAsync</c>), so the refusal is logged once
        /// rather than at every poll. Null while not refusing.
        /// </summary>
        public string? RefusedUntrackedAt { get; set; }

        /// <summary>
        /// Directories actually selected for this set (not the drive roots).
        /// Used to pick which volumes to read and which changes are relevant.
        /// </summary>
        public List<string> WatchRoots { get; set; } = [];

        /// <summary>
        /// No run of this set (full, targeted or moves) is attempted before this
        /// time. Set by <c>BlockRuns</c> when a run could not start.
        /// </summary>
        public DateTime RunsBlockedUntilUtc { get; set; }

        /// <summary>
        /// No full backup of this set (reconcile, safety net or schedule) starts
        /// before this time: the backoff after full backups that threw
        /// (<c>FailureBackoff</c>). Targeted runs and moves are not held by it.
        /// </summary>
        public DateTime FullBackupsBlockedUntilUtc { get; set; }

        /// <summary>
        /// When the destination was found not connected, or null while it is
        /// connected (or not yet looked at). Lets the loss and the return each be
        /// logged once.
        /// </summary>
        public DateTime? DestinationMissingSinceUtc { get; set; }

        /// <summary>Whether the last full run found none of the set's sources available, so the change can be logged once.</summary>
        public bool SourcesMissing { get; set; }

        /// <summary>The sources the last full run found missing while others were there, or null; logged when it changes.</summary>
        public string? PartlyMissingSources { get; set; }

        /// <summary>Full backups in a row that threw; sets the backoff before the next attempt.</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>
        /// The set's exclusion rules as tests, rebuilt by <see cref="ApplyExclusionRules"/>
        /// at each reload. <see cref="IsExcludedFile"/> is the backup's own per-file
        /// filter; <see cref="PrunesDirectory"/> says whether every file beneath one
        /// directory is excluded, answered for that directory alone;
        /// <see cref="IsInsideExcludedSubtree"/> answers the same for the directory
        /// or any of its ancestors.
        /// </summary>
        public Func<string, bool> IsExcludedFile { get; private set; } = _ => false;

        /// <inheritdoc cref="IsExcludedFile"/>
        public Func<string, bool> PrunesDirectory { get; private set; } = _ => false;

        /// <inheritdoc cref="IsExcludedFile"/>
        public Func<string, bool> IsInsideExcludedSubtree { get; private set; } = _ => false;

        /// <summary>The rules the tests above were built from, so that an unchanged set does not recompile them.</summary>
        private string? _exclusionRulesKey;

        /// <summary>
        /// Build the exclusion tests from the set's options, unless the rules are
        /// the ones already built: they compile a regular expression per pattern,
        /// and the sets are reloaded after every backup.
        /// </summary>
        public void ApplyExclusionRules(JobOptions? options)
        {
            var excluded = options?.ExcludedExtensions ?? [];
            var tierSets = options?.TierSets ?? [];
            string key = System.Text.Json.JsonSerializer.Serialize(new { excluded, tierSets });
            if (key == _exclusionRulesKey)
                return;

            IsExcludedFile = DirectoryBackupService.BuildExclusionFilter(excluded, tierSets) ?? (_ => false);
            var prunes = DirectoryBackupService.BuildDirectoryPruneFilter(excluded, tierSets) ?? (_ => false);
            PrunesDirectory = prunes;
            IsInsideExcludedSubtree = directory => SourceSelection.IsInsidePrunedDirectory(directory, prunes);
            _exclusionRulesKey = key;
        }

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

        /// <summary>
        /// When this set last completed a WHOLE-TREE scan. Distinct from
        /// <see cref="LastRunUtc"/>, which every targeted continuous backup
        /// bumps too — using that to time the safety-net scan below would mean a
        /// set that is busy continuously never gets one, which is exactly
        /// backwards. Persisted (see <c>FullScanStampPath</c>) so restarting the
        /// service does not reset the clock.
        /// </summary>
        public DateTime LastFullScanUtc { get; set; }
    }

    /// <summary>
    /// What a reconciling full scan actually did. "Did not run" and "ran but
    /// some files failed" must not be the same answer: the first means retry,
    /// the second means stop retrying the whole tree but keep the failed files'
    /// queued deltas. Collapsing them into a bool is what silently dropped the
    /// failures — see the continuous-backup entry in known-issues.md.
    /// </summary>
    private enum ReconcileOutcome
    {
        /// <summary>Skipped — lock busy, destination offline, or it threw.</summary>
        DidNotRun,

        /// <summary>Walked the whole tree; every file it tried succeeded.</summary>
        RanClean,

        /// <summary>Walked the whole tree; at least one file failed to copy.</summary>
        RanWithFailures,
    }
}
