namespace GreenSite.Core.Models;

/// <summary>单条规则。有效规则 = 所属组开关 ∧ 本条开关。</summary>
public sealed class Rule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public RuleType Type { get; init; } = RuleType.Domain;

    /// <summary>规范化后的值：域名小写无端口无尾点；网址规则为 host + 路径前缀。</summary>
    public string Value { get; init; } = "";

    /// <summary>用户原始输入，用于界面展示。</summary>
    public string RawValue { get; init; } = "";

    public string? Note { get; set; }

    /// <summary>单条开关。</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
