using System.Runtime.InteropServices;

namespace GreenSite.Core.Security.Win32;

/// <summary>DPAPI(CurrentUser) 封装。无需管理员，密钥绑定当前 Windows 用户。</summary>
internal static class Crypt32
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr pDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Protect(byte[] plain)
    {
        var input = new DataBlob { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.pbData, plain.Length);
            if (!CryptProtectData(ref input, "GreenSite", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException("DPAPI 加密失败");

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    public static byte[] Unprotect(byte[] encrypted)
    {
        var input = new DataBlob { cbData = encrypted.Length, pbData = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.pbData, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException("DPAPI 解密失败");

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }
}
