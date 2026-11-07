using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using GreenSite.Core.Security;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>
/// 设置页：开机自启（HKCU）、主题、通知、日志保留天数、修改密码、关闭保护、退出程序。
/// 自启开关、改密、关闭保护、退出均通过 RequirePassword 每次单独验证。
/// </summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    // Refresh / 回滚期间抑制控件事件，避免误触发保存
    private bool _updating;

    public SettingsPage() => InitializeComponent();

    public void Refresh()
    {
        var s = AppState.Instance;
        if (!s.IsUnlocked) return;

        _updating = true;
        AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
        NotifyCheck.IsChecked = s.Config.NotifyEnabled;
        ThemeLightRadio.IsChecked = !string.Equals(s.Config.Theme, "dark", System.StringComparison.OrdinalIgnoreCase);
        ThemeDarkRadio.IsChecked = string.Equals(s.Config.Theme, "dark", System.StringComparison.OrdinalIgnoreCase);
        RetentionBox.Text = s.Config.LogRetentionDays.ToString();
        _updating = false;
    }

    // ── 常规 ──────────────────────────────────────────────────
    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var value = AutoStartCheck.IsChecked == true;
        var applied = false;
        AppState.Instance.RequirePassword("开机自启开关", () =>
        {
            AutoStartService.SetEnabled(value); // 只写 HKCU
            AppState.Instance.SetAutoStartFlag(value);
            applied = true;
        });
        if (!applied)
        {
            _updating = true;
            AutoStartCheck.IsChecked = !value;
            _updating = false;
        }
    }

    private void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        AppState.Instance.SetNotifyEnabled(NotifyCheck.IsChecked == true);
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        AppState.Instance.SetTheme(ThemeDarkRadio.IsChecked == true ? "dark" : "light");
    }

    private void RetentionApply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionBox.Text.Trim(), out var days) || days is < 1 or > 365)
        {
            System.Windows.MessageBox.Show("请输入 1~365 之间的整数", "日志保留天数",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AppState.Instance.SetLogRetentionDays(days);
        System.Windows.MessageBox.Show("已应用", "日志保留天数",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 安全与保护 ────────────────────────────────────────────
    private void CloseProtection_Click(object sender, RoutedEventArgs e) =>
        AppState.Instance.RequirePassword("关闭保护", () => AppState.Instance.CloseProtection());

    private void Exit_Click(object sender, RoutedEventArgs e) =>
        AppState.Instance.RequirePassword("退出程序", () => AppState.Instance.ExitApplication());

    /// <summary>原密码由统一密码门禁验证，面板内输入新密码两次。</summary>
    private void ChangePwd_Click(object sender, RoutedEventArgs e)
    {
        AppState.Instance.RequirePassword("修改密码", () =>
        {
            ChangePwdPanel.Visibility = Visibility.Visible;
            ChangePwdError.Visibility = Visibility.Collapsed;
            NewPwdBox.Clear();
            ConfirmPwdBox.Clear();
            NewPwdBox.Focus();
        });
    }

    private void ChangePwdConfirm_Click(object sender, RoutedEventArgs e)
    {
        ChangePwdError.Visibility = Visibility.Collapsed;
        using var first = NewPwdBox.SecurePassword.Copy();
        using var second = ConfirmPwdBox.SecurePassword.Copy();

        if (first.Length < PasswordHasher.MinLength)
        {
            ShowChangeError($"新密码至少 {PasswordHasher.MinLength} 位");
            return;
        }
        if (!SecureEquals(first, second))
        {
            ShowChangeError("两次输入不一致");
            return;
        }

        try
        {
            AppState.Instance.ChangePassword(first);
        }
        catch
        {
            ShowChangeError("修改失败，请重试");
            return;
        }

        NewPwdBox.Clear();
        ConfirmPwdBox.Clear();
        ChangePwdPanel.Visibility = Visibility.Collapsed;
        System.Windows.MessageBox.Show("管理密码已修改，请牢记新密码。", "修改密码",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowChangeError(string message)
    {
        ChangePwdError.Text = message;
        ChangePwdError.Visibility = Visibility.Visible;
    }

    /// <summary>比较两个 SecureString，仅本地一致性校验，不写日志不落盘。</summary>
    private static bool SecureEquals(SecureString a, SecureString b)
    {
        if (a.Length != b.Length) return false;
        var pa = Marshal.SecureStringToBSTR(a);
        var pb = Marshal.SecureStringToBSTR(b);
        try
        {
            return (Marshal.PtrToStringBSTR(pa) ?? string.Empty)
                == (Marshal.PtrToStringBSTR(pb) ?? string.Empty);
        }
        finally
        {
            Marshal.ZeroFreeBSTR(pa);
            Marshal.ZeroFreeBSTR(pb);
        }
    }
}
