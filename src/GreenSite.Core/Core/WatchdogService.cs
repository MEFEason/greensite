using System.Diagnostics;
using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>
/// 守护协调：主进程写心跳；独立 Guard.exe（用户会话守护进程或服务）检测心跳超时后拉起主程序。
/// 用文件心跳而不是全局 Mutex——会话隔离下更可靠，且不依赖 SeDebugPrivilege 等敏感权限。
/// 不隐藏进程、不注入、不强杀；管理员主动结束无法阻止，只能自动恢复。
/// </summary>
public static class WatchdogService
{
    public const string WatchdogArgument = "--watchdog";

    /// <summary>v1.1.1：管理员守护参数（配合 --watchdog 使用）。</summary>
    public const string ElevatedWatchdogArgument = "--elevated-watchdog";
    public static TimeSpan DefaultMainTimeout { get; } = TimeSpan.FromSeconds(45);
    public static TimeSpan DefaultWatchdogTimeout { get; } = TimeSpan.FromSeconds(40);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GreenSite");

    public static string HeartbeatPath(string directory) => Path.Combine(directory, "heartbeat.bin");
    public static string WatchdogHeartbeatPath(string directory) => Path.Combine(directory, "watchdog.bin");

    /// <summary>v1.1.1：仅管理员守护写的心跳，用于区分管理员 / 普通守护。</summary>
    public static string ElevatedHeartbeatPath(string directory) => Path.Combine(directory, "watchdog-elevated.bin");

    public static void WriteHeartbeat(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(HeartbeatPath(directory), DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* 心跳失败不影响主流程 */ }
    }

    public static void WriteWatchdogHeartbeat(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(WatchdogHeartbeatPath(directory), DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* 同上 */ }
    }

    /// <summary>管理员守护写两份心跳：兼容普通守护状态，同时提供独立的提权状态来源。</summary>
    public static void WriteElevatedWatchdogHeartbeat(string directory)
    {
        WriteWatchdogHeartbeat(directory);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(ElevatedHeartbeatPath(directory), DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* 同上 */ }
    }

    public static bool IsMainAlive(string directory, TimeSpan maxAge) =>
        IsFresh(HeartbeatPath(directory), maxAge);

    public static bool IsWatchdogRunning() => IsWatchdogRunning(DefaultDirectory, DefaultWatchdogTimeout);

    public static bool IsWatchdogRunning(string directory, TimeSpan maxAge) =>
        IsFresh(WatchdogHeartbeatPath(directory), maxAge);

    /// <summary>v1.1.1：管理员守护心跳（普通守护写 watchdog.bin，不写这个文件）。</summary>
    public static bool IsElevatedWatchdogRunning(string directory, TimeSpan maxAge) =>
        IsFresh(ElevatedHeartbeatPath(directory), maxAge);

    /// <summary>管理员守护每 10 秒调用：用户关闭开关后自行退出，无需再次提权。</summary>
    public static bool ShouldElevatedWatchdogExit(AdvancedOptions? options) =>
        options is null || !options.ElevatedWatchdogEnabled;

    private static bool IsFresh(string path, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            if (!long.TryParse(text, out var ticks)) return false;
            var last = new DateTime(ticks, DateTimeKind.Utc);
            return DateTime.UtcNow - last <= maxAge;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>拉起用户会话内的守护进程（可见进程，无隐藏、无注入）。</summary>
    public static bool StartWatchdog(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = WatchdogArgument,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v1.1.1：经用户确认 UAC 后启动管理员守护。取消授权时抛异常并返回 false，由设置页回滚开关。
    /// 只提权 Guard.exe；主程序权限不变。
    /// </summary>
    public static bool StartElevatedWatchdog(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = $"{WatchdogArgument} {ElevatedWatchdogArgument}",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v1.1.1：从已提权的守护进程拉起主程序时，借 explorer.exe（普通用户完整性级别）代启动，
    /// 避免管理权限被继承给主程序。仍是可见的正常启动，无隐藏、无注入。
    /// </summary>
    public static bool LaunchMainDeescalated(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"\"{exePath}\" --minimized",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
