using LithicBackup.Core.Interfaces;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.Deduplication;
using LithicBackup.Infrastructure.Diagnostics;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;
using LithicBackup.Worker;

// Install process-global crash capture before anything else, so a failure
// during host build/startup is recorded to C:\ProgramData\LithicBackup\logs.
CrashLogger.Initialize("worker");

// Register Windows Error Reporting local dumps for BOTH executables. The
// service runs as LocalSystem, so unlike the (unelevated) GUI it can write the
// HKLM keys — a single successful registration here covers native crashes of
// the GUI too. Catches the access-violation / corrupted-state faults that the
// managed CrashLogger cannot.
NativeCrashDumps.TryEnableLocalDumps();

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "Lithic Backup";
    })
    .ConfigureLogging(logging =>
    {
        // Mirror ILogger output to a rolling daily file under the shared logs
        // directory, alongside the default EventLog/console providers.
        logging.AddProvider(new FileLoggerProvider("worker"));
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
        services.AddSingleton<IVolumeResolver, Win32VolumeResolver>();
        services.AddSingleton<IDestinationResolver, DestinationResolver>();
        services.AddSingleton<ISourceResolver, SourceResolver>();
        services.AddSingleton<IDeduplicationEngine, BlockDeduplicationEngine>();
        services.AddSingleton<VersionRetentionService>();
        services.AddSingleton<DirectoryBackupService>();

        services.AddHostedService<BackupWorker>();
    });

try
{
    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    // Host build/run failures (e.g. DI or startup faults) are otherwise lost
    // when running as a service — record them durably.
    CrashLogger.LogFatal(ex, "Host build/run failed");
    throw;
}
