using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using GreenSite.Core.Models;
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
        FollowUpstreamCheck.IsChecked = s.Config.FollowUpstreamProxy;
        CloseHideRadio.IsChecked = !string.Equals(s.Config.CloseAction, "exit", System.StringComparison.OrdinalIgnoreCase);
        CloseExitRadio.IsChecked = string.Equals(s.Config.CloseAction, "exit", System.StringComparison.OrdinalIgnoreCase);
        AllowScheduleCheck.IsChecked = s.Config.AllowlistSchedule.Enabled;
        RenderScheduleRows();
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

    private void CloseAction_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        AppState.Instance.SetCloseAction(CloseExitRadio.IsChecked == true ? "exit" : "hide");
    }

    private void FollowUpstream_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        // 常规偏好，非敏感项（与通知开关同级），免二次验证
        AppState.Instance.SetFollowUpstream(FollowUpstreamCheck.IsChecked == true);
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

    // ── 白名单生效时段 ────────────────────────────────────────
    /// <summary>按当前配置重绘时段行，并刷新底部状态与错误提示。</summary>
    private void RenderScheduleRows()
    {
        ScheduleRows.Children.Clear();
        var windows = AppState.Instance.Config.AllowlistSchedule.Windows;

        if (windows.Count == 0)
        {
            // 无时段时给出占位提示，CollectWindows 会跳过这类非行容器
            ScheduleRows.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "（暂无时段，白名单全天生效）",
                Style = (Style)FindResource("MutedTextStyle"),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        else
        {
            for (var i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                var index = i; // 局部变量捕获索引，避免删除按钮闭包延迟绑定
                var row = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                row.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "开始",
                    Style = (Style)FindResource("BodyTextStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                row.Children.Add(new System.Windows.Controls.TextBox
                {
                    Width = 70,
                    Text = w.Start,
                    Tag = "start",
                    Style = (Style)FindResource("InputTextBoxStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                row.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "结束",
                    Style = (Style)FindResource("BodyTextStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                row.Children.Add(new System.Windows.Controls.TextBox
                {
                    Width = 70,
                    Text = w.End,
                    Tag = "end",
                    Style = (Style)FindResource("InputTextBoxStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });

                var deleteButton = new System.Windows.Controls.Button
                {
                    Content = "删除",
                    Style = (Style)FindResource("SecondaryButtonStyle"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                deleteButton.Click += (_, _) =>
                {
                    var list = CollectWindows();
                    if (index < 0 || index >= list.Count) return;
                    list.RemoveAt(index);
                    AppState.Instance.SetAllowlistSchedule(new AllowlistSchedule
                    {
                        Enabled = AllowScheduleCheck.IsChecked == true,
                        Windows = list
                    });
                    RenderScheduleRows();
                };
                row.Children.Add(deleteButton);

                row.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "HH:mm",
                    Style = (Style)FindResource("MutedTextStyle"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                ScheduleRows.Children.Add(row);
            }
        }

        ScheduleSummary.Text = "当前：" + AppState.Instance.Config.AllowlistSchedule.Describe();
        ScheduleError.Text = string.Empty;
        ScheduleError.Visibility = Visibility.Collapsed;
    }

    /// <summary>收集当前行内输入，跳过结构不符（如占位提示）的行。</summary>
    private List<AllowTimeWindow> CollectWindows()
    {
        var list = new List<AllowTimeWindow>();
        foreach (var child in ScheduleRows.Children)
        {
            if (child is not System.Windows.Controls.StackPanel row) continue;
            string? start = null;
            string? end = null;
            foreach (var inner in row.Children)
            {
                if (inner is not System.Windows.Controls.TextBox tb || tb.Tag is not string tag) continue;
                if (tag == "start") start = tb.Text;
                else if (tag == "end") end = tb.Text;
            }
            if (start is null || end is null) continue;
            list.Add(new AllowTimeWindow { Start = start, End = end });
        }
        return list;
    }

    /// <summary>校验输入并构造待保存配置；格式非法时显示错误并返回 null。</summary>
    private AllowlistSchedule? ValidateAndBuild()
    {
        var schedule = new AllowlistSchedule
        {
            Enabled = AllowScheduleCheck.IsChecked == true,
            Windows = CollectWindows()
        };
        if (schedule.Enabled && schedule.Windows.Count > 0)
        {
            foreach (var w in schedule.Windows)
            {
                if (AllowlistSchedule.IsValidTime(w.Start) && AllowlistSchedule.IsValidTime(w.End)) continue;
                ScheduleError.Text = "时段格式须为 24 小时制 HH:mm，例如 09:00 或 22:30";
                ScheduleError.Visibility = Visibility.Visible;
                return null;
            }
        }
        return schedule;
    }

    private void ApplySchedule_Click(object sender, RoutedEventArgs e)
    {
        var schedule = ValidateAndBuild();
        if (schedule is null) return;
        try
        {
            AppState.Instance.SetAllowlistSchedule(schedule);
        }
        catch
        {
            ScheduleError.Text = "保存失败，请重试";
            ScheduleError.Visibility = Visibility.Visible;
            return;
        }
        ScheduleError.Visibility = Visibility.Collapsed;
        System.Windows.MessageBox.Show("已保存并立即生效。", "白名单生效时段",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        RenderScheduleRows();
    }

    private void AddWindow_Click(object sender, RoutedEventArgs e)
    {
        // 先收拢界面上未保存的输入，再追加默认时段并整体写回配置后重绘
        var list = CollectWindows();
        list.Add(new AllowTimeWindow { Start = "09:00", End = "18:00" });
        AppState.Instance.SetAllowlistSchedule(new AllowlistSchedule
        {
            Enabled = AllowScheduleCheck.IsChecked == true,
            Windows = list
        });
        RenderScheduleRows();
    }

    private void AllowSchedule_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var schedule = ValidateAndBuild();
        if (schedule is null)
        {
            // 校验失败：抑制事件并回滚到配置中的实际状态
            _updating = true;
            AllowScheduleCheck.IsChecked = AppState.Instance.Config.AllowlistSchedule.Enabled;
            _updating = false;
            return;
        }
        AppState.Instance.SetAllowlistSchedule(schedule);
        RenderScheduleRows();
    }

    // ── 安全与保护 ────────────────────────────────────────────
    private void CloseProtection_Click(object sender, RoutedEventArgs e) =>
        AppState.Instance.RequirePassword("关闭保护", () => AppState.Instance.CloseProtection());

    private void Exit_Click(object sender, RoutedEventArgs e) =>
        AppState.Instance.RequestExit(); // 退出决策唯一入口，内部按状态决定验证方式

    /// <summary>
    /// 重新生成恢复码：先经验证管理密码，旧恢复码立即失效。
    /// 新码明文只显示一次，提示用户立即抄录。
    /// </summary>
    private void RegenRecovery_Click(object sender, RoutedEventArgs e)
    {
        AppState.Instance.RequirePassword("重新生成恢复码", () =>
        {
            string code;
            try
            {
                code = AppState.Instance.RegenerateRecoveryCode();
            }
            catch
            {
                System.Windows.MessageBox.Show("重新生成失败，请重试", "恢复码",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            System.Windows.MessageBox.Show(
                $"新恢复码：\n\n{code}\n\n" +
                "请立即抄录并妥善保管。旧恢复码已立即失效；" +
                "若忘记新恢复码，只能卸载重装。",
                "恢复码已重新生成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

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
