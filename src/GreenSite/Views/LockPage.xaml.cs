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
/// 冷启动锁定页。首次运行显示「设置管理密码」，之后显示「输入密码解锁」。
/// 解锁失败统一显示 UnifiedError（不区分密码错 / 配置篡改），不提供无密码重置入口。
/// 失败冷却由 AppState.Gate（PasswordGate）统一负责。
/// </summary>
public partial class LockPage : System.Windows.Controls.UserControl
{
    public event EventHandler? Unlocked;

    private readonly DispatcherTimer _cooldownTimer;

    public LockPage()
    {
        InitializeComponent();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => RefreshCooldown();

        if (AppState.Instance.IsFirstRun)
        {
            SetupPanel.Visibility = Visibility.Visible;
        }
        else
        {
            UnlockPanel.Visibility = Visibility.Visible;
            RefreshCooldown();
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

        try
        {
            AppState.Instance.CompleteFirstRun(first);
        }
        catch
        {
            // 不把异常内容（可能含路径）展示给用户，更不涉及密码
            ShowSetupError("初始化失败，请检查程序数据文件是否完整");
            return;
        }

        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        Unlocked?.Invoke(this, EventArgs.Empty);
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
