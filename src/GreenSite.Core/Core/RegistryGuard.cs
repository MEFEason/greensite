using System.Runtime.InteropServices;

namespace GreenSite.Core.Core;

/// <summary>
/// 事件驱动监控 HKCU 代理设置（RegNotifyChangeKeyValue + 等待事件，不轮询、空闲零 CPU）。
/// 自身写入前调用 Suppress 抑制，避免自我回滚死循环。
/// </summary>
public sealed class RegistryGuard : IDisposable
{
    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    private const uint RegNotifyChangeLastSet = 0x4;
    private const int KeyNotify = 0x0010;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(
        IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        IntPtr hKey, bool bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, bool fAsynchronous);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    private readonly string _keyPath;
    private readonly ProxySettings _settings;
    private readonly Action _onTamper;
    private readonly AutoResetEvent _signal = new(false);
    private IntPtr _hKey;
    private Thread? _thread;
    private volatile string _expected;
    private volatile bool _running;
    private long _suppressUntil;

    public RegistryGuard(string keyPath, string expected, Action onTamper)
    {
        _keyPath = keyPath;
        _expected = expected;
        _onTamper = onTamper;
        _settings = new ProxySettings(keyPath);
    }

    public void SetExpected(string value) => _expected = value;

    public void Suppress(TimeSpan duration) =>
        Interlocked.Exchange(ref _suppressUntil,
            Environment.TickCount64 + (long)duration.TotalMilliseconds);

    public void Start()
    {
        if (_thread is not null) return;
        if (RegOpenKeyEx(HkeyCurrentUser, _keyPath, 0, KeyNotify, out _hKey) != 0)
            throw new InvalidOperationException("无法打开注册表键以监视代理设置");
        _running = true;
        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "GreenSite.RegistryGuard",
        };
        _thread.Start();
    }

    private void WatchLoop()
    {
        while (_running)
        {
            if (RegNotifyChangeKeyValue(_hKey, false, RegNotifyChangeLastSet,
                    _signal.SafeWaitHandle.DangerousGetHandle(), true) != 0) return;
            _signal.WaitOne();
            if (!_running) return;
            if (Environment.TickCount64 < Interlocked.Read(ref _suppressUntil)) continue;

            var current = _settings.ReadAutoConfigUrl();
            if (!string.Equals(current, _expected, StringComparison.Ordinal))
            {
                try { _onTamper(); }
                catch { /* 回滚失败不终止监视 */ }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _signal.Set(); } catch { }
        try { _thread?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _thread = null;
        if (_hKey != IntPtr.Zero)
        {
            RegCloseKey(_hKey);
            _hKey = IntPtr.Zero;
        }
        _signal.Dispose();
    }
}
