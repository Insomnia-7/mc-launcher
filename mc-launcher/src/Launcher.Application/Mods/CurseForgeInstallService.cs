using Launcher.Application.Abstractions;
using Launcher.Domain.Content;
using Launcher.Domain.Install;

namespace Launcher.Application.Mods;

/// <summary>
/// 从 CurseForge 安装单个项目（含必需依赖）到指定版本的 mods 目录。
/// 与 Modrinth 的 <see cref="ModInstallService"/> 结构对称，只是数据源不同。
/// </summary>
public sealed class CurseForgeInstallService(IDownloader downloader, ICurseForgeClient curseForge)
{
    private const int MaxDependencyDepth = 3;

    internal async Task<PreparedModInstall> PrepareAsync(
        string gameRoot,
        string instanceId,
        DownloadCategory category,
        int modId,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default, bool strict = false, string? versionId = null)
    {
        if (!curseForge.IsConfigured)
        {
            throw new CurseForgeNotConfiguredException(
                "未配置 CurseForge API Key。到 https://console.curseforge.com/ 免费申请后填入设置，" +
                "或设置环境变量 CURSEFORGE_API_KEY。");
        }

        var env = InstanceEnvironment.Resolve(gameRoot, instanceId);

        // 目标目录按资源类型走：mods / resourcepacks / datapacks / shaderpacks
        var subdirectory = category.InstanceSubdirectory()
            ?? throw new InvalidOperationException($"{category.Title()} 不能装进版本目录");

        var modsDirectory = Path.Combine(gameRoot, "instances", instanceId, ".minecraft", subdirectory);
        Directory.CreateDirectory(modsDirectory);

        progress?.Report(new ModInstallProgress("解析", 0, $"{env.Describe()} · 查询项目 {modId}"));

        var rootName = $"CurseForge #{modId}";
        var files = await curseForge.GetFilesAsync(modId, env.GameVersion, category.UsesLoader() ? env.LoaderSlug : null, ct)
            .ConfigureAwait(false);

        var main = (versionId is null ? files.FirstOrDefault(f => f.IsRelease) ?? files.FirstOrDefault() : files.FirstOrDefault(f => f.Id.ToString() == versionId))
            ?? throw new InvalidOperationException(
                $"CurseForge 上没有 {modId} 在 {env.Describe()} 下可用的文件。");

        var project = await curseForge.GetModAsync(modId, ct).ConfigureAwait(false);
        if (project is { Name.Length: > 0 })
        {
            rootName = project.Name;
        }

        var planned = new List<(CfFile File, int OwnerModId, string Name, string? Slug)>
        {
            (main, modId, rootName, project?.Slug)
        };
        var visited = new HashSet<int> { modId };
        var queue = new Queue<(int ModId, CfFile File, int Depth)>();
        queue.Enqueue((modId, main, 0));

        while (queue.Count > 0)
        {
            var (_, currentFile, depth) = queue.Dequeue();
            if (depth >= MaxDependencyDepth)
            {
                if (strict && currentFile.RequiredDependencies.Any(d => !visited.Contains(d.ModId)))
                    throw new InvalidOperationException("必需前置超过支持的递归深度，已保留旧模组。");
                continue;
            }

            foreach (var dependency in currentFile.RequiredDependencies)
            {
                if (!visited.Add(dependency.ModId))
                {
                    continue;
                }

                var dependencyFiles = await curseForge
                    .GetFilesAsync(dependency.ModId, env.GameVersion, category.UsesLoader() ? env.LoaderSlug : null, ct)
                    .ConfigureAwait(false);

                var chosen = dependencyFiles.FirstOrDefault(f => f.IsRelease) ?? dependencyFiles.FirstOrDefault();
                if (chosen is null)
                {
                    if (strict) throw new InvalidOperationException($"必需前置没有适配版本：{dependency.ModId}");
                    continue;
                }

                var dependencyProject = await curseForge.GetModAsync(dependency.ModId, ct)
                    .ConfigureAwait(false);

                var dependencyName = dependencyProject?.Name is { Length: > 0 } name
                    ? name
                    : $"CurseForge #{dependency.ModId}";

                planned.Add((chosen, dependency.ModId, dependencyName, dependencyProject?.Slug));
                queue.Enqueue((dependency.ModId, chosen, depth + 1));
            }
        }

        progress?.Report(new ModInstallProgress("解析", 5,
            $"{rootName} {main.DisplayName}，附带 {planned.Count - 1} 个依赖"));

        var entries = new List<PlannedModFile>(planned.Count);
        for (var i = 0; i < planned.Count; i++)
        {
            var (file, ownerModId, name, slug) = planned[i];
            var url = file.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = await curseForge.GetDownloadUrlAsync(ownerModId, file.Id, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException(
                    $"CurseForge 未提供 {name}（fileId={file.Id}）的下载地址，作者可能禁止第三方分发。");
            }

            entries.Add(new(new DownloadEntry(
                url,
                file.FileName,
                file.FileLength,
                file.Sha1,
                DownloadKind.Mod), new("curseforge", ownerModId.ToString(), file.Id.ToString(), file.DisplayName, file.Sha1 ?? "", file.FileLength), slug));

            progress?.Report(new ModInstallProgress("解析", 5 + i * 95d / planned.Count, name));
        }

        return new(rootName, main.DisplayName, main.FileName, main.FileLength, modsDirectory, entries);
    }

    public async Task<CfInstallReport> InstallAsync(string gameRoot, string instanceId, DownloadCategory category,
        int modId, IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = await PrepareAsync(gameRoot, instanceId, category, modId, progress, ct).ConfigureAwait(false);
        var skipped = await ModInstallExecutor.InstallAsync(plan, category, downloader, progress, ct).ConfigureAwait(false);
        return new(plan.Title, plan.RootFile, plan.RootSize, plan.TargetDirectory, Math.Max(0, plan.Files.Count - skipped - 1), skipped);
    }
}