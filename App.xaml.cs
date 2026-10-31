using System.Windows;
using OfficeTouchBar.Infrastructure;

namespace OfficeTouchBar;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private MainController? _controller;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _singleInstanceMutex = new Mutex(false, "OfficeTouchBar.SingleInstance");
            try { _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsSingleInstanceMutex = true; }
            if (!_ownsSingleInstanceMutex)
            {
                if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase)) System.Windows.MessageBox.Show(
                    "OfficeTouchBar 已在后台运行。\n\n请查看系统托盘区域的图标，或双击托盘图标打开设置窗口。",
                    "OfficeTouchBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
            _controller = new MainController();
            _controller.Start(e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
            DiagnosticLog.Write($"application started version={typeof(App).Assembly.GetName().Version} path={Environment.ProcessPath}");
            SessionEnding += (_, _) => Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"startup failed: {ex}");
            System.Windows.MessageBox.Show(
                $"OfficeTouchBar 启动失败：\n{ex.Message}\n\n详细信息已写入 %APPDATA%\\OfficeTouchBar\\diagnostic.log。",
                "OfficeTouchBar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        DiagnosticLog.Write("application exiting");
        DiagnosticLog.FlushAsync().GetAwaiter().GetResult();
        if (_ownsSingleInstanceMutex && _singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
