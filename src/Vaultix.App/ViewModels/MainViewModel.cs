using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using Vaultix.App.Services;
using Vaultix.Shared;

namespace Vaultix.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly VaultixIpcClient _client;
    private readonly StartupService _startup;
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
    private string? _currentFile;
    private double _progress;
    private SnapshotResponse? _selectedSnapshot;
    private SnapshotEntryDto? _selectedSnapshotFile;
    private BackupFolderDto? _selectedFolder;
    private string _restoreRelativePath = string.Empty;
    private bool _startupEnabled;

    public MainViewModel(VaultixIpcClient client, StartupService startup)
    {
        _client = client;
        _startup = startup;
        _startupEnabled = startup.GetInitialState();
        NavigateCommand = new RelayCommand<string>(page => SelectedPage = page);
        BackupNowCommand = new AsyncRelayCommand(() => SendAndRefreshAsync("RunBackup"), ShowError);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, ShowError);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveFolderAsync, ShowError, () => SelectedFolder is not null);
        ConnectServerCommand = new AsyncRelayCommand(ConnectServerAsync, ShowError);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, ShowError, () => SelectedSnapshot is not null && !string.IsNullOrWhiteSpace(RestoreRelativePath));
        _ = PollAsync(_lifetime.Token);
    }

    public ObservableCollection<BackupFolderDto> Folders { get; } = [];
    public ObservableCollection<SnapshotResponse> Snapshots { get; } = [];
    public ObservableCollection<SnapshotEntryDto> SnapshotFiles { get; } = [];
    public ObservableCollection<ActivityDto> Activities { get; } = [];
    public ICommand NavigateCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand ConnectServerCommand { get; }
    public ICommand RestoreCommand { get; }

    public string SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }
    public string ServiceState { get => _serviceState; private set { if (Set(ref _serviceState, value)) Notify(nameof(HeroTitle)); } }
    public bool ServerOnline { get => _serverOnline; private set { if (Set(ref _serverOnline, value)) { Notify(nameof(ServerLabel)); Notify(nameof(HealthLabel)); } } }
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public int PendingFiles { get => _pendingFiles; private set { if (Set(ref _pendingFiles, value)) { Notify(nameof(PendingLabel)); Notify(nameof(HealthLabel)); Notify(nameof(HeroTitle)); } } }
    public int FailedFiles { get => _failedFiles; private set { if (Set(ref _failedFiles, value)) Notify(nameof(HealthLabel)); } }
    public long ProtectedFiles { get => _protectedFiles; private set { if (Set(ref _protectedFiles, value)) Notify(nameof(ProtectedFilesLabel)); } }
    public long ProtectedBytes { get => _protectedBytes; private set { if (Set(ref _protectedBytes, value)) Notify(nameof(ProtectedBytesLabel)); } }
    public DateTimeOffset? LastBackup { get => _lastBackup; private set { if (Set(ref _lastBackup, value)) Notify(nameof(LastBackupLabel)); } }
    public string? CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string HeroTitle => FailedFiles > 0 ? "Backup braucht Aufmerksamkeit" : PendingFiles > 0 ? $"{PendingFiles:N0} Dateien warten" : ServiceState == "Alles gesichert" ? "Dein PC ist geschützt" : ServiceState;
    public string ServerLabel => ServerOnline ? "Online" : "Offline";
    public string HealthLabel => FailedFiles > 0 ? "Critical" : !ServerOnline ? "Warning" : PendingFiles > 0 ? "Good" : "Excellent";
    public string ProtectedFilesLabel => $"{ProtectedFiles:N0}";
    public string ProtectedBytesLabel => FormatBytes(ProtectedBytes);
    public string PendingLabel => $"{PendingFiles:N0}";
    public string LastBackupLabel => LastBackup is null ? "Noch kein Backup" : LastBackup.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    public SnapshotResponse? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (Set(ref _selectedSnapshot, value))
            {
                ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged();
                SnapshotFiles.Clear();
                if (value is not null)
                {
                    _ = LoadSnapshotAsync(value.Id);
                }
            }
        }
    }

    public SnapshotEntryDto? SelectedSnapshotFile
    {
        get => _selectedSnapshotFile;
        set
        {
            if (Set(ref _selectedSnapshotFile, value))
            {
                RestoreRelativePath = value?.RelativePath ?? string.Empty;
                ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public BackupFolderDto? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (Set(ref _selectedFolder, value))
            {
                ((AsyncRelayCommand)RemoveFolderCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string RestoreRelativePath
    {
        get => _restoreRelativePath;
        set
        {
            if (Set(ref _restoreRelativePath, value))
            {
                ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (Set(ref _startupEnabled, value))
            {
                _startup.SetEnabled(value);
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var status = await _client.SendAsync<ServiceStatusDto>("GetStatus", cancellationToken: cancellationToken);
                Apply(status);
                ErrorMessage = null;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ServiceState = "Vaultix Service nicht erreichbar";
                ServerOnline = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private void Apply(ServiceStatusDto status)
    {
        ServiceState = status.State;
        ServerOnline = status.ServerOnline;
        if (!string.IsNullOrWhiteSpace(status.ServerUrl)) ServerUrl = status.ServerUrl;
        PendingFiles = status.PendingFiles;
        FailedFiles = status.FailedFiles;
        ProtectedFiles = status.ProtectedFiles;
        ProtectedBytes = status.ProtectedBytes;
        LastBackup = status.LastSuccessfulBackupUtc;
        CurrentFile = status.CurrentFile;
        Progress = status.Progress;
        ReplaceIfChanged(Folders, status.Folders);
        ReplaceIfChanged(Snapshots, status.Snapshots);
        ReplaceIfChanged(Activities, status.Activities);
    }

    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Ordner mit Vaultix schützen", Multiselect = false };
        if (dialog.ShowDialog() == true)
        {
            await SendAndRefreshAsync("AddFolder", new AddFolderCommand(dialog.FolderName));
        }
    }

    private async Task RemoveFolderAsync()
    {
        if (SelectedFolder is not null)
        {
            await SendAndRefreshAsync("RemoveFolder", new RemoveFolderCommand(SelectedFolder.Id));
        }
    }

    private async Task ConnectServerAsync()
    {
        await SendAndRefreshAsync("ConfigureServer", new ConfigureServerCommand(ServerUrl, Environment.MachineName));
    }

    private async Task RestoreAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(RestoreRelativePath), Title = "Vaultix-Datei wiederherstellen" };
        if (dialog.ShowDialog() == true)
        {
            var result = await _client.SendAsync<RestoreFileResult>(
                "RestoreFile", new RestoreFileCommand(SelectedSnapshot.Id, RestoreRelativePath), _lifetime.Token);
            File.Copy(result.StagedPath, dialog.FileName, overwrite: true);
            Apply(await _client.SendAsync<ServiceStatusDto>("GetStatus", cancellationToken: _lifetime.Token));
        }
    }

    private async Task LoadSnapshotAsync(Guid snapshotId)
    {
        try
        {
            var details = await _client.SendAsync<SnapshotDetailsResponse>(
                "GetSnapshotDetails", new SnapshotDetailsCommand(snapshotId), _lifetime.Token);
            ReplaceIfChanged(SnapshotFiles, details.Entries);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task SendAndRefreshAsync(string command, object? payload = null)
    {
        ErrorMessage = null;
        await _client.SendAsync(command, payload, _lifetime.Token);
        await Task.Delay(250, _lifetime.Token);
        Apply(await _client.SendAsync<ServiceStatusDto>("GetStatus", cancellationToken: _lifetime.Token));
    }

    private void ShowError(Exception exception) => ErrorMessage = exception.Message;

    private static void ReplaceIfChanged<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        var values = source.ToArray();
        if (target.SequenceEqual(values))
        {
            return;
        }

        target.Clear();
        foreach (var item in values) target.Add(item);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}
