using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Vaultix.Core;
using Vaultix.Infrastructure;
using Vaultix.Service;
using Vaultix.Shared;

namespace Vaultix.IntegrationTests;

public sealed class ServicePipelineTests
{
    [Fact]
    public async Task ServiceScansUploadsSnapshotsAndRestoresAfterLocalDeletion()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "VaultixTests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(root, "repository");
        var sourcePath = Path.Combine(root, "source");
        var sourceFile = Path.Combine(sourcePath, "Documents", "daily.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        var original = "The service-to-server-to-restore path is real.";
        await File.WriteAllTextAsync(sourceFile, original);

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
            using var coordinator = new BackupCoordinator(
                configuration,
                queue,
                new FileScanner(new DefaultExcludePolicy()),
                new Sha256FileHasher(),
                server,
                runtime,
                paths,
                NullLogger<BackupCoordinator>.Instance);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var loop = coordinator.RunLoopAsync(cancellation.Token);

            await coordinator.ConfigureServerAsync(new ConfigureServerCommand("http://localhost", "pipeline-test"), cancellation.Token);
            await coordinator.AddFolderAsync(sourcePath, cancellation.Token);

            ServiceStatusDto status;
            do
            {
                await Task.Delay(500, cancellation.Token);
                status = await coordinator.GetStatusAsync(cancellation.Token);
            }
            while (status.Snapshots.Count == 0);

            var snapshot = Assert.Single(status.Snapshots);
            File.Delete(sourceFile);
            var details = await coordinator.GetSnapshotAsync(snapshot.Id, cancellation.Token);
            var entry = Assert.Single(details.Entries);
            var restored = await coordinator.RestoreAsync(new RestoreFileCommand(snapshot.Id, entry.RelativePath), cancellation.Token);
            Assert.Equal(original, await File.ReadAllTextAsync(restored.StagedPath, cancellation.Token));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
