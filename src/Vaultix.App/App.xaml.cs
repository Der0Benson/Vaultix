using System.Configuration;
using System.Data;
using System.Windows;

namespace Vaultix.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application, IDisposable
{
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var client = new Services.VaultixIpcClient();
        var viewModel = new ViewModels.MainViewModel(client, new Services.StartupService());
        _window = new MainWindow(viewModel);
        MainWindow = _window;
        _window.Show();
        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            _window.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
        GC.SuppressFinalize(this);
    }
}
