using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Background monitor that warns when a backup set's destination drive is
/// (practically) full <em>while that set has continuous backup enabled</em>.
///
/// Continuous sets are driven by the headless Worker service, which has no UI:
/// when its destination fills up, backups just fail silently — new file versions
/// never get written and nothing tells the user. This monitor runs in the
/// interactive GUI, polls each continuous set's destination free space, and
/// raises <see cref="DestinationFull"/> so the tray can pop a balloon the moment
/// a continuous destination can no longer accept writes.
/// </summary>
/// <remarks>
/// This is intentionally a proactive free-space check rather than a report of an
/// actual write failure: it warns the user <em>before</em> versions start
/// silently going missing, and it needs no IPC with the Worker (the GUI and the
/// Worker share only the catalog database).
/// </remarks>
public sealed class DestinationSpaceMonitor : IDisposable
{
    private readonly ICatalogRepository _catalog;
    private readonly IDestinationResolver _destinationResolver;

    /// <summary>
    /// Free bytes available on a drive root, or null when it can't be queried.
    /// Injectable so the debounce/hysteresis logic can be exercised against a
    /// scripted sequence of readings — the real condition (a volume dropping
    /// below 1 GB and recovering) is not something a test can stage on a live
    /// disk.
    /// </summary>
    private readonly Func<string, long?> _freeSpaceProbe;

    private Timer? _timer;
    private bool _disposed;

    // Serializes sweeps: a slow catalog read or drive query must not overlap the
    // next timer tick. WaitAsync(0) simply skips a tick if one is still running.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Drive roots (e.g. <c>J:\</c>) we've already warned about, so the warning
    /// fires once per fill event instead of on every poll. A root is removed once
    /// its free space recovers above <see cref="LowSpaceThresholdBytes"/> plus
    /// <see cref="RecoveryMarginBytes"/>. Only ever touched inside <see cref="_gate"/>.
    /// </summary>
    private readonly HashSet<string> _warnedRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-root count of consecutive sweeps that read below
    /// <see cref="LowSpaceThresholdBytes"/>. Reset to zero (removed) the moment a
    /// sweep reads above it. Only ever touched inside <see cref="_gate"/>.
    /// </summary>
    private readonly Dictionary<string, int> _lowSweepStreak = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Free-space floor below which a destination drive is treated as "full".
    /// 1 GB is a practical cut-off: below it, continuous backups can no longer
    /// reliably write new file versions.
    /// </summary>
    public long LowSpaceThresholdBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// How many <em>consecutive</em> sweeps a destination must read below
    /// <see cref="LowSpaceThresholdBytes"/> before <see cref="DestinationFull"/>
    /// fires. At the GUI's 30-second cadence, 3 means the drive has to have been
    /// genuinely out of space for roughly a minute and a half.
    /// </summary>
    /// <remarks>
    /// Free space can crater for a minute and then recover entirely on its own —
    /// most commonly when the Volume Shadow Copy Service grows a diff area,
    /// exhausts the volume, and Windows responds by discarding the shadow copies
    /// (System log: Volsnap 24 then Volsnap 35). A single low sample is therefore
    /// not evidence of a problem the user needs to act on, but it <em>was</em>
    /// enough to raise a modal that then sat on screen for hours — by which time
    /// the drive showed tens of GB free and the warning read as a plain lie.
    /// Requiring a sustained condition keeps the alert trustworthy; the per-row
    /// status published by <see cref="StatusUpdated"/> is undebounced and still
    /// reflects every sample.
    /// </remarks>
    public int ConsecutiveLowSweepsBeforeAlert { get; set; } = 3;

    /// <summary>
    /// Hysteresis margin. A drive we've already warned about is only re-armed
    /// (eligible to warn again) once its free space recovers to
    /// <see cref="LowSpaceThresholdBytes"/> + this margin, so a drive hovering
    /// right at the threshold doesn't emit a balloon on every poll.
    /// </summary>
    private const long RecoveryMarginBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Raised (on a thread-pool thread) when a continuous set's destination drive
    /// has been full for <see cref="ConsecutiveLowSweepsBeforeAlert"/> sweeps
    /// running. Subscribers must marshal to the UI thread themselves, and should
    /// call <see cref="IsStillFull"/> immediately before actually showing the
    /// alert — see that method for why.
    /// </summary>
    public event Action<DestinationFullAlert>? DestinationFull;

    /// <summary>
    /// Raised (on a thread-pool thread) after every sweep with the current
    /// destination-space status of <em>every</em> set that has a connected,
    /// ready destination (keyed by set id). Unlike <see cref="DestinationFull"/>
    /// — a one-shot, continuous-only alert — this reports the live full/not-full
    /// state for all sets so the GUI can show (and clear) a persistent per-row
    /// status. Sets whose destination is missing/offline are absent from the map;
    /// subscribers should treat "absent" as "not full / unknown". Subscribers must
    /// marshal to the UI thread themselves.
    /// </summary>
    public event Action<IReadOnlyDictionary<int, DestinationSpaceStatus>>? StatusUpdated;

    /// <param name="catalog">Source of the backup sets to sweep.</param>
    /// <param name="destinationResolver">Maps a set's stored destination to a live path.</param>
    /// <param name="freeSpaceProbe">
    /// Optional override for the free-space query (see <see cref="_freeSpaceProbe"/>).
    /// Defaults to <see cref="DriveInfo.AvailableFreeSpace"/>.
    /// </param>
    public DestinationSpaceMonitor(
        ICatalogRepository catalog,
        IDestinationResolver destinationResolver,
        Func<string, long?>? freeSpaceProbe = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _destinationResolver = destinationResolver ?? throw new ArgumentNullException(nameof(destinationResolver));
        _freeSpaceProbe = freeSpaceProbe ?? QueryDriveFreeSpace;
    }

    /// <summary>
    /// Start polling destination free space at the given interval. Fires an
    /// initial sweep immediately so an already-full drive is reported shortly
    /// after launch.
    /// </summary>
    public void Start(TimeSpan interval)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, interval);
    }

    /// <summary>
    /// Check every continuous-enabled set's destination once. Public so a caller
    /// can trigger an on-demand sweep. Never throws.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0))
            return; // disposed, or a previous sweep is still running

        try
        {
            IReadOnlyList<BackupSet> sets;
            try
            {
                sets = await _catalog.GetAllBackupSetsAsync();
            }
            catch
            {
                return; // catalog unavailable — try again next tick
            }

            // --- Pass 1: map each set to its destination drive root, sampling
            // each distinct root's free space exactly once. Several sets often
            // share a drive, and one DriveInfo query per set would both waste
            // work and let the same root be counted twice in a sweep. ---
            var rootBySet = new Dictionary<int, string>();
            var freeByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var set in sets)
            {
                var opts = set.JobOptions;
                if (opts is null)
                    continue;

                string? root = ResolveDestinationRoot(opts);
                if (root is null)
                    continue; // no configured / connected destination — no row status

                if (!freeByRoot.ContainsKey(root))
                {
                    if (!TryGetFreeBytes(root, out long bytes))
                        continue; // drive not ready / query failed — skip this one
                    freeByRoot[root] = bytes;
                }

                rootBySet[set.Id] = root;
            }

            // --- Pass 2: advance each root's consecutive-low streak. A root only
            // becomes alert-worthy once it has read low ConsecutiveLowSweepsBeforeAlert
            // times running, which filters the transient dips (VSS diff-area
            // growth, a big temp file) that Windows resolves by itself. ---
            var sustainedLow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (root, free) in freeByRoot)
            {
                if (free >= LowSpaceThresholdBytes)
                {
                    _lowSweepStreak.Remove(root);
                    continue;
                }

                int streak = _lowSweepStreak.TryGetValue(root, out int prior) ? prior + 1 : 1;
                _lowSweepStreak[root] = streak;
                if (streak >= ConsecutiveLowSweepsBeforeAlert)
                    sustainedLow.Add(root);
            }

            // A root we couldn't sample this sweep (drive unplugged mid-fill)
            // must not keep a half-built streak: when it comes back the count
            // has to start over from the reading we actually observed.
            foreach (var stale in _lowSweepStreak.Keys.Where(r => !freeByRoot.ContainsKey(r)).ToList())
                _lowSweepStreak.Remove(stale);

            // --- Pass 3: publish per-set status, and raise the one-shot alert. ---
            // Snapshot of every set's destination state, published to the GUI
            // for the persistent per-row status after the sweep.
            var statuses = new Dictionary<int, DestinationSpaceStatus>();
            var observedAt = DateTimeOffset.Now;

            foreach (var set in sets)
            {
                if (!rootBySet.TryGetValue(set.Id, out string? root))
                    continue;

                long free = freeByRoot[root];
                bool isFull = free < LowSpaceThresholdBytes;

                // Record the live state for the per-row status (all sets, not
                // just continuous ones). Undebounced on purpose: the row is a
                // live readout, not an interruption, so it should track reality
                // sample by sample.
                statuses[set.Id] = new DestinationSpaceStatus(
                    isFull, root.TrimEnd('\\'), FormatBytes(free));

                // The one-shot "drive full" alert is only meaningful for
                // continuous sets: they run under the headless Worker, so a full
                // destination fails silently. Interactive sets surface the same
                // condition through the persistent row status instead.
                bool isContinuous =
                    set.JobOptions?.Schedule is { Enabled: true, Mode: ScheduleMode.Continuous };
                if (!isContinuous)
                    continue;

                if (sustainedLow.Contains(root))
                {
                    // Warn once per fill event.
                    if (_warnedRoots.Add(root))
                        DestinationFull?.Invoke(new DestinationFullAlert(
                            set.Name, root, free, observedAt));
                }
                else if (free >= LowSpaceThresholdBytes + RecoveryMarginBytes)
                {
                    // Recovered comfortably — re-arm so a future fill warns again.
                    _warnedRoots.Remove(root);
                }
            }

            StatusUpdated?.Invoke(statuses);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resolve a set's destination to a drive root (e.g. <c>J:\</c>), or null if
    /// it has no configured/connected destination. Read-only: the resolver may
    /// backfill in-memory metadata, but this never persists it.
    /// </summary>
    private string? ResolveDestinationRoot(JobOptions opts)
    {
        try
        {
            var resolution = _destinationResolver.Resolve(opts);
            if (!resolution.IsConnected || string.IsNullOrWhiteSpace(resolution.LivePath))
                return null;
            return Path.GetPathRoot(resolution.LivePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-sample a drive root and report whether it is <em>still</em> below the
    /// "full" threshold. Subscribers to <see cref="DestinationFull"/> should call
    /// this on the UI thread immediately before displaying the alert, and drop it
    /// if this returns false.
    /// </summary>
    /// <remarks>
    /// The alert is raised on a pool thread and displayed asynchronously, so an
    /// arbitrary amount of time can pass in between — the dispatcher may be busy,
    /// or another modal may already be up, and the "Destination Drive Full" box is
    /// owner-less so it can sit unnoticed behind another window for hours. A drive
    /// that has recovered in the meantime must not be reported as full: that is
    /// precisely the failure the user saw (drive momentarily out of space at 7pm,
    /// dialog read hours later with 38 GB free). Re-checking at display time costs
    /// one <c>GetDiskFreeSpaceEx</c> call.
    /// </remarks>
    public bool IsStillFull(string root)
        => TryGetFreeBytes(root, out long free) && free < LowSpaceThresholdBytes;

    /// <summary>
    /// Free bytes available on a drive root, or false when the drive isn't ready
    /// or can't be queried.
    /// </summary>
    private bool TryGetFreeBytes(string root, out long free)
    {
        free = 0;
        long? probed;
        try
        {
            probed = _freeSpaceProbe(root);
        }
        catch
        {
            return false;
        }

        if (probed is null)
            return false;
        free = probed.Value;
        return true;
    }

    /// <summary>Default <see cref="_freeSpaceProbe"/>: ask the OS.</summary>
    private static long? QueryDriveFreeSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A sustained "destination drive is full" observation for one continuous backup
/// set, published by <see cref="DestinationSpaceMonitor.DestinationFull"/>.
/// </summary>
/// <param name="SetName">Name of the backup set whose destination filled up.</param>
/// <param name="Root">Destination drive root <em>with</em> its trailing slash
/// (e.g. <c>J:\</c>) — pass this back to
/// <see cref="DestinationSpaceMonitor.IsStillFull"/>.</param>
/// <param name="FreeBytes">Free space at the moment the alert was raised.</param>
/// <param name="ObservedAt">When the alert was raised. Carried through to the
/// message text because the dialog can be read long after the fact.</param>
public sealed record DestinationFullAlert(
    string SetName, string Root, long FreeBytes, DateTimeOffset ObservedAt)
{
    /// <summary>Drive root without its trailing slash, e.g. <c>J:</c>.</summary>
    public string DriveLabel => Root.TrimEnd('\\');

    /// <summary>Ready-to-display alert text.</summary>
    /// <remarks>
    /// States the <em>time</em> of the observation rather than an unqualified
    /// "is full", because free space can recover before the user reads the box
    /// and a bare present tense then contradicts what Explorer shows.
    /// </remarks>
    public string Message =>
        $"Backup destination \u201c{SetName}\u201d is on drive {DriveLabel}, which ran out " +
        $"of space at {ObservedAt.ToLocalTime():t} " +
        $"({DestinationSpaceMonitor.FormatBytes(FreeBytes)} free).\n\n" +
        "Continuous backup can\u2019t save new file versions until you free up space.";
}

/// <summary>
/// Live destination free-space status for one backup set, published by
/// <see cref="DestinationSpaceMonitor.StatusUpdated"/>.
/// </summary>
/// <param name="IsFull">True when free space is below the "full" threshold.</param>
/// <param name="Root">Destination drive root without a trailing slash (e.g. <c>J:</c>).</param>
/// <param name="FreeSpaceText">Human-readable free space (e.g. <c>512 MB</c>).</param>
public sealed record DestinationSpaceStatus(bool IsFull, string Root, string FreeSpaceText);
