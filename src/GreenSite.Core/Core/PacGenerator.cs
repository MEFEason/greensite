using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>
/// 生成 PAC 脚本。判定顺序：白名单（仅在生效时段） → 限制列表 → 加速/例外 → 上游。
/// 黑名单只走本地拦截端口（绝不附 DIRECT 兜底）；其余域名跟随系统手动代理
/// （加速器写入的 ProxyServer），没有则 DIRECT —— 保证加速器照常可用。
/// 时段判断写在 PAC 内由浏览器用 new Date() 实时执行，避免 PAC 缓存导致时段切换失效。
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

        // 白名单时段：只把合法窗口编进 PAC；无合法窗口 = 不限制（与服务端 IsActiveAt 同语义）
        var windows = ruleset.AllowSchedule.Enabled
            ? ruleset.AllowSchedule.Windows
                .Where(w => AllowlistSchedule.IsValidTime(w.Start) && AllowlistSchedule.IsValidTime(w.End))
                .Select(w => new[] { AllowlistSchedule.ParseMinutes(w.Start), AllowlistSchedule.ParseMinutes(w.End) })
                .ToArray()
            : [];
        var scheduleOn = windows.Length > 0;

        var allowJson = JsonSerializer.Serialize(allow);
        var blockJson = JsonSerializer.Serialize(block);
        var exceptionsJson = JsonSerializer.Serialize(exceptions);
        var windowsJson = JsonSerializer.Serialize(windows);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{allowJson}|{blockJson}|{exceptionsJson}|{windowsJson}|{scheduleOn}|{upstream}|{port}|{seq}")))[..16];

        var sb = new StringBuilder();
        sb.AppendLine($"// GreenSite PAC — seq={seq} sha256={digest} upstream={upstream}");
        sb.AppendLine("// 白名单仅在生效时段内优先；黑名单只走本地拦截端口，绝不写 DIRECT 兜底；");
        sb.AppendLine("// 其余域名跟随系统代理（加速器）或直连。");
        sb.AppendLine("var GS_ALLOW = " + allowJson + ";");
        sb.AppendLine("var GS_BLOCK = " + blockJson + ";");
        sb.AppendLine("var GS_EXCEPT = " + exceptionsJson + ";");
        sb.AppendLine($"var GS_UPSTREAM = '{upstream}';");
        sb.AppendLine($"var GS_SCHEDULE = {{ enabled: {(scheduleOn ? "true" : "false")}, windows: {windowsJson} }};");
        sb.AppendLine("function gsSuffix(host, list) {");
        sb.AppendLine("  host = host.toLowerCase();");
        sb.AppendLine("  for (var i = 0; i < list.length; i++) {");
        sb.AppendLine("    var d = list[i];");
        sb.AppendLine("    if (host === d) return true;");
        sb.AppendLine("    if (host.length > d.length && host.substring(host.length - d.length - 1) === '.' + d) return true;");
        sb.AppendLine("  }");
        sb.AppendLine("  return false;");
        sb.AppendLine("}");
        sb.AppendLine("function gsAllowActive() {");
        sb.AppendLine("  if (!GS_SCHEDULE.enabled) return true;");
        sb.AppendLine("  var d = new Date();");
        sb.AppendLine("  var t = d.getHours() * 60 + d.getMinutes();");
        sb.AppendLine("  var w = GS_SCHEDULE.windows;");
        sb.AppendLine("  for (var i = 0; i < w.length; i++) {");
        sb.AppendLine("    var s = w[i][0], e = w[i][1];");
        sb.AppendLine("    if (s === e) continue;");
        sb.AppendLine("    if (s < e) { if (t >= s && t < e) return true; }");
        sb.AppendLine("    else { if (t >= s || t < e) return true; }");
        sb.AppendLine("  }");
        sb.AppendLine("  return false;");
        sb.AppendLine("}");
        sb.AppendLine("function FindProxyForURL(url, host) {");
        sb.AppendLine("  if (gsAllowActive() && gsSuffix(host, GS_ALLOW)) return GS_UPSTREAM;");
        sb.AppendLine($"  if (gsSuffix(host, GS_BLOCK)) return 'PROXY 127.0.0.1:{port}';");
        sb.AppendLine("  if (gsSuffix(host, GS_EXCEPT)) return 'DIRECT';");
        sb.AppendLine("  return GS_UPSTREAM;");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
