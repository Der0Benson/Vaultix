using System.Security.Cryptography;
using System.Threading.Channels;
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
    VaultixMetricsService metrics,
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

    public void SetReconciliationSchedule(DateTimeOffset? lastUtc, DateTimeOffset? nextUtc) => state.SetSchedule(lastUtc, nextUtc);

    public async Task ConfigureServerAsync(ConfigureServerCommand command, CancellationToken cancellationToken)
    {
        var uri = ValidateServerUri(command.ServerUrl);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sameServer = current.ServerUrl is not null && new Uri(current.ServerUrl, UriKind.Absolute).Equals(uri);
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
            await SaveConfigurationAsync(updated, cancellationToken).ConfigureAwait(false);
            if (!sameServer) await queue.RequeueActiveRunsAsync(cancellationToken).ConfigureAwait(false);
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
        if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException("Der ausgewählte Ordner wurde nicht gefunden.");
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Folders.Any(folder => folder.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase))) return;
            var folders = current.Folders.ToList();
            var folder = new BackupFolderConfiguration(Guid.NewGuid(), canonical, true, "Kontinuierlich", null);
            folders.Add(folder);
            await SaveConfigurationAsync(current with { Folders = folders }, cancellationToken).ConfigureAwait(false);
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
            await SaveConfigurationAsync(current with { Folders = current.Folders.Where(folder => folder.Id != id).ToList() }, cancellationToken).ConfigureAwait(false);
            state.AddActivity("Info", "Backup-Ordner entfernt; vorhandene Snapshots bleiben erhalten");
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task UpdateProtectionSettingsAsync(UpdateProtectionSettingsCommand command, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            await SaveConfigurationAsync(current with { Protection = ProtectionSettingsConfiguration.FromDto(command.Settings) }, cancellationToken).ConfigureAwait(false);
            state.AddActivity("Info", "Schutzeinstellungen gespeichert");
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public Task<ServiceStatusDto> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(state.CreateStatus(metrics.GetSnapshot()));

    public async Task<RestoreFileResult> RestoreAsync(RestoreFileCommand command, CancellationToken cancellationToken)
    {
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null) throw new InvalidOperationException("Vaultix ist noch mit keinem Server verbunden.");
        var details = await server.GetSnapshotAsync(command.SnapshotId, cancellationToken).ConfigureAwait(false);
        var entry = details.Entries.FirstOrDefault(item => item.RelativePath.Equals(command.RelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Die Datei ist in diesem Snapshot nicht vorhanden.");
        var restoreDirectory = Path.Combine(paths.DataDirectory, "restores");
        Directory.CreateDirectory(restoreDirectory);
        foreach (var oldRestore in Directory.EnumerateFiles(restoreDirectory).Where(file => File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))) File.Delete(oldRestore);
        var stagedPath = Path.Combine(restoreDirectory, $"{Guid.NewGuid():N}-{Path.GetFileName(command.RelativePath)}");
        metrics.BeginRestore(entry.Size);
        state.SetWork("Wiederherstellung läuft", command.RelativePath);
        await server.RestoreAsync(command.SnapshotId, command.RelativePath, stagedPath, cancellationToken, metrics.ReportDownloaded).ConfigureAwait(false);
        metrics.CompleteFile(entry.Size);
        metrics.CompleteSession();
        state.SetWork("Bereit");
        state.AddActivity("Success", $"Wiederhergestellt: {Path.GetFileName(command.RelativePath)}");
        return new RestoreFileResult(stagedPath);
    }

    public async Task<SnapshotDetailsResponse> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null) throw new InvalidOperationException("Vaultix ist noch mit keinem Server verbunden.");
        return await server.GetSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await queue.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCachedStateAsync(cancellationToken).ConfigureAwait(false);
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is { } startupConfiguration)
        {
            try
            {
                var startupSnapshots = await server.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
                state.SetSnapshots(startupSnapshots);
                SetSnapshotSchedule(startupConfiguration, startupSnapshots);
            }
            catch (HttpRequestException) { state.SetServer(false); }
        }
        state.AddActivity("Info", "Vaultix Service gestartet");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_requests.Reader.TryRead(out var folderId)) await ScanAsync(folderId, cancellationToken).ConfigureAwait(false);
                await CheckServerAsync(cancellationToken).ConfigureAwait(false);
                await ProcessQueueAsync(cancellationToken).ConfigureAwait(false);
                await FinalizeRunsAsync(cancellationToken).ConfigureAwait(false);
                await RefreshCachedStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
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
        state.SetConfiguration(configuration);
        foreach (var folder in configuration.Folders.Where(folder => folder.Enabled && (folderId is null || folder.Id == folderId)))
        {
            Guid runId;
            if (await queue.FindActiveRunAsync(folder.Path, cancellationToken).ConfigureAwait(false) is { } activeRun)
            {
                runId = activeRun;
                await queue.PrepareRunForRescanAsync(runId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                runId = await queue.CreateRunAsync(folder.Path, cancellationToken).ConfigureAwait(false);
            }

            metrics.BeginSession(runId);
            state.SetWork("Dateien werden analysiert", folder.Path);
            var count = 0L;
            var totalBytes = 0L;
            await foreach (var file in scanner.ScanAsync(folder.Path, cancellationToken).ConfigureAwait(false))
            {
                var previous = await queue.GetFileVersionAsync(file.RootPath, file.RelativePath, cancellationToken).ConfigureAwait(false);
                var unchanged = previous is not null && previous.Size == file.Size && previous.LastWriteUtc == file.LastWriteUtc;
                if (unchanged)
                {
                    await queue.MarkUnchangedAsync(runId, file, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await queue.EnqueueAsync(runId, file, previous is null ? DetectedChangeType.New : DetectedChangeType.Changed, cancellationToken).ConfigureAwait(false);
                }
                metrics.DiscoverFile(file.Size, unchanged);
                count++;
                totalBytes = checked(totalBytes + file.Size);
                if (count % 100 == 0) state.SetWork("Dateien werden analysiert", file.RelativePath);
            }
            var deleted = await queue.CompleteScanAsync(runId, folder.Path, cancellationToken).ConfigureAwait(false);
            await MarkFolderScannedAsync(folder.Path, count, totalBytes, cancellationToken).ConfigureAwait(false);
            metrics.CompleteScan();
            state.AddActivity("Info", $"Abgleich beendet: {count:N0} Dateien, {deleted:N0} gelöscht");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null) return;
        while (await queue.LeaseNextAsync(cancellationToken).ConfigureAwait(false) is { } job)
        {
            try
            {
                metrics.ResumeSession(await queue.GetSessionAsync(job.RunId, cancellationToken).ConfigureAwait(false));
                var info = new FileInfo(job.FilePath);
                info.Refresh();
                if (!info.Exists)
                {
                    await queue.CompleteMissingAsync(job, cancellationToken).ConfigureAwait(false);
                    metrics.CompleteFile(0);
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

                metrics.BeginHashing();
                await queue.SetStateAsync(job.Id, BackupQueueState.Hashing, null, cancellationToken).ConfigureAwait(false);
                state.SetWork("Hash wird berechnet", job.RelativePath);
                var hash = await hasher.HashAsync(job.FilePath, cancellationToken).ConfigureAwait(false);
                await queue.RecordHashedAsync(job.RunId, job.Size, cancellationToken).ConfigureAwait(false);
                var previous = await queue.GetFileVersionAsync(job.RootPath, job.RelativePath, cancellationToken).ConfigureAwait(false);
                var uploaded = false;
                if (!string.Equals(previous?.ObjectHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    await queue.SetStateAsync(job.Id, BackupQueueState.CheckingServer, hash, cancellationToken).ConfigureAwait(false);
                    if (!await server.ObjectExistsAsync(hash, cancellationToken).ConfigureAwait(false))
                    {
                        metrics.BeginUploading();
                        await queue.SetStateAsync(job.Id, BackupQueueState.Uploading, hash, cancellationToken).ConfigureAwait(false);
                        state.SetWork("Datei wird gesichert", job.RelativePath);
                        await server.UploadAsync(hash, job.FilePath, cancellationToken, metrics.ReportUploaded).ConfigureAwait(false);
                        uploaded = true;
                    }
                }
                await queue.CompleteAsync(job, hash, uploaded, cancellationToken).ConfigureAwait(false);
                metrics.CompleteFile(job.Size);
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
            finally
            {
                metrics.SetQueueLength((await queue.GetStatisticsAsync(cancellationToken).ConfigureAwait(false)).Pending);
            }
        }
    }

    private async Task FinalizeRunsAsync(CancellationToken cancellationToken)
    {
        foreach (var run in await queue.GetReadyRunsAsync(cancellationToken).ConfigureAwait(false))
        {
            var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var session = await queue.GetSessionAsync(run.Id, cancellationToken).ConfigureAwait(false);
            metrics.ResumeSession(session);
            var hasChanges = session.NewFiles + session.ChangedFiles + session.DeletedFiles > 0;
            SnapshotResponse? snapshot = null;
            var status = state.CreateStatus(metrics.GetSnapshot());
            var due = status.NextSnapshotUtc is not { } next || DateTimeOffset.UtcNow >= next;
            var latestVisible = status.Snapshots.Where(item => item.SourceRoot.Equals(run.RootPath, StringComparison.OrdinalIgnoreCase)).OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
            var changesSinceVisible = hasChanges || await queue.HasChangesSinceAsync(run.RootPath, latestVisible?.CreatedUtc ?? DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false);
            var createVisible = due && (changesSinceVisible || !configuration.EffectiveProtection.SkipUnchangedSnapshots);
            var createCheckpoint = !due && hasChanges;
            if (createVisible || createCheckpoint)
            {
                metrics.BeginFinalizing();
                var entries = await queue.GetSnapshotEntriesAsync(run, cancellationToken).ConfigureAwait(false);
                state.SetWork("Snapshot wird erstellt", Path.GetFileName(run.RootPath));
                snapshot = await server.CreateSnapshotAsync(new CreateSnapshotRequest(
                    $"Snapshot {DateTime.Now:g}", run.RootPath, entries, IsCheckpoint: createCheckpoint), cancellationToken).ConfigureAwait(false);
            }
            if (due) state.SetNextSnapshot(DateTimeOffset.UtcNow.AddMinutes(configuration.EffectiveProtection.SnapshotMinutes));
            var speeds = metrics.GetCompletionSpeeds();
            await queue.CompleteRunAsync(run, speeds.AverageUploadSpeed, speeds.PeakUploadSpeed, cancellationToken).ConfigureAwait(false);
            await MarkFolderSuccessfulAsync(run.RootPath, snapshot?.CreatedUtc ?? DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            metrics.CompleteSession();
            state.SetWork("Alles gesichert");
            state.AddActivity("Success", snapshot is null
                ? "Keine Änderungen – unnötiger Snapshot übersprungen"
                : $"Snapshot erstellt – {snapshot.FileCount:N0} Dateien");
        }

        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            var snapshots = await server.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false);
            state.SetSnapshots(snapshots);
        }
    }

    private async Task RefreshCachedStateAsync(CancellationToken cancellationToken)
    {
        state.SetConfiguration(await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false));
        var statistics = await queue.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        state.SetStatistics(statistics);
        metrics.SetQueueLength(statistics.Pending);
        state.SetRecentSessions(await queue.GetRecentSessionsAsync(30, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ServiceConfiguration?> ConfigureClientAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        state.SetConfiguration(configuration);
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
        if (await ConfigureClientAsync(cancellationToken).ConfigureAwait(false) is null) return;
        try
        {
            await server.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            state.SetServer(true);
        }
        catch (HttpRequestException) { state.SetServer(false); }
    }

    private async Task MarkFolderSuccessfulAsync(string rootPath, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folderStatistics = await queue.GetFolderStatisticsAsync(rootPath, cancellationToken).ConfigureAwait(false);
            var folders = current.Folders.Select(folder => folder.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                ? folder with { LastSuccessfulBackupUtc = timestamp, LastScanUtc = DateTimeOffset.UtcNow, FileCount = folderStatistics.FileCount, TotalBytes = folderStatistics.TotalBytes }
                : folder).ToList();
            await SaveConfigurationAsync(current with { Folders = folders }, cancellationToken).ConfigureAwait(false);
        }
        finally { _configurationGate.Release(); }
    }

    private async Task MarkFolderScannedAsync(string rootPath, long fileCount, long totalBytes, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folders = current.Folders.Select(folder => folder.Path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                ? folder with { LastScanUtc = DateTimeOffset.UtcNow, FileCount = fileCount, TotalBytes = totalBytes }
                : folder).ToList();
            await SaveConfigurationAsync(current with { Folders = folders }, cancellationToken).ConfigureAwait(false);
        }
        finally { _configurationGate.Release(); }
    }

    private async Task SaveConfigurationAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        await configurationStore.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        state.SetConfiguration(configuration);
    }

    private void SetSnapshotSchedule(ServiceConfiguration configuration, IReadOnlyCollection<SnapshotResponse> snapshots)
    {
        var latest = snapshots.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
        if (latest is not null) state.SetNextSnapshot(latest.CreatedUtc.AddMinutes(configuration.EffectiveProtection.SnapshotMinutes));
    }

    private static Uri ValidateServerUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Bitte eine gültige HTTP- oder HTTPS-Adresse angeben.", nameof(value));
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new ArgumentException("Außerhalb dieses PCs ist für Vaultix HTTPS erforderlich.", nameof(value));
        return uri;
    }

    public void Dispose()
    {
        _configurationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
