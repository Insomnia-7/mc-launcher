using System.Net;

namespace Launcher.Infrastructure.Http;

/// <summary>
/// 统一的 <see cref="HttpClient"/> 构造。除了 UA / 解压 / 连接超时，这里做两件事：
/// <list type="number">
/// <item>问 <see cref="ProxyEnvironment"/> 有没有系统代理（并且它是不是真的能打通目标）；</item>
/// <item>把「直连」和「代理」两条通道一起塞进 <see cref="DualRouteHandler"/>，
/// 让每条请求都能在失败时换道重试。</item>
/// </list>
/// <para>
/// 背景：这台机器上 `bmclapi2.bangbang93.com` 的链路会时不时 TLS 断流
/// （Received an unexpected EOF / 连接被中止），而旧实现只有一个进程级 <c>UseProxy</c> 布尔：
/// 探测结论一旦和真实可用性不一致，整进程就锁死在坏掉的那条路上，一次瞬时抖动直接让安装失败。
/// </para>
/// </summary>
public static class HttpClientFactory
{
    public static HttpClient Create(
        string? userAgent = null,
        TimeSpan? timeout = null,
        TimeSpan? connectTimeout = null)
    {
        var probe = ProxyEnvironment.Current;

        var direct = NewHandler(useProxy: false, connectTimeout);
        var proxied = probe.Configured ? NewHandler(useProxy: true, connectTimeout) : null;

        // 顺序按实测定，不按「镜像在国内所以直连更快」这种直觉：
        // 这台机器上直连 bmclapi 是连接超时（8-9s）/ 429，走代理 10/10 通；官方域名两条都通。
        // 所以代理验证通过时先走代理，直连只当第二跳。
        // 隧道没验证过（或根本没配代理）时反过来先直连：死代理会把每条请求都拖到连接超时。
        // 之后每条请求的真实结果还会被 DualRouteHandler 按 host 记住，慢慢收敛到对的那条。
        Func<string?, bool> preferDirect = probe is { Configured: true, TunnelVerified: true }
            ? static _ => false
            : static _ => true;

        var handler = new DualRouteHandler(
            new HttpMessageInvoker(direct, disposeHandler: true),
            proxied is null ? null : new HttpMessageInvoker(proxied, disposeHandler: true),
            preferDirect,
            HttpDiagnostics.Log);

        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };

        if (userAgent is { Length: > 0 })
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        return client;
    }

    /// <summary>诊断用：一句话说清当前的代理结论。</summary>
    public static string DescribeProxy() => ProxyEnvironment.Current.Describe();

    /// <summary>
    /// 每条通道一个 handler。两条都必须有自己的连接池，否则共享池会把请求串到另一条通道上去。
    /// </summary>
    private static SocketsHttpHandler NewHandler(bool useProxy, TimeSpan? connectTimeout) => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
        UseProxy = useProxy,
    };
}
