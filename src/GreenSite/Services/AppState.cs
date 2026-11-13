using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Threading;
using GreenSite.Core.Core;
using GreenSite.Core.Models;
using GreenSite.Core.Security;
using GreenSite.Views;

namespace GreenSite.Services;

/// <summary>
/// 应用状态单例。集中持有配置、规则集、加密仓库、密码门禁、日志、保护控制器、
/// 编辑解锁状态与会话密码。会话密码只以 <see cref="SecureString"/> 驻留内存，
/// 绝不写文件、日志或异常消息；需要传给 Core 时临时转 string，用后不持久化。
///
/// 开机自启：config.bin 依赖管理密码解密，冷启动无密码无法读取，故另用
/// protection.bin（DPAPI 无密码快照）在 Initialize 阶段直接启动防护；
/// 用户解锁时复用同一控制器，不重启端点。
/// </summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    private readonly string _configDirectory;
    private SecureConfigStore? _store;
    private ProtectionSnapshotStore? _snapshotStore;
    private AppConfig? _config;
    private Ruleset? _ruleset;
    private ProtectionController? _controller;
    private SecureString? _sessionPassword;
    private DispatcherTimer? _editLockTimer;
    private bool _autoProtectionStarted;

    private AppState()
    {
        // %LOCALAPPDATA%\GreenSite\config.bin / protection.bin
        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GreenSite");
    }

    // ── 公开状态 ──────────────────────────────────────────────
    public AppConfig Config => _config ?? throw new InvalidOperationException("配置尚未加载");
    public Ruleset Ruleset => _ruleset ?? throw new InvalidOperationException("规则集尚未构建");
    public SecureConfigStore Store => _store!;
    public ProtectionController Controller => _controller!;
    public LogService Log => Controller.Log;
    public PasswordGate Gate { get; } = new();

    public bool IsFirstRun { get; private set; }
    public bool IsExiting { get; private set; }
    /// <summary>配置是否已解密并进入主界面。</summary>
    public bool IsUnlocked { get; private set; }
    /// <summary>本次会话是否由开机自启快照启动防护，且尚未解锁。</summary>
    public bool IsAutoProtectionActive => _autoProtectionStarted && IsProtectionActive;
    /// <summary>
    /// 开机自启已自动开启防护、但尚未解锁：关闭保护 / 退出程序必须先解锁，关闭窗口只最小化。
    /// 决策下沉到 <see cref="AccessPolicy"/> 纯逻辑，便于自检直接覆盖。
    /// </summary>
    public bool IsUnlockRequired =>
        AccessPolicy.RequiresUnlock(IsFirstRun, IsUnlocked, IsProtectionActive);
    /// <summary>本次会话是否由恢复码解锁、尚未重设管理密码（须强制重设）。</summary>
    public bool NeedPasswordReset { get; private set; }
    /// <summary>规则编辑模式是否解锁（会话内一次解锁，超时或隐藏到托盘自动锁定）。</summary>
    public bool IsEditUnlocked { get; private set; }
    public bool IsProtectionActive => _controller?.IsActive ?? false;

    // ── 事件（供各页面、托盘订阅；均在 UI 线程触发）────────────
    public event EventHandler<BlockLogEntry>? Blocked;
    public event Action? StateChanged;
    public event Action? RulesChanged;
    public event Action? EditLockChanged;
    public event Action? BeforeExit;

    /// <summary>
    /// 应用启动时调用。
    /// </summary>
    /// <param name="autoStartLaunch">
    /// 是否由开机自启（--minimized）拉起。为 true 时从无密码 DPAPI 快照直接构建规则集并
    /// 启动防护，无需密码、无需用户点击托盘；这是「开机自启」的明确预期行为。
    /// </param>
    public void Initialize(bool autoStartLaunch = false)
    {
        _store = new SecureConfigStore(_configDirectory);
        _snapshotStore = new ProtectionSnapshotStore(_configDirectory);
        IsFirstRun = !_store.Exists;
        if (IsFirstRun || !autoStartLaunch) return;

        var snapshot = _snapshotStore.TryLoad();
        if (snapshot is null) return; // 旧版配置 / 快照损坏 → 退化为「解锁后开启」，不阻塞启动

        try
        {
            var config = snapshot.ToConfig();
            _config = config;
            _ruleset = Ruleset.Build(config.Groups, config.Rules, config.AllowlistSchedule);
            _controller = new ProtectionController(config, _ruleset);
            _controller.Blocked += OnControllerBlocked;
            _controller.Start();

            _autoProtectionStarted = true;
            config.Port = _controller.Port;
            config.PacSeq = _controller.PacSeq;
            WriteSnapshot(config);
            StateChanged?.Invoke();
        }
        catch
        {
            // 端口占用 / 注册表异常等：不弹错、不阻塞 UI，退化为解锁后开启
            _controller = null;
            _config = null;
            _ruleset = null;
            _autoProtectionStarted = false;
        }
    }

    /// <summary>写无密码启动快照。失败不影响主配置保存。</summary>
    private void WriteSnapshot(AppConfig config)
    {
        try { _snapshotStore?.Save(ProtectionSnapshot.FromConfig(config)); }
        catch { /* 快照是加速路径，不是唯一数据源 */ }
    }

    // ── 密码处理 ──────────────────────────────────────────────
    // SecureString → string 仅用于即时传给 Core（Load/Save/Create/Verify）。
    // .NET 的 string 不可变、无法主动清零，故生命周期严格限制在本方法调用内：
    // 不落盘、不写日志、不进入异常消息、不缓存。
    private static string Reveal(SecureString secure)
    {
        var ptr = Marshal.SecureStringToBSTR(secure);
        try { return Marshal.PtrToStringBSTR(ptr) ?? string.Empty; }
        finally { Marshal.ZeroFreeBSTR(ptr); }
    }

    private string SessionPassword() => Reveal(_sessionPassword!);

    /// <summary>
    /// 把解密后的完整配置绑定到保护控制器。
    /// 开机自启已在运行时：复用端点（Reconfigure），绝不二次 Start，避免端口冲突与重复写注册表。
    /// </summary>
    private void BindLoadedConfig(AppConfig config)
    {
        if (_controller is { IsActive: true } && _autoProtectionStarted)
            config.Port = _controller.Port;
        config.PacSeq = _controller?.PacSeq ?? config.PacSeq;
        config.ProtectionEnabled = _controller?.IsActive ?? config.ProtectionEnabled;

        _config = config;
        _ruleset = Ruleset.Build(config.Groups, config.Rules, config.AllowlistSchedule);

        if (_controller is null)
        {
            _controller = new ProtectionController(config, _ruleset);
            _controller.Blocked += OnControllerBlocked;
        }
        else
        {
            _controller.Reconfigure(config, _ruleset);
        }

        if (!_controller.IsActive && config.ProtectionEnabled) _controller.Start();
    }

    // ── 首次运行：设置管理密码 + 生成恢复码 ───────────────────
    /// <summary>
    /// 生成默认规则 → 生成恢复码并写入 RecoveryHash/Salt → 构造配置 → 启动保护 →
    /// 加密保存（Save 内自动写入密码哈希与盐）。保存安排在 Start 之后，以一并持久化
    /// Port / ProxyBackup / ProtectionEnabled，并同步写无密码启动快照。
    /// 返回值 = 恢复码明文，由调用方（LockPage）显示一次让用户抄录，绝不落盘。
    /// </summary>
    public string CompleteFirstRun(SecureString password)
    {
        var plain = Reveal(password);
        try
        {
            // 恢复码明文只存在于本方法返回值；配置内仅存哈希与盐
            var recoveryCode = RecoveryCode.Generate();
            var (rHash, rSalt) = PasswordHasher.Create(recoveryCode);

            var (groups, rules) = DefaultRules.CreateDefault();
            var config = new AppConfig { Groups = groups, Rules = rules };
            config.RecoveryHash = rHash;
            config.RecoverySalt = rSalt;
            _config = config;
            _ruleset = Ruleset.Build(groups, rules, config.AllowlistSchedule);
            _controller = new ProtectionController(config, _ruleset);
            _controller.Blocked += OnControllerBlocked;
            _controller.Start();

            _store!.Save(config, plain);
            WriteSnapshot(config);
            _sessionPassword = password.Copy();
            IsUnlocked = true;
            IsEditUnlocked = false; // 规则编辑默认锁定，需再次验证密码进入编辑模式
            NeedPasswordReset = false;

            ThemeService.Apply(config.Theme);
            StateChanged?.Invoke();
            RulesChanged?.Invoke();

            return recoveryCode;
        }
        finally
        {
            // plain：不可变 string，交由 GC 回收；不作任何持久化
        }
    }

    // ── 之后启动：输入密码解锁 ────────────────────────────────
    /// <summary>
    /// 输入密码解锁。若开机自启已自动进入防护状态，则保持端点不中断、只同步完整配置。
    /// 失败由 SecureConfigStore.Load 抛 ConfigCorruptedException，消息统一为 UnifiedError。
    /// 不提供任何无密码重置入口。
    /// </summary>
    public void UnlockWithPassword(SecureString password)
    {
        var plain = Reveal(password);
        try
        {
            var config = _store!.Load(plain);
            BindLoadedConfig(config);
            _sessionPassword = password.Copy();
            IsUnlocked = true;
            IsEditUnlocked = false;
            NeedPasswordReset = false;

            SaveConfig(); // 持久化 ProtectionEnabled / ProxyBackup / Port + 启动快照
            ThemeService.Apply(config.Theme);
            StateChanged?.Invoke();
            RulesChanged?.Invoke();
        }
        finally
        {
            // 同上：plain 不持久化
        }
    }

    // ── 恢复码解锁（仅限忘记管理密码时）──────────────────────
    /// <summary>
    /// 用恢复码解锁本次会话：恢复码只用于验证身份，绝不能当密码常驻内存——
    /// 会话密码字段保持为空，随后必须调用 <see cref="ResetPasswordAfterRecovery"/> 强制重设。
    /// 失败由 SecureConfigStore.LoadWithRecoveryCode 抛 ConfigCorruptedException（UnifiedError）。
    /// </summary>
    public void UnlockWithRecoveryCode(string code)
    {
        var config = _store!.LoadWithRecoveryCode(RecoveryCode.Normalize(code)!);
        BindLoadedConfig(config);
        IsUnlocked = true;
        IsEditUnlocked = false;
        NeedPasswordReset = true; // 调用方须立即引导重设密码

        // 注意：此处不调用 SaveConfig()。会话密码为空，无法（也不应）以恢复码重新加密保存。
        ThemeService.Apply(config.Theme);
        StateChanged?.Invoke();
        RulesChanged?.Invoke();
    }

    // ── 恢复码解锁后的强制重设 ────────────────────────────────
    /// <summary>
    /// 用新密码重建 PasswordHash/Salt，并轮换恢复码（旧恢复码立即失效）。
    /// 返回值 = 新恢复码明文，由调用方显示一次让用户重新抄录。
    /// </summary>
    public string ResetPasswordAfterRecovery(SecureString newPassword)
    {
        var plain = Reveal(newPassword);
        try
        {
            var (hash, salt) = PasswordHasher.Create(plain);
            Config.PasswordHash = hash;
            Config.PasswordSalt = salt;

            // 轮换恢复码：旧码作废，新码只以哈希+盐落盘
            var recoveryCode = RecoveryCode.Generate();
            var (rHash, rSalt) = PasswordHasher.Create(recoveryCode);
            Config.RecoveryHash = rHash;
            Config.RecoverySalt = rSalt;

            _store!.Save(Config, plain); // 用新密码重新加密并重新签名
            if (_controller is not null)
            {
                Config.Port = _controller.Port;
                Config.PacSeq = _controller.PacSeq;
            }
            WriteSnapshot(Config);
            _sessionPassword?.Dispose();
            _sessionPassword = newPassword.Copy();
            NeedPasswordReset = false;

            StateChanged?.Invoke();
            return recoveryCode;
        }
        finally
        {
            // plain 不持久化
        }
    }

    // ── 重新生成恢复码（已解锁状态）───────────────────────────
    /// <summary>
    /// 轮换恢复码（调用方需先经 RequirePassword 验证管理密码）。
    /// 返回值 = 新恢复码明文，旧码立即失效。
    /// </summary>
    public string RegenerateRecoveryCode()
    {
        var recoveryCode = RecoveryCode.Generate();
        var (rHash, rSalt) = PasswordHasher.Create(recoveryCode);
        Config.RecoveryHash = rHash;
        Config.RecoverySalt = rSalt;
        SaveConfig(); // 已解锁，会话密码可用
        return recoveryCode;
    }

    // ── 配置保存 ──────────────────────────────────────────────
    public void SaveConfig()
    {
        if (_config is null || _sessionPassword is null || _controller is null) return;
        _config.Port = _controller.Port; // 保存前确保 controller.Port 已写入 config
        _config.PacSeq = _controller.PacSeq;
        _store!.Save(_config, SessionPassword());
        WriteSnapshot(_config);
    }

    // ── 规则变更（含组开关、单条开关、白名单时段、增删改、跨组移动）──
    public void RefreshRules()
    {
        _ruleset = Ruleset.Build(Config.Groups, Config.Rules, Config.AllowlistSchedule);
        _controller!.ApplyRules(_ruleset); // 同步端点判定引擎 + 重建 PAC
        SaveConfig();
        RulesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // ── 保护开关 ──────────────────────────────────────────────
    /// <summary>从托盘重新开启保护（需密码验证后调用）。</summary>
    public void StartProtection()
    {
        if (IsUnlockRequired) { ShowUnlockRequired(); return; }
        _controller!.Start();
        SaveConfig();
        StateChanged?.Invoke();
    }

    /// <summary>关闭保护（需密码验证后调用）：解除拦截，进程保留。仅本次会话有效，下次自启会重新自动防护。</summary>
    public void CloseProtection()
    {
        if (IsUnlockRequired) { ShowUnlockRequired(); return; }
        _controller!.StopProtection();
        SaveConfig();
        StateChanged?.Invoke();
    }

    /// <summary>自启未解锁时统一提示：敏感操作需先在登录页解锁。</summary>
    private static void ShowUnlockRequired() =>
        System.Windows.MessageBox.Show(
            "防护已由开机自启自动开启。关闭保护或退出程序前，请先打开主界面并输入管理密码解锁。",
            "需要解锁",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);

    /// <summary>
    /// 退出程序统一入口（所有 UI 出口都调它，决策只此一处）：
    /// - 恢复码解锁但未重设：身份已由恢复码验证，再要旧密码会僵局 → 确认框后直接退出（本次会话不保存）；
    /// - 已解锁：密码验证（RequirePassword）通过后退出；
    /// - 未解锁（含首启页、开机自启）：拦截未开启或已由 Shutdown 精确还原 → 确认后直接退出。
    /// </summary>
    public void RequestExit()
    {
        // 自启未解锁：防护真实在跑，禁止未经解锁就停止拦截；关闭窗口也只最小化。
        if (IsUnlockRequired)
        {
            ShowUnlockRequired();
            return;
        }
        if (NeedPasswordReset)
        {
            var answer = System.Windows.MessageBox.Show(
                "尚未设置新的管理密码，本次会话的修改不会被保存。\n\n仍要退出吗？",
                "退出程序",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (answer == System.Windows.MessageBoxResult.Yes) ExitApplicationRaw();
            return;
        }
        if (IsUnlocked)
        {
            RequirePassword("退出程序", ExitApplicationRaw);
            return;
        }
        var ans = System.Windows.MessageBox.Show(
            _autoProtectionStarted
                ? "退出程序？开机自启已自动开启防护，退出后将停止拦截并还原代理设置。"
                : "退出程序？（尚未完成设置，不会产生任何拦截残留）",
            "退出程序",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (ans == System.Windows.MessageBoxResult.Yes) ExitApplicationRaw();
    }

    /// <summary>
    /// 退出流程本体（调用方已完成身份决策）：Shutdown 保护器 → 保存配置（如有）→ 释放托盘 → Shutdown。
    /// </summary>
    public void ExitApplicationRaw()
    {
        IsExiting = true;
        try { _controller?.Shutdown(); } catch { /* 退出路径不再上抛 */ }
        try { SaveConfig(); } catch { /* 同上；无配置时 SaveConfig 内部直接返回 */ }
        // 自启未解锁时没有会话密码，也要把「本次已正常退出」写进快照，避免状态漂移
        if (_config is not null && _sessionPassword is null) WriteSnapshot(_config);
        BeforeExit?.Invoke();
        _sessionPassword?.Dispose();
        _sessionPassword = null;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 主窗口 Closing 处理。返回 true = 正在退出，调用方无需取消；
    /// 返回 false = 调用方应取消关闭并保持/隐藏窗口。
    /// 退出决策本身全部委托 RequestExit()（唯一决策点）。
    /// </summary>
    public bool OnWindowClose()
    {
        // 决策统一在 AccessPolicy：自启未解锁 → 只最小化；首启 → 允许确认退出；
        // 已解锁 → 按 CloseAction；恢复码解锁未重设 → 允许确认退出（僵局豁免）。
        if (!AccessPolicy.AllowsExitOnWindowClose(
                IsFirstRun, IsUnlocked, IsProtectionActive, NeedPasswordReset,
                _config?.CloseAction))
            return false;

        var before = IsExiting;
        RequestExit();
        return IsExiting && !before;
    }

    // ── 修改管理密码 ──────────────────────────────────────────
    /// <summary>调用方需先用 RequirePassword 验证原密码，再传入新密码。</summary>
    public void ChangePassword(SecureString newPassword)
    {
        var plain = Reveal(newPassword);
        try
        {
            var (hash, salt) = PasswordHasher.Create(plain);
            Config.PasswordHash = hash;
            Config.PasswordSalt = salt;
            _store!.Save(Config, plain); // 用新密码 + 新盐重新加密并重新签名
            WriteSnapshot(Config);
            _sessionPassword?.Dispose();
            _sessionPassword = newPassword.Copy();
        }
        finally
        {
            // plain 不持久化
        }
    }

    // ── 密码门禁 ──────────────────────────────────────────────
    /// <summary>单独验证密码（关闭保护、退出、改密、导出/清空日志、自启开关等）。</summary>
    public bool VerifyPassword(SecureString password)
    {
        var plain = Reveal(password);
        return PasswordHasher.Verify(plain, Config.PasswordHash, Config.PasswordSalt);
    }

    /// <summary>统一密码门禁入口：弹出 PasswordDialog，成功才执行 onSuccess。</summary>
    public void RequirePassword(string action, Action onSuccess)
    {
        var dialog = new PasswordDialog(action);
        var owner = System.Windows.Application.Current.MainWindow;
        // Owner 必须是已显示过的窗口；主窗口隐藏到托盘时不能设置，否则 ShowDialog 抛异常
        if (owner is not null && owner.IsLoaded && owner.IsVisible)
            dialog.Owner = owner;
        if (dialog.ShowDialog() == true)
        {
            onSuccess();
        }
    }

    // ── 编辑锁（会话内一次解锁；5 分钟超时或隐藏到托盘自动锁定）─
    public void UnlockEdit()
    {
        IsEditUnlocked = true;
        EnsureEditTimer();
        _editLockTimer!.Stop();
        _editLockTimer.Start();
        EditLockChanged?.Invoke();
    }

    public void LockEdit()
    {
        if (!IsEditUnlocked) return;
        IsEditUnlocked = false;
        _editLockTimer?.Stop();
        EditLockChanged?.Invoke();
    }

    /// <summary>编辑活动重置超时计时。</summary>
    public void NoteEditActivity()
    {
        if (!IsEditUnlocked || _editLockTimer is null) return;
        _editLockTimer.Stop();
        _editLockTimer.Start();
    }

    private void EnsureEditTimer()
    {
        if (_editLockTimer is not null) return;
        _editLockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _editLockTimer.Tick += (_, _) => LockEdit();
    }

    // ── 拦截事件转发（切回 UI 线程，便于托盘与页面直接更新）──
    private void OnControllerBlocked(object? sender, BlockLogEntry entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Blocked?.Invoke(this, entry);
            return;
        }
        dispatcher.BeginInvoke(new Action(() => Blocked?.Invoke(this, entry)));
    }

    /// <summary>供设置页切换主题时调用（写配置并保存）。</summary>
    public void SetTheme(string theme)
    {
        Config.Theme = theme;
        ThemeService.Apply(theme);
        SaveConfig();
    }

    /// <summary>供设置页写日志保留天数。</summary>
    public void SetLogRetentionDays(int days)
    {
        Config.LogRetentionDays = days;
        SaveConfig();
        Log.Cleanup(days);
    }

    /// <summary>供设置页写通知开关。</summary>
    public void SetNotifyEnabled(bool enabled)
    {
        Config.NotifyEnabled = enabled;
        SaveConfig();
    }

    /// <summary>供设置页写「关闭窗口时」行为："hide" 最小化到托盘 / "exit" 停止拦截并退出。</summary>
    public void SetCloseAction(string value)
    {
        Config.CloseAction = value == "exit" ? "exit" : "hide";
        SaveConfig();
    }

    /// <summary>供设置页切换「跟随加速器上游」；变更后立即重建 PAC 并刷新。</summary>
    public void SetFollowUpstream(bool enabled)
    {
        Config.FollowUpstreamProxy = enabled;
        if (_controller is not null)
        {
            _controller.RefreshUpstream(); // 内部 seq+1 并更新 AutoConfigURL
            SaveConfig();
        }
        StateChanged?.Invoke();
    }

    /// <summary>供设置页同步开机自启配置。</summary>
    public void SetAutoStartFlag(bool enabled)
    {
        Config.AutoStart = enabled;
        SaveConfig();
    }

    /// <summary>
    /// 供设置页保存白名单生效时段：规范化 → 写配置 → 重建规则集（同步端点引擎与 PAC）→ 持久化。
    /// 时段外的白名单自动失效并回退常规拦截判定，由 RuleEngine / PacGenerator 共同保证。
    /// </summary>
    public void SetAllowlistSchedule(AllowlistSchedule schedule)
    {
        schedule ??= new AllowlistSchedule();
        schedule.Normalize();
        Config.AllowlistSchedule = schedule;
        RefreshRules();
    }
}
