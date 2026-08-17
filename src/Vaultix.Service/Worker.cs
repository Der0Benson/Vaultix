namespace Vaultix.Service;

public sealed class Worker(BackupCoordinator coordinator) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => coordinator.RunLoopAsync(stoppingToken);
}
