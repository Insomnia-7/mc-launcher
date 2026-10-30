using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Install;
using Launcher.Domain.Versions;

namespace Launcher.Application.Install;

/// <summary>安装结果。SkippedLibraries &gt; 0 说明版本 JSON 里有 M1 还没覆盖的库形态，要盯。</summary>
public sealed record InstallReport(
    string VersionId,
    int TotalEntries,
    int DownloadedFiles,
    int SkippedFiles,
    long DownloadedBytes,
    TimeSpan Elapsed,
    bool AssetsIncluded,
    int SkippedLibraries)
{
    public override string ToString() =>
        $"{VersionId}: {TotalEntries} 项（下载 {DownloadedFiles} / 跳过 {SkippedFiles}），" +
        $"{DownloadedBytes / 1024.0 / 1024.0:F1} MB，{Elapsed.TotalSeconds:F1}s" +
        (AssetsIncluded ? "，含 assets" : "，不含 assets") +
        (SkippedLibraries > 0 ? $"，⚠ {SkippedLibraries} 个库被跳过" : "");
}

/// <summary>
/// 安装编排：版本 JSON → 文件清单（含 assets）→ 并发下载 → 落盘 version JSON。
/// 不做解压、不做校验缓存（交给下载器），也不碰 UI。
/// </summary>
public sealed partial class GameInstaller(
    IDownloader downloader,
    IVersionMetaClient meta,
    IUrlRewriter? urlRewriter = null,
    PlatformContext? platform = null)
{
    private readonly IUrlRewriter _rewriter = urlRewriter ?? OfficialUrlRewriter.Instance;
    private readonly PlatformContext _platform = platform ?? PlatformContext.Current;

    public async Task<InstallReport> InstallAsync(
        string versionId,
        string gameRoot,
        bool includeAssets = true,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var version = await meta.GetVersionAsync(versionId, ct).ConfigureAwait(false);
        return await InstallVersionAsync(version, gameRoot, includeAssets, progress, ct).ConfigureAwait(false);
    }

    /// <summary>安装一个已经拿到的版本 JSON（合并后的加载器 profile 走这里）。</summary>
    public async Task<InstallReport> InstallVersionAsync(
        VersionJson version,
        string gameRoot,
        bool includeAssets = true,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        var plan = InstallPlanner.Build(version, _platform);

        var entries = new List<DownloadEntry>(plan.Entries);

        if (includeAssets && version.AssetIndex is { } assetIndexRef)
        {
            var index = await meta.GetAssetIndexAsync(assetIndexRef, ct).ConfigureAwait(false);
            entries.AddRange(index.Objects.Values.Select(obj => new DownloadEntry(
                $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}",
                $"assets/{obj.RelativePath}".Replace('/', Path.DirectorySeparatorChar),
                obj.Size,
                obj.Hash,
                DownloadKind.Asset)));
        }

        // 同一路径只下一次（版本 JSON 里偶有重复条目）
        var unique = entries
            .GroupBy(e => e.RelativePath, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(e => e with { Url = _rewriter.Rewrite(e.Url) })
            .ToList();

        var summary = await downloader.DownloadAsync(unique, gameRoot, progress, ct).ConfigureAwait(false);

        // 版本 JSON 必须落盘：M3 的 inheritsFrom 合并、Forge 定位 libraries 都依赖它
        var versionDirectory = Path.Combine(gameRoot, "versions", version.Id);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, version.Id + ".json"),
            JsonSerializer.Serialize(version, new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);

        return new InstallReport(
            version.Id,
            unique.Count,
            summary.DownloadedFiles,
            summary.SkippedFiles,
            summary.DownloadedBytes,
            summary.Elapsed,
            includeAssets,
            plan.SkippedLibraryCount);
    }
}
