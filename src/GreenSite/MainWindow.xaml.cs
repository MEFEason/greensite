using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GreenSite.Core.Models;
using GreenSite.Services;
using GreenSite.Views;

namespace GreenSite;

/// <summary>
/// 主窗口：左侧导航 + 右侧内容区 + 系统托盘。
/// 冷启动显示 LockPage，解锁后切换到主界面。关闭窗口只隐藏到托盘并锁定编辑。
/// </summary>
public partial class MainWindow : Window
{
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly System.Windows.Forms.ContextMenuStrip _trayMenu;

    private System.Windows.Forms.ToolStripMenuItem _trayStatusItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _trayTodayItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _trayGroupsItem = null!;

    private readonly DashboardPage _dashboard;
    private readonly RuleListPage _blockPage;
    private readonly RuleListPage _allowPage;
    private readonly LogPage _logPage;
    private readonly SettingsPage _settingsPage;
    private readonly AboutPage _aboutPage;

    private string _currentNav = "";

    public MainWindow()
    {
        InitializeComponent();

        // 页面构造时不读取 Config（配置此时尚未解密），只在 Refresh/Reload 时读取
        _dashboard = new DashboardPage();
        _blockPage = new RuleListPage(isAllow: false);
        _allowPage = new RuleListPage(isAllow: true);
        _logPage = new LogPage();
        _settingsPage = new SettingsPage();
        _aboutPage = new AboutPage();

        // 托盘图标：必须是 assets/app.ico 文件路径，禁止 PNG / data URI
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconPath),
            Text = "GreenSite 网站访问控制",
            Visible = true,
        };
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        // NotificationService 订阅 AppState.Blocked，事件持有其引用，无需单独保存字段
        new NotificationService(_tray);

        var lockPage = new LockPage();
        lockPage.Unlocked += OnUnlocked;
        LockHost.Content = lockPage;

        var state = AppState.Instance;
        state.StateChanged += OnStateChanged;
        state.RulesChanged += OnRulesChanged;
        state.Blocked += OnBlocked;
        state.BeforeExit += ReleaseTray;

        RebuildTrayMenu();
        UpdateStatusUI();
    }

    // ── 启动与解锁 ────────────────────────────────────────────
    /// <summary>开机自启（--minimized）：主窗口保持未显示，托盘提示用户解锁。</summary>
    public void StartMinimized()
    {
        _tray.ShowBalloonTip(
            3000,
            "GreenSite 已启动",
            "已启动到托盘。点击托盘图标打开主界面，解锁后保护自动开启。",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OnUnlocked(object? sender, System.EventArgs e)
    {
        LockView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        RebuildTrayMenu();
        UpdateStatusUI();
        Navigate("dashboard");
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
        if (!AppState.Instance.IsUnlocked)
        {
            LockView.Visibility = Visibility.Visible;
            MainView.Visibility = Visibility.Collapsed;
        }
    }

    // ── 导航 ──────────────────────────────────────────────────
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key) Navigate(key);
    }

    private void Navigate(string key)
    {
        if (_currentNav == key && ContentHost.Content is not null) return;
        _currentNav = key;
        foreach (var child in NavPanel.Children)
        {
            if (child is not Button b) continue;
            var styleKey = (b.Tag as string) == key
                ? "NavButtonActiveStyle"
                : "NavButtonStyle";
            b.Style = (Style)FindResource(styleKey);
        }

        switch (key)
        {
            case "dashboard":
                ContentHost.Content = _dashboard;
                _dashboard.Refresh();
                break;
            case "block":
                ContentHost.Content = _blockPage;
                _blockPage.Reload();
                break;
            case "allow":
                ContentHost.Content = _allowPage;
                _allowPage.Reload();
                break;
            case "log":
                ContentHost.Content = _logPage;
                _logPage.Refresh();
                break;
            case "settings":
                ContentHost.Content = _settingsPage;
                _settingsPage.Refresh();
                break;
            case "about":
                ContentHost.Content = _aboutPage;
                break;
        }
    }

    // ── 状态显示 ──────────────────────────────────────────────
    private void OnStateChanged() => UpdateStatusUI();

    private void OnRulesChanged()
    {
        RebuildTrayMenu();
        UpdateStatusUI();
    }

    private void OnBlocked(object? sender, BlockLogEntry entry) => UpdateStatusUI();

    private void UpdateStatusUI()
    {
        var s = AppState.Instance;
        if (!s.IsUnlocked)
        {
            StatusPillText.Text = "未解锁";
            TodayBlockedText.Text = "今日拦截 0 次";
            UpdateTrayText();
            return;
        }

        var state = s.Controller.State;
        StatusPillText.Text = state switch
        {
            ProtectionState.Active => "保护中",
            ProtectionState.Locked => "已锁定",
            _ => "已关闭",
        };

        if (state == ProtectionState.Active)
        {
            StatusPill.SetResourceReference(Border.BackgroundProperty, "Accent");
            StatusPillText.SetResourceReference(TextBlock.ForegroundProperty, "WindowBackground");
        }
        else
        {
            StatusPill.SetResourceReference(Border.BackgroundProperty, "BorderBrushLight");
            StatusPillText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        }

        TodayBlockedText.Text = $"今日拦截 {s.Controller.TodayBlockedCount} 次";
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        var s = AppState.Instance;
        if (_trayStatusItem is not null)
        {
            _trayStatusItem.Text = s.IsUnlocked
                ? (s.IsProtectionActive ? "保护状态：保护中（点击关闭）" : "保护状态：已关闭（点击开启）")
                : "保护状态：未解锁";
            _trayStatusItem.Checked = s.IsProtectionActive;
        }
        if (_trayTodayItem is not null)
        {
            _trayTodayItem.Text = s.IsUnlocked
                ? $"今日拦截 {s.Controller.TodayBlockedCount} 次"
                : "今日拦截 0 次";
        }
    }

    // ── 托盘菜单 ──────────────────────────────────────────────
    private void RebuildTrayMenu()
    {
        _trayMenu.Items.Clear();

        var open = new System.Windows.Forms.ToolStripMenuItem("打开主界面");
        open.Click += (_, _) => ShowMainWindow();
        _trayMenu.Items.Add(open);

        _trayStatusItem = new System.Windows.Forms.ToolStripMenuItem();
        _trayStatusItem.Click += (_, _) => ToggleProtectionFromTray();
        _trayMenu.Items.Add(_trayStatusItem);

        _trayTodayItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
        _trayMenu.Items.Add(_trayTodayItem);

        // 分组快捷开关（二级菜单）
        _trayGroupsItem = new System.Windows.Forms.ToolStripMenuItem("分组快捷开关");
        if (AppState.Instance.IsUnlocked)
        {
            foreach (var g in AppState.Instance.Config.Groups.OrderBy(x => x.SortOrder))
            {
                var item = new System.Windows.Forms.ToolStripMenuItem(g.Name)
                {
                    Checked = g.Enabled,
                    Tag = g.Id,
                };
                item.Click += OnTrayGroupClick;
                _trayGroupsItem.DropDownItems.Add(item);
            }
            if (_trayGroupsItem.DropDownItems.Count == 0)
                _trayGroupsItem.DropDownItems.Add(
                    new System.Windows.Forms.ToolStripMenuItem("（暂无分组）") { Enabled = false });
        }
        else
        {
            _trayGroupsItem.DropDownItems.Add(
                new System.Windows.Forms.ToolStripMenuItem("（解锁后可用）") { Enabled = false });
        }
        _trayMenu.Items.Add(_trayGroupsItem);

        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var close = new System.Windows.Forms.ToolStripMenuItem("关闭保护");
        close.Click += (_, _) => AppState.Instance.RequirePassword(
            "关闭保护", () => AppState.Instance.CloseProtection());
        _trayMenu.Items.Add(close);

        var exit = new System.Windows.Forms.ToolStripMenuItem("退出程序");
        // 未解锁：RequirePassword → VerifyPassword 会读尚未加载的 Config 抛异常，
        // 且拦截本来未开启，退出无安全风险——直接 ExitApplicationRaw()。
        // 已解锁：保持密码验证，防止未经认证的人一键解除保护。
        exit.Click += (_, _) =>
        {
            if (!AppState.Instance.IsUnlocked)
                AppState.Instance.ExitApplicationRaw();
            else
                AppState.Instance.RequirePassword("退出程序", () => AppState.Instance.ExitApplication());
        };
        _trayMenu.Items.Add(exit);

        UpdateTrayText();
    }

    private void ToggleProtectionFromTray()
    {
        var s = AppState.Instance;
        if (!s.IsUnlocked) { ShowMainWindow(); return; }
        if (s.IsProtectionActive)
            s.RequirePassword("关闭保护", () => s.CloseProtection());
        else
            s.RequirePassword("开启保护", () => s.StartProtection());
    }

    private void OnTrayGroupClick(object? sender, System.EventArgs e)
    {
        if (sender is not System.Windows.Forms.ToolStripMenuItem item || item.Tag is not Guid id) return;
        var s = AppState.Instance;
        if (!s.IsUnlocked) { ShowMainWindow(); return; }

        if (!s.IsEditUnlocked)
        {
            // 切换分组开关需先解锁编辑（会话内一次解锁）
            s.RequirePassword("切换分组开关", () =>
            {
                s.UnlockEdit();
                ToggleGroup(id);
            });
        }
        else
        {
            s.NoteEditActivity();
            ToggleGroup(id);
        }
    }

    private void ToggleGroup(Guid id)
    {
        var g = AppState.Instance.Config.Groups.FirstOrDefault(x => x.Id == id);
        if (g is null) return;
        g.Enabled = !g.Enabled;
        AppState.Instance.RefreshRules(); // 触发 RulesChanged → 重建菜单
    }

    // ── 关闭行为 ──────────────────────────────────────────────
    // OnWindowClose 契约：true=放行关闭（未解锁时用户确认退出、或已解锁且 CloseAction=退出）；
    // false=取消关闭（隐藏到托盘、或用户在「退出？」中选择取消）。
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // 退出流程（ExitApplication 已置 IsExiting）内的 Shutdown 触发的 Closing 必须放行
        if (AppState.Instance.IsExiting) return;
        if (AppState.Instance.OnWindowClose()) return; // 放行关闭
        e.Cancel = true;
        Hide();
        AppState.Instance.LockEdit(); // 隐藏到托盘后自动锁定编辑
        _tray.ShowBalloonTip(
            2500, "GreenSite", "已最小化到托盘，规则编辑已锁定。",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ReleaseTray()
    {
        _tray.Visible = false;
        _tray.Dispose();
    }
}
