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
    /// Free-space floor below which a destination drive is treated as "full".
    /// 1 GB is a practical cut-off: below it, continuous backups can no longer
    /// reliably write new file versions.
    /// </summary>
    public long LowSpaceThresholdBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hysteresis margin. A drive we've already warned about is only re-armed
    /// (eligible to warn again) once its free space recovers to
    /// <see cref="LowSpaceThresholdBytes"/> + this margin, so a drive hovering
    /// right at the threshold doesn't emit a balloon on every poll.
    /// </summary>
    private const long RecoveryMarginBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Raised (on a thread-pool thread) when a continuous set's destination drive
    /// is full. The argument is a ready-to-display message. Subscribers must
    /// marshal to the UI thread themselves.
    /// </summary>
    public event Action<string>? DestinationFull;

    public DestinationSpaceMonitor(ICatalogRepository catalog, IDestinationResolver destinationResolver)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _destinationResolver = destinationResolver ?? throw new ArgumentNullException(nameof(destinationResolver));
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

            foreach (var set in sets)
            {
                var opts = set.JobOptions;

                // Only sets that have continuous backup switched on are relevant.
                if (opts?.Schedule is not { Enabled: true, Mode: ScheduleMode.Continuous })
                    continue;

                string? root = ResolveDestinationRoot(opts);
                if (root is null)
                    continue; // no configured / connected destination

                long free;
                try
                {
                    var drive = new DriveInfo(root);
                    if (!drive.IsReady)
                        continue;
                    free = drive.AvailableFreeSpace;
                }
                catch
                {
                    continue; // drive query failed — skip this one
                }

                if (free < LowSpaceThresholdBytes)
                {
                    // Warn once per fill event.
                    if (_warnedRoots.Add(root))
                        DestinationFull?.Invoke(
                            $"Backup destination \u201c{set.Name}\u201d is on drive " +
                            $"{root.TrimEnd('\\')} which is full ({FormatBytes(free)} free).\n\n" +
                            "Continuous backup can\u2019t save new file versions until you free up space.");
                }
                else if (free >= LowSpaceThresholdBytes + RecoveryMarginBytes)
                {
                    // Recovered comfortably — re-arm so a future fill warns again.
                    _warnedRoots.Remove(root);
                }
            }
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

    private static string FormatBytes(long bytes)
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
