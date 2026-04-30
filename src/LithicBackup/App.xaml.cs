using System.IO;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- Composition root: wire up services ---

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LithicBackup");
        Directory.CreateDirectory(appDataDir);

        var catalogPath = Path.Combine(appDataDir, "catalog.db");
        _catalog = new SqliteCatalogRepository(catalogPath);
        var burner = new Imapi2DiscBurner();
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

        // Directory backup service.
        var directoryBackupService = new DirectoryBackupService(
            _catalog, scanner, retentionService, deduplicationEngine);

        var mainViewModel = new MainViewModel(
            _catalog, burner, scanner, orchestrator, restoreService, directoryBackupService, _trayService);
        var mainWindow = new MainWindow { DataContext = mainViewModel };

        // --- System tray icon ---
        SetupNotifyIcon(mainWindow);

        // Wire up tray service to show balloon tips when changes accumulate.
        _trayService.BackupSuggested += reason =>
        {
            Current.Dispatcher.Invoke(() =>
            {
                _notifyIcon?.ShowBalloonTip(
                    5000,
                    "LithicBackup \u2014 Backup Suggested",
                    reason,
                    WinForms.ToolTipIcon.Info);
            });
        };

        mainWindow.Show();

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
        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => Shutdown());
        _notifyIcon.ContextMenuStrip = contextMenu;

        // Double-click tray icon to restore the window.
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        // Minimize to tray instead of taskbar — but only when idle on the
        // home screen.  If the user is mid-flow (creating a backup set,
        // burning, restoring, etc.) just minimize normally so they don't
        // lose sight of the window.
        mainWindow.StateChanged += (_, _) =>
        {
            if (mainWindow.WindowState == WindowState.Minimized
                && (mainWindow.DataContext as MainViewModel)?.CurrentView is null)
            {
                mainWindow.Hide();
                _notifyIcon.ShowBalloonTip(
                    1500,
                    "LithicBackup",
                    "Minimized to tray. Background monitoring continues.",
                    WinForms.ToolTipIcon.Info);
            }
        };

        // Clicking the balloon tip also restores the window.
        _notifyIcon.BalloonTipClicked += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null) return;

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
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
        base.OnExit(e);
    }
}
