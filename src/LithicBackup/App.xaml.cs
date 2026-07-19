using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using LithicBackup.Core.Interfaces;
using LithicBackup.Infrastructure.Burning;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.Deduplication;
using LithicBackup.Infrastructure.Diagnostics;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using LithicBackup.ViewModels;
using WinForms = System.Windows.Forms;

namespace LithicBackup;

public partial class App : Application
{
    private SqliteCatalogRepository? _catalog;
    private TrayService? _trayService;
    private DestinationSpaceMonitor? _destinationSpaceMonitor;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ToolStripMenuItem? _remindersItem;
    private UserSettings _settings = new();

    // --- Single-instance enforcement ---
    /// <summary>Shared name for the per-session single-instance primitives.</summary>
    private const string SingleInstanceName = "LithicBackup.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showInstanceEvent;
    private RegisteredWaitHandle? _showInstanceWait;
    private bool _ownsSingleInstance;

    // --- Installer shutdown signal (IPC) ---
    // The upgrade installer needs the running GUI gone before its file-in-use
    // check (InstallValidate), or the upgrade fails with "The setup was unable to
    // automatically close all requested applications." Rather than have the
    // installer TERMINATE us (which fails when the GUI runs elevated: a
    // Medium-integrity installer custom action cannot kill a High-integrity
    // process), the installer just SIGNALS this named event and we close
    // ourselves gracefully. A process can always shut itself down regardless of
    // integrity level, so no elevation, taskkill, or Burn-bundle is needed — see
    // installer\Package.wxs (SignalLithicGuiShutdown) and the MSI-upgrade entry in
    // known-issues.md. The event is session-local (matching the single-instance
    // primitives): the installer's pre-InstallValidate custom action runs in the
    // user's own msiexec client process, i.e. the same session as the GUI.
    private const string ShutdownSignalName = "LithicBackup.Shutdown";
    private EventWaitHandle? _shutdownSignalEvent;
    private RegisteredWaitHandle? _shutdownSignalWait;

    // --- Forced-shutdown watchdog ---
    // A graceful Application.Shutdown() can be blocked indefinitely by ANY open
    // modal — the owner-less "Destination Drive Full" MessageBox (which can hide
    // behind other windows or sit on a second monitor), a crash-handler dialog, an
    // About/Settings/editor dialog — or by a wedged/busy dispatcher. That keeps
    // LithicBackup.exe locked past the installer's bounded wait and resurrects the
    // "The setup was unable to automatically close all requested applications"
    // upgrade failure. When the upgrade signals us to close we still try a clean
    // shutdown first, but also arm this watchdog on a plain background thread: if
    // the process is still alive after a short grace period, hard-exit so the .exe
    // is released. A process can always terminate ITSELF regardless of integrity
    // level, so this needs no elevation. Only armed for a forced shutdown (installer
    // signal / session end), never for a user File > Exit, so it can never cut short
    // a normal quit or its unsaved-changes prompt.
    private const double ForcedShutdownGraceSeconds = 5.0;
    private int _forcedShutdownWatchdogArmed;

    /// <summary>
    /// Set when the user chooses Exit from the tray menu.
    /// Allows <see cref="MainWindow.OnClosing"/> to distinguish
    /// a real shutdown from the X button (which minimizes to tray).
    /// </summary>
    internal bool IsExiting { get; private set; }

    /// <summary>
    /// Set when the shutdown is driven by the upgrade installer's Restart-Manager
    /// signal or a Windows session end (log off / shutdown), as opposed to a
    /// user-initiated File &gt; Exit / tray Exit. During such a forced shutdown no
    /// window may show a blocking modal prompt: the upgrade installer waits only a
    /// bounded time for LithicBackup.exe to exit, so a modal (e.g. the backup-set
    /// editor's "save unsaved changes?" dialog) would stall <see cref="Application.Shutdown()"/>,
    /// keep the .exe locked, and make the upgrade fail with "unable to close all
    /// requested applications." Windows still lets a real File &gt; Exit prompt the
    /// user, since that path leaves this flag false.
    /// </summary>
    internal bool IsForcedShutdown { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- Crash diagnostics ---
        // Install process-global exception capture as the very first thing, so
        // even a failure during startup is recorded. Writes full stack traces +
        // environment context to C:\ProgramData\LithicBackup\logs.
        CrashLogger.Initialize("gui");

        // Best-effort: register Windows Error Reporting local dumps so native
        // crashes (access violations, corrupted-state faults, COM/interop
        // crashes) that the managed handlers below can't catch still leave a
        // .dmp under C:\ProgramData\LithicBackup\logs\dumps. Writing the keys
        // needs admin, so this typically no-ops for the unelevated GUI and is
        // instead performed by the LocalSystem service; it's attempted here too
        // in case the GUI is ever run elevated.
        NativeCrashDumps.TryEnableLocalDumps();

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogger.LogFatal(args.Exception, "Dispatcher.UnhandledException");
            try
            {
                MessageBox.Show(
                    "Lithic Backup hit an unexpected error and may be in an unstable state.\n\n" +
                    "A crash report has been written to:\n" +
                    CatalogLocation.LogsDirectory() + "\n\n" +
                    args.Exception.Message,
                    "Lithic Backup \u2014 Unexpected Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Never let the error dialog throw.
            }
            // Leave args.Handled at its default (false): WPF's default policy
            // still applies, but the crash is now durably recorded.
        };

        try
        {
            await StartupCoreAsync();
        }
        catch (Exception ex)
        {
            CrashLogger.LogFatal(ex, "OnStartup initialization failed");
            try
            {
                MessageBox.Show(
                    "Lithic Backup failed to start.\n\n" +
                    "A crash report has been written to:\n" +
                    CatalogLocation.LogsDirectory() + "\n\n" +
                    ex.Message,
                    "Lithic Backup \u2014 Startup Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }
            Shutdown();
        }
    }

    /// <summary>
    /// Core startup logic (composition root + window creation), wrapped by
    /// <see cref="OnStartup"/> in crash diagnostics so an initialization failure
    /// is logged rather than silently terminating the process.
    /// </summary>
    private async Task StartupCoreAsync()
    {
        // --- Single-instance enforcement ---
        // Only one LithicBackup UI may run per user session.  Acquire a named
        // mutex; if it's already held, we're a second launch — signal the
        // running instance to surface its window, then exit immediately.
        // The names are unprefixed, so they live in the session-local
        // namespace (one instance per logged-in session, no cross-session
        // permission concerns).
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out _ownsSingleInstance);
        _showInstanceEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, SingleInstanceName + ".ShowWindow");

        if (!_ownsSingleInstance)
        {
            // Another instance owns the mutex — wake it and bow out before
            // any windows or services are created.
            _showInstanceEvent.Set();
            Shutdown();
            return;
        }

        // We're the primary instance.  Listen for future launches asking us
        // to bring the window forward.  The callback marshals onto the UI
        // dispatcher; ShowMainWindow tolerates a not-yet-created window.
        _showInstanceWait = ThreadPool.RegisterWaitForSingleObject(
            _showInstanceEvent,
            (_, _) => Current?.Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        // Listen for the upgrade installer's "please close" signal (see the
        // ShutdownSignalName field comment). Grant Authenticated Users the right to
        // signal + wait on the event so the installer's custom action — which runs
        // as the invoking user, at Medium integrity — can Set it even when this GUI
        // is running elevated (High). Without an explicit label the event is Medium
        // integrity, so a Medium signaller is not blocked by no-write-up. When set,
        // we perform the same graceful shutdown as a Restart Manager close, which
        // releases LithicBackup.exe so the upgrade can replace it.
        try
        {
            var shutdownSecurity = new EventWaitHandleSecurity();
            shutdownSecurity.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));
            _shutdownSignalEvent = EventWaitHandleAcl.Create(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: ShutdownSignalName,
                createdNew: out _,
                eventSecurity: shutdownSecurity);
            _shutdownSignalWait = ThreadPool.RegisterWaitForSingleObject(
                _shutdownSignalEvent,
                (_, _) =>
                {
                    // Runs on a thread-pool thread, so it fires even if the UI
                    // dispatcher is wedged. Arm the hard-exit watchdog FIRST, then
                    // request the graceful shutdown — the watchdog only fires if that
                    // graceful path hasn't ended the process within the grace window.
                    ArmForcedShutdownWatchdog();
                    Current?.Dispatcher.BeginInvoke(new Action(ShutdownForRestartManager));
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            // A missing shutdown listener only degrades upgrade UX (the installer
            // falls back to its wait/RestartManager handling); it must never stop
            // the app from starting.
            CrashLogger.Log(ex, "Failed to register installer shutdown signal listener");
        }

        // Show splash screen immediately while services initialize.
        var splash = new SplashWindow();
        splash.Show();

        // Yield so the splash window renders before heavy initialization begins.
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);

        // --- Composition root: wire up services ---

        // Shared, account-independent catalog path (ProgramData) so the GUI and
        // the Windows Service (LocalSystem) open the SAME database. See
        // CatalogLocation for the rationale.
        var catalogPath = CatalogLocation.Resolve();
        _catalog = new SqliteCatalogRepository(catalogPath);

        // Volume-identity + destination resolution: lets backup sets follow
        // their destination drive across Windows drive-letter reassignments.
        IVolumeResolver volumeResolver = new Win32VolumeResolver();
        IDestinationResolver destinationResolver = new DestinationResolver(volumeResolver);
        ISourceResolver sourceResolver = new SourceResolver(volumeResolver);

        // --test-mode: enable the testing features (simulated burner + the
        // non-functional directory stub mode). The features are not engaged just
        // by passing the flag — it reveals opt-in checkboxes in the UI. The
        // burner is wrapped so that, even in test mode, real hardware is used
        // until the user ticks "use simulated burner".
        var args = Environment.GetCommandLineArgs();
        bool testMode = args.Any(a => a.Equals("--test-mode", StringComparison.OrdinalIgnoreCase));
        IDiscBurner burner = testMode
            ? new SwitchableDiscBurner(new Imapi2DiscBurner(), new SimulatedDiscBurner())
            : new Imapi2DiscBurner();
        var scanner = new FileScanner(_catalog);
        var packer = new BinPacker();
        var zipHandler = new ZipHandler();
        var sessionStrategy = new DiscSessionStrategy(burner, _catalog);

        // Block-level deduplication engine. Deduplicates directly against the
        // destination's content-addressed _blocks store (no catalog index).
        IDeduplicationEngine deduplicationEngine = new BlockDeduplicationEngine();

        // Filesystem monitor — shared between TrayService (background monitoring)
        // and BackupOrchestrator (live change detection during burns).
        var fileSystemMonitor = new FileSystemMonitorImpl();

        // A second monitor instance for the orchestrator's LiveBurnCoordinator,
        // since each IFileSystemMonitor tracks its own set of watched directories.
        var burnMonitor = new FileSystemMonitorImpl();

        var orchestrator = new BackupOrchestrator(
            _catalog, burner, scanner, packer,
            zipHandler, sessionStrategy,
            fileSystemMonitor: burnMonitor);

        // Restore service.
        var restoreService = new RestoreService(_catalog);

        // Catalog-free (disaster-recovery) restore service — reconstructs files
        // from a backup destination tree alone, no catalog required.
        var catalogFreeRestoreService = new CatalogFreeRestoreService();

        // Tray/background monitoring service.
        _trayService = new TrayService(fileSystemMonitor, _catalog);

        // Version retention service (available for use in consolidation workflows).
        var retentionService = new VersionRetentionService(_catalog);

        // Shared file-hash cache — dedup analysis pre-computes hashes that
        // the backup service reuses to avoid redundant I/O.
        var fileHashCache = new FileHashCache();

        // Directory backup service.
        var directoryBackupService = new DirectoryBackupService(
            _catalog, scanner, retentionService, deduplicationEngine, fileHashCache);

        // --- User settings (load before building the view model so the
        // memory budget and other preferences flow into backup jobs) ---
        _settings = UserSettings.Load();

        var mainViewModel = new MainViewModel(
            _catalog, burner, scanner, orchestrator, restoreService, catalogFreeRestoreService,
            directoryBackupService, _trayService,
            fileHashCache, destinationResolver, _settings, sourceResolver);
        var mainWindow = new MainWindow { DataContext = mainViewModel };

        // --- System tray icon ---
        SetupNotifyIcon(mainWindow);

        // Wire up tray service to show balloon tips when changes accumulate.
        _trayService.BackupSuggested += reason =>
        {
            if (_settings.SuppressBackupSuggestions)
                return;

            Current.Dispatcher.Invoke(() =>
            {
                _notifyIcon?.ShowBalloonTip(
                    5000,
                    "Lithic Backup \u2014 Backup Suggested",
                    reason + "\n\nClick to stop future reminders. You can re-enable them from the tray icon\u2019s right-click menu.",
                    WinForms.ToolTipIcon.Info);
            });
        };

        // Warn if a continuous set's destination drive is full. The Worker that
        // runs continuous backups is headless, so a full destination would
        // otherwise fail silently — new versions just never get written. This
        // GUI-side monitor polls destination free space and raises DestinationFull
        // the moment a continuous destination can no longer accept writes.
        _destinationSpaceMonitor = new DestinationSpaceMonitor(_catalog, destinationResolver);
        _destinationSpaceMonitor.DestinationFull += message =>
        {
            // A tray balloon self-dismisses after a few seconds, so an
            // away-from-keyboard user would miss it. Use a modal dialog that stays
            // up until acknowledged. BeginInvoke (not Invoke) so the monitor's poll
            // thread isn't blocked while the dialog is open — that lets a second
            // drive filling up still be detected and reported. The monitor's own
            // per-drive hysteresis guarantees this fires only once per fill event
            // (re-arming only after the drive recovers), so the dialog won't
            // reappear unless space is freed and that drive fills again, or a
            // different continuous destination fills.
            Current.Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(
                    message,
                    "Lithic Backup \u2014 Destination Drive Full",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
        };

        // Persistent per-row "destination full" status. The same sweep that
        // drives the continuous-set balloon also reports the live full/not-full
        // state of every set's destination; push it onto the home-screen rows so
        // a full destination is visible at a glance (and clears when space is
        // freed). Marshalled to the UI thread — the event fires on a pool thread.
        _destinationSpaceMonitor.StatusUpdated += statuses =>
            Current.Dispatcher.BeginInvoke(() =>
                mainViewModel.ApplyDestinationSpaceStatus(statuses));

        // Swap splash for main window.
        // Explicitly set MainWindow so MinimizeToTray and other
        // callers of Application.MainWindow reference the real window
        // (WPF auto-assigns it to the first Window instantiated, which
        // was the splash).
        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();

        // Closing the splash doesn't reliably transfer foreground activation to
        // the newly shown main window, so it can come up behind other windows.
        // Force it to the front with the same activation dance (Activate + brief
        // Topmost toggle) the tray "Open" path uses.
        ShowMainWindow();

        // Start background monitoring if there are existing backup sets.
        _ = StartBackgroundMonitoringAsync();

        // Start watching destination drives for a full disk (fires an initial
        // sweep immediately, then on a short cadence). The sweep is cheap — a
        // local catalog read plus one free-space query per connected
        // destination — so a 30-second interval keeps the per-row "destination
        // full" status responsive without meaningful cost.
        _destinationSpaceMonitor.Start(TimeSpan.FromSeconds(30));

        // Quietly check GitHub for a newer release (opt-out via settings). Runs
        // in the background so it never delays showing the window; surfaces an
        // in-window banner only if a new, non-dismissed version is available.
        if (_settings.CheckForUpdates)
            _ = mainViewModel.CheckForUpdatesAsync(userInitiated: false);
    }

    /// <summary>
    /// Load the app's monolith icon (packed <c>Assets\LithicBackup.ico</c>) at
    /// the system small-icon size for the tray, falling back to the generic
    /// application icon if the resource can't be read.
    /// </summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/LithicBackup.ico");
            if (System.Windows.Application.GetResourceStream(uri)?.Stream is { } stream)
            {
                using (stream)
                    return new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // Fall through to the system default below.
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void SetupNotifyIcon(Window mainWindow)
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Lithic Backup \u2014 Incremental Backup",
            Visible = true,
        };

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("Open Lithic Backup", null, (_, _) => ShowMainWindow());

        var remindersItem = new WinForms.ToolStripMenuItem("Backup reminders")
        {
            Checked = !_settings.SuppressBackupSuggestions,
            CheckOnClick = true,
        };
        remindersItem.CheckedChanged += (_, _) =>
        {
            _settings.SuppressBackupSuggestions = !remindersItem.Checked;
            _settings.Save();
        };
        contextMenu.Items.Add(remindersItem);
        _remindersItem = remindersItem;

        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;

        // Double-click tray icon to restore the window.
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        // Clicking the balloon tip suppresses future reminders immediately.
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            _settings.SuppressBackupSuggestions = true;
            _settings.Save();
            remindersItem.Checked = false;
        };
    }

    /// <summary>
    /// Perform a real application shutdown (File &gt; Exit or tray Exit).
    /// Sets <see cref="IsExiting"/> so <see cref="MainWindow.OnClosing"/>
    /// allows the window to close instead of minimizing to tray.
    /// </summary>
    internal void ExitApplication()
    {
        IsExiting = true;
        Shutdown();
    }

    /// <summary>
    /// A session-end request — Windows logging off/shutting down, OR an installer's
    /// Restart Manager asking us to close so it can replace our files (this is what
    /// the MSI upgrade sends). Treat it as a real exit: without this, the main
    /// window's OnClosing would cancel the close and minimize to tray, leaving the
    /// process alive and holding LithicBackup.exe locked — which is what makes an
    /// upgrade fail with "Setup was unable to automatically close the application."
    /// Flag the real shutdown so OnClosing lets the window close.
    ///
    /// Note: WPF only raises this for session messages delivered to its own hidden
    /// management window. The MSI's util:CloseApplication posts WM_QUERYENDSESSION/
    /// WM_ENDSESSION straight to the visible main window's HWND, which does NOT
    /// route through here — <see cref="MainWindow"/> hooks its WndProc and calls
    /// <see cref="ShutdownForRestartManager"/> to cover that path.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        IsForcedShutdown = true;
        ArmForcedShutdownWatchdog();
        Shutdown();
        base.OnSessionEnding(e);
    }

    /// <summary>
    /// Approve a pending session-end/Restart-Manager close (WM_QUERYENDSESSION):
    /// flag the real exit so a subsequent close is allowed to proceed instead of
    /// minimizing to tray. Does not shut down yet — the session end could still be
    /// vetoed, in which case <see cref="AbortRestartManagerExit"/> undoes this.
    /// </summary>
    internal void MarkRestartManagerExit() => IsExiting = true;

    /// <summary>
    /// The session end was cancelled (WM_ENDSESSION with wParam = FALSE): keep the
    /// app running in the tray by clearing the pending-exit flag.
    /// </summary>
    internal void AbortRestartManagerExit() => IsExiting = false;

    /// <summary>
    /// Actually tear the process down in response to a Restart Manager / session
    /// end (WM_ENDSESSION with wParam = TRUE). Because <c>ShutdownMode</c> is
    /// <see cref="ShutdownMode.OnExplicitShutdown"/>, closing the window is not
    /// enough to end the process — we must call <see cref="Application.Shutdown()"/>
    /// so LithicBackup.exe is released and the installer can replace it.
    /// </summary>
    internal void ShutdownForRestartManager()
    {
        IsExiting = true;
        IsForcedShutdown = true;
        ArmForcedShutdownWatchdog();
        Shutdown();
    }

    /// <summary>
    /// Guarantee the process (and its lock on LithicBackup.exe) is released for an
    /// upgrade even if the graceful <see cref="Application.Shutdown()"/> is blocked
    /// by an open modal or a busy/wedged dispatcher. Starts a one-shot background
    /// thread that hard-exits after <see cref="ForcedShutdownGraceSeconds"/> if the
    /// process is still alive; a clean shutdown that completes first makes it moot
    /// (the process is already gone before the timer elapses). Idempotent — safe to
    /// call from every forced-shutdown entry point. See the field-level comment on
    /// <see cref="_forcedShutdownWatchdogArmed"/> for why this needs no elevation.
    /// </summary>
    private void ArmForcedShutdownWatchdog()
    {
        if (Interlocked.Exchange(ref _forcedShutdownWatchdogArmed, 1) != 0)
            return; // already armed by an earlier forced-shutdown entry point

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(ForcedShutdownGraceSeconds));
            // If the graceful shutdown already ran, the process is gone and this
            // line never executes. Otherwise force the .exe free for the upgrade.
            CrashLogger.Log(null,
                "Forced-shutdown watchdog: graceful shutdown did not complete within " +
                ForcedShutdownGraceSeconds + "s (likely blocked by a modal); hard-exiting to release the executable for the upgrade.");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "ForcedShutdownWatchdog",
        };
        watchdog.Start();
    }

    /// <summary>
    /// Open the application settings dialog (memory budget, reminders).
    /// Keeps the tray reminders checkbox in sync with any change made here.
    /// </summary>
    internal void OpenSettings(Window owner)
    {
        var dialog = new Views.SettingsDialog(_settings) { Owner = owner };
        if (dialog.ShowDialog() == true && _remindersItem is not null)
            _remindersItem.Checked = !_settings.SuppressBackupSuggestions;
    }

    /// <summary>
    /// Hide the main window to the system tray and show a notification.
    /// Called by the title bar's tray button.
    /// </summary>
    internal void MinimizeToTray()
    {
        MainWindow?.Hide();
        _notifyIcon?.ShowBalloonTip(
            1500,
            "Lithic Backup",
            "Minimized to tray. Background monitoring continues.",
            WinForms.ToolTipIcon.Info);
    }

    private void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null) return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();

        // Force the window to the foreground even when the activation request
        // comes from another process (a second launch).  Windows suppresses
        // SetForegroundWindow for non-foreground callers, so briefly toggling
        // Topmost reliably raises the window above the requesting process.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private async Task StartBackgroundMonitoringAsync()
    {
        if (_catalog is null || _trayService is null)
            return;

        try
        {
            var sets = await _catalog.GetAllBackupSetsAsync();
            if (sets.Count > 0)
            {
                var directories = sets
                    .SelectMany(s => s.SourceRoots)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(Directory.Exists)
                    .ToList();

                if (directories.Count > 0)
                {
                    _trayService.Start(directories, TimeSpan.FromMinutes(5));
                }
            }
        }
        catch
        {
            // Non-fatal — monitoring is optional.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _trayService?.Dispose();
        _destinationSpaceMonitor?.Dispose();
        _catalog?.Dispose();

        // Release the installer shutdown-signal listener.
        _shutdownSignalWait?.Unregister(null);
        _shutdownSignalEvent?.Dispose();

        // Release single-instance primitives.
        _showInstanceWait?.Unregister(null);
        _showInstanceEvent?.Dispose();
        if (_ownsSingleInstance)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
