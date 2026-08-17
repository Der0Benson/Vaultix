using Vaultix.Shared;

namespace Vaultix.Service;

public sealed class ServiceRuntimeState
{
    private readonly object _gate = new();
    private readonly List<ActivityDto> _activities = [];
    private IReadOnlyCollection<SnapshotResponse> _snapshots = [];
    private string _state = "Bereit";
    private bool _serverOnline;
    private string? _currentFile;
    private double _progress;

    public void SetWork(string state, string? currentFile = null, double progress = 0)
    {
        lock (_gate)
        {
            _state = state;
            _currentFile = currentFile;
            _progress = Math.Clamp(progress, 0, 1);
        }
    }

    public void SetServer(bool online)
    {
        lock (_gate)
        {
            _serverOnline = online;
        }
    }

    public void SetSnapshots(IReadOnlyCollection<SnapshotResponse> snapshots)
    {
        lock (_gate)
        {
            _snapshots = snapshots;
        }
    }

    public void AddActivity(string level, string message)
    {
        lock (_gate)
        {
            _activities.Insert(0, new ActivityDto(DateTimeOffset.UtcNow, level, message));
            if (_activities.Count > 100)
            {
                _activities.RemoveRange(100, _activities.Count - 100);
            }
        }
    }

    public ServiceStatusDto CreateStatus(ServiceConfiguration configuration, QueueStatistics statistics)
    {
        lock (_gate)
        {
            var folders = configuration.Folders.Select(folder => new BackupFolderDto(
                folder.Id, folder.Path, folder.Enabled, folder.Schedule, folder.LastSuccessfulBackupUtc)).ToArray();
            return new ServiceStatusDto(
                _state,
                _serverOnline,
                configuration.ServerUrl,
                statistics.Pending,
                statistics.Failed,
                statistics.ProtectedFiles,
                statistics.ProtectedBytes,
                folders.Select(folder => folder.LastSuccessfulBackupUtc).Where(value => value.HasValue).Max(),
                _currentFile,
                _progress,
                folders,
                _snapshots.ToArray(),
                _activities.ToArray());
        }
    }
}
