using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Vaultix.Core;
using Vaultix.Shared;

namespace Vaultix.Service;

public sealed class VaultixMetricsService(BackupQueueStore store, ILogger<VaultixMetricsService> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Queue<TransferSampleDto> _samples = new();
    private readonly ExponentiallyWeightedMovingAverage _uploadEwma = new(0.25);
    private readonly ExponentiallyWeightedMovingAverage _downloadEwma = new(0.25);
    private readonly ExponentiallyWeightedMovingAverage _processingEwma = new(0.2);
    private readonly StableEtaEstimator _eta = new();
    private Guid? _sessionId;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _completedUtc;
    private string _phase = "Protected";
    private long _uploadedBytes;
    private long _downloadedBytes;
    private long _processedBytes;
    private long _totalBytes;
    private long _processedFiles;
    private long _totalFiles;
    private int _queueLength;
    private double _currentUploadSpeed;
    private double _currentDownloadSpeed;
    private double _currentFilesPerSecond;
    private double _peakUploadSpeed;
    private int? _estimatedSeconds;

    public void BeginSession(Guid sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _startedUtc = DateTimeOffset.UtcNow;
            _completedUtc = null;
            _phase = "Scanning";
            _uploadedBytes = 0;
            _downloadedBytes = 0;
            _processedBytes = 0;
            _totalBytes = 0;
            _processedFiles = 0;
            _totalFiles = 0;
            _currentUploadSpeed = 0;
            _currentDownloadSpeed = 0;
            _currentFilesPerSecond = 0;
            _peakUploadSpeed = 0;
            _estimatedSeconds = null;
            _uploadEwma.Reset();
            _downloadEwma.Reset();
            _processingEwma.Reset();
            _eta.Reset();
        }
    }

    public void ResumeSession(BackupSessionDto session)
    {
        lock (_gate)
        {
            if (_sessionId == session.Id)
            {
                return;
            }

            _sessionId = session.Id;
            _startedUtc = session.StartedUtc;
            _completedUtc = session.CompletedUtc;
            _phase = session.Status == "Scanning" ? "Scanning" : "BackingUp";
            _uploadedBytes = session.BytesUploaded;
            _processedBytes = Math.Min(session.BytesLogical, session.BytesDeduplicated + session.BytesUploaded);
            _totalBytes = session.BytesLogical;
            _processedFiles = session.FilesProcessed;
            _totalFiles = session.FilesDiscovered;
            _peakUploadSpeed = session.PeakUploadBytesPerSecond;
            _estimatedSeconds = null;
            _uploadEwma.Reset();
            _processingEwma.Reset();
            _eta.Reset();
        }
    }

    public void DiscoverFile(long bytes, bool alreadyProcessed)
    {
        lock (_gate)
        {
            _totalFiles++;
            _totalBytes = checked(_totalBytes + bytes);
            if (alreadyProcessed)
            {
                _processedFiles++;
                _processedBytes = checked(_processedBytes + bytes);
            }
        }
    }

    public void CompleteScan() => SetPhase("BackingUp");
    public void BeginHashing() => SetPhase("Hashing");
    public void BeginUploading() => SetPhase("Uploading");
    public void BeginFinalizing() => SetPhase("Finalizing");

    public void ReportUploaded(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _uploadedBytes, bytes);
    }

    public void ReportDownloaded(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _downloadedBytes, bytes);
    }

    public void CompleteFile(long logicalBytes)
    {
        if (logicalBytes <= 0)
        {
            Interlocked.Increment(ref _processedFiles);
            return;
        }

        Interlocked.Add(ref _processedBytes, logicalBytes);
        Interlocked.Increment(ref _processedFiles);
    }

    public void SetQueueLength(int queueLength) => Volatile.Write(ref _queueLength, Math.Max(0, queueLength));

    public void BeginRestore(long totalBytes)
    {
        BeginSession(Guid.NewGuid());
        lock (_gate)
        {
            _phase = "Restoring";
            _totalBytes = Math.Max(0, totalBytes);
            _totalFiles = 1;
        }
    }

    public void CompleteSession()
    {
        lock (_gate)
        {
            _phase = "Protected";
            _completedUtc = DateTimeOffset.UtcNow;
            _processedBytes = Math.Max(_processedBytes, _totalBytes);
            _processedFiles = Math.Max(_processedFiles, _totalFiles);
            _estimatedSeconds = 0;
            _queueLength = 0;
        }
    }

    public (double AverageUploadSpeed, double PeakUploadSpeed) GetCompletionSpeeds()
    {
        lock (_gate)
        {
            return (CalculateAverageUploadSpeed(), _peakUploadSpeed);
        }
    }

    public LiveMetricsDto GetSnapshot()
    {
        lock (_gate)
        {
            var duration = _startedUtc is null ? 0 : Math.Max(0, (int)((_completedUtc ?? DateTimeOffset.UtcNow) - _startedUtc.Value).TotalSeconds);
            return new LiveMetricsDto(
                _phase,
                _currentUploadSpeed,
                CalculateAverageUploadSpeed(),
                _peakUploadSpeed,
                _currentDownloadSpeed,
                CalculateAverageDownloadSpeed(),
                Interlocked.Read(ref _uploadedBytes),
                Interlocked.Read(ref _downloadedBytes),
                Interlocked.Read(ref _processedBytes),
                Interlocked.Read(ref _totalBytes),
                Interlocked.Read(ref _processedFiles),
                Interlocked.Read(ref _totalFiles),
                _currentFilesPerSecond,
                _estimatedSeconds,
                duration,
                _samples.ToArray());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var previousTimestamp = Stopwatch.GetTimestamp();
        long previousUploaded = 0;
        long previousDownloaded = 0;
        long previousProcessed = 0;
        long previousFiles = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds;
            previousTimestamp = timestamp;
            if (elapsed <= 0)
            {
                continue;
            }

            var uploaded = Interlocked.Read(ref _uploadedBytes);
            var downloaded = Interlocked.Read(ref _downloadedBytes);
            var processed = Interlocked.Read(ref _processedBytes);
            var files = Interlocked.Read(ref _processedFiles);
            var uploadedDelta = Math.Max(0, uploaded - previousUploaded);
            var downloadedDelta = Math.Max(0, downloaded - previousDownloaded);
            var processedDelta = Math.Max(0, processed - previousProcessed);
            var fileDelta = Math.Max(0, files - previousFiles);
            previousUploaded = uploaded;
            previousDownloaded = downloaded;
            previousProcessed = processed;
            previousFiles = files;

            TransferSampleDto sample;
            lock (_gate)
            {
                var rawUpload = uploadedDelta / elapsed;
                var rawDownload = downloadedDelta / elapsed;
                var rawProcessing = processedDelta / elapsed;
                _currentUploadSpeed = rawUpload > 0 ? _uploadEwma.Add(rawUpload) : 0;
                _currentDownloadSpeed = rawDownload > 0 ? _downloadEwma.Add(rawDownload) : 0;
                var processingSpeed = rawProcessing > 0 ? _processingEwma.Add(rawProcessing) : 0;
                _currentFilesPerSecond = fileDelta / elapsed;
                _peakUploadSpeed = Math.Max(_peakUploadSpeed, _currentUploadSpeed);
                var remaining = Math.Max(0, _totalBytes - processed);
                var eta = _phase is "Protected" or "Scanning" || processingSpeed <= 0
                    ? null
                    : _eta.Estimate(remaining, processingSpeed);
                _estimatedSeconds = eta is null ? null : Math.Max(0, (int)Math.Round(eta.Value.TotalSeconds));
                sample = new TransferSampleDto(DateTimeOffset.UtcNow, _currentUploadSpeed, _currentDownloadSpeed, _currentFilesPerSecond, _queueLength);
                _samples.Enqueue(sample);
                while (_samples.Count > 300)
                {
                    _samples.Dequeue();
                }
            }

            try
            {
                await store.RecordMetricSampleAsync(sample, uploadedDelta, downloadedDelta, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException)
            {
                logger.LogWarning(exception, "Could not persist the current metrics aggregate");
            }
        }
    }

    private void SetPhase(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }
    }

    private double CalculateAverageUploadSpeed()
    {
        if (_startedUtc is null)
        {
            return 0;
        }

        var seconds = Math.Max(0.001, ((_completedUtc ?? DateTimeOffset.UtcNow) - _startedUtc.Value).TotalSeconds);
        return Interlocked.Read(ref _uploadedBytes) / seconds;
    }

    private double CalculateAverageDownloadSpeed()
    {
        if (_startedUtc is null)
        {
            return 0;
        }

        var seconds = Math.Max(0.001, ((_completedUtc ?? DateTimeOffset.UtcNow) - _startedUtc.Value).TotalSeconds);
        return Interlocked.Read(ref _downloadedBytes) / seconds;
    }
}
