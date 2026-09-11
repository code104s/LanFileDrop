using System.Windows;
using System.Windows.Threading;
using LanFileDrop.Core;
using LanFileDrop.Network;

namespace LanFileDrop.Desktop;

public partial class App : System.Windows.Application
{
    private MainViewModel? _viewModel;
    private static int _dispatcherErrorShown;

    public App() => DispatcherUnhandledException += OnDispatcherUnhandledException;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanFileDrop");
            var history = new HistoryRepository(Path.Combine(dataDirectory, "history.db"));
            await history.InitializeAsync();
            var tray = new TrayService();
            var webShare = new WebShareService(history);
            _viewModel = new MainViewModel(new DiscoveryService(), new FileTransferService(history), history, tray, webShare);
            var window = new MainWindow { DataContext = _viewModel };
            MainWindow = window;
            window.Show();
            await _viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), ex.ToString()); } catch { }
            MessageBox.Show(ex.Message, "Không thể khởi động LanFileDrop", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.DisposeAsync();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "runtime-error.log"), $"[{DateTimeOffset.Now:O}]\n{e.Exception}\n\n"); } catch { }
        if (Interlocked.Exchange(ref _dispatcherErrorShown, 1) == 0)
            MessageBox.Show(e.Exception.Message, "LanFileDrop gặp lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
