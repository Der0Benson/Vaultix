namespace Vaultix.Core;

public enum BackupQueueState
{
    Pending,
    WaitingForStableFile,
    Hashing,
    CheckingServer,
    Uploading,
    Completed,
    Failed,
    RetryScheduled
}

public sealed record FileCandidate(
    string RootPath,
    string FullPath,
    string RelativePath,
    long Size,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes);

public sealed record HashedFile(FileCandidate File, string Sha256);

public interface IFileHasher
{
    Task<string> HashAsync(string path, CancellationToken cancellationToken);
}

public interface IExcludePolicy
{
    bool IsExcluded(string rootPath, string fullPath, FileAttributes attributes);
}

public interface IFileScanner
{
    IAsyncEnumerable<FileCandidate> ScanAsync(string rootPath, CancellationToken cancellationToken);
}

public static class RetrySchedule
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    ];

    public static TimeSpan ForAttempt(int retryCount) => Delays[Math.Clamp(retryCount, 0, Delays.Length - 1)];
}
