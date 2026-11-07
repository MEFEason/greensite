using Microsoft.Win32;

namespace GreenSite.Services;

/// <summary>
/// 开机自启：只写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
/// 不需要管理员权限、不写 HKLM、不改 PATH、不按进程名杀进程。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GreenSite";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value
            && value.Contains("GreenSite.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>写入/删除自启项，命令为 "GreenSite.exe" --minimized。</summary>
    public static void SetEnabled(bool enabled)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "GreenSite.exe");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;

        if (enabled)
            key.SetValue(ValueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
