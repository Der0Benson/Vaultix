using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using SkiaSharp;
using Vaultix.App.Services;
using Vaultix.Shared;

namespace Vaultix.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly VaultixIpcClient _client;
    private readonly StartupService _startup;
    private readonly VaultixUpdateService _updates;
    private readonly CancellationTokenSource _lifetime = new();
    private string _selectedPage = "Übersicht";
    private string _serviceState = "Service wird gesucht";
    private bool _serverOnline;
    private string _serverUrl = "https://192.168.178.20:7443";
    private string? _errorMessage;
    private int _pendingFiles;
    private int _failedFiles;
    private long _protectedFiles;
    private long _protectedBytes;
    private DateTimeOffset? _lastBackup;
    private DateTimeOffset? _lastReconciliation;
    private DateTimeOffset? _nextReconciliation;
    private DateTimeOffset? _nextSnapshot;
    private string? _currentFile;
    private double _progress;
    private LiveMetricsDto? _metrics;
    private DateTimeOffset? _lastChartSample;
    private SnapshotResponse? _selectedSnapshot;
    private SnapshotEntryDto? _selectedSnapshotFile;
    private BackupFolderDto? _selectedFolder;
    private string _restoreRelativePath = string.Empty;
    private bool _startupEnabled;
    private bool _continuousProtection = true;
    private int _debounceSeconds = 10;
    private int _reconciliationMinutes = 30;
    private int _snapshotMinutes = 30;
    private bool _skipUnchangedSnapshots = true;
    private bool _settingsInitialized;
    private bool _settingsDirty;
    private bool _serverUrlInitialized;
    private string _updateStatus = "Updates werden geprüft …";
    private bool _updateAvailable;

    public MainViewModel(VaultixIpcClient client, StartupService startup, VaultixUpdateService? updates = null)
    {
        _client = client;
        _startup = startup;
        _updates = updates ?? new VaultixUpdateService();
        _startupEnabled = startup.GetInitialState();
        TransferSeries =
        [
            new LineSeries<double>
            {
                Name = "Upload", Values = UploadSpeedValues, GeometrySize = 0,
                Stroke = new SolidColorPaint(new SKColor(139, 124, 255), 3),
                Fill = new LinearGradientPaint(new SKColor(139, 124, 255, 70), new SKColor(139, 124, 255, 0)),
                LineSmoothness = 0.65, AnimationsSpeed = TimeSpan.FromMilliseconds(300)
            },
            new LineSeries<double>
            {
                Name = "Download", Values = DownloadSpeedValues, GeometrySize = 0,
                Stroke = new SolidColorPaint(new SKColor(81, 211, 165), 2.5f),
                Fill = new LinearGradientPaint(new SKColor(81, 211, 165, 38), new SKColor(81, 211, 165, 0)),
                LineSmoothness = 0.65, AnimationsSpeed = TimeSpan.FromMilliseconds(300)
            }
        ];
        BackupHistorySeries =
        [
            new ColumnSeries<double>
            {
                Name = "Logische Backupdaten", Values = BackupHistoryValues,
                Fill = new LinearGradientPaint(new SKColor(153, 140, 255), new SKColor(92, 75, 222)),
                MaxBarWidth = 22, Rx = 7, Ry = 7, AnimationsSpeed = TimeSpan.FromMilliseconds(450)
            }
        ];
        XAxes = [new Axis
        {
            MinStep = 60, ForceStepToMin = true, TextSize = 11,
            Labeler = value => FormatChartTimeLabel(UploadSpeedValues.Count, value),
            LabelsPaint = new SolidColorPaint(new SKColor(111, 124, 145)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(38, 47, 63), 1)
        }];
        YAxes = [new Axis
        {
            MinLimit = 0, TextSize = 11, Labeler = value => $"{value:0.#} MB/s",
            LabelsPaint = new SolidColorPaint(new SKColor(126, 139, 159)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(38, 47, 63), 1)
        }];
        HistoryXAxes = [new Axis { TextSize = 11, LabelsPaint = new SolidColorPaint(new SKColor(111, 124, 145)), SeparatorsPaint = null }];
        HistoryYAxes = [new Axis { MinLimit = 0, TextSize = 11, Labeler = value => FormatChartBytes(value * 1024 * 1024), LabelsPaint = new SolidColorPaint(new SKColor(126, 139, 159)), SeparatorsPaint = new SolidColorPaint(new SKColor(38, 47, 63), 1) }];
        TooltipBackgroundPaint = new SolidColorPaint(new SKColor(22, 29, 42));
        TooltipTextPaint = new SolidColorPaint(new SKColor(242, 245, 251));
        LegendTextPaint = new SolidColorPaint(new SKColor(174, 184, 200));
        NavigateCommand = new RelayCommand<string>(page => SelectedPage = page);
        BackupNowCommand = new AsyncRelayCommand(() => SendAsync("RunBackup"), ShowError);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, ShowError);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveFolderAsync, ShowError, () => SelectedFolder is not null);
        ConnectServerCommand = new AsyncRelayCommand(ConnectServerAsync, ShowError);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, ShowError, () => SelectedSnapshot is not null && !string.IsNullOrWhiteSpace(RestoreRelativePath));
        SaveProtectionSettingsCommand = new AsyncRelayCommand(SaveProtectionSettingsAsync, ShowError);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, ShowError);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, ShowError, () => UpdateAvailable);
        _ = ListenStatusAsync(_lifetime.Token);
        _ = CheckForUpdatesPeriodicallyAsync(_lifetime.Token);
    }

    public ObservableCollection<BackupFolderDto> Folders { get; } = [];
    public ObservableCollection<SnapshotResponse> Snapshots { get; } = [];
    public ObservableCollection<SnapshotEntryDto> SnapshotFiles { get; } = [];
    public ObservableCollection<ActivityDto> Activities { get; } = [];
    public ObservableCollection<BackupSessionDto> RecentSessions { get; } = [];
    public ObservableCollection<double> UploadSpeedValues { get; } = [];
    public ObservableCollection<double> DownloadSpeedValues { get; } = [];
    public ObservableCollection<double> BackupHistoryValues { get; } = [];
    public ISeries[] TransferSeries { get; }
    public ISeries[] BackupHistorySeries { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }
    public Axis[] HistoryXAxes { get; }
    public Axis[] HistoryYAxes { get; }
    public SolidColorPaint TooltipBackgroundPaint { get; }
    public SolidColorPaint TooltipTextPaint { get; }
    public SolidColorPaint LegendTextPaint { get; }
    public IReadOnlyList<int> DebounceOptions { get; } = [5, 10, 30, 60];
    public IReadOnlyList<int> ReconciliationOptions { get; } = [5, 15, 30, 60, 180, 360];
    public IReadOnlyList<int> SnapshotOptions { get; } = [15, 30, 60, 180, 360, 720, 1440];
    public ICommand NavigateCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand ConnectServerCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand SaveProtectionSettingsCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    public string SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }
    public string ServiceState { get => _serviceState; private set { if (Set(ref _serviceState, value)) Notify(nameof(HeroTitle)); } }
    public bool ServerOnline { get => _serverOnline; private set { if (Set(ref _serverOnline, value)) { Notify(nameof(ServerLabel)); Notify(nameof(HealthLabel)); Notify(nameof(GraphStatusLabel)); Notify(nameof(GraphBadgeLabel)); Notify(nameof(PhaseLabel)); } } }
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public int PendingFiles { get => _pendingFiles; private set { if (Set(ref _pendingFiles, value)) { Notify(nameof(PendingLabel)); Notify(nameof(HealthLabel)); Notify(nameof(HeroTitle)); } } }
    public int FailedFiles { get => _failedFiles; private set { if (Set(ref _failedFiles, value)) { Notify(nameof(HealthLabel)); Notify(nameof(HasFailures)); } } }
    public long ProtectedFiles { get => _protectedFiles; private set { if (Set(ref _protectedFiles, value)) Notify(nameof(ProtectedFilesLabel)); } }
    public long ProtectedBytes { get => _protectedBytes; private set { if (Set(ref _protectedBytes, value)) Notify(nameof(ProtectedBytesLabel)); } }
    public DateTimeOffset? LastBackup { get => _lastBackup; private set { if (Set(ref _lastBackup, value)) Notify(nameof(LastBackupLabel)); } }
    public string? CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Notify(nameof(ProgressLabel)); } }
    public bool ContinuousProtection { get => _continuousProtection; set { if (Set(ref _continuousProtection, value)) _settingsDirty = true; } }
    public int DebounceSeconds { get => _debounceSeconds; set { if (Set(ref _debounceSeconds, value)) _settingsDirty = true; } }
    public int ReconciliationMinutes { get => _reconciliationMinutes; set { if (Set(ref _reconciliationMinutes, value)) _settingsDirty = true; } }
    public int SnapshotMinutes { get => _snapshotMinutes; set { if (Set(ref _snapshotMinutes, value)) _settingsDirty = true; } }
    public bool SkipUnchangedSnapshots { get => _skipUnchangedSnapshots; set { if (Set(ref _skipUnchangedSnapshots, value)) _settingsDirty = true; } }
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool UpdateAvailable { get => _updateAvailable; private set { if (Set(ref _updateAvailable, value)) ((AsyncRelayCommand)InstallUpdateCommand).RaiseCanExecuteChanged(); } }

    public string HeroTitle => FailedFiles > 0 ? "Backup braucht Aufmerksamkeit" : PendingFiles > 0 ? $"{PendingFiles:N0} Dateien warten" : ServiceState == "Alles gesichert" ? "Dein PC ist geschützt" : ServiceState;
    public string ServerLabel => ServerOnline ? "Online" : "Offline";
    public string HealthLabel => FailedFiles > 0 ? "Kritisch" : !ServerOnline ? "Offline" : PendingFiles > 0 ? "Gut" : "Ausgezeichnet";
    public bool HasFailures => FailedFiles > 0;
    public string ProtectedFilesLabel => $"{ProtectedFiles:N0}";
    public string ProtectedBytesLabel => FormatBytes(ProtectedBytes);
    public string PendingLabel => $"{PendingFiles:N0}";
    public string LastBackupLabel => LastBackup is null ? "Noch kein Backup" : LastBackup.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string ProgressLabel => $"{Progress:P0}";
    public string UploadSpeedLabel => FormatRate(_metrics?.CurrentUploadBytesPerSecond ?? 0);
    public string AverageUploadLabel => FormatRate(_metrics?.AverageUploadBytesPerSecond ?? 0);
    public string PeakUploadLabel => FormatRate(_metrics?.PeakUploadBytesPerSecond ?? 0);
    public string DownloadSpeedLabel => FormatRate(_metrics?.CurrentDownloadBytesPerSecond ?? 0);
    public string FileProgressLabel => $"{_metrics?.FilesProcessed ?? 0:N0} / {_metrics?.FilesTotal ?? 0:N0} Dateien";
    public string ByteProgressLabel => $"{FormatBytes(_metrics?.BytesProcessed ?? 0)} / {FormatBytes(_metrics?.BytesTotal ?? 0)}";
    public string EtaLabel => _metrics?.Phase == "Protected" ? "—" : _metrics?.EstimatedSecondsRemaining is { } seconds ? FormatDuration(seconds) : "wird berechnet";
    public string SessionDurationLabel => FormatDuration(_metrics?.SessionDurationSeconds ?? 0);
    public string NextReconciliationLabel => FormatScheduled(_nextReconciliation);
    public string NextSnapshotLabel => FormatScheduled(_nextSnapshot);
    public string PhaseLabel => !ServerOnline ? "Storage offline" : (_metrics?.Phase ?? "Protected") switch
    {
        "Scanning" => "Analyse",
        "Hashing" => "Hashing",
        "Uploading" => "Upload läuft",
        "Restoring" => "Restore läuft",
        "Finalizing" => "Snapshot wird finalisiert",
        "BackingUp" => "Änderungen werden verarbeitet",
        _ => "Live-Monitoring aktiv"
    };
    public string GraphStatusLabel => !ServerOnline
        ? "Storage offline · Änderungen bleiben sicher in der Queue"
        : UploadSpeedValues.Count == 0
            ? "Noch keine Transferdaten vorhanden"
            : _metrics?.Phase == "Protected" ? "Keine aktive Übertragung · letzter Verlauf" : string.Empty;
    public string GraphBadgeLabel => ServerOnline ? "LIVE" : "PAUSIERT";
    public string FilesPerSecondLabel => (_metrics?.FilesPerSecond ?? 0) <= 0.05 ? "—" : $"{_metrics!.FilesPerSecond:N1} Dateien/s";
    public string SessionTrafficLabel => FormatBytes((_metrics?.BytesUploaded ?? 0) + (_metrics?.BytesDownloaded ?? 0));
    public string DeduplicatedLabel => FormatBytes(RecentSessions.Sum(session => session.BytesDeduplicated));
    public string UploadedHistoryLabel => FormatBytes(RecentSessions.Sum(session => session.BytesUploaded));
    public double DedupeRatio
    {
        get
        {
            var deduplicated = RecentSessions.Sum(session => session.BytesDeduplicated);
            var total = deduplicated + RecentSessions.Sum(session => session.BytesUploaded);
            return total == 0 ? 0 : (double)deduplicated / total;
        }
    }
    public string DedupeRatioLabel
    {
        get
        {
            var deduplicated = RecentSessions.Sum(session => session.BytesDeduplicated);
            var total = deduplicated + RecentSessions.Sum(session => session.BytesUploaded);
            return total == 0 ? "0 %" : $"{(double)deduplicated / total:P1}";
        }
    }

    public SnapshotResponse? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (!Set(ref _selectedSnapshot, value)) return;
            ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged();
            SnapshotFiles.Clear();
            if (value is not null) _ = LoadSnapshotAsync(value.Id);
        }
    }

    public SnapshotEntryDto? SelectedSnapshotFile
    {
        get => _selectedSnapshotFile;
        set
        {
            if (!Set(ref _selectedSnapshotFile, value)) return;
            RestoreRelativePath = value?.RelativePath ?? string.Empty;
            ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged();
        }
    }

    public BackupFolderDto? SelectedFolder
    {
        get => _selectedFolder;
        set { if (Set(ref _selectedFolder, value)) ((AsyncRelayCommand)RemoveFolderCommand).RaiseCanExecuteChanged(); }
    }

    public string RestoreRelativePath
    {
        get => _restoreRelativePath;
        set { if (Set(ref _restoreRelativePath, value)) ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged(); }
    }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set { if (Set(ref _startupEnabled, value)) _startup.SetEnabled(value); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task ListenStatusAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var status in _client.StreamStatusAsync(cancellationToken).ConfigureAwait(false))
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Apply(status));
                }
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ServiceState = "Vaultix Service nicht erreichbar";
                    ServerOnline = false;
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Apply(ServiceStatusDto status)
    {
        ServiceState = status.State;
        ServerOnline = status.ServerOnline;
        if (!_serverUrlInitialized && !string.IsNullOrWhiteSpace(status.ServerUrl))
        {
            ServerUrl = status.ServerUrl;
            _serverUrlInitialized = true;
        }
        PendingFiles = status.PendingFiles;
        FailedFiles = status.FailedFiles;
        ProtectedFiles = status.ProtectedFiles;
        ProtectedBytes = status.ProtectedBytes;
        LastBackup = status.LastSuccessfulBackupUtc;
        _lastReconciliation = status.LastReconciliationUtc;
        _nextReconciliation = status.NextReconciliationUtc;
        _nextSnapshot = status.NextSnapshotUtc;
        CurrentFile = status.CurrentFile;
        Progress = status.Progress;
        _metrics = status.Metrics;
        UpdateChart(status.Metrics.Samples);
        NotifyMetricProperties();
        ReplaceIfChanged(Folders, status.Folders);
        ReplaceIfChanged(Snapshots, status.Snapshots);
        ReplaceIfChanged(Activities, status.Activities);
        ReplaceIfChanged(RecentSessions, status.RecentSessions);
        UpdateHistory(status.RecentSessions);
        Notify(nameof(DeduplicatedLabel));
        Notify(nameof(UploadedHistoryLabel));
        Notify(nameof(DedupeRatioLabel));
        Notify(nameof(DedupeRatio));
        if (!_settingsDirty || !_settingsInitialized) ApplySettings(status.Settings);
        ErrorMessage = null;
    }

    private void ApplySettings(ProtectionSettingsDto settings)
    {
        _continuousProtection = settings.ContinuousProtection;
        _debounceSeconds = settings.DebounceSeconds;
        _reconciliationMinutes = settings.ReconciliationMinutes;
        _snapshotMinutes = settings.SnapshotMinutes;
        _skipUnchangedSnapshots = settings.SkipUnchangedSnapshots;
        _settingsInitialized = true;
        _settingsDirty = false;
        Notify(nameof(ContinuousProtection));
        Notify(nameof(DebounceSeconds));
        Notify(nameof(ReconciliationMinutes));
        Notify(nameof(SnapshotMinutes));
        Notify(nameof(SkipUnchangedSnapshots));
    }

    private void UpdateChart(IEnumerable<TransferSampleDto> samples)
    {
        foreach (var sample in samples.Where(item => _lastChartSample is null || item.TimestampUtc > _lastChartSample).OrderBy(item => item.TimestampUtc))
        {
            UploadSpeedValues.Add(sample.UploadBytesPerSecond / 1024d / 1024d);
            DownloadSpeedValues.Add(sample.DownloadBytesPerSecond / 1024d / 1024d);
            _lastChartSample = sample.TimestampUtc;
        }
        while (UploadSpeedValues.Count > 300) UploadSpeedValues.RemoveAt(0);
        while (DownloadSpeedValues.Count > 300) DownloadSpeedValues.RemoveAt(0);
    }

    private void UpdateHistory(IEnumerable<BackupSessionDto> sessions)
    {
        var completed = sessions.Where(session => session.CompletedUtc is not null).Take(12).Reverse().ToArray();
        var values = completed.Select(session => session.BytesLogical / 1024d / 1024d).ToArray();
        HistoryXAxes[0].Labels = completed.Select(session => session.StartedUtc.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture)).ToArray();
        if (BackupHistoryValues.SequenceEqual(values)) return;
        BackupHistoryValues.Clear();
        foreach (var value in values) BackupHistoryValues.Add(value);
    }

    private void NotifyMetricProperties()
    {
        foreach (var property in new[] { nameof(UploadSpeedLabel), nameof(AverageUploadLabel), nameof(PeakUploadLabel), nameof(DownloadSpeedLabel), nameof(FileProgressLabel), nameof(ByteProgressLabel), nameof(EtaLabel), nameof(SessionDurationLabel), nameof(NextReconciliationLabel), nameof(NextSnapshotLabel), nameof(PhaseLabel), nameof(GraphStatusLabel), nameof(GraphBadgeLabel), nameof(FilesPerSecondLabel), nameof(SessionTrafficLabel) }) Notify(property);
    }

    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Ordner mit Vaultix schützen", Multiselect = false };
        if (dialog.ShowDialog() == true) await SendAsync("AddFolder", new AddFolderCommand(dialog.FolderName));
    }

    private Task RemoveFolderAsync() => SelectedFolder is null ? Task.CompletedTask : SendAsync("RemoveFolder", new RemoveFolderCommand(SelectedFolder.Id));
    private Task ConnectServerAsync() => SendAsync("ConfigureServer", new ConfigureServerCommand(ServerUrl, Environment.MachineName));

    private async Task SaveProtectionSettingsAsync()
    {
        await SendAsync("UpdateProtectionSettings", new UpdateProtectionSettingsCommand(new ProtectionSettingsDto(
            ContinuousProtection, DebounceSeconds, ReconciliationMinutes, SnapshotMinutes, SkipUnchangedSnapshots)));
        _settingsDirty = false;
    }

    private async Task CheckForUpdatesAsync()
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => UpdateStatus = "Updates werden geprüft …");
        var result = await _updates.CheckAsync(_lifetime.Token).ConfigureAwait(false);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            UpdateStatus = result.Message;
            UpdateAvailable = result.Kind == UpdateCheckResultKind.UpdateAvailable;
        });
    }

    private async Task CheckForUpdatesPeriodicallyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await CheckForUpdatesAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { }
            await Task.Delay(TimeSpan.FromHours(6), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InstallUpdateAsync()
    {
        await _updates.StartUpdateAsync().ConfigureAwait(false);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => System.Windows.Application.Current.Shutdown());
    }

    private async Task RestoreAsync()
    {
        if (SelectedSnapshot is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(RestoreRelativePath), Title = "Vaultix-Datei wiederherstellen" };
        if (dialog.ShowDialog() != true) return;
        var result = await _client.SendAsync<RestoreFileResult>("RestoreFile", new RestoreFileCommand(SelectedSnapshot.Id, RestoreRelativePath), _lifetime.Token);
        File.Copy(result.StagedPath, dialog.FileName, overwrite: true);
    }

    private async Task LoadSnapshotAsync(Guid snapshotId)
    {
        try
        {
            var details = await _client.SendAsync<SnapshotDetailsResponse>("GetSnapshotDetails", new SnapshotDetailsCommand(snapshotId), _lifetime.Token);
            ReplaceIfChanged(SnapshotFiles, details.Entries);
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async Task SendAsync(string command, object? payload = null)
    {
        ErrorMessage = null;
        await _client.SendAsync(command, payload, _lifetime.Token);
    }

    private void ShowError(Exception exception) => ErrorMessage = exception.Message;

    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var values = source.ToArray();
        if (target.SequenceEqual(values)) return;
        target.Clear();
        foreach (var item in values) target.Add(item);
    }

    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes((long)Math.Max(0, bytesPerSecond))}/s";
    private static string FormatChartTimeLabel(int count, double value)
    {
        var secondsAgo = Math.Max(0, count - 1 - (int)Math.Round(value));
        return secondsAgo == 0 ? "Jetzt" : secondsAgo < 30 ? $"−{secondsAgo}s" : $"−{Math.Ceiling(secondsAgo / 60d):0}m";
    }
    private static string FormatChartBytes(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => $"{bytes / 1024 / 1024 / 1024:0.#} GB",
        >= 1024d * 1024 => $"{bytes / 1024 / 1024:0.#} MB",
        >= 1024d => $"{bytes / 1024:0.#} KB",
        _ => $"{bytes:0} B"
    };
    private static string FormatScheduled(DateTimeOffset? value) => value is null ? "noch nicht geplant" : value.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    private static string FormatDuration(int seconds) => seconds < 60 ? $"{seconds} Sek." : seconds < 3600 ? $"{seconds / 60} Min. {seconds % 60} Sek." : $"{seconds / 3600} Std. {(seconds % 3600) / 60} Min.";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}
