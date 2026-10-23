using System.Net;
using Launcher.Application.Settings;

namespace Launcher.Infrastructure.Http;

/// <summary>所有启动器 HTTP 服务共用的网络策略。配置为进程快照，重启后生效。</summary>
public static class HttpClientFactory
{
    // 创建客户端只读一次本地设置；代理探测推迟到首个请求，失败也在请求时呈现。
    private static readonly Lazy<(ProxySettings? Settings, string? Error)> Saved = new(() =>
    {
        try { return (ProxySettingsStore.Read(), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { return (null, "代理配置无法读取，请打开网络设置修正，保存后重启启动器。"); }
    });

    public static HttpClient Create(
        string? userAgent = null,
        TimeSpan? timeout = null,
        TimeSpan? connectTimeout = null,
        ProxySettings? proxySettings = null)
    {
        var configuration = proxySettings is null ? Saved.Value : (Settings: proxySettings.Validate(), Error: (string?)null);
        var handler = new DeferredHttpHandler(() =>
        {
            if (configuration.Error is not null) throw new InvalidOperationException(configuration.Error);
            return CreateRoutes(configuration.Settings!, connectTimeout);
        });
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
        if (userAgent is { Length: > 0 }) client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    private static HttpMessageHandler CreateRoutes(ProxySettings settings, TimeSpan? connectTimeout)
    {
        if (settings.Mode == ProxyMode.Direct) return NewHandler(false, connectTimeout);
        if (settings.Mode == ProxyMode.Custom)
        {
            var custom = NewHandler(true, connectTimeout);
            custom.Proxy = new WebProxy(new Uri(settings.Address));
            return custom;
        }

        var probe = ProxyEnvironment.Current;
        return new DualRouteHandler(
            new HttpMessageInvoker(NewHandler(false, connectTimeout), disposeHandler: true),
            probe.Configured ? new HttpMessageInvoker(NewHandler(true, connectTimeout), disposeHandler: true) : null,
            _ => !probe.TunnelVerified,
            HttpDiagnostics.Log);
    }

    public static string DescribeProxy()
    {
        var configuration = Saved.Value;
        if (configuration.Error is not null) return configuration.Error;
        return configuration.Settings!.Mode switch
        {
            ProxyMode.Direct => "直接连接（不使用代理）",
            ProxyMode.Custom => "自定义代理（失败不回退直连）",
            _ => "自动选择（系统 / 环境变量代理，失败时尝试另一条通道）"
        };
    }

    private static SocketsHttpHandler NewHandler(bool useProxy, TimeSpan? connectTimeout) => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10),
        UseProxy = useProxy,
    };
}
