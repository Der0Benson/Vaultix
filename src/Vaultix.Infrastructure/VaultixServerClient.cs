using System.Net.Http.Json;
using Vaultix.Shared;

namespace Vaultix.Infrastructure;

public sealed record DeviceCredentials(Guid DeviceId, string DeviceSecret);

public sealed class VaultixServerClient(HttpClient httpClient)
{
    private readonly object _gate = new();
    private Uri? _server;
    private DeviceCredentials? _credentials;

    public void Configure(Uri server, DeviceCredentials? credentials)
    {
        lock (_gate)
        {
            _server = server;
            _credentials = credentials;
        }
    }

    public async Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{VaultixProtocol.ApiPrefix}/health", authenticated: false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<HealthResponse>(VaultixProtocol.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Der Server lieferte keinen Health-Status.");
    }

    public async Task<PairDeviceResponse> PairAsync(string deviceName, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{VaultixProtocol.ApiPrefix}/devices/pair", JsonContent.Create(new PairDeviceRequest(deviceName), options: VaultixProtocol.Json), authenticated: false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PairDeviceResponse>(VaultixProtocol.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Der Server lieferte keine Gerätekennung.");
    }

    public async Task<bool> ObjectExistsAsync(string hash, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{VaultixProtocol.ApiPrefix}/objects/check", JsonContent.Create(new ObjectCheckRequest([hash]), options: VaultixProtocol.Json));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ObjectCheckResponse>(VaultixProtocol.Json, cancellationToken).ConfigureAwait(false);
        return result is not null && !result.MissingHashes.Contains(hash, StringComparer.OrdinalIgnoreCase);
    }

    public async Task UploadAsync(string hash, string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var content = new StreamContent(file);
        content.Headers.ContentLength = file.Length;
        content.Headers.ContentType = new("application/octet-stream");
        using var request = CreateRequest(HttpMethod.Put, $"{VaultixProtocol.ApiPrefix}/objects/{hash}", content);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SnapshotResponse> CreateSnapshotAsync(CreateSnapshotRequest request, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, $"{VaultixProtocol.ApiPrefix}/snapshots", JsonContent.Create(request, options: VaultixProtocol.Json));
        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<SnapshotResponse>(VaultixProtocol.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Der Server lieferte keinen Snapshot.");
    }

    public async Task<IReadOnlyCollection<SnapshotResponse>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{VaultixProtocol.ApiPrefix}/snapshots");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyCollection<SnapshotResponse>>(VaultixProtocol.Json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task<SnapshotDetailsResponse> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{VaultixProtocol.ApiPrefix}/snapshots/{snapshotId:D}");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<SnapshotDetailsResponse>(VaultixProtocol.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Der Server lieferte keine Snapshot-Details.");
    }

    public async Task RestoreAsync(Guid snapshotId, string relativePath, string destinationPath, CancellationToken cancellationToken)
    {
        var url = $"{VaultixProtocol.ApiPrefix}/snapshots/{snapshotId:D}/files?path={Uri.EscapeDataString(relativePath)}";
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        var temporaryPath = destinationPath + ".vaultix-restoring";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Vaultix Server antwortete mit {(int)response.StatusCode}: {detail}");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null, bool authenticated = true)
    {
        Uri server;
        DeviceCredentials? credentials;
        lock (_gate)
        {
            server = _server ?? throw new InvalidOperationException("Der Vaultix Server wurde noch nicht konfiguriert.");
            credentials = _credentials;
        }

        var request = new HttpRequestMessage(method, new Uri(server, path)) { Content = content };
        if (authenticated)
        {
            if (credentials is null)
            {
                request.Dispose();
                throw new InvalidOperationException("Das Gerät wurde noch nicht mit dem Vaultix Server gekoppelt.");
            }

            request.Headers.Add(VaultixProtocol.DeviceIdHeader, credentials.DeviceId.ToString("D"));
            request.Headers.Add(VaultixProtocol.DeviceSecretHeader, credentials.DeviceSecret);
        }

        return request;
    }
}
