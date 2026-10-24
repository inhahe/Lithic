using LithicBackup.Infrastructure.FileSystem;

namespace LithicBackup.Worker;

/// <summary>
/// Flushes a volume on request from the app, which usually may not: a backup
/// started there makes what it copied durable before recording it, and flushing
/// the destination's volume once per commit needs an elevated process, which this
/// service (LocalSystem) is. See <see cref="VolumeFlushPipe"/>.
/// </summary>
internal sealed class VolumeFlushServer(ILogger<VolumeFlushServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Volume flush requests served on \\\\.\\pipe\\{Pipe}.", VolumeFlushPipe.Name);
        try
        {
            await VolumeFlushPipe.ServeAsync(stoppingToken,
                ex => logger.LogWarning(ex, "A volume flush request failed."));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }
}
