using System.IO.Pipes;
using System.Text.Json;
using Vaultix.Shared;

namespace Vaultix.Service;

public sealed class NamedPipeIpcServer(BackupCoordinator coordinator, ILogger<NamedPipeIpcServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = VaultixPipeServer.CreateCommandPipe();
            await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            await HandleConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        IpcResponse response;
        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var request = line is null ? null : JsonSerializer.Deserialize<IpcRequest>(line, VaultixProtocol.Json);
            response = request is null ? IpcResponse.Fail("Ungültige IPC-Anfrage.") : await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "IPC command failed");
            response = IpcResponse.Fail(exception.Message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, VaultixProtocol.Json)).ConfigureAwait(false);
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "GetStatus":
                return IpcResponse.Ok(await coordinator.GetStatusAsync(cancellationToken).ConfigureAwait(false));
            case "ConfigureServer":
                await coordinator.ConfigureServerAsync(GetPayload<ConfigureServerCommand>(request), cancellationToken).ConfigureAwait(false);
                return IpcResponse.Ok();
            case "AddFolder":
                await coordinator.AddFolderAsync(GetPayload<AddFolderCommand>(request).Path, cancellationToken).ConfigureAwait(false);
                return IpcResponse.Ok();
            case "RemoveFolder":
                await coordinator.RemoveFolderAsync(GetPayload<RemoveFolderCommand>(request).Id, cancellationToken).ConfigureAwait(false);
                return IpcResponse.Ok();
            case "RunBackup":
                coordinator.RequestBackup();
                return IpcResponse.Ok();
            case "UpdateProtectionSettings":
                await coordinator.UpdateProtectionSettingsAsync(GetPayload<UpdateProtectionSettingsCommand>(request), cancellationToken).ConfigureAwait(false);
                return IpcResponse.Ok();
            case "RestoreFile":
                return IpcResponse.Ok(await coordinator.RestoreAsync(GetPayload<RestoreFileCommand>(request), cancellationToken).ConfigureAwait(false));
            case "GetSnapshotDetails":
                return IpcResponse.Ok(await coordinator.GetSnapshotAsync(GetPayload<SnapshotDetailsCommand>(request).SnapshotId, cancellationToken).ConfigureAwait(false));
            default:
                return IpcResponse.Fail("Unbekannter IPC-Befehl.");
        }
    }

    private static T GetPayload<T>(IpcRequest request)
    {
        if (request.Payload is not { } payload)
        {
            throw new InvalidDataException("Die IPC-Anfrage enthält keine Daten.");
        }

        var value = payload.Deserialize<T>(VaultixProtocol.Json);
        return value is null ? throw new InvalidDataException("Die IPC-Anfrage enthält keine gültigen Daten.") : value;
    }
}
