using System.Security.AccessControl;
using System.Security.Principal;

namespace GreenSite.Core.Core;

/// <summary>
/// 用户级文件自保护：对配置文件设置 Deny Delete ACL + 完整性校验；被删时由备份恢复。
/// 只拒绝删除，不拒绝写入——否则程序自己无法正常保存配置。
/// 不安装驱动、不注入进程、不隐藏启动；管理员仍可取得所有权后强制删除（如实披露）。
/// </summary>
public static class FileProtectionService
{
    /// <summary>程序目录内需要保护的文件名。</summary>
    public static string[] ProtectedFileNames =>
        ["config.bin", "protection.bin", "options.bin"];

    public static string[] ProtectedFiles(string appDirectory, string localAppData)
    {
        var list = ProtectedFileNames
            .Select(n => Path.Combine(appDirectory, n))
            .Where(File.Exists)
            .ToList();
        var backup = Path.Combine(localAppData, "stgren", "bkp", "cache.idx");
        if (File.Exists(backup)) list.Add(backup);
        return [.. list];
    }

    public static bool Protect(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null) return false;

            var security = new FileInfo(path).GetAccessControl();
            var rule = new FileSystemAccessRule(
                sid, FileSystemRights.Delete, AccessControlType.Deny);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule existing in rules)
            {
                if (existing.AccessControlType == AccessControlType.Deny
                    && (existing.FileSystemRights & FileSystemRights.Delete) != 0
                    && existing.IdentityReference.Equals(sid))
                    return true; // 幂等
            }
            security.AddAccessRule(rule);
            new FileInfo(path).SetAccessControl(security);
            return IsProtected(path);
        }
        catch
        {
            return false; // 文件系统不支持 ACL / 无权限：不阻断主流程
        }
    }

    public static bool Unprotect(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null) return false;

            var security = new FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            var changed = false;
            foreach (FileSystemAccessRule existing in rules)
            {
                if (existing.AccessControlType != AccessControlType.Deny) continue;
                if ((existing.FileSystemRights & FileSystemRights.Delete) == 0) continue;
                if (!existing.IdentityReference.Equals(sid)) continue;
                security.RemoveAccessRule(existing);
                changed = true;
            }
            if (changed) new FileInfo(path).SetAccessControl(security);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsProtected(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null) return false;
            var rules = new FileInfo(path).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Deny
                    && (rule.FileSystemRights & FileSystemRights.Delete) != 0
                    && rule.IdentityReference.Equals(sid))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>完整性检查：给定「上一次存在的文件清单」，返回当前已被删除的那些路径。</summary>
    public static string[] MissingFiles(IEnumerable<string> expectedPaths) =>
        expectedPaths.Where(p => !File.Exists(p)).ToArray();

    /// <summary>供自检使用的纯逻辑：判断某个文件名是否属于保护清单。</summary>
    public static bool IsProtectedName(string fileName) =>
        ProtectedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
        || string.Equals(fileName, "cache.idx", StringComparison.OrdinalIgnoreCase);
}
