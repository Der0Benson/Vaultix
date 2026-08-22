using System.IO.Pipes;
using System.Text.Json;
using System.IO;
using System.Runtime.CompilerServices;
using Vaultix.Shared;

namespace Vaultix.App.Services;

public sealed class VaultixIpcClient
{
    public async IAsyncEnumerable<ServiceStatusDto> StreamStatusAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", VaultixProtocol.StatusPipeName, PipeDirection.In, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) yield break;
            yield return JsonSerializer.Deserialize<ServiceStatusDto>(line, VaultixProtocol.Json)
                ?? throw new InvalidDataException("Vaultix Service lieferte ungültige Statusdaten.");
        }
    }

    public async Task<T> SendAsync<T>(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        var response = await SendCoreAsync(command, payload, cancellationToken).ConfigureAwait(false);
        if (response.Data is not { } data)
        {
            throw new InvalidDataException("Vaultix Service lieferte keine Daten.");
        }

        var value = data.Deserialize<T>(VaultixProtocol.Json);
        if (value is null)
        {
            throw new InvalidDataException("Vaultix Service lieferte keine gültigen Daten.");
        }

        return value;
    }

    public async Task SendAsync(string command, object? payload = null, CancellationToken cancellationToken = default)
    {
        await SendCoreAsync(command, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IpcResponse> SendCoreAsync(string command, object? payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", VaultixProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var request = new IpcRequest(command, payload is null ? null : JsonSerializer.SerializeToElement(payload, VaultixProtocol.Json));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, VaultixProtocol.Json)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("Vaultix Service hat die Verbindung beendet.");
        var response = JsonSerializer.Deserialize<IpcResponse>(line, VaultixProtocol.Json)
            ?? throw new InvalidDataException("Vaultix Service lieferte eine ungültige Antwort.");
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "Vaultix Service konnte den Befehl nicht ausführen.");
        }

        return response;
    }
}
