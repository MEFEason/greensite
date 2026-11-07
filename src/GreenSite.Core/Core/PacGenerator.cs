using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GreenSite.Core.Core;

/// <summary>
/// 生成 PAC 脚本。白名单在前、黑名单只走本地拦截端口（绝不附 DIRECT 兜底）。
/// seq 与内容哈希写进注释，配合 URL 的 ?v=seq 强制浏览器刷新缓存。
/// </summary>
public static class PacGenerator
{
    public static string Generate(Ruleset ruleset, int port, long seq)
    {
        var allow = ruleset.EnabledAllowHosts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var block = ruleset.EnabledBlockHosts.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var allowJson = JsonSerializer.Serialize(allow);
        var blockJson = JsonSerializer.Serialize(block);
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{allowJson}|{blockJson}|{port}|{seq}")))[..16];

        var sb = new StringBuilder();
        sb.AppendLine($"// GreenSite PAC — seq={seq} sha256={digest}");
        sb.AppendLine("// 白名单优先；黑名单只走本地拦截端口，绝不写 DIRECT 兜底");
        sb.AppendLine("var GS_ALLOW = " + allowJson + ";");
        sb.AppendLine("var GS_BLOCK = " + blockJson + ";");
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
        sb.AppendLine("  if (gsSuffix(host, GS_ALLOW)) return 'DIRECT';");
        sb.AppendLine($"  if (gsSuffix(host, GS_BLOCK)) return 'PROXY 127.0.0.1:{port}';");
        sb.AppendLine("  return 'DIRECT';");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
