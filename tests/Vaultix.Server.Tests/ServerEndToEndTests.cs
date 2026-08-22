using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Vaultix.Shared;

namespace Vaultix.Server.Tests;

public sealed class ServerEndToEndTests : IDisposable
{
    private readonly string _repositoryPath = Path.Combine(Path.GetTempPath(), "VaultixTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HttpApiPairsUploadsSnapshotsAndRestoresExactBytes()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Vaultix:RepositoryPath", _repositoryPath);
            builder.UseSetting("Vaultix:PairingEnabled", "true");
        });
        using var client = factory.CreateClient();
        var cancellationToken = CancellationToken.None;

        var health = await client.GetFromJsonAsync<HealthResponse>("/api/v1/health", cancellationToken);
        Assert.Equal("healthy", health?.Status);

        using var pairResponse = await client.PostAsJsonAsync("/api/v1/devices/pair", new PairDeviceRequest("integration-pc"), cancellationToken);
        pairResponse.EnsureSuccessStatusCode();
        var pairing = await pairResponse.Content.ReadFromJsonAsync<PairDeviceResponse>(cancellationToken);
        Assert.NotNull(pairing);
        client.DefaultRequestHeaders.Add(VaultixProtocol.DeviceIdHeader, pairing.DeviceId.ToString("D"));
        client.DefaultRequestHeaders.Add(VaultixProtocol.DeviceSecretHeader, pairing.DeviceSecret);

        var original = Encoding.UTF8.GetBytes("Vaultix end-to-end restore payload");
        var hash = Convert.ToHexStringLower(SHA256.HashData(original));
        using var checkResponse = await client.PostAsJsonAsync("/api/v1/objects/check", new ObjectCheckRequest([hash]), cancellationToken);
        var check = await checkResponse.Content.ReadFromJsonAsync<ObjectCheckResponse>(cancellationToken);
        Assert.Contains(hash, check?.MissingHashes ?? []);

        using var upload = await client.PutAsync($"/api/v1/objects/{hash}", new ByteArrayContent(original), cancellationToken);
        upload.EnsureSuccessStatusCode();
        var entry = new SnapshotEntryDto(Path.Combine("Documents", "proof.txt"), hash, original.Length, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0);
        using var snapshotResponse = await client.PostAsJsonAsync("/api/v1/snapshots", new CreateSnapshotRequest("First", @"C:\Users\Test", [entry]), cancellationToken);
        snapshotResponse.EnsureSuccessStatusCode();
        var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<SnapshotResponse>(cancellationToken);
        Assert.NotNull(snapshot);

        using var restored = await client.GetAsync($"/api/v1/snapshots/{snapshot.Id:D}/files?path={Uri.EscapeDataString(entry.RelativePath)}", cancellationToken);
        restored.EnsureSuccessStatusCode();
        Assert.Equal(original, await restored.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task PairingEndpointIsUnavailableWhenExplicitlyDisabled()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Vaultix:RepositoryPath", _repositoryPath);
            builder.UseSetting("Vaultix:PairingEnabled", "false");
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/v1/devices/pair", new PairDeviceRequest("integration-pc"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryPath)) Directory.Delete(_repositoryPath, recursive: true);
        GC.SuppressFinalize(this);
    }
}
