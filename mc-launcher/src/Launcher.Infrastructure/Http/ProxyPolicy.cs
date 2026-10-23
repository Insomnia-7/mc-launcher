using System.Net;

namespace Launcher.Infrastructure.Http;

/// <summary>一条请求的通道。</summary>
public enum ProxyRoute
{
    /// <summary>不经过系统代理，直接连目标。</summary>
    Direct,

    /// <summary>走系统代理（Windows 上就是 WinINET 里配的那个）。</summary>
    Proxied,
}

/// <summary>
/// 「先走哪条通道」和「服务器答复了但答复说明这条通道此刻不行时要不要换道」的判断。
/// 纯函数，不做 IO；真正的换道重试在 <see cref="DualRouteHandler"/>。
/// <para>
/// 猜测的代价被兜底压得很低：猜错最多多一次失败请求，之后同一请求会自动换到另一条通道。
/// </para>
/// </summary>
public static class ProxyPolicy
{
    /// <summary>
    /// 「先直连」的 host 名单：本地地址 + 国内镜像 / 国内云 / .cn。
    /// 注意这只是**可选策略**，不是当前默认：<see cref="HttpClientFactory"/> 在代理验证通过时
    /// 默认先走代理（本机实测直连 bmclapi 是连接超时 / 429，代理 10/10 通），
    /// 然后在 <see cref="DualRouteHandler"/> 里按 host 记住谁赢。
    /// </summary>
    private static readonly string[] DirectHostSuffixes =
    [
        "bangbang93.com",      // BMCLAPI 镜像
        "aliyun.com",          // maven 镜像
        "aliyuncs.com",
        "tencent-cloud.com",   // maven 镜像
        "myqcloud.com",
        "huaweicloud.com",
        "cn",                  // .cn 顶级域
    ];

    /// <summary>该 host 是否应该先试直连（可选策略，见 <see cref="DirectHostSuffixes"/> 的说明）。</summary>
    public static bool PreferDirect(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        var name = host.Trim().TrimEnd('.');
        if (name.Length == 0) return true;

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(name, out var address)) return IsLocalOrPrivate(address);

        foreach (var suffix in DirectHostSuffixes)
        {
            if (name.Equals(suffix, StringComparison.OrdinalIgnoreCase)) return true;

            // 必须整段匹配："evilcn.example" 不是 .cn 域名；"example.cn" 才是
            if (name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// 服务器给了明确答复，但答复说明「这条通道此刻不健康」：限流、网关错误、后端不可用。
    /// 这时换另一条通道（另一个出口 IP / 另一条链路）重试有意义。
    /// <para>
    /// 404 / 403 不在其中——那是内容层面的答案，换通道拿到的还是一样。
    /// </para>
    /// </summary>
    public static bool ShouldTryOtherRoute(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        (HttpStatusCode)429 => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        _ => false,
    };

    private static bool IsLocalOrPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false,
        };
    }
}
