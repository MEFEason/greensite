using System;
using System.Windows;
using System.Windows.Threading;
using GreenSite.Core.Security;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>
/// 恢复码解锁对话框。失败由 PasswordGate 记录冷却（与 PasswordDialog 共享门禁），
/// 冷却期间禁用输入并显示剩余秒数。验证结果统一显示 UnifiedError，不区分原因。
/// 解锁成功后 AppState.NeedPasswordReset 为 true，调用方须立即引导重设密码。
/// </summary>
public partial class RecoveryDialog : System.Windows.Window
{
    private readonly DispatcherTimer _cooldownTimer;

    public RecoveryDialog()
    {
        InitializeComponent();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => RefreshCooldown();
        RefreshCooldown();
        CodeBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var gate = AppState.Instance.Gate;
        var now = DateTimeOffset.Now;
        if (gate.IsLocked(now)) { RefreshCooldown(); return; }

        ErrorText.Visibility = Visibility.Collapsed;

        // 恢复码为一次性解锁凭据：Normalize 后直接传给 AppState，不做任何缓存
        var code = RecoveryCode.Normalize(CodeBox.Text);
        if (string.IsNullOrEmpty(code))
        {
            // 格式不合法（长度/字符不符）：直接提示，不消耗门禁失败次数
            ErrorText.Text = "恢复码格式不正确，应为 25 位代码（5 组，每组 5 位）";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            AppState.Instance.UnlockWithRecoveryCode(code);
        }
        catch
        {
            // 恢复码错误、配置篡改、IO 异常一律统一提示，不泄露原因
            gate.RecordFailure(now);
            ErrorText.Text = SecureConfigStore.UnifiedError;
            ErrorText.Visibility = Visibility.Visible;
            RefreshCooldown();
            return;
        }

        CodeBox.Clear();
        gate.RecordSuccess();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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
            OkButton.IsEnabled = false;
            CodeBox.IsEnabled = false;
            _cooldownTimer.Start();
        }
        else
        {
            CooldownText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
            CodeBox.IsEnabled = true;
            _cooldownTimer.Stop();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cooldownTimer.Stop();
        base.OnClosed(e);
    }
}
