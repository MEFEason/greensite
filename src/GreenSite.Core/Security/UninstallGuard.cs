using System.Runtime.InteropServices;
using System.Security;

namespace GreenSite.Core.Security;

public enum UninstallVerifyResult
{
    /// <summary>管理密码验证通过，允许继续卸载。</summary>
    Verified,
    /// <summary>从未设置过管理密码（config.bin 不存在），无需验证，允许卸载。</summary>
    NoConfig,
    /// <summary>密码错误 / 配置损坏 / 校验失败，拒绝卸载。</summary>
    Rejected,
}

/// <summary>
/// 卸载门禁。卸载器（Inno）在 InitializeUninstall 里以 --verify-uninstall 拉起主程序，
/// 由主程序弹出密码框并调用本类；验证通过才允许卸载器执行任何删除/注册表步骤。
///
/// 验证走完整链路：header → DPAPI(CurrentUser) → PBKDF2 密码哈希 → HMAC-SHA256 完整性，
/// 与程序内解锁完全一致，不另设后门、不缓存密码。
/// </summary>
public static class UninstallGuard
{
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GreenSite");

    /// <summary>是否存在需要验证的管理密码配置。</summary>
    public static bool NeedsVerification(string? directory = null)
    {
        var dir = directory ?? ConfigDirectory;
        return File.Exists(Path.Combine(dir, "config.bin"));
    }

    /// <summary>
    /// 用管理密码校验配置。密码只以 SecureString 传入，临时转 string 后不缓存、
    /// 不写日志、不进异常消息（string 不可变，由 GC 回收，与项目其它位置一致）。
    /// </summary>
    public static UninstallVerifyResult Verify(SecureString password, string? directory = null)
    {
        var dir = directory ?? ConfigDirectory;
        if (!NeedsVerification(dir)) return UninstallVerifyResult.NoConfig;

        var ptr = Marshal.SecureStringToBSTR(password);
        string plain;
        try { plain = Marshal.PtrToStringBSTR(ptr) ?? string.Empty; }
        finally { Marshal.ZeroFreeBSTR(ptr); }

        try
        {
            new SecureConfigStore(dir).Load(plain);
            return UninstallVerifyResult.Verified;
        }
        catch
        {
            // 密码错 / DPAPI 失败 / 篡改 / IO 异常一律统一判定为拒绝，不泄露原因
            return UninstallVerifyResult.Rejected;
        }
    }
}
