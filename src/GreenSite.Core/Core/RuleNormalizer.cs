using System.Globalization;
using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>规则输入规范化。所有比较统一用小写、无端口、无尾点的 ASCII 域名。</summary>
public static class RuleNormalizer
{
    private static readonly IdnMapping Idn = new();

    private static readonly HashSet<string> BroadDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "bytedance.com", "snssdk.com", "snssdk.net", "pstatp.com", "ksyun.com",
        "byteoversea.com", "volcengineapi.com", "zijieapi.com",
    };

    /// <summary>较宽的基础设施主域，界面需标「可能误伤」。</summary>
    public static bool IsBroadDomain(string domain) => BroadDomains.Contains(domain);

    public static bool TryNormalizeDomain(string? raw, out string value, out string error)
    {
        value = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) { error = "不能为空"; return false; }

        var s = raw.Trim();
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }
        var cut = s.IndexOfAny(['/', '?', '#', '\\']);
        if (cut >= 0) s = s[..cut];

        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        if (!s.StartsWith('['))
        {
            var colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon) s = s[..colon];
        }

        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0) { error = "域名为空"; return false; }
        if (s.Length > 253) { error = "域名过长"; return false; }
        if (s.Contains('*') || s.Contains(' ')) { error = "不支持通配符或空格"; return false; }

        try { s = Idn.GetAscii(s); }
        catch (ArgumentException) { error = "域名含非法字符"; return false; }

        var labels = s.Split('.');
        if (labels.Length < 2) { error = "请填写完整域名，如 douyin.com"; return false; }
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63) { error = "域名标签长度非法"; return false; }
            if (label[0] == '-' || label[^1] == '-') { error = "域名标签不能以连字符开头或结尾"; return false; }
            foreach (var ch in label)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-') { error = "域名含非法字符"; return false; }
            }
        }

        value = s;
        return true;
    }

    /// <summary>网址规则：host + 路径前缀（如 douyin.com/video/）。HTTPS 下 PAC 只能拿到 host，故仅明文 HTTP 生效。</summary>
    public static bool TryNormalizeUrlPath(string? raw, out string value, out string error)
    {
        value = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) { error = "不能为空"; return false; }

        var s = raw.Trim();
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }
        var slash = s.IndexOf('/');
        if (slash <= 0) { error = "网址规则需包含路径，如 douyin.com/video/"; return false; }

        var hostPart = s[..slash];
        var path = s[slash..];
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        if (!TryNormalizeDomain(hostPart, out var host, out error)) return false;

        value = host + path;
        return true;
    }

    /// <summary>从 CONNECT / HTTP 请求里取出的主机名做轻量规范化，不抛异常。</summary>
    public static string NormalizeHost(string raw)
    {
        var s = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            return end > 0 ? s[..(end + 1)] : s;
        }
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon) s = s[..colon];
        return s;
    }
}
