namespace GreenSite.Core.Models;

/// <summary>规则分组。每组一个总开关，组内每条规则另有独立开关。</summary>
public sealed class RuleGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>true = 属于白名单；false = 属于限制列表。</summary>
    public bool IsAllow { get; init; }

    /// <summary>组总开关。关闭后整组不参与 PAC 生成。</summary>
    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
