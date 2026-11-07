using System.Runtime.InteropServices;

namespace GreenSite.Core.Security.Win32;

/// <summary>通知 WinINET 重新读取代理设置，使 AutoConfigURL 变更即时生效。</summary>
internal static class WinInet
{
    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static void RefreshProxySettings()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
    }
}
