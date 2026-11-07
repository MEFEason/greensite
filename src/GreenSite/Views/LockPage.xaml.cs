using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GreenSite.Core.Security;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>
/// 冷启动锁定页。首次运行显示「设置管理密码」→「恢复码展示」；
/// 之后显示「输入密码解锁」，并提供恢复码解锁入口（RecoveryDialog）。
/// 恢复码解锁成功后进入「强制重设密码」，重设返回的新恢复码复用展示面板。
/// 解锁失败统一显示 UnifiedError（不区分密码错 / 配置篡改）。
/// 失败冷却由 AppState.Gate（PasswordGate）统一负责。
/// </summary>
public partial class LockPage : System.Windows.Controls.UserControl
{
    public event EventHandler? Unlocked;

    private readonly DispatcherTimer _cooldownTimer;

    /// <summary>待展示给用户的恢复码明文（仅在切换到展示面板期间驻留，抄录确认后清空）。</summary>
    private string? _pendingRecoveryCode;

    public LockPage()
    {
        InitializeComponent();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => RefreshCooldown();

        if (AppState.Instance.IsFirstRun)
        {
            ShowPanel(SetupPanel);
        }
        else if (AppState.Instance.NeedPasswordReset)
        {
            // 恢复码解锁路径：上次用恢复码解锁后尚未完成重设，直接进入强制重设面板。
            ShowPanel(ResetPanel);
        }
        else
        {
            ShowPanel(UnlockPanel);
            RefreshCooldown();
        }
    }

    /// <summary>五个面板互斥显示；离开解锁面板时停止冷却计时（UI 不可见时无需刷新）。</summary>
    private void ShowPanel(FrameworkElement target)
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Collapsed;
        ResetPanel.Visibility = Visibility.Collapsed;
        RecoveryPanel.Visibility = Visibility.Collapsed;
        target.Visibility = Visibility.Visible;

        if (!ReferenceEquals(target, UnlockPanel))
        {
            _cooldownTimer.Stop();
            CooldownText.Visibility = Visibility.Collapsed;
            UnlockButton.IsEnabled = true;
            UnlockPasswordBox.IsEnabled = true;
        }
    }

    // ── 首次运行 ──────────────────────────────────────────────
    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        SetupErrorText.Visibility = Visibility.Collapsed;
        using var first = NewPasswordBox.SecurePassword.Copy();
        using var second = ConfirmPasswordBox.SecurePassword.Copy();

        if (first.Length < PasswordHasher.MinLength)
        {
            ShowSetupError($"管理密码至少 {PasswordHasher.MinLength} 位");
            return;
        }
        if (!SecureEquals(first, second))
        {
            ShowSetupError("两次输入不一致");
            return;
        }

        string recoveryCode;
        try
        {
            recoveryCode = AppState.Instance.CompleteFirstRun(first);
        }
        catch
        {
            // 不把异常内容（可能含路径）展示给用户，更不涉及密码
            ShowSetupError("初始化失败，请检查程序数据文件是否完整");
            return;
        }

        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        ShowRecoveryPanel(recoveryCode);
    }

    private void ShowSetupError(string message)
    {
        SetupErrorText.Text = message;
        SetupErrorText.Visibility = Visibility.Visible;
    }

    // ── 之后启动解锁 ──────────────────────────────────────────
    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var gate = AppState.Instance.Gate;
        var now = DateTimeOffset.Now;
        if (gate.IsLocked(now)) { RefreshCooldown(); return; }

        UnlockErrorText.Visibility = Visibility.Collapsed;
        using var secure = UnlockPasswordBox.SecurePassword.Copy();
        try
        {
            AppState.Instance.UnlockWithPassword(secure);
        }
        catch
        {
            // 密码错误、配置篡改、IO 异常一律统一提示
            UnlockPasswordBox.Clear();
            UnlockErrorText.Text = SecureConfigStore.UnifiedError;
            UnlockErrorText.Visibility = Visibility.Visible;
            gate.RecordFailure(now);
            RefreshCooldown();
            return;
        }

        UnlockPasswordBox.Clear();
        gate.RecordSuccess();
        OnUnlocked();
    }

    // ── 忘记管理密码：恢复码解锁 ──────────────────────────────
    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RecoveryDialog();
        var owner = System.Windows.Application.Current.MainWindow;
        // Owner 必须是已显示过的窗口；主窗口隐藏到托盘时不能设置，否则 ShowDialog 抛异常
        if (owner is not null && owner.IsLoaded && owner.IsVisible)
            dialog.Owner = owner;

        if (dialog.ShowDialog() != true) return; // 用户取消，停留在解锁面板

        // 恢复码解锁成功：UnlockWithRecoveryCode 置 NeedPasswordReset=true，
        // 必须重设管理密码后才放行进入主界面；若契约未来允许免重设则直接解锁。
        if (AppState.Instance.NeedPasswordReset)
        {
            ShowPanel(ResetPanel);
        }
        else
        {
            OnUnlocked();
        }
    }

    // ── 强制重设管理密码 ──────────────────────────────────────
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetErrorText.Visibility = Visibility.Collapsed;
        using var first = ResetNewPasswordBox.SecurePassword.Copy();
        using var second = ResetConfirmPasswordBox.SecurePassword.Copy();

        if (first.Length < PasswordHasher.MinLength)
        {
            ShowResetError($"管理密码至少 {PasswordHasher.MinLength} 位");
            return;
        }
        if (!SecureEquals(first, second))
        {
            ShowResetError("两次输入不一致");
            return;
        }

        string recoveryCode;
        try
        {
            recoveryCode = AppState.Instance.ResetPasswordAfterRecovery(first);
        }
        catch
        {
            ShowResetError("重设失败，请检查程序数据文件是否完整");
            return;
        }

        ResetNewPasswordBox.Clear();
        ResetConfirmPasswordBox.Clear();
        // 重设成功返回新恢复码，复用展示面板；抄录确认后才触发 Unlocked 进入主界面
        ShowRecoveryPanel(recoveryCode);
    }

    private void ShowResetError(string message)
    {
        ResetErrorText.Text = message;
        ResetErrorText.Visibility = Visibility.Visible;
    }

    // ── 恢复码展示（首次设置 / 重设后共用）────────────────────
    private void ShowRecoveryPanel(string recoveryCode)
    {
        _pendingRecoveryCode = recoveryCode;
        RecoveryCodeBox.Text = recoveryCode;
        ShowPanel(RecoveryPanel);
        RecoveryCodeBox.SelectAll();
        RecoveryCodeBox.Focus();
    }

    private void RecoveryConfirm_Click(object sender, RoutedEventArgs e)
    {
        // 用户确认已抄录：清空界面中的恢复码明文引用再进入主界面
        _pendingRecoveryCode = null;
        RecoveryCodeBox.Clear();
        OnUnlocked();
    }

    // ── 退出程序 ──────────────────────────────────────────────
    // 安全推理：
    // - 首次运行（尚无配置文件）：磁盘无加密数据、拦截未开启，直接 Shutdown 无风险，
    //   且此时 Config 未加载，任何密码验证路径都会抛异常。
    // - 冷启动未解锁（IsUnlocked==false）：拦截本来就未开启，退出不会解除任何保护；
    //   且 RequirePassword → VerifyPassword 会读取尚未加载的 Config 并抛异常，
    //   故此处绝不能弹密码框，直接 ExitApplicationRaw()（跳过验证的原始退出）。
    // - 已解锁（IsUnlocked==true，如恢复码解锁后停留在重设/展示面板）：拦截已开启，
    //   必须经 RequirePassword 验证，防止未经认证的人一键解除保护；
    //   此时 Config 已加载，VerifyPassword 可正常工作。
    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Instance;
        if (s.IsFirstRun)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }
        if (!s.IsUnlocked)
        {
            s.ExitApplicationRaw();
            return;
        }
        s.RequirePassword("退出程序", s.ExitApplication);
    }

    private void OnUnlocked()
    {
        Unlocked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>冷却期间禁用输入，每秒刷新剩余秒数。</summary>
    private void RefreshCooldown()
    {
        var remaining = AppState.Instance.Gate.RemainingCooldown(DateTimeOffset.Now);
        if (remaining > TimeSpan.Zero)
        {
            var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
            CooldownText.Text = $"尝试过于频繁，请等待 {seconds} 秒后重试";
            CooldownText.Visibility = Visibility.Visible;
            UnlockButton.IsEnabled = false;
            UnlockPasswordBox.IsEnabled = false;
            _cooldownTimer.Start();
        }
        else
        {
            CooldownText.Visibility = Visibility.Collapsed;
            UnlockButton.IsEnabled = true;
            UnlockPasswordBox.IsEnabled = true;
            _cooldownTimer.Stop();
        }
    }

    /// <summary>比较两个 SecureString 内容，仅用于本地一致性校验，不写日志不落盘。</summary>
    private static bool SecureEquals(SecureString a, SecureString b)
    {
        if (a.Length != b.Length) return false;
        var pa = Marshal.SecureStringToBSTR(a);
        var pb = Marshal.SecureStringToBSTR(b);
        try
        {
            var sa = Marshal.PtrToStringBSTR(pa) ?? string.Empty;
            var sb = Marshal.PtrToStringBSTR(pb) ?? string.Empty;
            return sa == sb;
        }
        finally
        {
            Marshal.ZeroFreeBSTR(pa);
            Marshal.ZeroFreeBSTR(pb);
        }
    }
}
