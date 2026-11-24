using System.Diagnostics;

namespace GreenSite.Core.Core;

/// <summary>
/// 守护协调：主进程写心跳；独立 Guard.exe（用户会话守护进程或服务）检测心跳超时后拉起主程序。
/// 用文件心跳而不是全局 Mutex——会话隔离下更可靠，且不依赖 SeDebugPrivilege 等敏感权限。
/// 不隐藏进程、不注入、不强杀；管理员主动结束无法阻止，只能自动恢复。
/// </summary>
public static class WatchdogService
{
    public const string WatchdogArgument = "--watchdog";
    public static TimeSpan DefaultMainTimeout { get; } = TimeSpan.FromSeconds(45);
    public static TimeSpan DefaultWatchdogTimeout { get; } = TimeSpan.FromSeconds(40);

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GreenSite");

    public static string HeartbeatPath(string directory) => Path.Combine(directory, "heartbeat.bin");
    public static string WatchdogHeartbeatPath(string directory) => Path.Combine(directory, "watchdog.bin");

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

    public static bool IsMainAlive(string directory, TimeSpan maxAge) =>
        IsFresh(HeartbeatPath(directory), maxAge);

    public static bool IsWatchdogRunning() => IsWatchdogRunning(DefaultDirectory, DefaultWatchdogTimeout);

    public static bool IsWatchdogRunning(string directory, TimeSpan maxAge) =>
        IsFresh(WatchdogHeartbeatPath(directory), maxAge);

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
}
