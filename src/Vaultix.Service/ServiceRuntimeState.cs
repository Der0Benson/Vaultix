using Vaultix.Shared;

namespace Vaultix.Service;

public sealed class ServiceRuntimeState
{
    private readonly object _gate = new();
    private readonly List<ActivityDto> _activities = [];
    private ServiceConfiguration _configuration = ServiceConfiguration.Empty;
    private QueueStatistics _statistics = new(0, 0, 0, 0);
    private IReadOnlyCollection<SnapshotResponse> _snapshots = [];
    private IReadOnlyCollection<BackupSessionDto> _recentSessions = [];
    private string _state = "Bereit";
    private bool _serverOnline;
    private string? _currentFile;
    private DateTimeOffset? _lastReconciliationUtc;
    private DateTimeOffset? _nextReconciliationUtc;
    private DateTimeOffset? _nextSnapshotUtc;

    public void SetWork(string state, string? currentFile = null)
    {
        lock (_gate)
        {
            _state = state;
            _currentFile = currentFile;
        }
    }

    public void SetServer(bool online)
    {
        lock (_gate) _serverOnline = online;
    }

    public void SetConfiguration(ServiceConfiguration configuration)
    {
        lock (_gate) _configuration = configuration;
    }

    public void SetStatistics(QueueStatistics statistics)
    {
        lock (_gate) _statistics = statistics;
    }

    public void SetSnapshots(IReadOnlyCollection<SnapshotResponse> snapshots)
    {
        lock (_gate) _snapshots = snapshots;
    }

    public void SetRecentSessions(IReadOnlyCollection<BackupSessionDto> sessions)
    {
        lock (_gate) _recentSessions = sessions;
    }

    public void SetSchedule(DateTimeOffset? lastReconciliationUtc, DateTimeOffset? nextReconciliationUtc)
    {
        lock (_gate)
        {
            _lastReconciliationUtc = lastReconciliationUtc;
            _nextReconciliationUtc = nextReconciliationUtc;
        }
    }

    public void SetNextSnapshot(DateTimeOffset? nextSnapshotUtc)
    {
        lock (_gate) _nextSnapshotUtc = nextSnapshotUtc;
    }

    public void AddActivity(string level, string message)
    {
        lock (_gate)
        {
            _activities.Insert(0, new ActivityDto(DateTimeOffset.UtcNow, level, message));
            if (_activities.Count > 100) _activities.RemoveRange(100, _activities.Count - 100);
        }
    }

    public ServiceStatusDto CreateStatus(LiveMetricsDto metrics)
    {
        lock (_gate)
        {
            var folderStatus = !_serverOnline ? "Offline" : _statistics.Failed > 0 ? "Fehler" : metrics.Phase == "Protected" ? "Geschützt" : "Wird gesichert";
            var folders = _configuration.Folders.Select(folder => new BackupFolderDto(
                folder.Id, folder.Path, folder.Enabled, folder.Schedule, folder.LastSuccessfulBackupUtc,
                folder.LastScanUtc, folder.FileCount, folder.TotalBytes,
                folder.Enabled ? folderStatus : "Pausiert")).ToArray();
            return new ServiceStatusDto(
                _state,
                _serverOnline,
                _configuration.ServerUrl,
                _statistics.Pending,
                _statistics.Failed,
                _statistics.ProtectedFiles,
                _statistics.ProtectedBytes,
                folders.Select(folder => folder.LastSuccessfulBackupUtc).Max(),
                _currentFile,
                CalculateProgress(metrics, _statistics),
                _lastReconciliationUtc,
                _nextReconciliationUtc,
                _nextSnapshotUtc,
                _configuration.EffectiveProtection.ToDto(),
                metrics,
                _recentSessions.ToArray(),
                folders,
                _snapshots.ToArray(),
                _activities.ToArray());
        }
    }

    private static double CalculateProgress(LiveMetricsDto metrics, QueueStatistics statistics)
    {
        if (metrics.Phase == "Protected" && statistics.Pending == 0 && statistics.Failed == 0) return 1;
        if (metrics.Phase == "Finalizing") return 0.98;
        if (metrics.BytesTotal <= 0) return metrics.Phase == "Scanning" ? 0.01 : 0;
        return Math.Min(0.97, Math.Max(0, (double)metrics.BytesProcessed / metrics.BytesTotal * 0.97));
    }
}
