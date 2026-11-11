using System;
using System.Collections.Generic;
using GreenSite.Core.Models;

namespace GreenSite.Services;

/// <summary>
/// 托盘气泡拦截通知。同域 30 秒节流、全局每分钟不超过 5 条。
/// 日志仍由 LogService 逐条记录，不受节流影响。
/// 事件由 AppState 切回 UI 线程后再触发，故此处可直接调用 ShowBalloonTip。
/// </summary>
public sealed class NotificationService
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Dictionary<string, DateTimeOffset> _lastByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _recent = new();

    private static readonly TimeSpan PerHostCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);
    private const int MaxPerMinute = 5;

    public NotificationService(System.Windows.Forms.NotifyIcon icon)
    {
        _icon = icon;
        AppState.Instance.Blocked += OnBlocked;
    }

    private void OnBlocked(object? sender, BlockLogEntry entry)
    {
        // 开机自启无需密码即进入防护状态；此时 IsUnlocked=false 但拦截真实发生，
        // 不能因未解锁而吞掉提示，否则「界面提示 ↔ 日志 block」不一致。
        if (entry.Whitelisted) return; // 白名单放行不弹「已拦截」
        Show(entry);
    }

    public void Show(BlockLogEntry entry)
    {
        if (!AppState.Instance.Config.NotifyEnabled) return;

        var now = DateTimeOffset.Now;
        if (_lastByHost.TryGetValue(entry.Host, out var last) && now - last < PerHostCooldown)
            return;

        while (_recent.Count > 0 && now - _recent.Peek() > GlobalWindow)
            _recent.Dequeue();
        if (_recent.Count >= MaxPerMinute) return;

        _lastByHost[entry.Host] = now;
        _recent.Enqueue(now);

        var group = string.IsNullOrEmpty(entry.GroupName) ? "-" : entry.GroupName;
        var rule = string.IsNullOrEmpty(entry.RuleValue) ? "-" : entry.RuleValue;
        _icon.ShowBalloonTip(
            3000,
            "GreenSite 已拦截",
            $"已拦截 {entry.Host} · 命中 {group}/{rule}",
            System.Windows.Forms.ToolTipIcon.Info);
    }
}
