using System.Net;
using System.Net.Sockets;
using System.Text;
using GreenSite.Core.Core;
using GreenSite.Core.Models;
using GreenSite.Core.Security;
using Microsoft.Win32;

namespace GreenSite.SelfTest;

/// <summary>手写断言 runner（零 NuGet 测试框架）。阶段 3 覆盖用例 1/2/3。</summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"[PASS] {name}"); }
        else { _fail++; Console.WriteLine($"[FAIL] {name}"); }
    }

    private static void Equal<T>(string name, T expected, T actual) =>
        Check($"{name}（期望 {expected} / 实际 {actual}）",
            EqualityComparer<T>.Default.Equals(expected, actual));

    private static RuleGroup Group(string name, bool isAllow = false, bool enabled = true) =>
        new() { Name = name, IsAllow = isAllow, Enabled = enabled };

    private static Rule Domain(RuleGroup g, string value, bool enabled = true) =>
        new() { GroupId = g.Id, Type = RuleType.Domain, Value = value, RawValue = value, Enabled = enabled };

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RawHttp(int port, string request)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 3000;
        using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        stream.Write(bytes, 0, bytes.Length);
        var buffer = new byte[16384];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("GreenSite.SelfTest — 用例 1~8：规则引擎 · 白名单优先 · 分组开关 · 安全存储 · PAC · 拦截链路 · 状态机");

        // ── 用例 1：规范化与后缀匹配 ──────────────────────────────
        Check("规范化 去协议/端口/路径/尾点/大小写",
            RuleNormalizer.TryNormalizeDomain(" HTTPS://WWW.Douyin.Com:443/video ", out var n1, out _) &&
            n1 == "www.douyin.com");
        Check("规范化 尾点与大小写",
            RuleNormalizer.TryNormalizeDomain("Douyin.COM.", out var n2, out _) && n2 == "douyin.com");
        Check("规范化 拒绝通配符", !RuleNormalizer.TryNormalizeDomain("*.douyin.com", out _, out _));
        Check("规范化 拒绝空输入", !RuleNormalizer.TryNormalizeDomain("  ", out _, out _));
        Check("规范化 拒绝单标签", !RuleNormalizer.TryNormalizeDomain("localhost", out _, out _));
        Check("规范化 IDN → punycode",
            RuleNormalizer.TryNormalizeDomain("例子.中国", out var idn, out _) && idn.StartsWith("xn--"));
        Check("网址规则 保留路径且去查询串",
            RuleNormalizer.TryNormalizeUrlPath("http://douyin.com/video/123?x=1", out var uv, out _) &&
            uv == "douyin.com/video/123");
        Check("后缀匹配 自身", RuleEngine.Matches("douyin.com", "douyin.com"));
        Check("后缀匹配 子域", RuleEngine.Matches("v.douyin.com", "douyin.com"));
        Check("后缀匹配 多级子域", RuleEngine.Matches("a.b.douyin.com", "douyin.com"));
        Check("后缀匹配 拒绝相似域", !RuleEngine.Matches("evildouyin.com", "douyin.com"));
        Check("NormalizeHost 去掉 CONNECT 端口",
            RuleNormalizer.NormalizeHost("www.douyin.com:443") == "www.douyin.com");

        // ── 默认清单 ──────────────────────────────────────────────
        var (groups, rules) = DefaultRules.CreateDefault();
        var groupById = groups.ToDictionary(g => g.Id);
        Equal("默认分组数", 6, groups.Count);
        Equal("默认限制条数", 41,
            rules.Count(r => !groupById[r.GroupId].IsAllow));
        Equal("默认白名单条数", 15,
            rules.Count(r => groupById[r.GroupId].IsAllow));
        Check("较宽主域已标注", RuleNormalizer.IsBroadDomain("bytedance.com"));
        Check("行内注释作为备注",
            rules.Any(r => r.Value == "bytedance.com" && (r.Note?.Contains("[宽]") ?? false)));

        var engine = new RuleEngine(Ruleset.Build(groups, rules));
        Check("抖音被拦", engine.Evaluate("www.douyin.com").Kind == DecisionKind.Block);
        Check("斗鱼被拦（带端口）", engine.Evaluate("live.douyu.com:443").Kind == DecisionKind.Block);
        Check("B 站放行", engine.Evaluate("live.bilibili.com").Kind == DecisionKind.Allow);
        Check("无关域名直连", engine.Evaluate("example.com").Kind == DecisionKind.Direct);
        Check("拦截归因带组名", engine.Evaluate("www.douyin.com").GroupName != null);

        // ── 用例 2：白名单优先于限制列表 ──────────────────────────
        var blockGroup = Group("限制组");
        var allowGroup = Group("白名单组", isAllow: true);
        var overlap = new List<Rule>
        {
            Domain(blockGroup, "example.com"),
            Domain(allowGroup, "example.com"),
        };
        var engine2 = new RuleEngine(Ruleset.Build([blockGroup, allowGroup], overlap));
        Check("同值同时黑白 → 放行", engine2.Evaluate("example.com").Kind == DecisionKind.Allow);
        Check("白名单子域不被黑名单父域拦", engine2.Evaluate("a.example.com").Kind == DecisionKind.Allow);

        allowGroup.Enabled = false;
        var engine3 = new RuleEngine(Ruleset.Build([blockGroup, allowGroup], overlap));
        Check("白名单组关闭 → 转为拦截", engine3.Evaluate("example.com").Kind == DecisionKind.Block);
        allowGroup.Enabled = true;

        // ── 用例 3：组开关 / 单条开关 / 跨组移动 ───────────────────
        var gA = Group("组A");
        var gB = Group("组B");
        var rA1 = Domain(gA, "a.com");
        var rA2 = Domain(gA, "b.com", enabled: false);
        var rB1 = Domain(gB, "c.com");
        var allRules = new List<Rule> { rA1, rA2, rB1 };

        var rs1 = Ruleset.Build([gA, gB], allRules);
        Equal("有效规则数（单条关闭）", 2, rs1.EffectiveRuleCount);
        Check("单条关 → 不进集合", !rs1.EnabledBlockHosts.Contains("b.com"));
        Check("单条开 → 进集合", rs1.EnabledBlockHosts.Contains("a.com"));

        gA.Enabled = false;
        var rs2 = Ruleset.Build([gA, gB], allRules);
        Equal("已关闭组数", 1, rs2.DisabledGroupCount);
        Check("组关 → 整组不进集合", !rs2.EnabledBlockHosts.Contains("a.com"));
        Check("组关 → 其它组不受影响", rs2.EnabledBlockHosts.Contains("c.com"));
        gA.Enabled = true;

        var gAllow = Group("放行组", isAllow: true);
        rA1.GroupId = gAllow.Id;
        var rs3 = Ruleset.Build([gA, gB, gAllow], allRules);
        Check("跨组移动 → 新组语义生效",
            rs3.EnabledAllowHosts.Contains("a.com") && !rs3.EnabledBlockHosts.Contains("a.com"));
        Check("跨组移动后判定为放行",
            new RuleEngine(rs3).Evaluate("sub.a.com").Kind == DecisionKind.Allow);

        // ── 用例 5：配置加解密往返 / 篡改检测 / 统一报错 / 冷却退避 ──
        static bool Unified(Action action)
        {
            try { action(); return false; }
            catch (ConfigCorruptedException e) { return e.Message == SecureConfigStore.UnifiedError; }
        }
        static bool ThrowsArg(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "greensite-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            const string pwd = "Passw0rd!2026";
            var store = new SecureConfigStore(tmpDir);
            var (hash, salt) = PasswordHasher.Create(pwd);
            var cfg = new AppConfig { PasswordHash = hash, PasswordSalt = salt, PacSeq = 7 };
            cfg.Groups.AddRange(groups);
            cfg.Rules.AddRange(rules);
            store.Save(cfg, pwd);
            Check("配置文件已生成", store.Exists);

            var loaded = store.Load(pwd);
            Equal("配置往返 PacSeq", 7L, loaded.PacSeq);
            Equal("配置往返 规则数", rules.Count, loaded.Rules.Count);
            Equal("配置往返 分组数", 6, loaded.Groups.Count);
            Check("错误密码统一报错", Unified(() => store.Load("wrong-password")));

            cfg.PacSeq = 8;
            store.Save(cfg, pwd);
            Check("二次保存生成 config.bak", File.Exists(store.BackupPath));

            var raw = File.ReadAllBytes(store.Path);
            raw[20] ^= 0xFF;
            File.WriteAllBytes(store.Path, raw);
            Check("篡改 1 字节触发统一报错", Unified(() => store.Load(pwd)));

            File.Copy(store.BackupPath, store.Path, overwrite: true);
            Check("config.bak 恢复可读", store.Load(pwd).Groups.Count == 6);

            // 恢复码路径：设置恢复码 → 用恢复码解锁 → 换密码后旧恢复码失效
            var code = RecoveryCode.Generate();
            Check("恢复码格式", code.Length == 29 && code.Count(c => c == '-') == 4);
            Check("恢复码规范化往返", RecoveryCode.Normalize(code) == code);
            Check("恢复码小写/空格可规范化",
                RecoveryCode.Normalize(code.ToLowerInvariant().Replace('-', ' ')) == code);
            Check("非法恢复码拒绝", RecoveryCode.Normalize("XXXX!") is null);
            var (rh, rs) = PasswordHasher.Create(code);
            cfg.RecoveryHash = rh; cfg.RecoverySalt = rs;
            store.Save(cfg, pwd);
            Check("恢复码可解锁配置",
                store.LoadWithRecoveryCode(code).PacSeq == 8);
            Check("错误恢复码统一报错",
                Unified(() => store.LoadWithRecoveryCode("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE")));
            Check("密码仍可用", store.Load(pwd).RecoveryHash != null);

            Check("密码最短长度校验", ThrowsArg(() => PasswordHasher.Create("123")));

            var gate = new PasswordGate();
            var t0 = DateTimeOffset.UtcNow;
            Check("初始未锁定", !gate.IsLocked(t0));
            gate.RecordFailure(t0);
            Check("首次失败进入冷却", gate.RemainingCooldown(t0) >= TimeSpan.FromSeconds(4));
            gate.RecordSuccess();
            Check("成功后解锁", !gate.IsLocked(t0));
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }

        // ── 用例 4：PAC 生成 ──────────────────────────────────────
        var pacSet = Ruleset.Build(groups, rules);
        var pac = PacGenerator.Generate(pacSet, 31234, 7);
        Check("PAC 白名单判断在前",
            pac.IndexOf("if (gsSuffix(host, GS_ALLOW))", StringComparison.Ordinal) <
            pac.IndexOf("if (gsSuffix(host, GS_BLOCK))", StringComparison.Ordinal));
        Check("PAC 黑名单只走本地端口",
            pac.Contains("return 'PROXY 127.0.0.1:31234';", StringComparison.Ordinal));
        Check("PAC 黑名单无 DIRECT 兜底",
            !pac.Contains("31234; DIRECT", StringComparison.Ordinal));
        Check("PAC 带 seq 与 sha256",
            pac.Contains("seq=7", StringComparison.Ordinal) && pac.Contains("sha256=", StringComparison.Ordinal));
        Check("PAC 版本变化导致内容变化", PacGenerator.Generate(pacSet, 31234, 8) != pac);
        Check("PAC 含默认域名",
            pac.Contains("douyin.com", StringComparison.Ordinal) &&
            pac.Contains("bilibili.com", StringComparison.Ordinal));

        // ── 用例 4b：加速器兼容（跟随系统手动代理）──
        var pacAcc = PacGenerator.Generate(pacSet, 31234, 9, "PROXY 127.0.0.1:7892", ["*.example.com"]);
        Check("加速器：白名单走上游而非 DIRECT",
            pacAcc.Contains("return GS_UPSTREAM", StringComparison.Ordinal) &&
            pacAcc.Contains("upstream=PROXY 127.0.0.1:7892", StringComparison.Ordinal));
        Check("加速器：黑名单仍被拦",
            pacAcc.Contains("return 'PROXY 127.0.0.1:31234';", StringComparison.Ordinal));
        Check("加速器：ProxyOverride 例外直连",
            pacAcc.Contains("GS_EXCEPT", StringComparison.Ordinal) &&
            pacAcc.Contains("\"example.com\"", StringComparison.Ordinal));
        Check("加速器：例外已剥星号",
            !pacAcc.Contains("\"*.example.com\"", StringComparison.Ordinal));
        Check("ParseProxyServer 裸地址",
            ProxySettings.ParseProxyServer("127.0.0.1:7892") == "PROXY 127.0.0.1:7892");
        Check("ParseProxyServer https 优先",
            ProxySettings.ParseProxyServer("http=1.1.1.1:80;https=2.2.2.2:8080") == "PROXY 2.2.2.2:8080");
        Check("ParseProxyServer socks-only → DIRECT",
            ProxySettings.ParseProxyServer("socks=127.0.0.1:7891") == "DIRECT");
        Check("ParseProxyOverride 过滤 <local>/IP 段",
            ProxySettings.ParseProxyOverride("*.baidu.com;<local>;192.168.*.*").SequenceEqual(["*.baidu.com"]));

        // ── 用例 6：拦截端口端到端（PAC / HTTP / CONNECT / 日志脱敏）──
        var e2eDir = Path.Combine(Path.GetTempPath(), "greensite-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var e2eLog = new LogService(Path.Combine(e2eDir, "logs"));
            var e2eRules = Ruleset.Build(groups, rules);
            var port = FreePort();
            var endpoint = new BlockingProxy(port, new RuleEngine(e2eRules), e2eLog,
                () => PacGenerator.Generate(e2eRules, port, 1));
            var notified = 0;
            endpoint.Blocked += (_, _) => Interlocked.Increment(ref notified);
            endpoint.Start();

            var pacResp = RawHttp(port, "GET /proxy.pac?v=1 HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            Check("端点提供 PAC 脚本",
                pacResp.Contains("HTTP/1.1 200", StringComparison.Ordinal) &&
                pacResp.Contains("FindProxyForURL", StringComparison.Ordinal));

            var httpResp = RawHttp(port, "GET http://v.douyin.com/x?secret=1 HTTP/1.1\r\nHost: v.douyin.com\r\n\r\n");
            Check("被拦 HTTP 返回 403", httpResp.Contains("403 Forbidden", StringComparison.Ordinal));

            var connectResp = RawHttp(port, "CONNECT live.douyu.com:443 HTTP/1.1\r\nHost: live.douyu.com:443\r\n\r\n");
            Check("被拦 CONNECT 返回 403", connectResp.Contains("403 Forbidden", StringComparison.Ordinal));

            var entries = e2eLog.ReadRecent(1);
            Check("拦截事件触发通知", notified >= 2);
            Check("日志记录域名",
                entries.Any(e => e.Host == "v.douyin.com") &&
                entries.Any(e => e.Host == "live.douyu.com"));
            Check("日志不含 URL 查询参数",
                !File.ReadAllText(Path.Combine(e2eLog.Directory, $"block-{DateTimeOffset.Now:yyyyMMdd}.log"))
                    .Contains("secret=1", StringComparison.Ordinal));
            Check("日志归因带分组", entries.Any(e => e.GroupName.Length > 0));
            endpoint.Stop();
        }
        finally
        {
            try { System.IO.Directory.Delete(e2eDir, recursive: true); } catch { }
        }

        // ── 用例 7：代理设置快照 / 精确还原（临时注册表键，不碰 Internet Settings）──
        var testKey = @"Software\GreenSiteSelfTest\" + Guid.NewGuid().ToString("N");
        var proxySettings = new ProxySettings(testKey);
        try
        {
            var empty = proxySettings.Snapshot();
            Check("空键快照 原值不存在", !empty.AutoConfigUrlExisted);
            proxySettings.ApplyAutoConfigUrl("http://127.0.0.1:12345/proxy.pac?v=1");
            Check("写入后可读",
                proxySettings.ReadAutoConfigUrl() == "http://127.0.0.1:12345/proxy.pac?v=1");
            proxySettings.Restore(empty);
            Check("还原后删除自己写入的值", proxySettings.ReadAutoConfigUrl() is null);

            using (var key = Registry.CurrentUser.CreateSubKey(testKey))
            {
                key.SetValue("AutoConfigURL", "http://original.example/pac", RegistryValueKind.String);
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            }
            var original = proxySettings.Snapshot();
            Check("快照记录用户原值",
                original.AutoConfigUrl == "http://original.example/pac" && original.ProxyEnable == 0);
            proxySettings.ApplyAutoConfigUrl("http://127.0.0.1:2222/proxy.pac?v=2");
            // 保护期间用户开启加速器（ProxyEnable=1 + ProxyServer），GreenSite 全程不碰这两个值
            using (var key = Registry.CurrentUser.OpenSubKey(testKey, writable: true)!)
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "127.0.0.1:7892", RegistryValueKind.String);
            }
            proxySettings.Restore(original);
            Check("原 AutoConfigURL 精确还原", proxySettings.ReadAutoConfigUrl() == "http://original.example/pac");
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Check("退出不抹除加速器：ProxyEnable/ProxyServer 保持用户当前值",
                    key?.GetValue("ProxyEnable") is int pe && pe == 1 &&
                    key?.GetValue("ProxyServer") as string == "127.0.0.1:7892");
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false); } catch { }
        }

        // ── 用例 8：保护状态机（临时键 + 禁用注册表监视）──
        var stateKey = @"Software\GreenSiteSelfTest\" + Guid.NewGuid().ToString("N");
        var stateProxy = new ProxySettings(stateKey);
        var stateDir = Path.Combine(Path.GetTempPath(), "greensite-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stateLog = new LogService(Path.Combine(stateDir, "logs"));
            var stateConfig = new AppConfig { PacSeq = 1 };
            var controller = new ProtectionController(
                stateConfig, Ruleset.Build(groups, rules), stateLog, stateProxy,
                port: FreePort(), enableGuard: false);
            Check("初始 Inactive", controller.State == ProtectionState.Inactive);
            controller.Start();
            Check("开启后 Active", controller.State == ProtectionState.Active);
            Check("PAC URL 带版本号", controller.PacUrl.Contains("/proxy.pac?v=", StringComparison.Ordinal));
            Check("已写入 AutoConfigURL", stateProxy.ReadAutoConfigUrl() == controller.PacUrl);

            var urlBefore = controller.PacUrl;
            controller.ApplyRules(Ruleset.Build(groups, rules));
            Check("规则变更递增 PAC 版本", controller.PacUrl != urlBefore);
            Check("AutoConfigURL 同步更新", stateProxy.ReadAutoConfigUrl() == controller.PacUrl);

            // 加速器跟随：用户在保护期间开启系统手动代理 → PAC 上游切换且 seq 递增
            using (var key = Registry.CurrentUser.OpenSubKey(stateKey, writable: true)!)
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "127.0.0.1:7892", RegistryValueKind.String);
            }
            var urlBeforeAcc = controller.PacUrl;
            controller.RefreshUpstream();
            Check("检测到加速器后 PAC 递增版本", controller.PacUrl != urlBeforeAcc);
            var pacBody = RawHttp(controller.Port,
                "GET /proxy.pac HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            Check("PAC 上游指向加速器",
                pacBody.Contains("GS_UPSTREAM = 'PROXY 127.0.0.1:7892'", StringComparison.Ordinal));
            Check("黑名单分支仍然拦截",
                pacBody.Contains($"return 'PROXY 127.0.0.1:{controller.Port}'", StringComparison.Ordinal));

            controller.Shutdown();
            Check("退出后 Inactive", controller.State == ProtectionState.Inactive);
            Check("退出后恢复代理设置", stateProxy.ReadAutoConfigUrl() is null);
            using (var key = Registry.CurrentUser.OpenSubKey(stateKey))
                Check("退出后加速器设置未被抹除",
                    key?.GetValue("ProxyEnable") is int pe2 && pe2 == 1 &&
                    key?.GetValue("ProxyServer") as string == "127.0.0.1:7892");
            Check("退出后 ProtectionEnabled=false", !stateConfig.ProtectionEnabled);
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(stateKey, throwOnMissingSubKey: false); } catch { }
            try { System.IO.Directory.Delete(stateDir, recursive: true); } catch { }
        }

        Console.WriteLine($"结果：通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }
}
