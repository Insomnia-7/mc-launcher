using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Launcher.Infrastructure.Http;

/// <summary>系统代理的探测结论。</summary>
/// <param name="Configured">是否配置了代理（系统设置 / 环境变量 / 强制开关）。</param>
/// <param name="Address">代理地址；探测异常导致只知道「配了代理」时为 null。</param>
/// <param name="TunnelVerified">
/// 是否验证过「这个代理真的能 CONNECT 到目标」。
/// 只是端口 listening 不足以证明这一点——代理进程活着但上游隧道已经废了很常见，
/// 那种情况下把整进程钉死在代理上，所有请求都会一起死。
/// </param>
/// <param name="Detail">给人看的探测说明。</param>
public sealed record ProxyProbe(bool Configured, Uri? Address, bool TunnelVerified, string Detail)
{
    public static readonly ProxyProbe Direct = new(false, null, false, "未配置代理，全程直连");

    public string Describe() => Configured
        ? $"走系统代理 {Address?.ToString() ?? "(地址未知)"}（{(TunnelVerified ? "隧道已验证" : "隧道未验证")}：{Detail}）"
        : Detail;
}

/// <summary>
/// 探测一次系统代理，结果整进程复用。<see cref="Current"/> 只回答「有没有代理、它看起来能不能用」，
/// 不决定任何请求走哪条路——那是 <see cref="DualRouteHandler"/> 的事。
/// </summary>
public static class ProxyEnvironment
{
    /// <summary>隧道探测的目标。境外 + 所有联网流程都会碰它，代理连不上它基本等于没用。</summary>
    public const string ProbeHost = "piston-meta.mojang.com";
    public const int ProbePort = 443;

    private const int ProbeTimeoutMs = 1500;

    private static readonly object Gate = new();
    private static ProxyProbe? _cached;

    public static ProxyProbe Current
    {
        get
        {
            if (Volatile.Read(ref _cached) is { } cached) return cached;

            lock (Gate)
            {
                return _cached ??= Detect();
            }
        }
    }

    /// <summary>丢掉缓存，下次重新探测（诊断命令 / 自检用）。</summary>
    public static void Forget() => Volatile.Write(ref _cached, null);

    public static ProxyProbe Detect()
    {
        try
        {
            if (IsSet("MCLAUNCHER_FORCE_DIRECT"))
                return new ProxyProbe(false, null, false, "MCLAUNCHER_FORCE_DIRECT=1，强制直连");

            // 代理可能来自 WinINET，也可能来自 http_proxy / HTTPS_PROXY 环境变量，
            // 两种情况下 DefaultProxy 的具体类型不同，统一用 GetProxy 问出来。
            var probeTarget = new Uri($"https://{ProbeHost}:{ProbePort}/");
            var proxy = HttpClient.DefaultProxy.GetProxy(probeTarget);

            // 没配代理时 GetProxy 原样返回目标地址
            if (proxy is null || proxy == probeTarget || HttpClient.DefaultProxy.IsBypassed(probeTarget))
                return ProxyProbe.Direct;

            if (IsSet("MCLAUNCHER_FORCE_PROXY"))
                return new ProxyProbe(true, proxy, true, "MCLAUNCHER_FORCE_PROXY=1，按可用处理");

            // 老开关的语义是「别提了，就当代理能用」
            if (IsSet("MCLAUNCHER_NO_PROXY_PROBE"))
                return new ProxyProbe(true, proxy, true, "MCLAUNCHER_NO_PROXY_PROBE=1，跳过隧道探测");

            if (!proxy.IsLoopback)
                return new ProxyProbe(true, proxy, true, "远端代理，不做隧道探测");

            // 只看端口在听不够：真发一次 CONNECT，看它到底能不能打到目标
            var (ok, detail) = ProbeTunnel(proxy);
            return new ProxyProbe(true, proxy, ok, detail);
        }
        catch (Exception ex)
        {
            // 探测自己炸了不能判成「没有代理」：这台机器可能靠代理才能访问境外域名。
            // 结论留成「配了代理但没验证」——请求先试直连，失败再走代理兜底。
            return new ProxyProbe(
                Configured: true,
                Address: null,
                TunnelVerified: false,
                Detail: $"探测异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 向本地代理发一次 <c>CONNECT host:443</c>，只看状态行。
    /// 成功即证明「代理 → 目标」这条隧道此刻是通的，比端口探测强得多。
    /// </summary>
    private static (bool Ok, string Detail) ProbeTunnel(Uri proxy)
    {
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            using var timeout = new CancellationTokenSource(ProbeTimeoutMs);
            socket.ConnectAsync(proxy.Host, proxy.Port, timeout.Token).AsTask().GetAwaiter().GetResult();

            using var stream = new NetworkStream(socket, ownsSocket: false)
            {
                ReadTimeout = ProbeTimeoutMs,
                WriteTimeout = ProbeTimeoutMs,
            };

            var head = Encoding.ASCII.GetBytes(
                $"CONNECT {ProbeHost}:{ProbePort} HTTP/1.1\r\n" +
                $"Host: {ProbeHost}:{ProbePort}\r\n" +
                "Proxy-Connection: keep-alive\r\n\r\n");
            stream.Write(head, 0, head.Length);

            var buffer = new byte[96];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return (false, $"CONNECT {ProbeHost}:{ProbePort} 无响应");

            var statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\r', '\n')[0];
            var parts = statusLine.Split(' ');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var code))
            {
                return code is >= 200 and < 300
                    ? (true, $"CONNECT {ProbeHost}:{ProbePort} → {statusLine}")
                    : (false, $"CONNECT {ProbeHost}:{ProbePort} → {statusLine}");
            }

            return (false, $"代理回复了看不懂的状态行：{statusLine}");
        }
        catch (OperationCanceledException)
        {
            return (false, $"连 {proxy.Host}:{proxy.Port} 超时（{ProbeTimeoutMs}ms）");
        }
        catch (Exception ex)
        {
            return (false, $"{proxy.Host}:{proxy.Port} 连不上：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsSet(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && value != "0";
}
