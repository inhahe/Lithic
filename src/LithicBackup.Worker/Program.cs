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
        // Catalog database — shared, account-independent path (ProgramData) so
        // the service (LocalSystem) and the GUI (interactive user) open the SAME
        // database. See CatalogLocation for the rationale.
        var catalogPath = CatalogLocation.Resolve();

        services.AddSingleton<ICatalogRepository>(
            _ => new SqliteCatalogRepository(catalogPath));

        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<IDeduplicationEngine, BlockDeduplicationEngine>();
        services.AddSingleton<VersionRetentionService>();
        services.AddSingleton<DirectoryBackupService>();

        services.AddHostedService<BackupWorker>();
    });

var host = builder.Build();
host.Run();
