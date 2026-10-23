using System.Net.Http;
using Launcher.Application.Abstractions;
using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Meta;

/// <summary>
/// 「镜像先试、官方兜底」的一次性回落。
/// <para>
/// 镜像站不是全量也不总健康：BMCLAPI 会 404（没镜像这个产物）、429（限流）、
/// 偶发 TLS 断流（Received an unexpected EOF）。文件下载那条链路由 RewritingDownloader 兜底，
/// 元数据（manifest / 版本 JSON / asset index）原先没有兜底，于是镜像一抖整个安装就失败——
/// 这正是「装到一半报 SSL 错误」的直接原因。这里把同样的兜底补到元数据上。
/// </para>
/// </summary>
public static class MirrorFallback
{
    /// <summary>算出一条元数据 URL 的「先试哪个」与「失败回落哪个」。官方源只有一个地址，Fallback 为 null。</summary>
    public static (string Primary, string? Fallback) Route(IUrlRewriter rewriter, string officialUrl)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
        ArgumentException.ThrowIfNullOrWhiteSpace(officialUrl);

        var primary = rewriter.Rewrite(officialUrl);
        return string.Equals(primary, officialUrl, StringComparison.Ordinal)
            ? (primary, null)
            : (primary, officialUrl);
    }

    /// <summary>
    /// 先按 <paramref name="officialUrl"/> 走当前下载源；失败（且不是调用方取消）就用官方地址再来一次。
    /// 两次都失败时抛出的消息里带两个 URL 和各自的失败原因。
    /// </summary>
    public static async Task<string> GetStringAsync(
        Func<string, Task<string>> fetch,
        string officialUrl,
        IUrlRewriter rewriter,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        var (primary, fallback) = Route(rewriter, officialUrl);

        try
        {
            return await fetch(primary).ConfigureAwait(false);
        }
        catch (Exception ex) when (fallback is not null && !IsCallerCancellation(ex, ct))
        {
            log?.Invoke($"镜像不可用，回落官方：{primary}（{DualRouteHandler.Describe(ex)}）");

            try
            {
                return await fetch(fallback).ConfigureAwait(false);
            }
            catch (Exception second) when (!IsCallerCancellation(second, ct))
            {
                throw new HttpRequestException(
                    $"镜像与官方都拿不到：{primary}（{DualRouteHandler.Describe(ex)}）/ {fallback}（{DualRouteHandler.Describe(second)}）",
                    second);
            }
        }
    }

    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;
}
