using GreenSite.Core.Models;
using GreenSite.Core.Security.Win32;
using Microsoft.Win32;

namespace GreenSite.Core.Core;

/// <summary>
/// 读写当前用户 WinINET 代理设置（无需管理员）。写前必须 Snapshot，
/// 解除保护时按「原本不存在 → 删除 / 原本有值 → 写回」精确还原。
/// </summary>
public sealed class ProxySettings
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly string _keyPath;

    /// <param name="keyPath">仅供自检注入临时键，生产留空。</param>
    public ProxySettings(string? keyPath = null) => _keyPath = keyPath ?? DefaultKeyPath;

    public ProxyBackup Snapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        if (key is null) return new ProxyBackup();

        var names = key.GetValueNames();
        return new ProxyBackup
        {
            AutoConfigUrlExisted = names.Contains("AutoConfigURL"),
            AutoConfigUrl = key.GetValue("AutoConfigURL") as string,
            ProxyEnableExisted = names.Contains("ProxyEnable"),
            ProxyEnable = key.GetValue("ProxyEnable") is int enable ? enable : null,
            ProxyServerExisted = names.Contains("ProxyServer"),
            ProxyServer = key.GetValue("ProxyServer") as string,
        };
    }

    public string? ReadAutoConfigUrl()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue("AutoConfigURL") as string;
    }

    public void ApplyAutoConfigUrl(string url)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath)
            ?? throw new InvalidOperationException("无法写入当前用户代理设置");
        key.SetValue("AutoConfigURL", url, RegistryValueKind.String);
        WinInet.RefreshProxySettings();
    }

    public void Restore(ProxyBackup backup)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath)
            ?? throw new InvalidOperationException("无法恢复当前用户代理设置");
        RestoreValue(key, "AutoConfigURL", backup.AutoConfigUrlExisted,
            backup.AutoConfigUrl, RegistryValueKind.String);
        RestoreValue(key, "ProxyServer", backup.ProxyServerExisted,
            backup.ProxyServer, RegistryValueKind.String);
        RestoreValue(key, "ProxyEnable", backup.ProxyEnableExisted,
            backup.ProxyEnable, RegistryValueKind.DWord);
        WinInet.RefreshProxySettings();
    }

    private static void RestoreValue(
        RegistryKey key, string name, bool existed, object? value, RegistryValueKind kind)
    {
        if (!existed)
        {
            if (key.GetValueNames().Contains(name)) key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        key.SetValue(name, value ?? (kind == RegistryValueKind.DWord ? 0 : ""), kind);
    }
}
