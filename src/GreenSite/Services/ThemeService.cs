using System;
using System.Linq;
using System.Windows;

namespace GreenSite.Services;

/// <summary>运行时切换浅色/深色主题：替换 MergedDictionaries 中的颜色字典。</summary>
public static class ThemeService
{
    private const string LightSource = "Themes/Colors.Light.xaml";
    private const string DarkSource = "Themes/Colors.Dark.xaml";

    /// <summary>
    /// 颜色字典以含 "WindowBackground" 键来识别；只替换它的 Source，
    /// Controls.xaml 中的样式用 DynamicResource 引用主题键，会自动跟随刷新。
    /// </summary>
    public static void Apply(string theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var source = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
            ? DarkSource
            : LightSource;

        var dictionaries = app.Resources.MergedDictionaries;
        var colorDict = dictionaries.FirstOrDefault(d => d.Contains("WindowBackground"));
        if (colorDict is not null)
        {
            colorDict.Source = new Uri(source, UriKind.Relative);
        }
        else
        {
            // 兜底：若颜色字典缺失，插入到最前（在 Controls.xaml 之前）
            dictionaries.Insert(0, new ResourceDictionary
            {
                Source = new Uri(source, UriKind.Relative),
            });
        }
    }
}
