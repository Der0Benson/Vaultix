using Vaultix.Core;
using Vaultix.Service;

namespace Vaultix.IntegrationTests;

public sealed class QueuePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VaultixTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PendingJobSurvivesStoreRestart()
    {
        Directory.CreateDirectory(_root);
        var paths = new VaultixPaths(_root);
        var firstStore = new BackupQueueStore(paths);
        await firstStore.InitializeAsync(CancellationToken.None);
        var run = await firstStore.CreateRunAsync(_root, CancellationToken.None);
        var timestamp = DateTimeOffset.UtcNow;
        var candidate = new FileCandidate(_root, Path.Combine(_root, "pending.txt"), "pending.txt", 42, timestamp, timestamp, FileAttributes.Normal);
        await firstStore.EnqueueAsync(run, candidate, DetectedChangeType.New, CancellationToken.None);
        await firstStore.CompleteScanAsync(run, _root, CancellationToken.None);

        var restartedStore = new BackupQueueStore(paths);
        await restartedStore.InitializeAsync(CancellationToken.None);
        var leased = await restartedStore.LeaseNextAsync(CancellationToken.None);

        Assert.NotNull(leased);
        Assert.Equal(run, leased.RunId);
        Assert.Equal("pending.txt", leased.RelativePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
