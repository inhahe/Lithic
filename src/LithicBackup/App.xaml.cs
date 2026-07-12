using System.IO;
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

    /// <summary>
    /// Set when the user chooses Exit from the tray menu.
    /// Allows <see cref="MainWindow.OnClosing"/> to distinguish
    /// a real shutdown from the X button (which minimizes to tray).
    /// </summary>
    internal bool IsExiting { get; private set; }

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
        _catalog?.Dispose();

        // Release single-instance primitives.
        _showInstanceWait?.Unregister(null);
        _showInstanceEvent?.Dispose();
        if (_ownsSingleInstance)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
