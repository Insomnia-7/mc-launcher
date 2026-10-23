using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Domain.Install;

namespace Launcher.Application.Mods;

/// <summary>
/// 从 Modrinth 直接装整合包：先下 .mrpack 到缓存目录，再交给 <see cref="ModpackInstaller"/>
/// 走"原版 → 加载器 → 清单文件 → overrides"那一套。
/// </summary>
public sealed class ModpackDownloadService(
    IDownloader downloader,
    IModrinthClient modrinth,
    ICurseForgeClient curseForge,
    IModpackSourceFactory sourceFactory,
    ModpackInstaller modpackInstaller)
{
    public async Task<ModpackImportReport> InstallAsync(
        string gameRoot,
        string projectIdOrSlug,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 整合包自带 MC 版本与加载器，不能按当前实例的版本过滤，否则大多数包会被筛掉
        progress?.Report(new ModpackImportProgress("解析整合包", 0, projectIdOrSlug));

        var versions = await modrinth.GetVersionsAsync(projectIdOrSlug, null, null, ct).ConfigureAwait(false);
        var version = versions.FirstOrDefault()
            ?? throw new InvalidOperationException($"Modrinth 上没有 {projectIdOrSlug} 的可用版本");

        var file = version.PrimaryFile
            ?? throw new InvalidOperationException($"整合包 {version.VersionNumber} 没有可下载的文件");

        if (!file.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
            && !file.Filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{file.Filename} 不是 Modrinth 整合包格式（.mrpack），无法自动导入");
        }

        var cacheDirectory = Path.Combine(gameRoot, ".cache", "modpacks");
        Directory.CreateDirectory(cacheDirectory);

        var localPath = Path.Combine(cacheDirectory, file.Filename);
        if (!File.Exists(localPath) || new FileInfo(localPath).Length != file.Size)
        {
            progress?.Report(new ModpackImportProgress("下载整合包", 0, $"{file.Filename} {file.Size / 1024.0 / 1024.0:F1} MB"));

            var adapter = progress is null
                ? null
                : new DownloadProgressAdapter(progress, "下载整合包");

            await downloader.DownloadAsync(
                [new DownloadEntry(file.Url, file.Filename, file.Size, file.Hashes?.Sha1, DownloadKind.Library)],
                cacheDirectory,
                adapter,
                ct).ConfigureAwait(false);
        }

        await using var source = sourceFactory.Open(localPath);
        return await modpackInstaller.ImportAsync(source, gameRoot, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 CurseForge 直接装整合包：按项目 ID 查最新 zip，下到缓存，再交给同一个 <see cref="ModpackInstaller"/>。
    /// 和 Modrinth 那条路只差前两步——后面「原版 → 加载器 → 清单文件 → overrides」完全共用。
    /// </summary>
    public async Task<ModpackImportReport> InstallCurseForgeAsync(
        string gameRoot,
        int modId,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!curseForge.IsConfigured)
        {
            throw new CurseForgeNotConfiguredException(
                "CurseForge 整合包要先在设置里填 API Key，否则解析不出下载地址");
        }

        progress?.Report(new ModpackImportProgress("解析整合包", 0, $"CurseForge#{modId}"));

        var files = await curseForge.GetFilesAsync(modId, ct: ct).ConfigureAwait(false);
        var file = files.FirstOrDefault(f => f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault()
                   ?? throw new InvalidOperationException($"CurseForge 项目 {modId} 没有可下载的文件");

        var url = file.DownloadUrl
                  ?? await curseForge.GetDownloadUrlAsync(modId, file.Id, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException(
                      $"「{file.FileName}」没有直链（作者关闭了第三方分发），换一个文件或用「导入整合包」选本地文件");

        var cacheDirectory = Path.Combine(gameRoot, ".cache", "modpacks");
        Directory.CreateDirectory(cacheDirectory);

        var localPath = Path.Combine(cacheDirectory, Path.GetFileName(file.FileName));
        if (!File.Exists(localPath) || new FileInfo(localPath).Length != file.FileLength)
        {
            progress?.Report(new ModpackImportProgress("下载整合包", 0,
                $"{file.FileName} {file.FileLength / 1024.0 / 1024.0:F1} MB"));

            var adapter = progress is null ? null : new DownloadProgressAdapter(progress, "下载整合包");
            await downloader.DownloadAsync(
                [new DownloadEntry(url, file.FileName, file.FileLength, file.Sha1, DownloadKind.Library)],
                cacheDirectory,
                adapter,
                ct).ConfigureAwait(false);
        }

        await using var source = sourceFactory.Open(localPath);
        return await modpackInstaller.ImportAsync(source, gameRoot, progress, ct).ConfigureAwait(false);
    }

    private sealed class DownloadProgressAdapter(IProgress<ModpackImportProgress> outer, string stage)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            outer.Report(new ModpackImportProgress(
                stage,
                value.Percent,
                $"{value.CompletedBytes / 1024.0 / 1024.0:F1} / {value.TotalBytes / 1024.0 / 1024.0:F1} MB"));
    }
}
