using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Vaultix.Core;
using Vaultix.Infrastructure;
using Vaultix.Service;
using Vaultix.Shared;

namespace Vaultix.IntegrationTests;

public sealed class ContinuousProtectionTests
{
    [Fact]
    public async Task WatcherDebouncesAndAutomaticallyProtectsANewFile()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "VaultixTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(root, "repository");
        var sourcePath = Path.Combine(root, "source");
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "initial.txt"), "initial");

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.UseSetting("Vaultix:RepositoryPath", repositoryPath));
            using var httpClient = factory.CreateClient();
            var paths = new VaultixPaths(Path.Combine(root, "service"));
            using var configuration = new ServiceConfigurationStore(paths);
            var queue = new BackupQueueStore(paths);
            var runtime = new ServiceRuntimeState();
            var server = new VaultixServerClient(httpClient);
            using var metrics = new VaultixMetricsService(queue, NullLogger<VaultixMetricsService>.Instance);
            using var coordinator = new BackupCoordinator(configuration, queue, new FileScanner(new DefaultExcludePolicy()),
                new Sha256FileHasher(), server, runtime, metrics, paths, NullLogger<BackupCoordinator>.Instance);
            using var monitor = new FileChangeMonitor(configuration, coordinator, NullLogger<FileChangeMonitor>.Instance);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var loop = coordinator.RunLoopAsync(cancellation.Token);

            await coordinator.ConfigureServerAsync(new ConfigureServerCommand("http://localhost", "watcher-test"), cancellation.Token);
            await coordinator.UpdateProtectionSettingsAsync(new UpdateProtectionSettingsCommand(new ProtectionSettingsDto(true, 1, 60, 30, true)), cancellation.Token);
            await coordinator.AddFolderAsync(sourcePath, cancellation.Token);
            await monitor.StartAsync(cancellation.Token);

            ServiceStatusDto status;
            do
            {
                await Task.Delay(250, cancellation.Token);
                status = await coordinator.GetStatusAsync(cancellation.Token);
            }
            while (status.LastReconciliationUtc is null || status.RecentSessions.Count < 2 || status.RecentSessions.First().CompletedUtc is null);

            var before = status.RecentSessions.Count;
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "automatic.txt"), "protected by watcher", cancellation.Token);
            do
            {
                await Task.Delay(250, cancellation.Token);
                status = await coordinator.GetStatusAsync(cancellation.Token);
            }
            while (status.RecentSessions.Count <= before || status.RecentSessions.First().CompletedUtc is null);

            Assert.Equal(2, status.ProtectedFiles);
            Assert.Collection(Directory.EnumerateFiles(Path.Combine(repositoryPath, "objects"), "*", SearchOption.AllDirectories), _ => { }, _ => { });

            await monitor.StopAsync(CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
