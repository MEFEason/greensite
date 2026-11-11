using System.Net;
using System.Net.Sockets;
using System.Text;
using GreenSite.Core.Models;

namespace GreenSite.Core.Core;

/// <summary>
/// 本机单端口端点：既提供 PAC 脚本，也承接被分流过来的拦截流量。
/// 只绑定 127.0.0.1、只读请求首行、不建隧道、不解密 TLS。
/// 判定引擎可热更新：规则变更后必须同步重建，否则端点会一直使用启动时的旧规则集。
/// </summary>
public sealed class BlockingProxy : IDisposable
{
    private const int MaxConcurrent = 32;
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly int _port;
    private readonly LogService _log;
    private readonly Func<string> _pacProvider;

    private RuleEngine _engine;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _active;
    private bool _disposed;

    public event EventHandler<BlockLogEntry>? Blocked;
    public int Port => _port;
    public bool IsRunning => _listener is not null;

    public BlockingProxy(int port, RuleEngine engine, LogService log, Func<string> pacProvider)
    {
        _port = port;
        _engine = engine;
        _log = log;
        _pacProvider = pacProvider;
    }

    /// <summary>规则变更后重建判定引擎（Volatile 保证跨线程可见）。</summary>
    public void ApplyRules(Ruleset ruleset) =>
        Volatile.Write(ref _engine, new RuleEngine(ruleset));

    public void Start()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener!.AcceptTcpClientAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }

                if (Interlocked.Increment(ref _active) > MaxConcurrent)
                {
                    Interlocked.Decrement(ref _active);
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch { /* 监听退出不需上报 */ }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var stream = client.GetStream();
            var buffer = new byte[8192];

            int read;
            try { read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token); }
            catch { return; }
            if (read <= 0) return;

            var firstLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')[0].Trim();
            if (firstLine.StartsWith("GET /proxy.pac", StringComparison.OrdinalIgnoreCase))
            {
                await WritePacAsync(stream, timeout.Token);
                return;
            }

            var target = firstLine.Split(' ').Skip(1).FirstOrDefault() ?? "";
            var url = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? target : null;
            var host = ExtractHost(firstLine, target);
            var decision = Volatile.Read(ref _engine).Evaluate(host, url);

            // Direct 不该到达本机端点（PAC 不会分流），不记日志以免虚增计数。
            // Blocked 事件只在真正拦截时触发，保证「界面提示 ↔ 日志 block ↔ 403」一致。
            if (decision.Kind != DecisionKind.Direct)
            {
                var entry = new BlockLogEntry(
                    DateTimeOffset.Now, decision.Host,
                    decision.GroupName ?? "", decision.RuleValue ?? "",
                    decision.Kind == DecisionKind.Allow);
                _log.Append(entry);
                if (decision.Kind == DecisionKind.Block) Blocked?.Invoke(this, entry);
            }
            await WriteForbiddenAsync(stream, decision.Host, decision.GroupName, timeout.Token);
        }
        catch { /* 单个连接失败不影响服务 */ }
        finally
        {
            Interlocked.Decrement(ref _active);
            client.Dispose();
        }
    }

    private async Task WritePacAsync(NetworkStream stream, CancellationToken token)
    {
        var body = Utf8.GetBytes(_pacProvider());
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/x-ns-proxy-autoconfig\r\n" +
            "Cache-Control: no-store, must-revalidate\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        // 合并成一次写入，避免客户端只读一次时只拿到响应头
        var response = new byte[header.Length + body.Length];
        header.CopyTo(response, 0);
        body.CopyTo(response, header.Length);
        await stream.WriteAsync(response, token);
    }

    private static async Task WriteForbiddenAsync(
        NetworkStream stream, string host, string? group, CancellationToken token)
    {
        var html = "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
                   "<title>访问被限制 · GreenSite</title></head>" +
                   "<body style=\"font-family:system-ui;padding:48px;color:#14231D\">" +
                   "<h1>访问被 GreenSite 限制</h1>" +
                   $"<p>域名：{WebUtility.HtmlEncode(host)}</p>" +
                   $"<p>命中分组：{WebUtility.HtmlEncode(group ?? "-")}</p>" +
                   "<p>如需放行，请在 GreenSite 中用管理密码将该域名加入白名单或调整生效时段。</p>" +
                   "</body></html>";
        var body = Utf8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 403 Forbidden\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        // 合并成一次写入，避免客户端只读一次时只拿到响应头
        var response = new byte[header.Length + body.Length];
        header.CopyTo(response, 0);
        body.CopyTo(response, header.Length);
        await stream.WriteAsync(response, token);
    }

    private static string ExtractHost(string firstLine, string target)
    {
        if (string.IsNullOrEmpty(target)) return "";
        if (firstLine.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase)) return target;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) return uri.Host;
        var slash = target.IndexOf('/');
        return slash > 0 ? target[..slash] : target;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
