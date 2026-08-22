using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Vaultix.App.Services;

public enum UpdateCheckResultKind { UpToDate, UpdateAvailable, Unavailable }

public sealed record UpdateCheckResult(UpdateCheckResultKind Kind, string Message);

public sealed class VaultixUpdateService
{
    private const string RepositoryApi = "https://api.github.com/repos/Der0Benson/Vaultix/commits/main";
    private static readonly string InstallationFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vaultix", "installation.json");

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var installation = await LoadInstallationAsync(cancellationToken).ConfigureAwait(false);
        if (installation is null || string.IsNullOrWhiteSpace(installation.RepositoryPath))
            return new(UpdateCheckResultKind.Unavailable, "Update-Prüfung ist erst nach einer Vaultix-Installation verfügbar.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Vaultix-Desktop-Updater");
            using var response = await client.GetAsync(RepositoryApi, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var latestCommit = document.RootElement.GetProperty("sha").GetString();
            if (string.IsNullOrWhiteSpace(latestCommit)) throw new InvalidDataException("GitHub lieferte keine Commit-ID.");

            return latestCommit.Equals(installation.InstalledCommit, StringComparison.OrdinalIgnoreCase)
                ? new(UpdateCheckResultKind.UpToDate, "Vaultix ist auf dem neuesten Stand.")
                : new(UpdateCheckResultKind.UpdateAvailable, "Ein Vaultix-Update ist verfügbar.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            return new(UpdateCheckResultKind.Unavailable, "Updates konnten gerade nicht geprüft werden.");
        }
    }

    public async Task StartUpdateAsync()
    {
        var installation = await LoadInstallationAsync(CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Keine Vaultix-Installation gefunden.");
        var updateScript = Path.Combine(installation.RepositoryPath, "deployment", "Update-Vaultix.ps1");
        if (!File.Exists(updateScript)) throw new FileNotFoundException("Das Vaultix-Update-Skript wurde nicht gefunden.", updateScript);

        var escapedScript = updateScript.Replace("'", "''");
        var escapedRepository = installation.RepositoryPath.Replace("'", "''");
        var command = "Start-Sleep -Seconds 3; & '" + escapedScript + "' -RepositoryPath '" + escapedRepository + "'";
        var startInfo = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = installation.RepositoryPath
        };
        if (Process.Start(startInfo) is null) throw new InvalidOperationException("Das Update konnte nicht gestartet werden.");
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';

    private static async Task<InstallationInfo?> LoadInstallationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(InstallationFile)) return null;
        await using var stream = File.OpenRead(InstallationFile);
        return await JsonSerializer.DeserializeAsync<InstallationInfo>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private sealed record InstallationInfo(string RepositoryPath, string InstalledCommit);
}
