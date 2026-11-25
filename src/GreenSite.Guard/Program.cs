using System.Diagnostics;
using System.Runtime.InteropServices;
using GreenSite.Core.Core;

namespace GreenSite.Guard;

/// <summary>
/// 独立守护程序（零 NuGet、无驱动、无注入、无隐藏启动）。
/// --watchdog：用户会话内的托盘守护进程（无服务时的降级路径，托盘无退出菜单）；
/// 无参数：由 sc.exe 以 LocalSystem 服务启动，在活动用户会话拉起主程序（v1.1.2 唯一高权限守护）。
/// 两者只做「检测心跳 → 超时拉起主程序」，不结束任何进程。
/// </summary>
internal static class Program
{
    private static string _dataDir = WatchdogService.DefaultDirectory;
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "GreenSite.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a == WatchdogService.WatchdogArgument))
        {
            _dataDir = WatchdogService.DefaultDirectory;
            RunUserWatchdog();
            return;
        }

        // 服务：必须通过 SCM 分发器启动；服务主函数内再读注册表参数。
        RunService();
    }

    private static string? ReadServiceUserDataDir()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\GreenSiteGuard\Parameters");
            return key?.GetValue(WatchdogService.ServiceUserDataParam) as string;
        }
        catch { return null; }
    }

    private static void Tick(bool isService)
    {
        WatchdogService.WriteWatchdogHeartbeat(_dataDir);

        // v1.1.2：设置页关闭守护后写标记；守护读到即停止守护并（用户级）自行退出。
        if (WatchdogService.IsWatchdogDisabled(_dataDir))
        {
            if (!isService) RequestGuardExit();
            return;
        }

        if (WatchdogService.IsMainAlive(_dataDir, WatchdogService.DefaultMainTimeout)) return;
        if (!File.Exists(ExePath)) return;

        if (isService)
        {
            // 服务在 Session 0：必须借用户 token 在活动会话拉起，否则 GUI 在错误会话显示「未响应」。
            LaunchMainInUserSession();
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(ExePath)
                {
                    Arguments = "--minimized",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "",
                });
            }
            catch { /* 拉起失败下一次再试 */ }
        }
    }

    // ── 用户会话守护进程（托盘可见，无退出菜单） ──────────────
    private static void RunUserWatchdog()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        using var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(
                Path.Combine(AppContext.BaseDirectory, "assets", "app.ico")),
            Text = "GreenSite 守护进程（运行中）",
            Visible = true,
        };
        // v1.1.2：托盘不再提供退出菜单，避免普通用户结束守护；关闭走设置页 + 密码。
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "守护进程运行中（关闭请在主程序设置页操作）") { Enabled = false });
        tray.ContextMenuStrip = menu;

        using var timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        timer.Tick += (_, _) => Tick(false);
        Tick(false);
        timer.Start();
        System.Windows.Forms.Application.Run();
        timer.Stop();
    }

    private static void RequestGuardExit() => System.Windows.Forms.Application.ExitThread();

    // ── Windows 服务（最小 P/Invoke 实现，零 NuGet） ───────────
    private static readonly ManualResetEvent StopEvent = new(false);
    private static IntPtr _statusHandle;
    private static readonly HandlerEx Handler = ServiceHandler;
    private static Thread? _worker;

    private static void RunService()
    {
        // v1.1.3：SCM 要求进程调用 StartServiceCtrlDispatcher 才能建立控制连接；
        // 旧实现直接 RegisterServiceCtrlHandlerEx，SCM 等不到连接 → 事件 7009 / 错误 1053。
        // 委托必须作为静态字段持有，否则被 GC 回收后函数指针失效。
        _serviceMain = ServiceMain;
        var table = new[]
        {
            new ServiceTableEntry
            {
                ServiceName = Marshal.StringToHGlobalUni("GreenSiteGuard"),
                ServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMain),
            },
            new ServiceTableEntry(), // 终止项：两个指针均为零
        };
        StartServiceCtrlDispatcher(table);
    }

    private static ServiceMainDelegate? _serviceMain;

    private static void ServiceMain(int argc, IntPtr argv)
    {
        // 服务主函数运行在 SCM 分发的线程上：先注册控制处理器，再报 StartPending。
        _statusHandle = RegisterServiceCtrlHandlerEx("GreenSiteGuard", Handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;

        SetState(ServiceState.StartPending);
        _dataDir = ReadServiceUserDataDir() ?? WatchdogService.DefaultDirectory;

        _worker = new Thread(() =>
        {
            SetState(ServiceState.Running);
            while (!StopEvent.WaitOne(15_000)) Tick(true);
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

    // ── 跨会话拉起主程序（WTS → DuplicateTokenEx → CreateEnvironmentBlock → CreateProcessAsUser）──
    private static void LaunchMainInUserSession()
    {
        IntPtr userToken = IntPtr.Zero, primaryToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            var session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return;
            if (!WTSQueryUserToken(session, out userToken)) return;
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primaryToken)) return;
            if (!CreateEnvironmentBlock(out env, primaryToken, false)) return;

            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
            if (!CreateProcessAsUser(primaryToken, null, $"\"{ExePath}\" --minimized",
                    IntPtr.Zero, IntPtr.Zero, false, 0, env,
                    Path.GetDirectoryName(ExePath), ref si, out var pi)) return;
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        catch { /* 跨会话拉起失败下一次再试 */ }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public IntPtr ServiceName; // LPWSTR
        public IntPtr ServiceProc; // LPTHREAD_START_ROUTINE
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? application, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerEx handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
