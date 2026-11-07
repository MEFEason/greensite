using System.Windows.Controls;
using GreenSite.Core.Models;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>状态总览：保护状态、今日拦截、分组/有效规则统计、PAC 地址、白名单优先说明。</summary>
public partial class DashboardPage : System.Windows.Controls.UserControl
{
    public DashboardPage()
    {
        InitializeComponent();
        var s = AppState.Instance;
        s.Blocked += (_, _) => Refresh();
        s.StateChanged += Refresh;
        s.RulesChanged += Refresh;
    }

    public void Refresh()
    {
        var s = AppState.Instance;
        if (!s.IsUnlocked) return;

        var rs = s.Ruleset;
        var state = s.Controller.State;

        StatePillText.Text = state switch
        {
            ProtectionState.Active => "保护中",
            ProtectionState.Locked => "已锁定",
            _ => "已关闭",
        };
        if (state == ProtectionState.Active)
        {
            StatePill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Accent");
            StatePillText.SetResourceReference(TextBlock.ForegroundProperty, "WindowBackground");
        }
        else
        {
            StatePill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderBrushLight");
            StatePillText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        }

        TodayCountText.Text = s.Controller.TodayBlockedCount.ToString();
        GroupCountText.Text = rs.GroupCount.ToString();
        EffectiveCountText.Text = rs.EffectiveRuleCount.ToString();
        DisabledGroupCountText.Text = $"已关闭组 {rs.DisabledGroupCount}";
        PacUrlBox.Text = s.Controller.PacUrl;
    }
}
