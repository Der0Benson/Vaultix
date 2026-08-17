using System.Security.Cryptography;
using System.Text;
using Vaultix.Shared;
using Vaultix.Storage;

namespace Vaultix.IntegrationTests;

public sealed class RepositoryIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VaultixTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RepositoryDeduplicatesAndKeepsSnapshotRestorableAfterSourceDeletion()
    {
        var repository = new VaultixRepository(_root);
        await repository.InitializeAsync(CancellationToken.None);
        var pairing = await repository.PairDeviceAsync("test-pc", CancellationToken.None);
        var content = Encoding.UTF8.GetBytes("immutable vaultix object");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));

        await repository.StoreObjectAsync(hash, new MemoryStream(content), 1024, CancellationToken.None);
        await repository.StoreObjectAsync(hash, new MemoryStream(content), 1024, CancellationToken.None);
        var snapshot = await repository.CreateSnapshotAsync(pairing.DeviceId, new CreateSnapshotRequest(
            "Test", @"C:\Data", [new SnapshotEntryDto("notes.txt", hash, content.Length, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0)]), CancellationToken.None);

        var restored = await repository.OpenSnapshotFileAsync(pairing.DeviceId, snapshot.Id, "notes.txt", CancellationToken.None);
        Assert.NotNull(restored);
        await using var stream = restored.Value.Stream;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, CancellationToken.None);
        Assert.Equal(content, memory.ToArray());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RepositoryRejectsHashMismatchAndPathTraversal()
    {
        var repository = new VaultixRepository(_root);
        await repository.InitializeAsync(CancellationToken.None);
        var badHash = new string('a', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StoreObjectAsync(
            badHash, new MemoryStream(Encoding.UTF8.GetBytes("different")), 1024, CancellationToken.None));

        var pairing = await repository.PairDeviceAsync("test-pc", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.CreateSnapshotAsync(pairing.DeviceId, new CreateSnapshotRequest(
            "Unsafe", @"C:\Data", [new SnapshotEntryDto("..\\secret.txt", badHash, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0)]), CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
