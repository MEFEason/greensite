using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace GreenSite.Views;

/// <summary>关于页：完整版本串、许可说明、NOTICE/THIRD_PARTY_LICENSES 入口、设计致谢、logo。</summary>
public partial class AboutPage : System.Windows.Controls.UserControl
{
    public AboutPage()
    {
        InitializeComponent();

        // 只显示版本串本身，不单独展示构建时间
        var assembly = Assembly.GetExecutingAssembly();
        VersionText.Text = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "开发构建";

        // logo：assets/logo.png（复制到输出目录）
        var logoPath = Path.Combine(AppContext.BaseDirectory, "assets", "logo.png");
        if (File.Exists(logoPath))
            LogoImage.Source = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
    }

    private void OpenNotice_Click(object sender, RoutedEventArgs e) => OpenFile("NOTICE");

    private void OpenThirdParty_Click(object sender, RoutedEventArgs e) =>
        OpenFile("THIRD_PARTY_LICENSES/README.md");

    private void OpenLicense_Click(object sender, RoutedEventArgs e) => OpenFile("LICENSE");

    private static void OpenFile(params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path)) continue;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        System.Windows.MessageBox.Show($"未找到 {names[0]} 文件（应位于程序目录）。", "打开文件",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
