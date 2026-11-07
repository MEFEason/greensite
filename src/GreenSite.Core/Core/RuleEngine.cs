using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>判定引擎。白名单优先于限制列表，顺序不可调整。</summary>
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
    public BlockDecision Evaluate(string host, string? url = null)
    {
        var h = RuleNormalizer.NormalizeHost(host);

        // 1) 白名单优先：命中即放行，绝不进入限制列表判断
        foreach (var allow in _ruleset.EnabledAllowHosts)
        {
            if (!Matches(h, allow)) continue;
            var (group, value) = Lookup(allow);
            return new BlockDecision(DecisionKind.Allow, h, group, value, true);
        }

        // 2) 网址/路径规则（仅当调用方能提供完整 URL）
        if (!string.IsNullOrEmpty(url))
        {
            foreach (var u in _ruleset.EnabledUrlPathRules)
            {
                if (!Matches(h, u.Host)) continue;
                if (!url.Contains(u.PathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (u.IsAllow) return new BlockDecision(DecisionKind.Allow, h, u.GroupName, u.RuleValue, true);
                return new BlockDecision(DecisionKind.Block, h, u.GroupName, u.RuleValue);
            }
        }

        // 3) 限制列表
        foreach (var blocked in _ruleset.EnabledBlockHosts)
        {
            if (!Matches(h, blocked)) continue;
            var (group, value) = Lookup(blocked);
            return new BlockDecision(DecisionKind.Block, h, group, value);
        }

        return new BlockDecision(DecisionKind.Direct, h);
    }

    private (string? Group, string? Value) Lookup(string suffix)
    {
        foreach (var (key, attr) in _ruleset.Attribution)
        {
            if (Matches(suffix, key)) return (attr.GroupName, attr.RuleValue);
        }
        return (null, suffix);
    }
}
