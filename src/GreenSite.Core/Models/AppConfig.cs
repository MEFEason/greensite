namespace GreenSite.Core.Models;

/// <summary>持久化配置。阶段 4 起由 SecureConfigStore 加密写入 config.bin。</summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<RuleGroup> Groups { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    /// <summary>PAC 版本号，规则变更时递增，避免浏览器缓存旧脚本。</summary>
    public long PacSeq { get; set; }

    public bool ProtectionEnabled { get; set; } = true;

    /// <summary>本地拦截端口，首次随机分配后持久化复用。</summary>
    public int Port { get; set; }

    public string Theme { get; set; } = "light";
    public bool NotifyEnabled { get; set; } = true;
    public bool AutoStart { get; set; }
    public int LogRetentionDays { get; set; } = 14;

    // 密码哈希（阶段 4 写入）：只存 PBKDF2 结果与 salt，绝不存明文。
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }

    /// <summary>密保恢复码哈希与 salt（首次设密码时生成，忘记密码用它解锁并重置）。</summary>
    public string? RecoveryHash { get; set; }
    public string? RecoverySalt { get; set; }

    /// <summary>点主窗口关闭按钮的行为：hide=最小化到托盘（默认）；exit=停止拦截并退出。</summary>
    public string CloseAction { get; set; } = "hide";

    /// <summary>用户原代理快照之外，记录是否跟随加速器：auto=检测到系统手动代理则非黑名单域名走该代理。</summary>
    public bool FollowUpstreamProxy { get; set; } = true;

    /// <summary>用户原代理设置快照，解除保护时精确还原。</summary>
    public ProxyBackup? ProxyBackup { get; set; }
}

/// <summary>记录「原本是否存在」及原值，避免把用户原有代理配置弄丢。</summary>
public sealed class ProxyBackup
{
    public bool AutoConfigUrlExisted { get; set; }
    public string? AutoConfigUrl { get; set; }
    public bool ProxyEnableExisted { get; set; }
    public int? ProxyEnable { get; set; }
    public bool ProxyServerExisted { get; set; }
    public string? ProxyServer { get; set; }
}
