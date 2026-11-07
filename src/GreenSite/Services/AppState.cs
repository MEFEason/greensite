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
/// </summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    private readonly string _configDirectory;
    private SecureConfigStore? _store;
    private AppConfig? _config;
    private Ruleset? _ruleset;
    private ProtectionController? _controller;
    private SecureString? _sessionPassword;
    private DispatcherTimer? _editLockTimer;

    private AppState()
    {
        // %LOCALAPPDATA%\GreenSite\config.bin
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
    /// <summary>规则编辑模式是否解锁（会话内一次解锁，超时或隐藏到托盘自动锁定）。</summary>
    public bool IsEditUnlocked { get; private set; }
    public bool IsProtectionActive => _controller?.IsActive ?? false;

    // ── 事件（供各页面、托盘订阅；均在 UI 线程触发）────────────
    public event EventHandler<BlockLogEntry>? Blocked;
    public event Action? StateChanged;
    public event Action? RulesChanged;
    public event Action? EditLockChanged;
    public event Action? BeforeExit;

    /// <summary>应用启动时调用，判断首次运行。</summary>
    public void Initialize()
    {
        _store = new SecureConfigStore(_configDirectory);
        IsFirstRun = !_store.Exists;
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

    // ── 首次运行：设置管理密码 ────────────────────────────────
    /// <summary>
    /// 生成 6 组默认规则 → 构造配置 → 启动保护 → 加密保存（Save 内自动写入哈希与盐）。
    /// 保存安排在 Start 之后，以一并持久化 Port / ProxyBackup / ProtectionEnabled。
    /// </summary>
    public void CompleteFirstRun(SecureString password)
    {
        var plain = Reveal(password);
        try
        {
            var (groups, rules) = DefaultRules.CreateDefault();
            var config = new AppConfig { Groups = groups, Rules = rules };
            _config = config;
            _ruleset = Ruleset.Build(groups, rules);
            _controller = new ProtectionController(config, _ruleset);
            _controller.Blocked += OnControllerBlocked;
            _controller.Start();

            _store!.Save(config, plain);
            _sessionPassword = password.Copy();
            IsUnlocked = true;
            IsEditUnlocked = false; // 规则编辑默认锁定，需再次验证密码进入编辑模式

            ThemeService.Apply(config.Theme);
            StateChanged?.Invoke();
            RulesChanged?.Invoke();
        }
        finally
        {
            // plain：不可变 string，交由 GC 回收；不作任何持久化
        }
    }

    // ── 之后启动：输入密码解锁 ────────────────────────────────
    /// <summary>
    /// 冷启动 UI 锁定，但解锁成功后保护自动开启（无需手动点开启）。
    /// 失败由 SecureConfigStore.Load 抛 ConfigCorruptedException，消息统一为 UnifiedError。
    /// 不提供任何无密码重置入口。
    /// </summary>
    public void UnlockWithPassword(SecureString password)
    {
        var plain = Reveal(password);
        try
        {
            var config = _store!.Load(plain);
            _config = config;
            _ruleset = Ruleset.Build(config.Groups, config.Rules);
            _controller = new ProtectionController(config, _ruleset);
            _controller.Blocked += OnControllerBlocked;
            _sessionPassword = password.Copy();
            _controller.Start();
            IsUnlocked = true;
            IsEditUnlocked = false;

            SaveConfig(); // 持久化 ProtectionEnabled / ProxyBackup / Port
            ThemeService.Apply(config.Theme);
            StateChanged?.Invoke();
            RulesChanged?.Invoke();
        }
        finally
        {
            // 同上：plain 不持久化
        }
    }

    // ── 配置保存 ──────────────────────────────────────────────
    public void SaveConfig()
    {
        if (_config is null || _sessionPassword is null || _controller is null) return;
        _config.Port = _controller.Port; // 保存前确保 controller.Port 已写入 config
        var plain = SessionPassword();
        _store!.Save(_config, plain);
    }

    // ── 规则变更（含组开关、单条开关、增删改、跨组移动）──────
    public void RefreshRules()
    {
        _ruleset = Ruleset.Build(Config.Groups, Config.Rules);
        _controller!.ApplyRules(_ruleset);
        SaveConfig();
        RulesChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // ── 保护开关 ──────────────────────────────────────────────
    /// <summary>从托盘重新开启保护（需密码验证后调用）。</summary>
    public void StartProtection()
    {
        _controller!.Start();
        SaveConfig();
        StateChanged?.Invoke();
    }

    /// <summary>关闭保护（需密码验证后调用）：解除拦截，进程保留。</summary>
    public void CloseProtection()
    {
        _controller!.StopProtection();
        SaveConfig();
        StateChanged?.Invoke();
    }

    /// <summary>退出程序（需密码验证后调用）：恢复原代理、停止拦截、结束进程。</summary>
    public void ExitApplication()
    {
        IsExiting = true;
        try { _controller?.Shutdown(); } catch { /* 退出路径不再上抛 */ }
        try { SaveConfig(); } catch { /* 同上 */ }
        BeforeExit?.Invoke();
        _sessionPassword?.Dispose();
        _sessionPassword = null;
        System.Windows.Application.Current.Shutdown();
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

    /// <summary>供设置页同步开机自启配置。</summary>
    public void SetAutoStartFlag(bool enabled)
    {
        Config.AutoStart = enabled;
        SaveConfig();
    }
}
