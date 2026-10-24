using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace Launcher.Application.Diagnostics;

/// <summary>下载失败到底是哪一类：让用户知道该去「网络设置」还是该等一等。</summary>
public enum NetworkFailure
{
    None = 0,
    Dns,
    ConnectionRefused,
    Timeout,
    Tls,
    Proxy,
    Unknown,
}

/// <summary>
/// 把 HttpRequestException / SocketException 这些「一句话很难懂」的异常翻成人话。
/// 分类只用于提示，不影响重试逻辑（重试仍由 HttpDownloader 的双通道负责）。
/// </summary>
public static class NetworkErrors
{
    public static NetworkFailure Classify(Exception? exception)
    {
        var sawHttp = false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => NetworkFailure.Dns,
                        SocketError.ConnectionRefused => NetworkFailure.ConnectionRefused,
                        SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => NetworkFailure.Timeout,
                        _ => NetworkFailure.Unknown,
                    };
                case AuthenticationException:
                    return NetworkFailure.Tls;
                case TimeoutException:
                    return NetworkFailure.Timeout;
                case TaskCanceledException:
                    return NetworkFailure.Timeout;
                case HttpRequestException http when MentionsProxy(http.Message):
                    return NetworkFailure.Proxy;
                case HttpRequestException:
                    // HttpClient 总是把真正的原因塞在 InnerException 里，继续往下看
                    sawHttp = true;
                    continue;
            }
        }
        return sawHttp ? NetworkFailure.Unknown : NetworkFailure.None;
    }

    /// <summary>认出来时返回带处置建议的中文；认不出来返回 null（交给上层按原样显示）。</summary>
    public static string? Describe(Exception? exception)
    {
        var failure = Classify(exception);
        return failure switch
        {
            NetworkFailure.Dns => "域名解析失败：网络可能断了，或者 DNS 被拦截。检查系统网络，或在「网络设置…」里换个下载源再试。",
            NetworkFailure.ConnectionRefused => "连接被拒绝：对方端口没开着。如果你在「网络设置…」里填了代理，先确认代理还在运行，或者直接关掉它。",
            NetworkFailure.Timeout => "连接超时：网络太慢或对方没响应。可以重试，或在「网络设置…」里换国内镜像（BMCLAPI）。",
            NetworkFailure.Tls => "HTTPS 证书校验失败：多半是代理在做中间人解密，或者系统时间不对。关掉代理或在「网络设置…」里换一条路再试。",
            NetworkFailure.Proxy => "代理连不上：填的代理地址已失效。到「网络设置…」里改成直连，或更新代理地址。",
            _ => null,
        };
    }

    private static bool MentionsProxy(string message) =>
        message.Contains("proxy", StringComparison.OrdinalIgnoreCase)
        || message.Contains("代理", StringComparison.Ordinal);
}
