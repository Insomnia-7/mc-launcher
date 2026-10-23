using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Launcher.Infrastructure.Http;

/// <summary>
/// 一个 <see cref="HttpClient"/> 里同时握着「直连」和「走系统代理」两条通道：
/// 先按调用方给的顺序选一条（见 <see cref="HttpClientFactory"/> 的默认策略），
/// 只有在传输层失败（TLS 断流 / 连接被重置 / 超时）或服务器明确说「这条链路现在不行」
/// （408 / 429 / 5xx）时，才换另一条通道重试一次。
/// <para>
/// 换道成功之后会记住这个 host 要走哪条：否则同一个 host 上每条新连接都要先把死掉的那条
/// 重新失败一次（实测直连 bmclapi 是 8-9 秒的连接超时，16 个并发就是十几秒白等）。
/// 记忆只在「默认路线失败、另一条成功」时写入，不因为一次瞬时抖动就翻回来。
/// </para>
/// <para>
/// 为什么放在 handler 层而不是给调用方两个 HttpClient：元数据、下载器、加载器安装器、Mod 安装
/// 都只拿到一个 HttpClient，而且还要共享 UA / 解压 / 超时这些配置。分层放在这里，调用方一行都不用改。
/// </para>
/// <para>
/// 带请求体的请求不重放——请求流只能读一次，重放会发出空 body。本启动器里带 body 的只有
/// 登录换 token，那类请求本来也不走镜像。
/// </para>
/// </summary>
public sealed class DualRouteHandler : HttpMessageHandler
{
    private readonly HttpMessageInvoker _direct;
    private readonly HttpMessageInvoker? _proxied;
    private readonly Func<string?, bool> _preferDirect;
    private readonly Action<string>? _log;

    /// <summary>host → 实测该先走哪条。只记录「翻转过」的 host。</summary>
    private readonly ConcurrentDictionary<string, ProxyRoute> _learned = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="direct">直连通道。</param>
    /// <param name="proxied">代理通道；没配代理时传 null，此时退化成单通道。</param>
    /// <param name="preferDirect">按 host 决定先走哪条；默认见 <see cref="ProxyPolicy.PreferDirect"/>。</param>
    /// <param name="log">诊断出口，通常是 <see cref="HttpDiagnostics.Log"/>。</param>
    public DualRouteHandler(
        HttpMessageInvoker direct,
        HttpMessageInvoker? proxied = null,
        Func<string?, bool>? preferDirect = null,
        Action<string>? log = null)
    {
        _direct = direct ?? throw new ArgumentNullException(nameof(direct));
        _proxied = proxied;
        _preferDirect = preferDirect ?? ProxyPolicy.PreferDirect;
        _log = log;
    }

    /// <summary>是否存在第二条通道。</summary>
    public bool HasFallback => _proxied is not null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("HTTP 请求缺少 URL");
        var proxied = _proxied;

        var directFirst = _preferDirect(uri.Host);

        // 这个 host 之前已经实测翻转过：直接用上次赢的那条，别再拿 8 秒去验证一遍死路
        if (_learned.TryGetValue(uri.Host, out var learned))
            directFirst = learned == ProxyRoute.Direct;

        var first = directFirst ? _direct : proxied ?? _direct;
        var firstLabel = proxied is null ? "直连" : directFirst ? "直连" : "代理";

        // 没配代理，或者带 body 的请求不可重放：只试一条路（但按 host 选对那条），错误里也要带上 URL
        if (proxied is null || request.Content is not null)
            return await SendOnOneRouteAsync(first, firstLabel, uri, request, ct).ConfigureAwait(false);

        var second = directFirst ? proxied : _direct;
        var secondLabel = directFirst ? "代理" : "直连";

        Exception? firstError = null;
        try
        {
            var response = await first.SendAsync(request, ct).ConfigureAwait(false);
            if (!ProxyPolicy.ShouldTryOtherRoute(response.StatusCode)) return response;

            _log?.Invoke($"{uri} 先走{firstLabel}得到 {(int)response.StatusCode}，换{secondLabel}再试一次");
            response.Dispose();
        }
        catch (Exception ex) when (IsRouteFailure(ex, ct))
        {
            firstError = ex;
            _log?.Invoke($"{uri} 先走{firstLabel}失败（{Describe(ex)}），换{secondLabel}再试一次");
        }

        using var retry = Clone(request);
        try
        {
            var response = await second.SendAsync(retry, ct).ConfigureAwait(false);

            // 另一条路通了：把这个 host 钉到赢的那条上（但别把一个同样 429 的答复当成「通了」）
            if (!ProxyPolicy.ShouldTryOtherRoute(response.StatusCode))
                _learned[uri.Host] = directFirst ? ProxyRoute.Proxied : ProxyRoute.Direct;

            return response;
        }
        catch (Exception ex) when (IsRouteFailure(ex, ct))
        {
            // 超时沿用原语义（上层有自己的单文件超时判定），但通道信息仍然打进日志
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                _log?.Invoke($"{uri} {secondLabel}也超时了");
                throw;
            }

            var detail = firstError is null
                ? $"{secondLabel}：{Describe(ex)}"
                : $"{firstLabel}：{Describe(firstError)}；{secondLabel}：{Describe(ex)}";
            _log?.Invoke($"{uri} 两条通道都失败：{detail}");
            throw new HttpRequestException($"请求失败：{uri}（{firstLabel} / {secondLabel} 两条通道都没通）：{detail}", ex);
        }
    }

    private static async Task<HttpResponseMessage> SendOnOneRouteAsync(
        HttpMessageInvoker invoker, string label, Uri uri, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await invoker.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            throw new HttpRequestException($"请求失败：{uri}（{label}）{Describe(ex)}", ex);
        }
    }

    /// <summary>
    /// 传输层没打通。人为取消不算；服务器给了状态码的 <see cref="HttpRequestException"/> 也不算
    /// （那是「通了但答复不合意」，由状态码规则处理）。
    /// </summary>
    private static bool IsRouteFailure(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException http => http.StatusCode is null,
        IOException or SocketException or AuthenticationException => true,
        _ => false,
    };

    private static bool IsTransportFailure(Exception ex) =>
        ex is IOException or SocketException or AuthenticationException ||
        ex is HttpRequestException { StatusCode: null };

    /// <summary>把异常链上的消息串起来：TLS 断流的真正原因在 inner exception 里，不能只报最外层。</summary>
    internal static string Describe(Exception ex)
    {
        var messages = new List<string>(3);
        for (var current = ex; current is not null && messages.Count < 4; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)) messages.Add(current.Message.Trim());
        }

        return messages.Count switch
        {
            0 => ex.GetType().Name,
            1 => messages[0],
            _ => string.Join(" ← ", messages),
        };
    }

    private static HttpRequestMessage Clone(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
            Content = source.Content,
        };

        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in source.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _direct.Dispose();
            _proxied?.Dispose();
        }

        base.Dispose(disposing);
    }
}
