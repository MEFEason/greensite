namespace GreenSite.Core.Core;

/// <summary>
/// 未解锁会话的敏感操作门禁（纯逻辑，供自检直接覆盖）。
///
/// 开机自启会用无密码 DPAPI 快照自动进入防护状态：此时 IsUnlocked=false 但拦截真实生效，
/// 因此「关闭保护 / 退出程序」必须先解锁主界面；关闭窗口只允许最小化到托盘。
/// 首次运行（无配置、无密码、无拦截）豁免，否则会重现「设置密码页无法退出」的缺陷；
/// 保护未激活的普通冷启动也豁免（退出不会解除任何拦截）。
/// </summary>
public static class AccessPolicy
{
    /// <summary>已保存配置、防护正在运行、但尚未解锁：需先解锁主界面。</summary>
    public static bool RequiresUnlock(bool isFirstRun, bool isUnlocked, bool isProtectionActive) =>
        !isFirstRun && !isUnlocked && isProtectionActive;

    /// <summary>是否允许关闭保护。恢复码解锁未重设态已验证过身份，放行以免僵局。</summary>
    public static bool CanStopProtection(
        bool isFirstRun, bool isUnlocked, bool isProtectionActive, bool needPasswordReset) =>
        needPasswordReset || !RequiresUnlock(isFirstRun, isUnlocked, isProtectionActive);

    /// <summary>是否允许进入退出流程（关闭保护与退出是同一档门禁）。</summary>
    public static bool CanExit(
        bool isFirstRun, bool isUnlocked, bool isProtectionActive, bool needPasswordReset) =>
        CanStopProtection(isFirstRun, isUnlocked, isProtectionActive, needPasswordReset);

    /// <summary>
    /// 关闭主窗口时是否允许进入退出流程。
    /// false = 只最小化到托盘（自启未解锁，或已解锁且 CloseAction=hide）。
    /// 首启返回 true：点 X 走确认退出，保留「设置密码页可退出」的既有修复。
    /// </summary>
    public static bool AllowsExitOnWindowClose(
        bool isFirstRun, bool isUnlocked, bool isProtectionActive,
        bool needPasswordReset, string? closeAction)
    {
        if (isFirstRun) return true;
        if (RequiresUnlock(isFirstRun, isUnlocked, isProtectionActive)) return false;
        if (needPasswordReset) return true; // 恢复码解锁未重设：确认后可直接退出，避免僵局
        return string.Equals(closeAction, "exit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 主界面状态栏「开启 / 关闭防护」快捷按钮的文案与可用状态。
    /// 与托盘菜单共用同一套门禁：自启未解锁且防护真实在跑时禁用，避免绕过解锁停止拦截；
    /// 首启、普通冷启动、恢复码解锁未重设三类豁免与 <see cref="CanStopProtection"/> 一致。
    /// 返回纯数据，便于自检直接覆盖按钮状态矩阵（UI 只负责显示与调用 AppState.ToggleProtection）。
    /// </summary>
    public static (string Label, bool Enabled) ToggleProtectionButton(
        bool isFirstRun, bool isUnlocked, bool isProtectionActive, bool needPasswordReset)
    {
        var canToggle = CanStopProtection(
            isFirstRun, isUnlocked, isProtectionActive, needPasswordReset);
        if (!canToggle) return ("需先解锁", false);
        return (isProtectionActive ? "关闭防护" : "开启防护", true);
    }
}
