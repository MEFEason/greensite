using System.Text;
using System.Windows;
using System.Windows.Controls;
using GreenSite.Core.Models;
using GreenSite.Services;

namespace GreenSite.Views;

/// <summary>拦截日志：ReadRecent 列表、刷新、导出（需密码）、清空（需密码）。</summary>
public partial class LogPage : System.Windows.Controls.UserControl
{
    public LogPage()
    {
        InitializeComponent();
        BuildHeader();
        // 有新拦截且当前可见时自动刷新
        AppState.Instance.Blocked += (_, _) =>
        {
            if (IsVisible) Refresh();
        };
    }

    private void BuildHeader()
    {
        DefineColumns(HeaderGrid);
        AddHeaderCell("时间", 0);
        AddHeaderCell("域名", 1);
        AddHeaderCell("命中组", 2);
        AddHeaderCell("规则", 3);
        AddHeaderCell("allow|block", 4);
    }

    private void AddHeaderCell(string caption, int column)
    {
        var tb = Text(caption, "SectionTitleStyle");
        Grid.SetColumn(tb, column);
        HeaderGrid.Children.Add(tb);
    }

    public void Refresh()
    {
        var s = AppState.Instance;
        if (!s.IsUnlocked) return;

        var entries = s.Log.ReadRecent(); // 默认近 7 天、最多 500 条
        LogRowsPanel.Children.Clear();
        foreach (var e in entries) LogRowsPanel.Children.Add(BuildRow(e));
        if (entries.Count == 0)
            LogRowsPanel.Children.Add(Text("暂无日志记录", "MutedTextStyle"));
    }

    private Grid BuildRow(BlockLogEntry e)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        DefineColumns(grid);

        var time = Text(e.Time.ToString("MM-dd HH:mm:ss"), "MutedTextStyle");
        Grid.SetColumn(time, 0);
        grid.Children.Add(time);

        var host = Text(e.Host, "BodyTextStyle");
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);

        var group = Text(e.GroupName, "MutedTextStyle");
        Grid.SetColumn(group, 2);
        grid.Children.Add(group);

        var rule = Text(e.RuleValue, "MutedTextStyle");
        Grid.SetColumn(rule, 3);
        grid.Children.Add(rule);

        var kind = Text(e.Whitelisted ? "allow" : "block", "MutedTextStyle");
        Grid.SetColumn(kind, 4);
        grid.Children.Add(kind);

        return grid;
    }

    private static void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Export_Click(object sender, RoutedEventArgs e) =>
        AppState.Instance.RequirePassword("导出日志", Export);

    private void Clear_Click(object sender, RoutedEventArgs e) =>
        AppState.Instance.RequirePassword("清空日志", Clear);

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = "GreenSite-logs.txt",
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("时间\t域名\t命中组\t规则\t类型");
        foreach (var e in AppState.Instance.Log.ReadRecent(30, 100000))
        {
            sb.AppendLine(string.Join('\t',
                e.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                e.Host, e.GroupName, e.RuleValue,
                e.Whitelisted ? "allow" : "block"));
        }
        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        System.Windows.MessageBox.Show("日志已导出", "导出日志",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Clear()
    {
        var dir = AppState.Instance.Log.Directory;
        foreach (var file in Directory.GetFiles(dir, "block-????????.log"))
        {
            try { File.Delete(file); } catch { /* 单个文件失败不影响其它 */ }
        }
        Refresh();
    }

    private TextBlock Text(string value, string styleKey)
    {
        var tb = new TextBlock { Text = value };
        tb.Style = (Style)FindResource(styleKey);
        return tb;
    }
}
