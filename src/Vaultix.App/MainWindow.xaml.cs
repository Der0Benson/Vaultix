using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Vaultix.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, IDisposable
{
    private readonly ViewModels.MainViewModel _viewModel;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly System.Windows.Threading.DispatcherTimer _trayTimer;
    private readonly System.Drawing.Icon _greenIcon = CreateStatusIcon(System.Drawing.Color.FromArgb(84, 214, 161));
    private readonly System.Drawing.Icon _blueIcon = CreateStatusIcon(System.Drawing.Color.FromArgb(91, 155, 255));
    private readonly System.Drawing.Icon _yellowIcon = CreateStatusIcon(System.Drawing.Color.FromArgb(244, 196, 107));
    private readonly System.Drawing.Icon _redIcon = CreateStatusIcon(System.Drawing.Color.FromArgb(240, 115, 125));
    private readonly System.Drawing.Icon _grayIcon = CreateStatusIcon(System.Drawing.Color.FromArgb(126, 137, 154));
    private bool _exitRequested;
    private bool _closeHintShown;
    private bool _disposed;

    public MainWindow(ViewModels.MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Vaultix öffnen", null, (_, _) => ShowFromTray());
        menu.Items.Add("Backup jetzt starten", null, (_, _) => viewModel.BackupNowCommand.Execute(null));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitDesktop());
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Vaultix – Schutz wird geprüft",
            Icon = _grayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trayTimer.Tick += (_, _) => UpdateTrayState();
        _trayTimer.Start();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (_exitRequested)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
        if (!_closeHintShown)
        {
            _closeHintShown = true;
            _trayIcon.ShowBalloonTip(3000, "Vaultix", "Dein PC wird vom Vaultix Service weiter geschützt.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitDesktop()
    {
        _exitRequested = true;
        Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _greenIcon.Dispose();
        _blueIcon.Dispose();
        _yellowIcon.Dispose();
        _redIcon.Dispose();
        _grayIcon.Dispose();
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }

    private void UpdateTrayState()
    {
        if (_viewModel.FailedFiles > 0)
        {
            _trayIcon.Icon = _redIcon;
            _trayIcon.Text = "Vaultix – Fehler";
        }
        else if (!_viewModel.ServerOnline)
        {
            _trayIcon.Icon = _grayIcon;
            _trayIcon.Text = "Vaultix – Server offline";
        }
        else if (_viewModel.ServiceState.Contains("wird", StringComparison.OrdinalIgnoreCase) ||
                 _viewModel.ServiceState.Contains("läuft", StringComparison.OrdinalIgnoreCase))
        {
            _trayIcon.Icon = _blueIcon;
            _trayIcon.Text = "Vaultix – Backup läuft";
        }
        else if (_viewModel.PendingFiles > 0)
        {
            _trayIcon.Icon = _yellowIcon;
            _trayIcon.Text = $"Vaultix – {_viewModel.PendingFiles:N0} Dateien warten";
        }
        else
        {
            _trayIcon.Icon = _greenIcon;
            _trayIcon.Text = "Vaultix – Alles gesichert";
        }
    }

    private static System.Drawing.Icon CreateStatusIcon(System.Drawing.Color color)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var brush = new System.Drawing.SolidBrush(color))
        using (var outline = new System.Drawing.Pen(System.Drawing.Color.FromArgb(60, 255, 255, 255), 2))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.FillEllipse(brush, 4, 4, 24, 24);
            graphics.DrawEllipse(outline, 4, 4, 24, 24);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint iconHandle);
}
