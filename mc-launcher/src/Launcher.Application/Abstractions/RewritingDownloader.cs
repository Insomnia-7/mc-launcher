using Launcher.Domain.Install;

namespace Launcher.Application.Abstractions;

/// <summary>
/// 下载器装饰器：在真正下载前把每条 URL 过一遍 <see cref="IUrlRewriter"/>。
/// 这样"换源"是下载链路本身的属性，而不是只有 GameInstaller 记得改写——
/// Forge installer、整合包文件、Mod 文件走的是同一个 IDownloader，也就一起生效。
/// 重复改写是幂等的：镜像前缀不会再命中官方前缀。
/// </summary>
public sealed class RewritingDownloader : IDownloader
{
    private readonly IDownloader _inner;
    private readonly Func<IUrlRewriter> _rewriterProvider;

    public RewritingDownloader(IDownloader inner, IUrlRewriter rewriter)
        : this(inner, () => rewriter)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
    }

    /// <summary>传函数是为了支持运行期换源：每次批量下载重新取一次当前 rewriter。</summary>
    public RewritingDownloader(IDownloader inner, Func<IUrlRewriter> rewriterProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(rewriterProvider);
        _inner = inner;
        _rewriterProvider = rewriterProvider;
    }

    public async Task<DownloadSummary> DownloadAsync(
        IReadOnlyList<DownloadEntry> entries,
        string rootDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            return await _inner.DownloadAsync(entries, rootDirectory, progress, ct).ConfigureAwait(false);
        }

        var rewriter = _rewriterProvider();
        var rewritten = new DownloadEntry[entries.Count];
        var changed = false;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var url = rewriter.Rewrite(entry.Url);
            if (url == entry.Url)
            {
                rewritten[i] = entry;
                continue;
            }

            // 改写成镜像时把官方地址记进 FallbackUrl：单条失败后下载器会自己切回去，
            // 不用等整批抛异常再重来（那样一批里只要有一个慢文件就全批失败）。
            rewritten[i] = entry with { Url = url, FallbackUrl = entry.FallbackUrl ?? entry.Url };
            changed = true;
        }

        if (!changed)
        {
            return await _inner.DownloadAsync(entries, rootDirectory, progress, ct).ConfigureAwait(false);
        }

        try
        {
            return await _inner.DownloadAsync(rewritten, rootDirectory, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 镜像是「尽量覆盖」而不是「全量」，例如 BMCLAPI 的 /maven 就没有 quilt 的产物，
            // 改写过去只会拿到 404。这种情况回落到官方地址再试一次：已经下好的文件会因为
            // size / sha1 命中而跳过，不会重复下载，代价只有一次失败请求。
            return await _inner.DownloadAsync(entries, rootDirectory, progress, ct).ConfigureAwait(false);
        }
    }
}
