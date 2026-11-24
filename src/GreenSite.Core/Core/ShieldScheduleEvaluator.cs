using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>关闭防护时段的纯逻辑判定，供自检直接覆盖（UI 与状态机共用）。</summary>
public static class ShieldScheduleEvaluator
{
    public static bool IsSuppressed(ShieldSchedule? schedule, DateTime now) =>
        schedule?.IsActiveAt(now) ?? false;

    /// <summary>时段结束后自动恢复：当前不再命中且之前曾被抑制，则返回 true。</summary>
    public static bool ShouldRestore(ShieldSchedule? schedule, DateTime now, bool suppressedLastTick) =>
        suppressedLastTick && !IsSuppressed(schedule, now);
}
