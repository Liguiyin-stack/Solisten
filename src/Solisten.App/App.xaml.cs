using Microsoft.UI.Xaml;
using Solisten.App.Services;

namespace Solisten.App;

public partial class App : Application
{
    private AppHost? _host;
    private MainWindow? _window;
    private MediaControls? _media;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => _host?.Log.Write($"unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost();
        UiDispatch.Log = _host.Log;
        _window = new MainWindow(_host);
        _window.Activate();
        try { _media = new MediaControls(_host); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or TypeInitializationException)
        {
            _host.Log.Write($"smtc unavailable: {e.Message}");
        }
        _window.Closed += (_, _) => _media?.Dispose();
        // 先显示聆听画布，再恢复会话与校验扫描。
        var paths = Program.OpenablePaths(Environment.GetCommandLineArgs().Skip(1));
        var window = _window;
        var host = _host;
        window.DispatcherQueue.Run(() =>
        {
            host.Start(paths);
            // 另一个实例转发来的路径：窗口回到前台，再打开路径。
            Program.OnPathsReceived(forwarded => window.DispatcherQueue.Run(() =>
            {
                window.BringToFront();
                if (forwarded.Count > 0) _ = host.OpenPathsAsync(forwarded);
            }));
        }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
    }
}
