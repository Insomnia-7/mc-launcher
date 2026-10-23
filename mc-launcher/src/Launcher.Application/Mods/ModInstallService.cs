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

    internal async Task<PreparedModInstall> PrepareAsync(
        string gameRoot,
        string instanceId,
        DownloadCategory category,
        string projectIdOrSlug,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default, bool strict = false, string? versionId = null)
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

        var version = (versionId is null ? versions.FirstOrDefault() : versions.FirstOrDefault(v => v.Id == versionId))
            ?? throw new InvalidOperationException(
                $"这个项目没有适配 {environment.Describe()} 的版本（Modrinth 上没有匹配的发布）");

        var file = version.PrimaryFile
            ?? throw new InvalidOperationException($"版本 {version.VersionNumber} 没有可下载的文件");

        var entries = new List<PlannedModFile>
        {
            new(new DownloadEntry(file.Url, file.Filename, file.Size, file.Hashes?.Sha1, DownloadKind.Mod),
                new("modrinth", version.ProjectId, version.Id, version.VersionNumber, file.Hashes?.Sha1 ?? "", file.Size), projectIdOrSlug),
        };

        // 必需依赖：只做广度扩散，深度用 visited 控住
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { version.ProjectId };

        var pending = new Queue<(string ProjectId, string? VersionId, int Depth)>();
        foreach (var dependency in version.RequiredDependencies)
        {
            if (dependency.ProjectId is { Length: > 0 } id) pending.Enqueue((id, dependency.VersionId, 1));
            else if (strict) throw new InvalidOperationException("必需前置缺少可确认的项目 ID。");
        }

        while (pending.Count > 0)
        {
            var (projectId, dependencyVersionId, depth) = pending.Dequeue();
            if (!visited.Add(projectId)) continue;
            if (depth > MaxDependencyDepth)
            {
                if (strict) throw new InvalidOperationException("必需前置超过支持的递归深度，已保留旧模组。");
                continue;
            }

            progress?.Report(ModInstallProgress.Of("解析依赖", 0, projectId));

            try
            {
                var dependencyVersions = await modrinth
                    .GetVersionsAsync(projectId, environment.GameVersion, loader, ct)
                    .ConfigureAwait(false);

                var chosen = dependencyVersionId is null ? dependencyVersions.FirstOrDefault() : dependencyVersions.FirstOrDefault(v => v.Id == dependencyVersionId);
                var dependencyFile = chosen?.PrimaryFile;
                if (dependencyFile is null)
                {
                    if (strict) throw new InvalidOperationException($"必需前置没有适配版本：{projectId}");
                    continue;
                }

                entries.Add(new(new DownloadEntry(
                    dependencyFile.Url,
                    dependencyFile.Filename,
                    dependencyFile.Size,
                    dependencyFile.Hashes?.Sha1,
                    DownloadKind.Mod), new("modrinth", projectId, chosen!.Id, chosen.VersionNumber, dependencyFile.Hashes?.Sha1 ?? "", dependencyFile.Size), RequiresExactVersion: dependencyVersionId is not null));

                foreach (var nested in chosen!.RequiredDependencies)
                {
                    if (nested.ProjectId is { Length: > 0 } nestedId) pending.Enqueue((nestedId, nested.VersionId, depth + 1));
                    else if (strict) throw new InvalidOperationException("必需前置缺少可确认的项目 ID。");
                }
            }
            catch (HttpRequestException) when (!strict)
            {
                // 普通安装保留原有容错；事务更新不允许漏掉必需前置。
            }
        }

        return new PreparedModInstall(version.Name, version.VersionNumber, file.Filename, file.Size, targetDirectory, entries);
    }

    public async Task<ModInstallReport> InstallAsync(string gameRoot, string instanceId, DownloadCategory category,
        string projectIdOrSlug, IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = await PrepareAsync(gameRoot, instanceId, category, projectIdOrSlug, progress, ct).ConfigureAwait(false);
        var skipped = await ModInstallExecutor.InstallAsync(plan, category, downloader, progress, ct).ConfigureAwait(false);
        return new(plan.Title, plan.Version, plan.RootFile, plan.RootSize, plan.TargetDirectory, plan.Files.Count - 1, skipped);
    }
}