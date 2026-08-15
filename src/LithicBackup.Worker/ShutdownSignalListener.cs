using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LithicBackup.Worker;

/// <summary>
/// Lets the MSI ask the Worker service to shut itself down BEFORE Windows
/// Installer's file-in-use check, so an upgrade never hits the "setup was unable
/// to automatically close all requested applications" dialog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed at all.</b> The obvious mechanism — the
/// <c>&lt;ServiceControl Stop="both"&gt;</c> in <c>installer\Package.wxs</c> — cannot
/// prevent that dialog, because of where the standard actions sit in the
/// sequence:
/// </para>
/// <code>
///   1399  SignalLithicShutdown   (our custom action)
///   1400  InstallValidate        &lt;- file-in-use check happens HERE
///   1500  InstallInitialize      &lt;- UAC elevation happens HERE
///   1900  StopServices           &lt;- ServiceControl Stop runs HERE, 500 steps too late
/// </code>
/// <para>
/// At <c>InstallValidate</c> the service is still running, still holding
/// <c>LithicBackup.Worker.exe</c> and every self-contained DLL it loaded out of
/// the install folder. Restart Manager sees them, tries to close them, and fails.
/// </para>
/// <para>
/// <b>Why it fails rather than just stopping the service.</b> <c>InstallValidate</c>
/// runs in the unelevated client process for a double-clicked per-machine MSI, at
/// the invoking user's Medium integrity. Stopping a LocalSystem service from
/// there is Access Denied. This is the same integrity wall that the GUI hit (see
/// <c>App.xaml.cs</c>, <c>ShutdownSignalName</c>), and it has the same answer: a
/// process can always shut ITSELF down, whatever its integrity level. So the
/// installer asks, and we comply.
/// </para>
/// <para>
/// <b>Namespace and ACL.</b> The event is created in the <c>Global\</c> namespace
/// because this service runs in session 0 while the installer's custom action runs
/// in the user's interactive session — a session-local name would not be visible
/// to both. Creating a <c>Global\</c> object needs SeCreateGlobalPrivilege, which
/// LocalSystem has. The DACL grants Authenticated Users
/// <see cref="EventWaitHandleRights.Modify"/> so that Medium-integrity custom
/// action can actually Set it; the only capability this confers is "ask the backup
/// service to stop", which is the same exposure the GUI's listener already carries.
/// </para>
/// </remarks>
internal sealed class ShutdownSignalListener : IHostedService, IDisposable
{
    /// <summary>
    /// Must match <c>ShutdownEventName</c> in
    /// <c>installer\CustomActions\CustomAction.cs</c>.
    /// </summary>
    internal const string ShutdownSignalName = @"Global\LithicBackup.Worker.Shutdown";

    /// <summary>
    /// How long a graceful host shutdown gets before the process hard-exits.
    /// </summary>
    /// <remarks>
    /// A backup in flight can keep <see cref="BackupWorker"/> busy well past the
    /// window the installer is willing to wait, and an upgrade that silently turns
    /// into "reboot required" is exactly the outcome this whole mechanism exists to
    /// prevent. Aborting a backup mid-run is safe by design: destination writes
    /// that never got a catalog record are orphans the reconcile pass removes, and
    /// an uncommitted catalog transaction is rolled back by SQLite's WAL. So a
    /// bounded hard exit is strictly better than blocking the upgrade.
    /// </remarks>
    private static readonly TimeSpan ForcedExitGrace = TimeSpan.FromSeconds(20);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ShutdownSignalListener> _logger;

    private EventWaitHandle? _signal;
    private RegisteredWaitHandle? _registration;

    public ShutdownSignalListener(
        IHostApplicationLifetime lifetime,
        ILogger<ShutdownSignalListener> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));

            _signal = EventWaitHandleAcl.Create(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: ShutdownSignalName,
                createdNew: out _,
                eventSecurity: security);

            _registration = ThreadPool.RegisterWaitForSingleObject(
                _signal,
                (_, _) => OnShutdownRequested(),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);

            _logger.LogInformation(
                "Installer shutdown listener registered on \"{Event}\".", ShutdownSignalName);
        }
        catch (Exception ex)
        {
            // Losing the listener only degrades upgrade UX (the installer falls
            // back to Windows Installer's own file-in-use handling). It must never
            // stop the backup service from starting.
            _logger.LogWarning(ex,
                "Could not register the installer shutdown listener; upgrades may report files in use.");
        }

        return Task.CompletedTask;
    }

    private void OnShutdownRequested()
    {
        _logger.LogInformation(
            "Installer requested shutdown — stopping the Worker so the upgrade can replace its files.");

        // Arm the hard-exit watchdog BEFORE asking for a graceful stop, so a
        // backup that refuses to wind down inside the grace window cannot leave
        // the installer waiting. Background + IsBackground so this thread never
        // itself keeps the process alive.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(ForcedExitGrace);
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "LithicBackup.Worker shutdown watchdog",
        };
        watchdog.Start();

        _lifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _registration?.Unregister(null);
        _signal?.Dispose();
    }
}
