using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GreenSite.Core.Core;
using GreenSite.Core.Models;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>
/// 限制列表 / 白名单共用页面（构造参数 isAllow 区分）。
/// 分组卡片、组内规则行由 code-behind 动态生成；所有编辑操作先经编辑锁门禁，
/// 任何开关变化都走 AppState.RefreshRules()（重建规则集 → 应用 → 加密保存）。
/// </summary>
public partial class RuleListPage : System.Windows.Controls.UserControl
{
    private readonly bool _isAllow;
    private readonly HashSet<Guid> _collapsed = new();

    private sealed class MoveContext
    {
        public Rule Rule = null!;
        public System.Windows.Controls.ComboBox Combo = null!;
    }

    public RuleListPage(bool isAllow)
    {
        _isAllow = isAllow;
        InitializeComponent();
        TitleText.Text = isAllow ? "白名单" : "限制列表";
        AppState.Instance.EditLockChanged += UpdateLockBanner;
        UpdateLockBanner();
    }

    // ── 重建整个页面 ──────────────────────────────────────────
    public void Reload()
    {
        var s = AppState.Instance;
        if (!s.IsUnlocked) return;

        UpdateLockBanner();
        FillGroupCombos();
        GroupsPanel.Children.Clear();

        var filter = SearchBox.Text?.Trim() ?? string.Empty;
        var groups = s.Config.Groups
            .Where(g => g.IsAllow == _isAllow)
            .OrderBy(g => g.SortOrder)
            .ToList();

        foreach (var g in groups)
        {
            var rules = s.Config.Rules.Where(r => r.GroupId == g.Id).ToList();
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                rules = rules.Where(r => MatchesFilter(r, filter)).ToList();
            GroupsPanel.Children.Add(BuildGroupCard(g, rules));
        }

        if (GroupsPanel.Children.Count == 0)
            GroupsPanel.Children.Add(
                Text("暂无分组，可点击「新建组」或「导入默认分组」。", "MutedTextStyle"));
    }

    private void FillGroupCombos()
    {
        var groups = AppState.Instance.Config.Groups
            .Where(g => g.IsAllow == _isAllow)
            .OrderBy(g => g.SortOrder)
            .ToList();
        CurrentGroupCombo.ItemsSource = groups;
        AddGroupCombo.ItemsSource = groups;
        if (CurrentGroupCombo.SelectedIndex < 0 && groups.Count > 0)
            CurrentGroupCombo.SelectedIndex = 0;
        if (AddGroupCombo.SelectedIndex < 0 && groups.Count > 0)
            AddGroupCombo.SelectedIndex = 0;
    }

    private static bool MatchesFilter(Rule r, string filter) =>
        (r.RawValue ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (r.Value ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (r.Note ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

    // ── 分组卡片 ──────────────────────────────────────────────
    private Border BuildGroupCard(RuleGroup g, List<Rule> rules)
    {
        var card = new Border { Margin = new Thickness(0, 0, 0, 12) };
        card.Style = (Style)FindResource("CardBorderStyle");

        var root = new StackPanel();
        card.Child = root;

        var effective = g.Enabled ? rules.Count(r => r.Enabled) : 0; // 有效 = 组开关 ∧ 单条开关
        var expanded = !_collapsed.Contains(g.Id);

        // 组头：组名 + 有效条数 + 组总开关 + 展开折叠
        var header = new DockPanel();

        var groupToggle = new CheckBox
        {
            Content = "启用",
            IsChecked = g.Enabled,
            Tag = g,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        groupToggle.Style = (Style)FindResource("SwitchCheckBoxStyle");
        groupToggle.Checked += GroupToggle_Changed;
        groupToggle.Unchecked += GroupToggle_Changed;
        DockPanel.SetDock(groupToggle, Dock.Right);
        header.Children.Add(groupToggle);

        var expandBtn = new Button
        {
            Content = expanded ? "收起" : "展开",
            Tag = g,
            Width = 56,
            Margin = new Thickness(8, 0, 0, 0),
        };
        expandBtn.Style = (Style)FindResource("SecondaryButtonStyle");
        expandBtn.Click += Expand_Click;
        DockPanel.SetDock(expandBtn, Dock.Right);
        header.Children.Add(expandBtn);

        var title = Text($"{g.Name}   ·   有效 {effective} 条   ·   共 {rules.Count} 条", "SectionTitleStyle");
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title); // 最后添加，填充剩余空间
        root.Children.Add(header);

        var divider = new Border { Margin = new Thickness(0, 10, 0, 10) };
        divider.Style = (Style)FindResource("DividerStyle");
        root.Children.Add(divider);

        var rows = new StackPanel
        {
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
        };
        root.Children.Add(rows);
        foreach (var r in rules) rows.Children.Add(BuildRuleRow(g, r));
        if (rules.Count == 0) rows.Children.Add(Text("（暂无规则）", "MutedTextStyle"));

        return card;
    }

    // ── 单条规则行 ────────────────────────────────────────────
    private Grid BuildRuleRow(RuleGroup g, Rule r)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        // 域名 + 可能误伤 / 白名单优先 标记
        var valuePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valuePanel.Children.Add(Text(
            string.IsNullOrEmpty(r.RawValue) ? r.Value : r.RawValue, "BodyTextStyle"));
        if (IsBroad(r))
        {
            var badge = new Border { Margin = new Thickness(6, 0, 0, 0) };
            badge.Style = (Style)FindResource("WarnBadgeStyle");
            var badgeText = new TextBlock { Text = "可能误伤" };
            badgeText.Style = (Style)FindResource("WarnBadgeTextStyle");
            badge.Child = badgeText;
            valuePanel.Children.Add(badge);
        }
        if (ExistsInOpposite(r))
            valuePanel.Children.Add(Text("  白名单优先", "MutedTextStyle"));
        Grid.SetColumn(valuePanel, 0);
        grid.Children.Add(valuePanel);

        var note = Text(r.Note ?? "", "MutedTextStyle");
        note.VerticalAlignment = VerticalAlignment.Center;
        note.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);

        var type = Text(r.Type == RuleType.Domain ? "域名" : "网址", "MutedTextStyle");
        type.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(type, 2);
        grid.Children.Add(type);

        var toggle = new CheckBox
        {
            IsChecked = r.Enabled,
            Tag = r,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        toggle.Style = (Style)FindResource("SwitchCheckBoxStyle");
        toggle.Checked += RuleToggle_Changed;
        toggle.Unchecked += RuleToggle_Changed;
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        var del = new Button { Content = "删除", Tag = r };
        del.Style = (Style)FindResource("SecondaryButtonStyle");
        del.Click += DeleteRule_Click;
        Grid.SetColumn(del, 4);
        grid.Children.Add(del);

        // 跨组移动：下拉选择目标组 + 移动按钮
        var targets = AppState.Instance.Config.Groups
            .Where(x => x.Id != g.Id)
            .OrderBy(x => x.SortOrder)
            .ToList();
        var combo = new System.Windows.Controls.ComboBox
        {
            ItemsSource = targets,
            DisplayMemberPath = "Name",
            VerticalAlignment = VerticalAlignment.Center,
        };
        combo.Style = (Style)FindResource("InputComboBoxStyle");
        Grid.SetColumn(combo, 5);
        grid.Children.Add(combo);

        var move = new Button
        {
            Content = "移动",
            Tag = new MoveContext { Rule = r, Combo = combo },
        };
        move.Style = (Style)FindResource("SecondaryButtonStyle");
        move.Click += MoveRule_Click;
        Grid.SetColumn(move, 6);
        grid.Children.Add(move);

        return grid;
    }

    private static bool IsBroad(Rule r)
    {
        var host = r.Type == RuleType.Domain ? r.Value : r.Value.Split('/')[0];
        return RuleNormalizer.IsBroadDomain(host);
    }

    /// <summary>同值是否也存在于相反名单（黑/白）中。</summary>
    private bool ExistsInOpposite(Rule r)
    {
        var s = AppState.Instance;
        return s.Config.Rules.Any(x => x.Value == r.Value && x.GroupId != r.GroupId &&
            s.Config.Groups.Any(g => g.Id == x.GroupId && g.IsAllow != _isAllow));
    }

    // ── 编辑锁门禁 ────────────────────────────────────────────
    private bool EnsureUnlocked(string action)
    {
        var s = AppState.Instance;
        if (s.IsEditUnlocked) { s.NoteEditActivity(); return true; }
        var ok = false;
        s.RequirePassword(action, () => { s.UnlockEdit(); ok = true; });
        UpdateLockBanner();
        return ok;
    }

    private void UpdateLockBanner()
    {
        var unlocked = AppState.Instance.IsEditUnlocked;
        LockStateText.Text = unlocked ? "已解锁 · 可编辑" : "已锁定 · 编辑需验证密码";
        RelockButton.IsEnabled = unlocked;
    }

    private void Relock_Click(object sender, RoutedEventArgs e)
    {
        AppState.Instance.LockEdit();
        AddRulePanel.Visibility = Visibility.Collapsed;
        UpdateLockBanner();
    }

    // ── 开关 / 展开 / 删除 / 移动 ─────────────────────────────
    private void GroupToggle_Changed(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        var g = (RuleGroup)cb.Tag;
        var value = cb.IsChecked == true;
        if (!EnsureUnlocked("切换分组开关")) { Reload(); return; }
        g.Enabled = value;
        AppState.Instance.RefreshRules();
        Reload();
    }

    private void RuleToggle_Changed(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        var r = (Rule)cb.Tag;
        var value = cb.IsChecked == true;
        if (!EnsureUnlocked("切换单条开关")) { Reload(); return; }
        r.Enabled = value;
        AppState.Instance.RefreshRules();
        Reload();
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        var g = (RuleGroup)((Button)sender).Tag;
        if (_collapsed.Contains(g.Id)) _collapsed.Remove(g.Id);
        else _collapsed.Add(g.Id);
        Reload();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("删除规则")) return;
        var r = (Rule)((Button)sender).Tag;
        AppState.Instance.Config.Rules.Remove(r);
        AppState.Instance.RefreshRules();
        Reload();
    }

    private void MoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("移动规则")) return;
        var ctx = (MoveContext)((Button)sender).Tag;
        if (ctx.Combo.SelectedItem is not RuleGroup target)
        {
            System.Windows.MessageBox.Show("请先选择目标分组", "移动规则",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ctx.Rule.GroupId = target.Id;
        AppState.Instance.RefreshRules();
        Reload();
        System.Windows.MessageBox.Show($"已移动到分组「{target.Name}」", "移动规则",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 分组管理 ──────────────────────────────────────────────
    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("新建分组")) return;
        if (!ShowTextPrompt("新建分组", "分组名称", "", out var name)) return;
        name = name.Trim();
        if (name.Length == 0) return;

        var s = AppState.Instance;
        if (s.Config.Groups.Any(g => g.IsAllow == _isAllow && g.Name == name))
        {
            System.Windows.MessageBox.Show("同名分组已存在", "新建分组",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        s.Config.Groups.Add(new RuleGroup
        {
            Name = name,
            IsAllow = _isAllow,
            SortOrder = s.Config.Groups.Count,
        });
        s.RefreshRules();
        Reload();
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGroupCombo.SelectedItem is not RuleGroup g)
        {
            System.Windows.MessageBox.Show("请先在下拉框中选择分组", "重命名",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureUnlocked("重命名分组")) return;
        if (!ShowTextPrompt("重命名分组", "新名称", g.Name, out var name)) return;
        name = name.Trim();
        if (name.Length == 0) return;

        g.Name = name;
        AppState.Instance.RefreshRules();
        Reload();
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGroupCombo.SelectedItem is not RuleGroup g)
        {
            System.Windows.MessageBox.Show("请先在下拉框中选择分组", "删除组",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureUnlocked("删除分组")) return;
        if (System.Windows.MessageBox.Show(
                $"确定删除分组「{g.Name}」及其全部规则？", "删除分组",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var s = AppState.Instance;
        s.Config.Rules.RemoveAll(r => r.GroupId == g.Id);
        s.Config.Groups.Remove(g);
        s.RefreshRules();
        Reload();
    }

    /// <summary>导入默认 6 组清单（同名组复用、同值规则去重）。</summary>
    private void ImportDefault_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("导入默认分组")) return;
        var s = AppState.Instance;
        var (defGroups, defRules) = DefaultRules.CreateDefault();

        foreach (var dg in defGroups)
        {
            var target = s.Config.Groups.FirstOrDefault(
                g => g.IsAllow == dg.IsAllow && g.Name == dg.Name);
            if (target is null)
            {
                target = new RuleGroup
                {
                    Name = dg.Name,
                    IsAllow = dg.IsAllow,
                    SortOrder = s.Config.Groups.Count,
                };
                s.Config.Groups.Add(target);
            }
            foreach (var dr in defRules.Where(r => r.GroupId == dg.Id))
            {
                if (s.Config.Rules.Any(r => r.GroupId == target.Id && r.Value == dr.Value))
                    continue;
                s.Config.Rules.Add(new Rule
                {
                    GroupId = target.Id,
                    Type = dr.Type,
                    Value = dr.Value,
                    RawValue = dr.RawValue,
                    Note = dr.Note,
                });
            }
        }
        s.RefreshRules();
        Reload();
        System.Windows.MessageBox.Show("默认分组已导入（同名组复用、重复规则已跳过）。",
            "导入默认分组", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 添加规则 ──────────────────────────────────────────────
    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("添加规则")) return;
        FillGroupCombos();
        if (CurrentGroupCombo.SelectedItem is RuleGroup g)
            AddGroupCombo.SelectedItem = g;
        AddErrorText.Visibility = Visibility.Collapsed;
        AddRulePanel.Visibility = Visibility.Visible;
        AddValueBox.Focus();
    }

    private void AddCancel_Click(object sender, RoutedEventArgs e)
    {
        AddRulePanel.Visibility = Visibility.Collapsed;
        AddErrorText.Visibility = Visibility.Collapsed;
    }

    private void AddConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUnlocked("添加规则")) return;
        AddErrorText.Visibility = Visibility.Collapsed;

        if (AddGroupCombo.SelectedItem is not RuleGroup target)
        {
            ShowAddError("请先选择目标分组");
            return;
        }
        var raw = AddValueBox.Text.Trim();
        var type = AddTypeCombo.SelectedIndex == 1 ? RuleType.UrlPath : RuleType.Domain;

        string value;
        string error;
        bool ok;
        if (type == RuleType.Domain)
            ok = RuleNormalizer.TryNormalizeDomain(raw, out value, out error);
        else
            ok = RuleNormalizer.TryNormalizeUrlPath(raw, out value, out error);
        if (!ok)
        {
            // 明确告诉用户非法原因
            ShowAddError($"输入不合法：{error}");
            return;
        }

        var s = AppState.Instance;
        if (s.Config.Rules.Any(r => r.GroupId == target.Id && r.Value == value))
        {
            ShowAddError("该分组已存在相同规则");
            return;
        }

        var oppositeExists = s.Config.Rules.Any(r => r.Value == value &&
            s.Config.Groups.Any(g => g.Id == r.GroupId && g.IsAllow != target.IsAllow));

        s.Config.Rules.Add(new Rule
        {
            GroupId = target.Id,
            Type = type,
            Value = value,
            RawValue = raw,
            Note = string.IsNullOrWhiteSpace(AddNoteBox.Text) ? null : AddNoteBox.Text.Trim(),
        });
        s.RefreshRules();

        AddValueBox.Clear();
        AddNoteBox.Clear();
        AddRulePanel.Visibility = Visibility.Collapsed;
        Reload();

        if (oppositeExists)
            System.Windows.MessageBox.Show("白名单优先，将始终放行", "规则冲突提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowAddError(string message)
    {
        AddErrorText.Text = message;
        AddErrorText.Visibility = Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Reload();

    // ── 单行文本输入对话框（动态构造，不新增 XAML 文件）────────
    private bool ShowTextPrompt(string title, string label, string defaultValue, out string result)
    {
        result = string.Empty;
        var window = new System.Windows.Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Text(label, "BodyTextStyle"));

        var box = new TextBox { Text = defaultValue, Margin = new Thickness(0, 6, 0, 0) };
        box.Style = (Style)FindResource("InputTextBoxStyle");
        panel.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Style = (Style)FindResource("SecondaryButtonStyle");
        var confirm = new Button { Content = "确定" };
        confirm.Style = (Style)FindResource("PrimaryButtonStyle");
        var confirmed = false;
        cancel.Click += (_, _) => window.Close();
        confirm.Click += (_, _) => { confirmed = true; window.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.ShowDialog();
        if (confirmed) result = box.Text;
        return confirmed;
    }

    private TextBlock Text(string value, string styleKey)
    {
        var tb = new TextBlock { Text = value };
        tb.Style = (Style)FindResource(styleKey);
        return tb;
    }
}
