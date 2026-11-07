using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

public sealed record UrlPathRule(
    string Host, string PathPrefix, string GroupName, string RuleValue, bool IsAllow);

public sealed record Attribution(string GroupName, string RuleValue, bool IsAllow);

/// <summary>由「分组 + 规则」预编译出的有效规则集合。有效 = 组开关 ∧ 单条开关。</summary>
public sealed class Ruleset
{
    public IReadOnlySet<string> EnabledAllowHosts { get; }
    public IReadOnlySet<string> EnabledBlockHosts { get; }
    public IReadOnlyList<UrlPathRule> EnabledUrlPathRules { get; }
    public IReadOnlyDictionary<string, Attribution> Attribution { get; }
    public int GroupCount { get; }
    public int DisabledGroupCount { get; }
    public int EffectiveRuleCount { get; }

    private Ruleset(
        HashSet<string> allow, HashSet<string> block,
        List<UrlPathRule> urlRules, Dictionary<string, Attribution> attribution,
        int groupCount, int disabledGroupCount, int effectiveRuleCount)
    {
        EnabledAllowHosts = allow;
        EnabledBlockHosts = block;
        EnabledUrlPathRules = urlRules;
        Attribution = attribution;
        GroupCount = groupCount;
        DisabledGroupCount = disabledGroupCount;
        EffectiveRuleCount = effectiveRuleCount;
    }

    public static Ruleset Build(IEnumerable<RuleGroup> groups, IEnumerable<Rule> rules)
    {
        var groupList = groups.ToList();
        var ruleList = rules.ToList();
        var byId = groupList.ToDictionary(g => g.Id);

        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var block = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlRules = new List<UrlPathRule>();
        var attribution = new Dictionary<string, Attribution>(StringComparer.OrdinalIgnoreCase);
        var effective = 0;

        foreach (var rule in ruleList)
        {
            if (!byId.TryGetValue(rule.GroupId, out var group)) continue;
            if (!group.Enabled || !rule.Enabled) continue;
            effective++;

            if (rule.Type == RuleType.Domain)
            {
                var target = group.IsAllow ? allow : block;
                target.Add(rule.Value);
                // 白名单归因优先，日志能说明「为何放行」
                if (group.IsAllow || !attribution.ContainsKey(rule.Value))
                    attribution[rule.Value] = new Attribution(group.Name, rule.Value, group.IsAllow);
            }
            else
            {
                var host = rule.Value.Split('/')[0];
                urlRules.Add(new UrlPathRule(host, rule.Value[host.Length..], group.Name, rule.Value, group.IsAllow));
            }
        }

        return new Ruleset(allow, block, urlRules, attribution,
            groupList.Count, groupList.Count(g => !g.Enabled), effective);
    }
}
