using Launcher.Application.Abstractions;
using Launcher.Domain.Content;
using Launcher.Domain.Install;

namespace Launcher.Application.Mods;

public sealed record ModInstallProgress(string Stage, double Percent, string? Detail)
{
    public static ModInstallProgress Of(string stage, double percent, string? detail = null) =>
        new(stage, percent, detail);
}

/// <summary>
/// 把 Modrinth 上的一个项目装进某个版本，顺带拉必需依赖。
/// 目标目录由 <see cref="DownloadCategory"/> 决定：mods / resourcepacks / datapacks / shaderpacks。
/// 整合包不走这里（它要新建版本，见 <see cref="Install.ModpackInstaller"/>）。
/// </summary>
public sealed class ModInstallService(IDownloader downloader, IModrinthClient modrinth)
{
    /// <summary>依赖递归深度上限，防止环和"装一个 mod 下 40 个库"。</summary>
    private const int MaxDependencyDepth = 3;

    public async Task<ModInstallReport> InstallAsync(
        string gameRoot,
        string instanceId,
        DownloadCategory category,
        string projectIdOrSlug,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var subdirectory = category.InstanceSubdirectory()
            ?? throw new InvalidOperationException($"{category.Title()} 不能装进版本目录");

        var environment = InstanceEnvironment.Resolve(gameRoot, instanceId);
        var targetDirectory = Path.Combine(gameRoot, "instances", instanceId, ".minecraft", subdirectory);
        Directory.CreateDirectory(targetDirectory);

        // 资源包 / 光影 / 数据包与加载器无关，按加载器过滤会一个都搜不到
        var loader = category.UsesLoader() ? environment.LoaderSlug : null;

        progress?.Report(ModInstallProgress.Of("解析版本", 0, $"{environment.Describe()} → {subdirectory}"));

        var versions = await modrinth
            .GetVersionsAsync(projectIdOrSlug, environment.GameVersion, loader, ct)
            .ConfigureAwait(false);

        var version = versions.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"这个项目没有适配 {environment.Describe()} 的版本（Modrinth 上没有匹配的发布）");

        var file = version.PrimaryFile
            ?? throw new InvalidOperationException($"版本 {version.VersionNumber} 没有可下载的文件");

        var entries = new List<(DownloadEntry Entry, string ModId)>
        {
            (new DownloadEntry(file.Url, file.Filename, file.Size, file.Hashes?.Sha1, DownloadKind.Mod),
                version.ProjectId is { Length: > 0 } primaryId ? primaryId : projectIdOrSlug),
        };

        // 必需依赖：只做广度扩散，深度用 visited 控住
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { version.ProjectId };
        var dependencyCount = 0;

        var pending = new Queue<(string ProjectId, int Depth)>();
        foreach (var dependency in version.RequiredDependencies)
        {
            if (dependency.ProjectId is { Length: > 0 } id) pending.Enqueue((id, 1));
        }

        while (pending.Count > 0)
        {
            var (projectId, depth) = pending.Dequeue();
            if (depth > MaxDependencyDepth || !visited.Add(projectId)) continue;

            progress?.Report(ModInstallProgress.Of("解析依赖", 0, projectId));

            try
            {
                var dependencyVersions = await modrinth
                    .GetVersionsAsync(projectId, environment.GameVersion, loader, ct)
                    .ConfigureAwait(false);

                var dependencyFile = dependencyVersions.FirstOrDefault()?.PrimaryFile;
                if (dependencyFile is null) continue;

                entries.Add((new DownloadEntry(
                    dependencyFile.Url,
                    dependencyFile.Filename,
                    dependencyFile.Size,
                    dependencyFile.Hashes?.Sha1,
                    DownloadKind.Mod), projectId));
                dependencyCount++;

                foreach (var nested in dependencyVersions[0].RequiredDependencies)
                {
                    if (nested.ProjectId is { Length: > 0 } nestedId) pending.Enqueue((nestedId, depth + 1));
                }
            }
            catch (HttpRequestException)
            {
                // 单个依赖解析失败不该拖垮整次安装，主文件已经就位
            }
        }

        // 整合包常给 jar 加「[中文名] 」前缀，也常自带旧版本；同一个 mod 装两份会让 Forge
        // 报 duplicate mod 直接起不来。先按名字比，模组再按 jar 内的 modId 比。
        var skipped = 0;
        var pendingDownloads = new List<DownloadEntry>(entries.Count);
        foreach (var (entry, modId) in entries)
        {
            if (ModFileGuard.FindExisting(targetDirectory, entry.RelativePath) is not null
                || (category.NeedsModIdGuard()
                    && ModFileGuard.FindExistingByModId(targetDirectory, modId, projectIdOrSlug) is not null))
            {
                skipped++;
                continue;
            }

            pendingDownloads.Add(entry);
        }

        if (pendingDownloads.Count == 0)
        {
            progress?.Report(ModInstallProgress.Of("完成", 100, $"{file.Filename} 已在版本里，跳过"));
            return new ModInstallReport(
                version.Name, version.VersionNumber, file.Filename, file.Size,
                targetDirectory, 0, skipped);
        }

        var adapter = progress is null
            ? null
            : new DownloadProgressAdapter(progress, $"下载{category.Title()}");

        await downloader
            .DownloadAsync(pendingDownloads, targetDirectory, adapter, ct)
            .ConfigureAwait(false);

        progress?.Report(ModInstallProgress.Of("完成", 100, file.Filename));

        return new ModInstallReport(
            version.Name,
            version.VersionNumber,
            file.Filename,
            file.Size,
            targetDirectory,
            dependencyCount,
            skipped);
    }

    private sealed class DownloadProgressAdapter(IProgress<ModInstallProgress> outer, string stage)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            outer.Report(ModInstallProgress.Of(
                stage,
                value.Percent,
                $"{value.CompletedFiles}/{value.TotalFiles} · {value.CompletedBytes / 1024.0 / 1024.0:F1} MB"));
    }
}
