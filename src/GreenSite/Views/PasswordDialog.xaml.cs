using System;
using System.Windows;
using System.Windows.Threading;
using GreenSite.Core.Security;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>
/// 统一密码门禁对话框。失败由 PasswordGate 记录冷却（5s→15s→60s→5m→15m），
/// 冷却期间禁用输入并显示剩余秒数。验证结果统一显示 UnifiedError，不区分原因。
/// </summary>
public partial class PasswordDialog : System.Windows.Window
{
    private readonly DispatcherTimer _cooldownTimer;

    public PasswordDialog(string action)
    {
        InitializeComponent();
        ActionText.Text = $"操作「{action}」需要验证管理密码。";

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => RefreshCooldown();
        RefreshCooldown();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var gate = AppState.Instance.Gate;
        var now = DateTimeOffset.Now;
        if (gate.IsLocked(now)) { RefreshCooldown(); return; }

        ErrorText.Visibility = Visibility.Collapsed;
        // 只经 SecurePassword / SecureString，用完清零副本
        using var secure = PasswordBox.SecurePassword.Copy();
        bool ok;
        try { ok = AppState.Instance.VerifyPassword(secure); }
        catch { ok = false; }
        PasswordBox.Clear();

        if (ok)
        {
            gate.RecordSuccess();
            DialogResult = true;
            Close();
        }
        else
        {
            gate.RecordFailure(now);
            ErrorText.Text = SecureConfigStore.UnifiedError;
            ErrorText.Visibility = Visibility.Visible;
            RefreshCooldown();
        }
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
            PasswordBox.IsEnabled = false;
            _cooldownTimer.Start();
        }
        else
        {
            CooldownText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = true;
            PasswordBox.IsEnabled = true;
            _cooldownTimer.Stop();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cooldownTimer.Stop();
        base.OnClosed(e);
    }
}
