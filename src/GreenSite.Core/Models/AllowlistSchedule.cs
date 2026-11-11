using System.Globalization;

namespace GreenSite.Core.Models;

/// <summary>
/// 单个白名单生效时段，24 小时制 "HH:mm"。半开区间 [Start, End)，支持跨午夜
/// （如 22:00–06:00 表示 22:00 起至次日 06:00 前）。
/// </summary>
public sealed class AllowTimeWindow
{
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "18:00";

    /// <summary>该时刻是否落在窗口内。起止相同或格式非法时视为不匹配。</summary>
    public bool Contains(TimeOnly now)
    {
        var start = AllowlistSchedule.ParseMinutes(Start);
        var end = AllowlistSchedule.ParseMinutes(End);
        if (start < 0 || end < 0 || start == end) return false;
        var current = now.Hour * 60 + now.Minute;
        return start < end
            ? current >= start && current < end
            : current >= start || current < end; // 跨午夜
    }
}

/// <summary>
/// 白名单时间控制。与现有「白名单分组开关」「单条规则开关」同时生效：
/// 有效条件 = 组开关 ∧ 单条开关 ∧ 时段判定。
/// 未启用时间控制或没有任何合法时段时，白名单全天生效（与旧行为完全一致）。
/// </summary>
public sealed class AllowlistSchedule
{
    /// <summary>启用状态；false = 不限制，白名单全天生效。</summary>
    public bool Enabled { get; set; }

    /// <summary>多个开始 / 结束时间段。</summary>
    public List<AllowTimeWindow> Windows { get; set; } = [];

    /// <summary>解析 24 小时制 "HH:mm" 为当天分钟数；非法返回 -1。</summary>
    public static int ParseMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return -1;
        return time.Hour * 60 + time.Minute;
    }

    public static bool IsValidTime(string? text) => ParseMinutes(text) >= 0;

    /// <summary>
    /// 该时刻白名单是否生效。未启用 / 无合法时段 → true（不限制），
    /// 避免用户配错时段后把所有白名单意外锁死。
    /// </summary>
    public bool IsActiveAt(TimeOnly now)
    {
        if (!Enabled) return true;
        var valid = Windows
            .Where(w => IsValidTime(w.Start) && IsValidTime(w.End))
            .ToArray();
        if (valid.Length == 0) return true;
        return valid.Any(w => w.Contains(now));
    }

    public bool IsActiveNow() => IsActiveAt(TimeOnly.FromDateTime(DateTime.Now));

    /// <summary>保存前规范化：剔除非法时段。返回保留的有效时段数。</summary>
    public int Normalize()
    {
        Windows.RemoveAll(w => !IsValidTime(w.Start) || !IsValidTime(w.End));
        return Windows.Count;
    }

    /// <summary>供界面显示当前生效范围。</summary>
    public string Describe() =>
        !Enabled || Windows.Count == 0
            ? "全天生效"
            : string.Join("、", Windows.Select(w => $"{w.Start}–{w.End}"));
}
