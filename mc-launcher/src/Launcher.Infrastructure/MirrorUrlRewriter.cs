using Launcher.Application.Abstractions;

namespace Launcher.Infrastructure;

/// <summary>把官方地址改写到 BMCLAPI 镜像。规则集中在 SourceRewriter 里。</summary>
public sealed class MirrorUrlRewriter : IUrlRewriter
{
    public static readonly MirrorUrlRewriter Instance = new();
    public string Rewrite(string url) => SourceRewriter.Apply(url, useMirror: true);
}

/// <summary>按下载源挑 rewriter。这是"官方 / 镜像"唯一的接线点。</summary>
public static class UrlRewriterFactory
{
    public static IUrlRewriter From(Launcher.Application.Settings.DownloadSource source) =>
        source == Launcher.Application.Settings.DownloadSource.Bmclapi
            ? MirrorUrlRewriter.Instance
            : OfficialUrlRewriter.Instance;
}
