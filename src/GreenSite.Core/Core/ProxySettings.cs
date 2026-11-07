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

    /// <summary>
    /// 读取当前系统手动代理（加速器通常写入此处）。
    /// ProxyEnable=1 且 ProxyServer 可解析出 http/https 端点时返回 PAC 可用的 "PROXY host:port"，
    /// 否则返回 "DIRECT"；同时返回 ProxyOverride 例外列表供 PAC 使用。
    /// </summary>
    public (string PacUpstream, string[] Bypass) ReadUpstream()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        if (key is null) return ("DIRECT", []);
        if (key.GetValue("ProxyEnable") is not int enabled || enabled == 0) return ("DIRECT", []);
        var server = key.GetValue("ProxyServer") as string;
        var target = ParseProxyServer(server);
        var bypass = ParseProxyOverride(key.GetValue("ProxyOverride") as string);
        return (target, bypass);
    }

    public static string ParseProxyServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return "DIRECT";
        // 形如 "http=1.2.3.4:8080;https=1.2.3.4:8080;socks=..." 或裸 "1.2.3.4:8080"
        string? generic = null;
        string? https = null;
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) { generic ??= part; continue; }
            var scheme = part[..eq].Trim().ToLowerInvariant();
            var host = part[(eq + 1)..].Trim();
            if (host.Length == 0) continue;
            if (scheme == "https") https ??= host;
            else if (scheme == "http") generic ??= host;
        }
        var chosen = https ?? generic;
        return chosen is null ? "DIRECT" : $"PROXY {chosen}";
    }

    /// <summary>只保留 "*.domain" 形式的例外（PAC 可复刻）；&lt;local&gt;、IP 段等一律丢弃。</summary>
    public static string[] ParseProxyOverride(string? overrideValue)
    {
        if (string.IsNullOrWhiteSpace(overrideValue)) return [];
        return overrideValue
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Length > 2 && e.StartsWith("*.", StringComparison.Ordinal))
            .ToArray();
    }

    public void ApplyAutoConfigUrl(string url)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath)
            ?? throw new InvalidOperationException("无法写入当前用户代理设置");
        key.SetValue("AutoConfigURL", url, RegistryValueKind.String);
        WinInet.RefreshProxySettings();
    }

    /// <summary>
    /// 解除保护时还原。**只精确还原 AutoConfigURL**：GreenSite 从不修改
    /// ProxyEnable / ProxyServer，它们是加速器或其它工具的配置，退出时把旧快照写回
    /// 反而会抹掉用户在保护期间开启的加速器设置。快照仍保留记录供诊断。
    /// </summary>
    public void Restore(ProxyBackup backup)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath)
            ?? throw new InvalidOperationException("无法恢复当前用户代理设置");
        RestoreValue(key, "AutoConfigURL", backup.AutoConfigUrlExisted,
            backup.AutoConfigUrl, RegistryValueKind.String);
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
