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

    public async Task<CfInstallReport> InstallAsync(
        string gameRoot,
        string instanceId,
        DownloadCategory category,
        int modId,
        IProgress<ModInstallProgress>? progress = null,
        CancellationToken ct = default)
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
        var files = await curseForge.GetFilesAsync(modId, env.GameVersion, env.LoaderSlug, ct)
            .ConfigureAwait(false);

        var main = files.FirstOrDefault(f => f.IsRelease) ?? files.FirstOrDefault()
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
                continue;
            }

            foreach (var dependency in currentFile.RequiredDependencies)
            {
                if (!visited.Add(dependency.ModId))
                {
                    continue;
                }

                var dependencyFiles = await curseForge
                    .GetFilesAsync(dependency.ModId, env.GameVersion, env.LoaderSlug, ct)
                    .ConfigureAwait(false);

                var chosen = dependencyFiles.FirstOrDefault(f => f.IsRelease) ?? dependencyFiles.FirstOrDefault();
                if (chosen is null)
                {
                    // 拿不到匹配版本的依赖就跳过，避免整单失败
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

        var entries = new List<(DownloadEntry Entry, string? Slug)>(planned.Count);
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

            entries.Add((new DownloadEntry(
                url,
                file.FileName,
                file.FileLength,
                file.Sha1,
                DownloadKind.Mod), slug));

            progress?.Report(new ModInstallProgress("解析", 5 + i * 95d / planned.Count, name));
        }

        // 同一个 mod 已经装过（哪怕被整合包改了前缀名、或是别的版本）就不再下一份
        var skipped = 0;
        var pendingDownloads = new List<DownloadEntry>(entries.Count);
        foreach (var (entry, slug) in entries)
        {
            if (ModFileGuard.FindExisting(modsDirectory, entry.RelativePath) is not null
                || (category.NeedsModIdGuard()
                    && ModFileGuard.FindExistingByModId(modsDirectory, slug) is not null))
            {
                skipped++;
                continue;
            }

            pendingDownloads.Add(entry);
        }

        if (pendingDownloads.Count == 0)
        {
            progress?.Report(new ModInstallProgress("完成", 100, $"{main.FileName} 已在版本里，跳过"));
            return new CfInstallReport(rootName, main.FileName, main.FileLength, modsDirectory, 0, skipped);
        }

        var adapter = progress is null
            ? null
            : new DownloadProgressAdapter(progress, "下载");

        var summary = await downloader
            .DownloadAsync(pendingDownloads, modsDirectory, adapter, ct)
            .ConfigureAwait(false);

        progress?.Report(new ModInstallProgress("完成", 100,
            $"{rootName} {main.DisplayName} · {summary.DownloadedFiles} 个文件 {summary.DownloadedBytes / 1024d / 1024d:F1} MB"));

        return new CfInstallReport(
            rootName,
            main.FileName,
            main.FileLength,
            modsDirectory,
            pendingDownloads.Count - 1,
            skipped);
    }

    /// <summary>把 <see cref="DownloadProgress"/> 适配成 <see cref="ModInstallProgress"/>。</summary>
    private sealed class DownloadProgressAdapter(IProgress<ModInstallProgress> outer, string stage)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => outer.Report(new ModInstallProgress(
            stage,
            value.Percent,
            $"{value.CompletedBytes / 1024d / 1024d:F1} / {value.TotalBytes / 1024d / 1024d:F1} MB · {value.CurrentFile}"));
    }
}
