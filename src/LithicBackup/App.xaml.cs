using System.IO;
using System.Threading;
using System.Windows;
using LithicBackup.Core.Interfaces;
using LithicBackup.Infrastructure.Burning;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.Deduplication;
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

        // --simulate-burner: use a mock disc burner for testing without hardware.
        var args = Environment.GetCommandLineArgs();
        bool simulateBurner = args.Any(a => a.Equals("--simulate-burner", StringComparison.OrdinalIgnoreCase));
        IDiscBurner burner = simulateBurner
            ? new SimulatedDiscBurner()
            : new Imapi2DiscBurner();
        var scanner = new FileScanner(_catalog);
        var packer = new BinPacker();
        var zipHandler = new ZipHandler();
        var fileSplitter = new FileSplitter();
        var sessionStrategy = new DiscSessionStrategy(burner, _catalog);

        // Block-level deduplication engine.
        IDeduplicationEngine deduplicationEngine = new BlockDeduplicationEngine(_catalog);

        // Filesystem monitor — shared between TrayService (background monitoring)
        // and BackupOrchestrator (live change detection during burns).
        var fileSystemMonitor = new FileSystemMonitorImpl();

        // A second monitor instance for the orchestrator's LiveBurnCoordinator,
        // since each IFileSystemMonitor tracks its own set of watched directories.
        var burnMonitor = new FileSystemMonitorImpl();

        var orchestrator = new BackupOrchestrator(
            _catalog, burner, scanner, packer,
            zipHandler, fileSplitter, sessionStrategy,
            deduplicationEngine,
            fileSystemMonitor: burnMonitor);

        // Restore service.
        var restoreService = new RestoreService(_catalog);

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

        var mainViewModel = new MainViewModel(
            _catalog, burner, scanner, orchestrator, restoreService, directoryBackupService, _trayService,
            fileHashCache);
        var mainWindow = new MainWindow { DataContext = mainViewModel };

        // --- User settings & system tray icon ---
        _settings = UserSettings.Load();
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
                    "LithicBackup \u2014 Backup Suggested",
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

        // Start background monitoring if there are existing backup sets.
        _ = StartBackgroundMonitoringAsync();
    }

    private void SetupNotifyIcon(Window mainWindow)
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "LithicBackup \u2014 Incremental Backup",
            Visible = true,
        };

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("Open LithicBackup", null, (_, _) => ShowMainWindow());

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
    /// Hide the main window to the system tray and show a notification.
    /// Called by the title bar's tray button.
    /// </summary>
    internal void MinimizeToTray()
    {
        MainWindow?.Hide();
        _notifyIcon?.ShowBalloonTip(
            1500,
            "LithicBackup",
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
