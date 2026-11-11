using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

public sealed record UrlPathRule(
    string Host, string PathPrefix, string GroupName, string RuleValue, bool IsAllow);

public sealed record Attribution(string GroupName, string RuleValue, bool IsAllow);

/// <summary>
/// 由「分组 + 规则 + 白名单时段」预编译出的有效规则集合。
/// 有效 = 组开关 ∧ 单条开关 ∧（白名单额外受时段控制）。
/// 归因按 allow / block 分离，保证黑名单命中不会记成白名单的组名与规则。
/// </summary>
public sealed class Ruleset
{
    public IReadOnlySet<string> EnabledAllowHosts { get; }
    public IReadOnlySet<string> EnabledBlockHosts { get; }
    public IReadOnlyList<UrlPathRule> EnabledUrlPathRules { get; }
    public IReadOnlyDictionary<string, Attribution> Attribution { get; }
    public IReadOnlyDictionary<string, Attribution> AllowAttribution { get; }
    public IReadOnlyDictionary<string, Attribution> BlockAttribution { get; }
    public AllowlistSchedule AllowSchedule { get; }
    public int GroupCount { get; }
    public int DisabledGroupCount { get; }
    public int EffectiveRuleCount { get; }

    private Ruleset(
        HashSet<string> allow, HashSet<string> block,
        List<UrlPathRule> urlRules,
        Dictionary<string, Attribution> attribution,
        Dictionary<string, Attribution> allowAttribution,
        Dictionary<string, Attribution> blockAttribution,
        AllowlistSchedule schedule,
        int groupCount, int disabledGroupCount, int effectiveRuleCount)
    {
        EnabledAllowHosts = allow;
        EnabledBlockHosts = block;
        EnabledUrlPathRules = urlRules;
        Attribution = attribution;
        AllowAttribution = allowAttribution;
        BlockAttribution = blockAttribution;
        AllowSchedule = schedule;
        GroupCount = groupCount;
        DisabledGroupCount = disabledGroupCount;
        EffectiveRuleCount = effectiveRuleCount;
    }

    /// <param name="schedule">白名单时段；null 表示不限制（兼容旧调用）。</param>
    public static Ruleset Build(
        IEnumerable<RuleGroup> groups, IEnumerable<Rule> rules,
        AllowlistSchedule? schedule = null)
    {
        var groupList = groups.ToList();
        var ruleList = rules.ToList();
        var byId = groupList.ToDictionary(g => g.Id);

        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var block = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlRules = new List<UrlPathRule>();
        var attribution = new Dictionary<string, Attribution>(StringComparer.OrdinalIgnoreCase);
        var allowAttr = new Dictionary<string, Attribution>(StringComparer.OrdinalIgnoreCase);
        var blockAttr = new Dictionary<string, Attribution>(StringComparer.OrdinalIgnoreCase);
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

                var attr = new Attribution(group.Name, rule.Value, group.IsAllow);
                // 按类别分别归因：白名单规则不再覆盖同域名黑名单规则的日志信息
                (group.IsAllow ? allowAttr : blockAttr)[rule.Value] = attr;
                // 兼容字段保留「白名单优先」口径，判定引擎不再读取它
                if (group.IsAllow || !attribution.ContainsKey(rule.Value))
                    attribution[rule.Value] = attr;
            }
            else
            {
                var host = rule.Value.Split('/')[0];
                urlRules.Add(new UrlPathRule(host, rule.Value[host.Length..], group.Name, rule.Value, group.IsAllow));
            }
        }

        return new Ruleset(allow, block, urlRules, attribution, allowAttr, blockAttr,
            schedule ?? new AllowlistSchedule(),
            groupList.Count, groupList.Count(g => !g.Enabled), effective);
    }
}
