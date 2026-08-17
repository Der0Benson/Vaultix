using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vaultix.Infrastructure;

namespace Vaultix.Service;

public sealed record ServiceConfiguration(
    string? ServerUrl,
    Guid? DeviceId,
    string? ProtectedDeviceSecret,
    List<BackupFolderConfiguration> Folders)
{
    public static ServiceConfiguration Empty { get; } = new(null, null, null, []);
}

public sealed record BackupFolderConfiguration(Guid Id, string Path, bool Enabled, string Schedule, DateTimeOffset? LastSuccessfulBackupUtc);

public sealed class VaultixPaths
{
    public VaultixPaths(string? dataDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable("VAULTIX_DATA_DIR");
        DataDirectory = Path.GetFullPath(dataDirectory ?? configured ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Vaultix"));
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string ConfigurationFile => Path.Combine(DataDirectory, "service.json");
    public string QueueDatabase => Path.Combine(DataDirectory, "queue.db");
}

public sealed class ServiceConfigurationStore(VaultixPaths paths) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(paths.ConfigurationFile))
            {
                return ServiceConfiguration.Empty;
            }

            await using var stream = new FileStream(paths.ConfigurationFile, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<ServiceConfiguration>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? ServiceConfiguration.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ServiceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = paths.ConfigurationFile + ".new";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, paths.ConfigurationFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            _gate.Release();
        }
    }

    public static string ProtectSecret(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static string UnprotectSecret(string protectedSecret)
    {
        var encrypted = Convert.FromBase64String(protectedSecret);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static DeviceCredentials? GetCredentials(ServiceConfiguration configuration)
    {
        return configuration.DeviceId is { } deviceId && configuration.ProtectedDeviceSecret is { } secret
            ? new DeviceCredentials(deviceId, UnprotectSecret(secret))
            : null;
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
