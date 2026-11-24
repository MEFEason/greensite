using System.Diagnostics;
using System.Runtime.InteropServices;
using GreenSite.Core.Core;
using GreenSite.Core.Security;

namespace GreenSite.Guard;

/// <summary>
/// 独立守护进程 / 服务（零 NuGet、无驱动、无注入、无隐藏启动）。
/// --watchdog：用户会话内以托盘图标运行的守护进程；
/// --watchdog --elevated-watchdog：v1.1.1 管理员守护（UAC 提权，普通用户无法结束）；
/// 无参数：由 sc.exe 以 Windows 服务方式启动（最小 ServiceBase 实现）。
/// 三者都只做「检测心跳 → 超时拉起主程序」，不结束任何进程；
/// 管理员守护拉起主程序时经 explorer.exe 降权，不让主程序继承管理员权限。
/// </summary>
internal static class Program
{
    private static readonly string AppData = WatchdogService.DefaultDirectory;
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "GreenSite.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a == WatchdogService.WatchdogArgument))
        {
            // v1.1.1：同一用户会话内经 UAC 提权运行的守护，只多写一份心跳。
            var elevated = args.Any(a => a == WatchdogService.ElevatedWatchdogArgument);
            RunUserWatchdog(elevated);
            return;
        }

        RunService();
    }

    private static void Tick(bool elevated)
    {
        if (elevated)
        {
            WatchdogService.WriteElevatedWatchdogHeartbeat(AppData);
            var store = new OptionsStore(AppData);
            if (store.Exists && WatchdogService.ShouldElevatedWatchdogExit(store.Load()))
            {
                // 用户在设置页关闭开关后自行退出，无需再次触发 UAC。
                RequestGuardExit();
                return;
            }
        }
        else
        {
            WatchdogService.WriteWatchdogHeartbeat(AppData);
        }

        if (WatchdogService.IsMainAlive(AppData, WatchdogService.DefaultMainTimeout)) return;
        try
        {
            if (!File.Exists(ExePath)) return;
            if (elevated)
            {
                // 管理员守护只负责恢复主程序，不能让主程序继承管理员权限。
                WatchdogService.LaunchMainDeescalated(ExePath);
            }
            else
            {
                Process.Start(new ProcessStartInfo(ExePath)
                {
                    Arguments = "--minimized",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "",
                });
            }
        }
        catch { /* 拉起失败下一次再试，不上抛 */ }
    }

    private static void RequestGuardExit()
    {
        System.Windows.Forms.Application.ExitThread();
        StopEvent.Set();
    }

    // ── 用户会话守护进程（托盘可见） ───────────────────────────
    private static void RunUserWatchdog(bool elevated)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        var title = elevated ? "GreenSite 管理员守护（运行中）" : "GreenSite 守护进程（运行中）";
        using var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(
                Path.Combine(AppContext.BaseDirectory, "assets", "app.ico")),
            Text = title,
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            elevated ? "管理员守护运行中（UAC 提权，普通用户无法结束）" :
                       "守护进程运行中（关闭菜单退出守护）") { Enabled = false });
        var exit = new System.Windows.Forms.ToolStripMenuItem(
            elevated ? "退出管理员守护（需管理员权限）" : "退出守护");
        exit.Click += (_, _) => System.Windows.Forms.Application.ExitThread();
        menu.Items.Add(exit);
        tray.ContextMenuStrip = menu;

        using var timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        timer.Tick += (_, _) => Tick(elevated);
        Tick(elevated);
        timer.Start();
        System.Windows.Forms.Application.Run();
        timer.Stop();
    }

    // ── Windows 服务（最小 P/Invoke 实现，零 NuGet） ───────────
    private static readonly ManualResetEvent StopEvent = new(false);
    private static IntPtr _statusHandle;
    private static readonly HandlerEx Handler = ServiceHandler;
    private static Thread? _worker;

    private static void RunService()
    {
        _statusHandle = RegisterServiceCtrlHandlerEx("GreenSiteGuard", Handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;

        SetState(ServiceState.StartPending);
        _worker = new Thread(() =>
        {
            SetState(ServiceState.Running);
            while (!StopEvent.WaitOne(15_000)) Tick(false);
            SetState(ServiceState.Stopped);
        });
        _worker.IsBackground = true;
        _worker.Start();
        StopEvent.WaitOne();
        _worker.Join(TimeSpan.FromSeconds(5));
    }

    private static int ServiceHandler(int control, int eventType, IntPtr data, IntPtr context)
    {
        if (control is 1 or 2 or 3) // SERVICE_CONTROL_STOP / SHUTDOWN / CLOSE
        {
            SetState(ServiceState.StopPending);
            StopEvent.Set();
        }
        return 0;
    }

    private enum ServiceState { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4 }

    private static void SetState(ServiceState state)
    {
        var status = new ServiceStatus
        {
            ServiceType = 0x10, // SERVICE_WIN32_OWN_PROCESS
            CurrentState = (int)state,
            ControlsAccepted = state == ServiceState.Running ? 1 : 0, // SERVICE_ACCEPT_STOP
            Win32ExitCode = 0,
            CheckPoint = 0,
            WaitHint = state is ServiceState.StartPending or ServiceState.StopPending ? 3000 : 0,
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    private delegate int HandlerEx(int control, int eventType, IntPtr data, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName, HandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
