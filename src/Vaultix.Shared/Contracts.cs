using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vaultix.Shared;

public static class VaultixProtocol
{
    public const string ApiPrefix = "/api/v1";
    public const string DeviceIdHeader = "X-Vaultix-Device";
    public const string DeviceSecretHeader = "X-Vaultix-Secret";
    public const string PipeName = "Vaultix.Service.v1";
    public const string StatusPipeName = "Vaultix.Status.v1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record HealthResponse(string Status, string Version, bool StorageAvailable, long AvailableBytes);
public sealed record PairDeviceRequest(string DeviceName);
public sealed record PairDeviceResponse(Guid DeviceId, string DeviceSecret);
public sealed record ObjectCheckRequest(IReadOnlyCollection<string> Hashes);
public sealed record ObjectCheckResponse(IReadOnlyCollection<string> MissingHashes);

public sealed record SnapshotEntryDto(
    string RelativePath,
    string ObjectHash,
    long Size,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastWriteUtc,
    int Attributes);

public sealed record CreateSnapshotRequest(string Name, string SourceRoot, IReadOnlyCollection<SnapshotEntryDto> Entries, bool IsCheckpoint = false);
public sealed record SnapshotResponse(Guid Id, string Name, string SourceRoot, DateTimeOffset CreatedUtc, int FileCount, long TotalBytes, bool IsCheckpoint = false);
public sealed record SnapshotDetailsResponse(SnapshotResponse Snapshot, IReadOnlyCollection<SnapshotEntryDto> Entries);

public sealed record BackupFolderDto(
    Guid Id,
    string Path,
    bool Enabled,
    string Schedule,
    DateTimeOffset? LastSuccessfulBackupUtc,
    DateTimeOffset? LastScanUtc = null,
    long FileCount = 0,
    long TotalBytes = 0,
    string Status = "Protected");
public sealed record ActivityDto(DateTimeOffset TimestampUtc, string Level, string Message);

public sealed record ProtectionSettingsDto(
    bool ContinuousProtection,
    int DebounceSeconds,
    int ReconciliationMinutes,
    int SnapshotMinutes,
    bool SkipUnchangedSnapshots);

public sealed record TransferSampleDto(
    DateTimeOffset TimestampUtc,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    double FilesPerSecond,
    int QueueLength);

public sealed record BackupSessionDto(
    Guid Id,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    string Status,
    long FilesDiscovered,
    long FilesProcessed,
    long FilesUploaded,
    long FilesSkipped,
    long FilesFailed,
    long NewFiles,
    long ChangedFiles,
    long DeletedFiles,
    long BytesLogical,
    long BytesHashed,
    long BytesUploaded,
    long BytesDeduplicated,
    double AverageUploadBytesPerSecond,
    double PeakUploadBytesPerSecond);

public sealed record LiveMetricsDto(
    string Phase,
    double CurrentUploadBytesPerSecond,
    double AverageUploadBytesPerSecond,
    double PeakUploadBytesPerSecond,
    double CurrentDownloadBytesPerSecond,
    double AverageDownloadBytesPerSecond,
    long BytesUploaded,
    long BytesDownloaded,
    long BytesProcessed,
    long BytesTotal,
    long FilesProcessed,
    long FilesTotal,
    double FilesPerSecond,
    int? EstimatedSecondsRemaining,
    int SessionDurationSeconds,
    IReadOnlyCollection<TransferSampleDto> Samples);

public sealed record ServiceStatusDto(
    string State,
    bool ServerOnline,
    string? ServerUrl,
    int PendingFiles,
    int FailedFiles,
    long ProtectedFiles,
    long ProtectedBytes,
    DateTimeOffset? LastSuccessfulBackupUtc,
    string? CurrentFile,
    double Progress,
    DateTimeOffset? LastReconciliationUtc,
    DateTimeOffset? NextReconciliationUtc,
    DateTimeOffset? NextSnapshotUtc,
    ProtectionSettingsDto Settings,
    LiveMetricsDto Metrics,
    IReadOnlyCollection<BackupSessionDto> RecentSessions,
    IReadOnlyCollection<BackupFolderDto> Folders,
    IReadOnlyCollection<SnapshotResponse> Snapshots,
    IReadOnlyCollection<ActivityDto> Activities);

public sealed record ConfigureServerCommand(string ServerUrl, string DeviceName);
public sealed record AddFolderCommand(string Path);
public sealed record RemoveFolderCommand(Guid Id);
public sealed record RestoreFileCommand(Guid SnapshotId, string RelativePath);
public sealed record RestoreFileResult(string StagedPath);
public sealed record SnapshotDetailsCommand(Guid SnapshotId);
public sealed record UpdateProtectionSettingsCommand(ProtectionSettingsDto Settings);

public sealed record IpcRequest(string Command, JsonElement? Payload = null);
public sealed record IpcResponse(bool Success, JsonElement? Data = null, string? Error = null)
{
    public static IpcResponse Ok<T>(T value) => new(true, JsonSerializer.SerializeToElement(value, VaultixProtocol.Json));
    public static IpcResponse Ok() => new(true);
    public static IpcResponse Fail(string error) => new(false, Error: error);
}
