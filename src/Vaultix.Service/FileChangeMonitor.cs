using System.Collections.Concurrent;

namespace Vaultix.Service;

public sealed class FileChangeMonitor(
    ServiceConfigurationStore configurationStore,
    BackupCoordinator coordinator,
    ILogger<FileChangeMonitor> logger) : BackgroundService
{
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _changes = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextControlScan = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SynchronizeWatchersAsync(stoppingToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var change in _changes.ToArray())
            {
                if (now - change.Value >= TimeSpan.FromSeconds(10) && _changes.TryRemove(change.Key, out _))
                {
                    coordinator.RequestBackup(change.Key);
                }
            }

            if (now >= nextControlScan)
            {
                coordinator.RequestBackup();
                nextControlScan = now + TimeSpan.FromHours(1);
            }
        }
    }

    public override void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        base.Dispose();
    }

    private async Task SynchronizeWatchersAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var wanted = configuration.Folders.Where(folder => folder.Enabled && Directory.Exists(folder.Path)).ToDictionary(folder => folder.Id);
        foreach (var obsolete in _watchers.Keys.Except(wanted.Keys).ToArray())
        {
            _watchers[obsolete].Dispose();
            _watchers.Remove(obsolete);
        }

        foreach (var folder in wanted.Values.Where(folder => !_watchers.ContainsKey(folder.Id)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, _) => MarkChanged(folder.Id);
                watcher.Changed += (_, _) => MarkChanged(folder.Id);
                watcher.Deleted += (_, _) => MarkChanged(folder.Id);
                watcher.Renamed += (_, _) => MarkChanged(folder.Id);
                watcher.Error += (_, eventArgs) =>
                {
                    logger.LogWarning(eventArgs.GetException(), "File watcher overflow for {Folder}", folder.Path);
                    MarkChanged(folder.Id);
                };
                _watchers.Add(folder.Id, watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not monitor {Folder}; periodic scanning remains active", folder.Path);
            }
        }
    }

    private void MarkChanged(Guid folderId) => _changes[folderId] = DateTimeOffset.UtcNow;
}
