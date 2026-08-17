using System.Threading.Channels;
using System.Security.Cryptography;
using Vaultix.Core;
using Vaultix.Infrastructure;
using Vaultix.Shared;

namespace Vaultix.Service;

public sealed class BackupCoordinator(
    ServiceConfigurationStore configurationStore,
    BackupQueueStore queue,
    IFileScanner scanner,
    IFileHasher hasher,
    VaultixServerClient server,
    ServiceRuntimeState state,
    VaultixPaths paths,
    ILogger<BackupCoordinator> logger) : IDisposable
{
    private readonly Channel<Guid?> _requests = Channel.CreateBounded<Guid?>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly SemaphoreSlim _configurationGate = new(1, 1);

    public void RequestBackup(Guid? folderId = null) => _requests.Writer.TryWrite(folderId);

    public async Task ConfigureServerAsync(ConfigureServerCommand command, CancellationToken cancellationToken)
    {
        var uri = ValidateServerUri(command.ServerUrl);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sameServer = current.ServerUrl is not null &&
                new Uri(current.ServerUrl, UriKind.Absolute).Equals(uri);
            var existingCredentials = sameServer ? ServiceConfigurationStore.GetCredentials(current) : null;
            server.Configure(uri, existingCredentials);
            await server.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (existingCredentials is not null)
            {
                try
                {
                    state.SetSnapshots(await server.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false));
                    state.SetServer(true);
                    state.AddActivity("Success", "Vaultix Server verbunden");
                    return;
                }
                catch (HttpRequestException)
                {
                    server.Configure(uri, null);
                }
            }

            var pairing = await server.PairAsync(string.IsNullOrWhiteSpace(command.DeviceName) ? Environment.MachineName : command.DeviceName.Trim(), cancellationToken).ConfigureAwait(false);
            var updated = current with
            {
                ServerUrl = uri.ToString().TrimEnd('/'),
                DeviceId = pairing.DeviceId,
                ProtectedDeviceSecret = ServiceConfigurationStore.ProtectSecret(pairing.DeviceSecret)
            };
            await configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            if (!sameServer)
            {
                await queue.RequeueActiveRunsAsync(cancellationToken).ConfigureAwait(false);
            }
            server.Configure(uri, new DeviceCredentials(pairing.DeviceId, pairing.DeviceSecret));
            state.SetServer(true);
            state.AddActivity("Success", "Vaultix Server verbunden");
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task AddFolderAsync(string path, CancellationToken cancellationToken)
    {
        var canonical = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("Der ausgewählte Ordner wurde nicht gefunden.");
        }

        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Folders.Any(folder => folder.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var folders = current.Folders.ToList();
            var folder = new BackupFolderConfiguration(Guid.NewGuid(), canonical, true, "Kontinuierlich", null);
            folders.Add(folder);
            await configurationStore.SaveAsync(current with { Folders = folders }, cancellationToken).ConfigureAwait(false);
            state.AddActivity("Info", $"Ordner hinzugefügt: {Path.GetFileName(canonical)}");
            RequestBackup(folder.Id);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task RemoveFolderAsync(Guid id, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folders = current.Folders.Where(folder => folder.Id != id).ToList();
            await configurationStore.SaveAsync(current with { Folders = folders }, cancellationToken).ConfigureAwait(false);
            state.AddActivity("Info", "Backup-Ordner entfernt; vorhandene Snapshots bleiben erhalten");
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task<ServiceStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var statistics = await queue.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        return state.CreateStatus(configuration, statistics);
    }

    public async Task<RestoreFileResult> RestoreAsync(RestoreFileCommand command, CancellationToken cancellationToken)
    {
        var configuration = await ConfigureClientAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is null)
        {
            throw new InvalidOperationException("Vaultix ist noch mit keinem Server verbunden.");
        }

        var restoreDirectory = Path.Combine(paths.DataDirectory, "restores");
        Directory.CreateDirectory(restoreDirectory);
        foreach (var oldRestore in Directory.EnumerateFiles(restoreDirectory).Where(file => File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)))
        {
            File.Delete(oldRestore);
        }
        var safeName = Path.GetFileName(command.RelativePath);
        var stagedPath = Path.Combine(restoreDirectory, $"{Guid.NewGuid():N}-{safeName}");
        state.SetWork("Wiederherstellung läuft", command.RelativePath);
        await server.RestoreAsync(command.SnapshotId, command.RelativePath, stagedPath, cancellationToken).ConfigureAwait(false);
        state.SetWork("Bereit");
        state.AddActivity("Success", $"Wiederhergestellt: {Path.GetFileName(command.RelativePath)}");
        return new RestoreFileResult(stagedPath);
    }

    public async Task<SnapshotDetailsResponse> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("Vaultix ist noch mit keinem Server verbunden.");
        }

        return await server.GetSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await queue.InitializeAsync(cancellationToken).ConfigureAwait(false);
        state.AddActivity("Info", "Vaultix Service gestartet");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_requests.Reader.TryRead(out var folderId))
                {
                    await ScanAsync(folderId, cancellationToken).ConfigureAwait(false);
                }

                await CheckServerAsync(cancellationToken).ConfigureAwait(false);
                await ProcessQueueAsync(cancellationToken).ConfigureAwait(false);
                await FinalizeRunsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Backup loop failed safely");
                state.AddActivity("Error", "Backup konnte nicht abgeschlossen werden");
                state.SetWork("Fehler");
            }

            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScanAsync(Guid? folderId, CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var folders = configuration.Folders.Where(folder => folder.Enabled && (folderId is null || folder.Id == folderId)).ToArray();
        foreach (var folder in folders)
        {
            if (await queue.FindActiveRunAsync(folder.Path, cancellationToken).ConfigureAwait(false) is { } activeRun)
            {
                await queue.RetryFailedRunAsync(activeRun, cancellationToken).ConfigureAwait(false);
                continue;
            }

            state.SetWork("Dateien werden analysiert", folder.Path);
            var runId = await queue.CreateRunAsync(folder.Path, cancellationToken).ConfigureAwait(false);
            var count = 0;
            await foreach (var file in scanner.ScanAsync(folder.Path, cancellationToken).ConfigureAwait(false))
            {
                await queue.EnqueueAsync(runId, file, cancellationToken).ConfigureAwait(false);
                count++;
                if (count % 100 == 0)
                {
                    state.SetWork("Dateien werden analysiert", file.RelativePath);
                }
            }

            state.AddActivity("Info", $"{count:N0} Dateien gefunden");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        while (await queue.LeaseNextAsync(cancellationToken).ConfigureAwait(false) is { } job)
        {
            try
            {
                var info = new FileInfo(job.FilePath);
                info.Refresh();
                if (!info.Exists)
                {
                    await queue.SetStateAsync(job.Id, BackupQueueState.Completed, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                state.SetWork("Datei wird geprüft", job.RelativePath);
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
                info.Refresh();
                if (info.Length != job.Size || info.LastWriteTimeUtc != job.LastWriteUtc.UtcDateTime)
                {
                    await queue.RetryAsync(job, "Datei wird noch verändert", info, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await queue.SetStateAsync(job.Id, BackupQueueState.Hashing, null, cancellationToken).ConfigureAwait(false);
                state.SetWork("Hash wird berechnet", job.RelativePath);
                var hash = await hasher.HashAsync(job.FilePath, cancellationToken).ConfigureAwait(false);
                await queue.SetStateAsync(job.Id, BackupQueueState.CheckingServer, hash, cancellationToken).ConfigureAwait(false);
                if (!await server.ObjectExistsAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    await queue.SetStateAsync(job.Id, BackupQueueState.Uploading, hash, cancellationToken).ConfigureAwait(false);
                    state.SetWork("Datei wird gesichert", job.RelativePath);
                    await server.UploadAsync(hash, job.FilePath, cancellationToken).ConfigureAwait(false);
                }

                await queue.CompleteAsync(job, hash, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                await queue.RetryAsync(job, exception.Message, null, cancellationToken).ConfigureAwait(false);
                state.SetServer(exception is not HttpRequestException);
                state.AddActivity("Warning", "Verbindung unterbrochen – Dateien bleiben in der Warteschlange");
                break;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or CryptographicException)
            {
                await queue.FailAsync(job, exception.Message, cancellationToken).ConfigureAwait(false);
                state.AddActivity("Error", $"Datei konnte nicht gesichert werden: {Path.GetFileName(job.FilePath)}");
            }
        }
    }

    private async Task FinalizeRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var run in await queue.GetReadyRunsAsync(cancellationToken).ConfigureAwait(false))
        {
            var entries = await queue.GetSnapshotEntriesAsync(run, cancellationToken).ConfigureAwait(false);
            state.SetWork("Snapshot wird erstellt", Path.GetFileName(run.RootPath));
            var snapshot = await server.CreateSnapshotAsync(new CreateSnapshotRequest(
                $"Snapshot {DateTime.Now:g}", run.RootPath, entries), cancellationToken).ConfigureAwait(false);
            await queue.CompleteRunAsync(run, cancellationToken).ConfigureAwait(false);
            await MarkFolderSuccessfulAsync(run.RootPath, snapshot.CreatedUtc, cancellationToken).ConfigureAwait(false);
            state.AddActivity("Success", $"Snapshot erstellt – {snapshot.FileCount:N0} Dateien");
            state.SetWork("Alles gesichert", progress: 1);
        }

        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            state.SetSnapshots(await server.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<ServiceConfiguration?> ConfigureClientAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.ServerUrl is null || ServiceConfigurationStore.GetCredentials(configuration) is not { } credentials)
        {
            state.SetServer(false);
            return null;
        }

        server.Configure(new Uri(configuration.ServerUrl, UriKind.Absolute), credentials);
        return configuration;
    }

    private async Task CheckServerAsync(CancellationToken cancellationToken)
    {
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        try
        {
            await server.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            state.SetServer(true);
        }
        catch (HttpRequestException)
        {
            state.SetServer(false);
        }
    }

    private async Task MarkFolderSuccessfulAsync(string rootPath, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folders = current.Folders.Select(folder => folder.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                ? folder with { LastSuccessfulBackupUtc = timestamp }
                : folder).ToList();
            await configurationStore.SaveAsync(current with { Folders = folders }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private static Uri ValidateServerUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Bitte eine gültige HTTP- oder HTTPS-Adresse angeben.", nameof(value));
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new ArgumentException("Außerhalb dieses PCs ist für Vaultix HTTPS erforderlich.", nameof(value));
        }

        return uri;
    }

    public void Dispose()
    {
        _configurationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
