using System.Windows;
using GreenSite.Core.Security;

namespace GreenSite.Views;

/// <summary>
/// 卸载门禁窗口。由 Inno 卸载器以 --verify-uninstall 拉起，独立于主程序单实例与保护状态。
/// 一次验证失败或取消即返回 false（退出码 1），卸载器据此立即终止，不执行任何卸载步骤。
/// 不重试：重试需要重新启动卸载器，天然形成节流，且避免密码在内存中长时间驻留。
/// </summary>
public partial class UninstallGuardWindow : System.Windows.Window
{
    public UninstallGuardWindow()
    {
        InitializeComponent();
        PwdBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        using var secure = PwdBox.SecurePassword.Copy();
        PwdBox.Clear(); // 立刻清空密码框，明文不经命令行/日志/文件

        var result = UninstallGuard.Verify(secure);
        DialogResult = result is UninstallVerifyResult.Verified or UninstallVerifyResult.NoConfig;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PwdBox.Clear();
        DialogResult = false;
        Close();
    }
}
