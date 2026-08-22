using System.IO.Pipes;
using System.Text.Json;
using Vaultix.Shared;

namespace Vaultix.Service;

public sealed class NamedPipeStatusServer(BackupCoordinator coordinator, ILogger<NamedPipeStatusServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = VaultixPipeServer.CreateStatusPipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                do
                {
                    var status = await coordinator.GetStatusAsync(stoppingToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(status, VaultixProtocol.Json)).ConfigureAwait(false);
                }
                while (pipe.IsConnected && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Status client disconnected");
            }
        }
    }
}
