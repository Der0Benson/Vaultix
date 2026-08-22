using Vaultix.Core;
using Vaultix.Infrastructure;
using Vaultix.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Vaultix Service");
builder.Services.AddSingleton<VaultixPaths>();
builder.Services.AddSingleton<ServiceConfigurationStore>();
builder.Services.AddSingleton<BackupQueueStore>();
builder.Services.AddSingleton<ServiceRuntimeState>();
builder.Services.AddSingleton<IExcludePolicy, DefaultExcludePolicy>();
builder.Services.AddSingleton<IFileScanner, FileScanner>();
builder.Services.AddSingleton<IFileHasher, Sha256FileHasher>();
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(10)
}) { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<VaultixServerClient>();
builder.Services.AddSingleton<VaultixMetricsService>();
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VaultixMetricsService>());
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<FileChangeMonitor>();
builder.Services.AddHostedService<NamedPipeIpcServer>();
builder.Services.AddHostedService<NamedPipeStatusServer>();

var host = builder.Build();
await host.RunAsync();
