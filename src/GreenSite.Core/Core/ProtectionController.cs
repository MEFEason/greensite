using System.Net;
using System.Net.Sockets;
using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>
/// 保护状态机。开启：启动本机端点 → 备份用户代理 → 写 AutoConfigURL → 启动篡改监视。
/// 关闭/退出：先停监视 → 精确还原用户代理 → 停端点（顺序不可颠倒，否则被误判为篡改）。
/// </summary>
public sealed class ProtectionController : IDisposable
{
    private readonly AppConfig _config;
    private readonly ProxySettings _proxy;
    private readonly LogService _log;
    private readonly int _port;
    private readonly bool _enableGuard;

    private Ruleset _ruleset;
    private long _seq;
    private BlockingProxy? _endpoint;
    private RegistryGuard? _guard;
    private bool _started;
    private string _upstreamPac = "DIRECT";
    private string[] _upstreamBypass = [];

    public ProtectionState State { get; private set; } = ProtectionState.Inactive;
    public event EventHandler<BlockLogEntry>? Blocked;

    public string PacUrl => $"http://127.0.0.1:{_port}/proxy.pac?v={_seq}";
    public int Port => _port;
    public LogService Log => _log;
    public int TodayBlockedCount => _log.TodayCount;
    public bool IsActive => _started;

    public ProtectionController(
        AppConfig config,
        Ruleset ruleset,
        LogService? log = null,
        ProxySettings? proxy = null,
        int? port = null,
        bool enableGuard = true)
    {
        _config = config;
        _ruleset = ruleset;
        _log = log ?? new LogService();
        _proxy = proxy ?? new ProxySettings();
        _enableGuard = enableGuard;
        _port = port ?? (config.Port > 0 ? config.Port : FindFreePort());
        _seq = Math.Max(config.PacSeq, 1);
        config.Port = _port;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Start()
    {
        if (_started) return;

        _endpoint = new BlockingProxy(
            _port, new RuleEngine(_ruleset), _log,
            () => PacGenerator.Generate(_ruleset, _port, _seq, _upstreamPac, _upstreamBypass));
        _endpoint.Blocked += (_, e) => Blocked?.Invoke(this, e);
        _endpoint.Start();

        _config.ProxyBackup = SafeSnapshot();
        RefreshUpstream();

        if (_enableGuard)
        {
            _guard = new RegistryGuard(ProxySettings.DefaultKeyPath, PacUrl, OnTamper);
            _guard.Suppress(TimeSpan.FromSeconds(3));
        }
        _proxy.ApplyAutoConfigUrl(PacUrl);
        _guard?.Start();

        _started = true;
        State = ProtectionState.Active;
        _config.ProtectionEnabled = true;
    }

    /// <summary>读取系统手动代理（加速器）并带入 PAC；变化则递增 seq 强制刷新。</summary>
    public void RefreshUpstream()
    {
        if (!_config.FollowUpstreamProxy)
        {
            if (_upstreamPac != "DIRECT" || _upstreamBypass.Length != 0)
            {
                _upstreamPac = "DIRECT";
                _upstreamBypass = [];
                _seq++;
                _config.PacSeq = _seq;
            }
            return;
        }
        var (pac, bypass) = _proxy.ReadUpstream();
        if (pac == _upstreamPac && bypass.SequenceEqual(_upstreamBypass)) return;
        _upstreamPac = pac;
        _upstreamBypass = bypass;
        _seq++;
        _config.PacSeq = _seq;
        if (_started)
        {
            _guard?.SetExpected(PacUrl);
            _guard?.Suppress(TimeSpan.FromSeconds(3));
            _proxy.ApplyAutoConfigUrl(PacUrl);
        }
    }

    /// <summary>异常退出可能残留自己的 PAC；此时不能把它当成「用户原代理」。</summary>
    private ProxyBackup SafeSnapshot()
    {
        var snapshot = _proxy.Snapshot();
        var current = snapshot.AutoConfigUrl;
        if (string.IsNullOrEmpty(current) ||
            !current.Contains("127.0.0.1", StringComparison.Ordinal) ||
            !current.Contains("/proxy.pac", StringComparison.Ordinal))
            return snapshot;

        if (_config.ProxyBackup is { } previous) return previous;
        snapshot.AutoConfigUrlExisted = false;
        snapshot.AutoConfigUrl = null;
        return snapshot;
    }

    private void OnTamper()
    {
        // 该键的任何变化都会触发：先吸收加速器对 ProxyServer/ProxyEnable 的正常改动，
        // 再检查 AutoConfigURL 是否真的被移走，被移走才回滚。
        RefreshUpstream();
        if (string.Equals(_proxy.ReadAutoConfigUrl(), PacUrl, StringComparison.Ordinal)) return;
        _guard?.Suppress(TimeSpan.FromSeconds(3));
        _proxy.ApplyAutoConfigUrl(PacUrl);
    }

    /// <summary>规则变更（含组开关、单条开关）：重建规则集并递增 PAC 版本，强制浏览器刷新。</summary>
    public void ApplyRules(Ruleset ruleset)
    {
        _ruleset = ruleset;
        _seq++;
        _config.PacSeq = _seq;
        if (!_started) return;
        _guard?.SetExpected(PacUrl);
        _guard?.Suppress(TimeSpan.FromSeconds(3));
        _proxy.ApplyAutoConfigUrl(PacUrl);
    }

    /// <summary>关闭保护（密码验证后调用）：解除拦截，进程保留。</summary>
    public void StopProtection()
    {
        if (!_started) return;
        _guard?.Dispose();
        _guard = null;
        _proxy.Restore(_config.ProxyBackup ?? _proxy.Snapshot());
        _endpoint?.Stop();
        _endpoint = null;
        _started = false;
        State = ProtectionState.Inactive;
        _config.ProtectionEnabled = false;
    }

    /// <summary>退出程序（密码验证后调用）：停止拦截并结束进程。</summary>
    public void Shutdown() => StopProtection();

    public void Dispose() => Shutdown();
}
