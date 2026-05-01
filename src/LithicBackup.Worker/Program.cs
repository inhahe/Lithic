using LithicBackup.Core.Interfaces;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.Deduplication;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using LithicBackup.Worker;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "LithicBackup";
    })
    .ConfigureServices(services =>
    {
        // Catalog database — same path the GUI uses.
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LithicBackup");
        Directory.CreateDirectory(appDataDir);
        var catalogPath = Path.Combine(appDataDir, "catalog.db");

        services.AddSingleton<ICatalogRepository>(
            _ => new SqliteCatalogRepository(catalogPath));

        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<IDeduplicationEngine, BlockDeduplicationEngine>();
        services.AddSingleton<VersionRetentionService>();
        services.AddSingleton<DirectoryBackupService>();

        // File-system monitor for continuous-mode change detection.
        services.AddSingleton<IFileSystemMonitor, FileSystemMonitorImpl>();

        services.AddHostedService<BackupWorker>();
    });

var host = builder.Build();
host.Run();
