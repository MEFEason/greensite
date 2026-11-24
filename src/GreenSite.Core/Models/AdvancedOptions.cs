using System.Globalization;

namespace GreenSite.Core.Models;

/// <summary>
/// v1.1.0 新增能力的可配置集合。独立存于 options.bin，不污染 config.bin 的密码派生加密结构。
/// 全部字段都是非凭据数据：即使被读取也无法得到管理密码或恢复码。
/// </summary>
public sealed class AdvancedOptions
{
    /// <summary>受保护文件防直接删除（ACL Deny Delete + 完整性校验 + 备份恢复）。</summary>
    public bool FileProtectionEnabled { get; set; }

    /// <summary>公共桌面快捷方式守护。</summary>
    public bool ShortcutGuardEnabled { get; set; }

    /// <summary>快捷方式检查间隔（分钟），1~1440。</summary>
    public int ShortcutCheckMinutes { get; set; } = 30;

    /// <summary>守护进程 / 服务：主进程异常退出后自动恢复。</summary>
    public bool WatchdogEnabled { get; set; }

    /// <summary>加密备份（单份、混淆后缀）。</summary>
    public bool BackupEnabled { get; set; } = true;

    /// <summary>关闭防护时段规则。</summary>
    public ShieldSchedule ShieldSchedule { get; set; } = new();

    /// <summary>读取后规范化，非法值回落默认，避免 UI / 磁盘篡改导致异常。</summary>
    public void Normalize()
    {
        if (ShortcutCheckMinutes < 1 || ShortcutCheckMinutes > 1440) ShortcutCheckMinutes = 30;
        ShieldSchedule ??= new ShieldSchedule();
        ShieldSchedule.Rules ??= [];
        foreach (var rule in ShieldSchedule.Rules)
        {
            if (rule is null) continue;
            if (!ShieldSchedule.IsValidTime(rule.Start)) rule.Start = "22:00";
            if (!ShieldSchedule.IsValidTime(rule.End)) rule.End = "06:00";
        }
    }

    public AdvancedOptions Copy() => new()
    {
        FileProtectionEnabled = FileProtectionEnabled,
        ShortcutGuardEnabled = ShortcutGuardEnabled,
        ShortcutCheckMinutes = ShortcutCheckMinutes,
        WatchdogEnabled = WatchdogEnabled,
        BackupEnabled = BackupEnabled,
        ShieldSchedule = new ShieldSchedule
        {
            Enabled = ShieldSchedule?.Enabled ?? false,
            Rules = ShieldSchedule?.Rules?.Select(r => new ShieldRule
            {
                Day = r.Day, Start = r.Start, End = r.End,
            }).ToList() ?? [],
        },
    };
}

/// <summary>「关闭防护」时段配置：多条 星期 + 起止时间规则，支持跨午夜。</summary>
public sealed class ShieldSchedule
{
    public bool Enabled { get; set; }
    public List<ShieldRule> Rules { get; set; } = [];

    public static bool IsValidTime(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    public bool IsActiveAt(DateTime now) =>
        Enabled && Rules is { Count: > 0 } && Rules.Any(r => r.Contains(now));

    public string Describe()
    {
        if (!Enabled) return "未启用（全天按常规防护）";
        if (Rules is not { Count: > 0 }) return "已启用但无规则（全天按常规防护）";
        var dayNames = new[] { "日", "一", "二", "三", "四", "五", "六" };
        var parts = Rules.Select(r => $"周{dayNames[(int)r.Day]} {r.Start}–{r.End}");
        return string.Join("；", parts);
    }
}

/// <summary>单条时段规则。跨午夜时，结束日同样命中（例：周六 22:00–06:00 覆盖周日 01:00）。</summary>
public sealed class ShieldRule
{
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    public string Start { get; set; } = "22:00";
    public string End { get; set; } = "06:00";

    public bool Contains(DateTime now)
    {
        if (!ShieldSchedule.IsValidTime(Start) || !ShieldSchedule.IsValidTime(End)) return false;
        var start = TimeOnly.ParseExact(Start.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(End.Trim(), "HH:mm", CultureInfo.InvariantCulture);
        var time = TimeOnly.FromDateTime(now);

        if (start <= end)
            return now.DayOfWeek == Day && time >= start && time < end;

        var nextDay = (DayOfWeek)(((int)Day + 1) % 7);
        return (now.DayOfWeek == Day && time >= start)
            || (now.DayOfWeek == nextDay && time < end);
    }
}
