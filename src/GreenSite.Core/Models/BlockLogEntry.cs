namespace GreenSite.Core.Models;

/// <summary>拦截日志条目。结构上不含 URL 与查询参数，杜绝隐私泄露。</summary>
public sealed record BlockLogEntry(
    DateTimeOffset Time,
    string Host,
    string GroupName,
    string RuleValue,
    bool Whitelisted);
