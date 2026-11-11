using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>
/// 判定引擎。白名单在其生效时段内优先于限制列表；时段外白名单整体失效，
/// 回退到常规拦截判定（命中黑名单则拦截）。顺序不可调整。
/// </summary>
public sealed class RuleEngine
{
    private readonly Ruleset _ruleset;

    public RuleEngine(Ruleset ruleset) => _ruleset = ruleset;

    /// <summary>后缀逐层父域匹配：命中规则本身或其任一级子域。</summary>
    public static bool Matches(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        (host.Length > suffix.Length && host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));

    /// <param name="host">主机名（可带端口，如 CONNECT 的 host:443）。</param>
    /// <param name="url">完整 URL，仅明文 HTTP 时有值；HTTPS 下 PAC 只传 host。</param>
    /// <param name="now">判定时刻；null = 本机当前时间。供自检注入固定时间。</param>
    public BlockDecision Evaluate(string host, string? url = null, TimeOnly? now = null)
    {
        var h = RuleNormalizer.NormalizeHost(host);
        var allowActive = _ruleset.AllowSchedule.IsActiveAt(
            now ?? TimeOnly.FromDateTime(DateTime.Now));

        // 1) 白名单优先：仅在其生效时段内放行，绝不进入限制列表判断
        if (allowActive)
        {
            foreach (var allow in _ruleset.EnabledAllowHosts)
            {
                if (!Matches(h, allow)) continue;
                var (group, value) = Lookup(allow, isAllow: true);
                return new BlockDecision(DecisionKind.Allow, h, group, value, true);
            }
        }

        // 2) 网址/路径规则（仅当调用方能提供完整 URL）
        if (!string.IsNullOrEmpty(url))
        {
            foreach (var u in _ruleset.EnabledUrlPathRules)
            {
                if (!Matches(h, u.Host)) continue;
                if (!url.Contains(u.PathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (u.IsAllow)
                {
                    // 时段外的白名单路径规则同样失效，继续检查后续规则
                    if (allowActive)
                        return new BlockDecision(DecisionKind.Allow, h, u.GroupName, u.RuleValue, true);
                    continue;
                }
                return new BlockDecision(DecisionKind.Block, h, u.GroupName, u.RuleValue);
            }
        }

        // 3) 限制列表
        foreach (var blocked in _ruleset.EnabledBlockHosts)
        {
            if (!Matches(h, blocked)) continue;
            var (group, value) = Lookup(blocked, isAllow: false);
            return new BlockDecision(DecisionKind.Block, h, group, value);
        }

        return new BlockDecision(DecisionKind.Direct, h);
    }

    private (string? Group, string? Value) Lookup(string suffix, bool isAllow)
    {
        var table = isAllow ? _ruleset.AllowAttribution : _ruleset.BlockAttribution;
        foreach (var (key, attr) in table)
        {
            if (Matches(suffix, key)) return (attr.GroupName, attr.RuleValue);
        }
        return (null, suffix);
    }
}
