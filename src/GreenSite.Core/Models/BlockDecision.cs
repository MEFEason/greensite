namespace GreenSite.Core.Models;

public enum DecisionKind
{
    Direct = 0,
    Allow = 1,
    Block = 2,
}

/// <summary>判定结果。白名单命中返回 Allow，且永不被限制列表覆盖。</summary>
public sealed record BlockDecision(
    DecisionKind Kind,
    string Host,
    string? GroupName = null,
    string? RuleValue = null,
    bool IsWhitelist = false);
