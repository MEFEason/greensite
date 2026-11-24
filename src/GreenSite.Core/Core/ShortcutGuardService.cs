using System.Runtime.InteropServices;

namespace GreenSite.Core.Core;

/// <summary>
/// 公共桌面快捷方式守护：定时检查 GreenSite.lnk，缺失时用系统 Shell Link COM 重建。
/// 不注入、不隐藏、不写 Run 恶意项；公共桌面无写权限时回退当前用户桌面。
/// </summary>
public static class ShortcutGuardService
{
    public static string PublicDesktop =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public static string UserDesktop =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string ShortcutPath => Path.Combine(PublicDesktop, "GreenSite.lnk");
    public static string FallbackShortcutPath => Path.Combine(UserDesktop, "GreenSite.lnk");

    public static bool IsMissing() => !File.Exists(ShortcutPath);

    /// <summary>创建 .lnk。优先公共桌面，失败回退当前用户桌面；两者都失败返回 false。</summary>
    public static bool EnsureCreated(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        if (!IsMissing()) return true;

        if (TryCreate(ShortcutPath, exePath)) return true;
        return TryCreate(FallbackShortcutPath, exePath);
    }

    private static bool TryCreate(string path, string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type is null) return false;
            var link = Activator.CreateInstance(type)!;
            var flags = System.Reflection.BindingFlags.SetProperty;
            type.InvokeMember("Path", flags, null, link, [exePath]);
            type.InvokeMember("WorkingDirectory", flags, null, link,
                [Path.GetDirectoryName(exePath) ?? ""]);
            type.InvokeMember("Description", flags, null, link, ["GreenSite 网站访问控制"]);
            type.InvokeMember("IconLocation", flags, null, link,
                [Path.Combine(Path.GetDirectoryName(exePath) ?? "", "assets", "app.ico") + ",0"]);

            var persist = (IPersistFile)link;
            persist.Save(path, true);
            Marshal.ReleaseComObject(link);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
