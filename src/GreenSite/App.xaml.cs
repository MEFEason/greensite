using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GreenSite.Services;

namespace GreenSite;

/// <summary>
/// 应用入口。负责单实例、冷启动初始化与进程生命周期。
/// 同时启用 WPF 与 WinForms，Application / MessageBox / Timer 等歧义类型一律完全限定。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Global\GreenSite.SingleInstance";
    private const string ShowEventName = @"Global\GreenSite.ShowWindow";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static volatile bool _listening;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 单实例：命名 Mutex，第二个实例激活已有窗口后退出 ──
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set(); // 请求已有实例显示主窗口
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 已有实例尚未创建事件，忽略
            }
            Shutdown();
            return;
        }

        _mutex = mutex;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _listening = true;
        _ = Task.Run(ListenShowRequests);

        AppState.Instance.Initialize();

        var window = new MainWindow();
        MainWindow = window;

        if (e.Args is not null && System.Array.Exists(e.Args, a => a == "--minimized"))
        {
            // 开机自启：主窗口先隐藏到托盘，用户点击托盘打开后解锁
            window.StartMinimized();
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>监听「显示主窗口」请求，切回 UI 线程激活窗口。</summary>
    private void ListenShowRequests()
    {
        while (_listening)
        {
            try { _showEvent!.WaitOne(); }
            catch (ObjectDisposedException) { break; }
            if (!_listening) break;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var window = MainWindow;
                if (window is null) return;
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listening = false;
        try { _showEvent?.Set(); } catch { /* 退出路径 */ }
        _showEvent?.Dispose();
        _showEvent = null;
        try { _mutex?.ReleaseMutex(); } catch { /* 未拥有时不释放 */ }
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }
}
