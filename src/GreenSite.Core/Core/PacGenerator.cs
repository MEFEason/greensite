using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GreenSite.Core.Core;

/// <summary>
/// 生成 PAC 脚本。判定顺序：白名单 → 限制列表 → 加速/例外 → 上游。
/// 黑名单只走本地拦截端口（绝不附 DIRECT 兜底）；其余域名跟随系统手动代理
/// （加速器写入的 ProxyServer），没有则 DIRECT —— 保证加速器照常可用。
/// seq 与内容哈希写进注释，配合 URL 的 ?v=seq 强制浏览器刷新缓存。
/// </summary>
public static class PacGenerator
{
    public static string Generate(
        Ruleset ruleset, int port, long seq,
        string upstream = "DIRECT", IReadOnlyList<string>? bypass = null)
    {
        bypass ??= [];
        var allow = ruleset.EnabledAllowHosts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var block = ruleset.EnabledBlockHosts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var exceptions = bypass
            .Where(e => e.StartsWith("*.", StringComparison.Ordinal)) // 通配域名；其余形式（IP 段/<local>）PAC 内无法可靠复刻，忽略
            .Select(e => e[2..]) // 剥掉 "*.": gsSuffix 以「裸域+子域」匹配；保留星号将永不命中
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var allowJson = JsonSerializer.Serialize(allow);
        var blockJson = JsonSerializer.Serialize(block);
        var exceptionsJson = JsonSerializer.Serialize(exceptions);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{allowJson}|{blockJson}|{exceptionsJson}|{upstream}|{port}|{seq}")))[..16];

        var sb = new StringBuilder();
        sb.AppendLine($"// GreenSite PAC — seq={seq} sha256={digest} upstream={upstream}");
        sb.AppendLine("// 白名单优先；黑名单只走本地拦截端口，绝不写 DIRECT 兜底；");
        sb.AppendLine("// 其余域名跟随系统代理（加速器）或直连。");
        sb.AppendLine("var GS_ALLOW = " + allowJson + ";");
        sb.AppendLine("var GS_BLOCK = " + blockJson + ";");
        sb.AppendLine("var GS_EXCEPT = " + exceptionsJson + ";");
        sb.AppendLine($"var GS_UPSTREAM = '{upstream}';");
        sb.AppendLine("function gsSuffix(host, list) {");
        sb.AppendLine("  host = host.toLowerCase();");
        sb.AppendLine("  for (var i = 0; i < list.length; i++) {");
        sb.AppendLine("    var d = list[i];");
        sb.AppendLine("    if (host === d) return true;");
        sb.AppendLine("    if (host.length > d.length && host.substring(host.length - d.length - 1) === '.' + d) return true;");
        sb.AppendLine("  }");
        sb.AppendLine("  return false;");
        sb.AppendLine("}");
        sb.AppendLine("function FindProxyForURL(url, host) {");
        sb.AppendLine("  if (gsSuffix(host, GS_ALLOW)) return GS_UPSTREAM;");
        sb.AppendLine($"  if (gsSuffix(host, GS_BLOCK)) return 'PROXY 127.0.0.1:{port}';");
        sb.AppendLine("  if (gsSuffix(host, GS_EXCEPT)) return 'DIRECT';");
        sb.AppendLine("  return GS_UPSTREAM;");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
