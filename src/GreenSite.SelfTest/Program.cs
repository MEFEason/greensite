using System.Text;
using GreenSite.Core.Core;
using GreenSite.Core.Models;
using GreenSite.Core.Security;

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

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("GreenSite.SelfTest — 用例 1/2/3/5：规则引擎 · 白名单优先 · 分组开关 · 安全存储");

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

        Console.WriteLine($"结果：通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }
}
