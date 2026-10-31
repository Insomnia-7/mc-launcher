using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Domain.Install;

namespace Launcher.Application.Mods;

public sealed record ModpackRelease(ModpackOrigin Origin, string Name, string Channel, string GameVersions, DateTimeOffset? Date)
{
    public string Label => $"{Name} · {Channel} · {GameVersions}";
    public override string ToString() => Label;
}

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
    public async Task<IReadOnlyList<ModpackRelease>> GetReleasesAsync(ModpackOrigin origin, CancellationToken ct = default)
    {
        if (origin.Source.Equals("Modrinth", StringComparison.OrdinalIgnoreCase))
            return (await modrinth.GetVersionsAsync(origin.ProjectId, ct: ct).ConfigureAwait(false))
                .Where(v => v.PrimaryFile?.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) == true)
                .Select(v => new ModpackRelease(new("Modrinth", v.ProjectId, v.Id), v.VersionNumber,
                    v.VersionType switch { "release" => "正式版", "beta" => "测试版", "alpha" => "开发版", _ => "未标注" }, string.Join(" / ", v.GameVersions), v.DatePublished)).ToArray();
        if (!origin.Source.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) || !int.TryParse(origin.ProjectId, out var project))
            throw new InvalidOperationException("此实例尚未关联支持的整合包平台，可选择本地更新包。");
        return (await curseForge.GetFilesAsync(project, ct: ct).ConfigureAwait(false)).Where(f => f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ModpackRelease(new("CurseForge", project.ToString(), f.Id.ToString()), f.DisplayName,
                f.ReleaseType switch { 1 => "正式版", 2 => "测试版", 3 => "开发版", _ => "未标注" }, string.Join(" / ", f.GameVersions.Where(v => v.Length > 0 && char.IsDigit(v[0]))), f.FileDate)).ToArray();
    }

    public async Task<string> DownloadReleaseAsync(string gameRoot, ModpackOrigin origin, CancellationToken ct = default)
    {
        DownloadEntry entry;
        if (origin.Source.Equals("Modrinth", StringComparison.OrdinalIgnoreCase))
        {
            var release = await modrinth.GetVersionAsync(origin.ReleaseId, ct).ConfigureAwait(false) ?? throw new InvalidDataException("整合包版本已不存在。");
            if (release.ProjectId != origin.ProjectId) throw new InvalidDataException("整合包项目与所选版本不符。");
            var file = release.PrimaryFile ?? throw new InvalidDataException("版本没有可用文件。");
            entry = new(file.Url, Path.GetFileName(file.Filename), file.Size, file.Hashes?.Sha1, DownloadKind.ModpackFile);
        }
        else
        {
            if (!origin.Source.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) || !int.TryParse(origin.ProjectId, out var project) || !int.TryParse(origin.ReleaseId, out var id))
                throw new InvalidDataException("无效的整合包来源。");
            var file = await curseForge.GetFileAsync(project, id, ct).ConfigureAwait(false) ?? throw new InvalidDataException("整合包版本已不存在。");
            if (file.ModId != project) throw new InvalidDataException("整合包项目与所选版本不符。");
            var url = file.DownloadUrl ?? await curseForge.GetDownloadUrlAsync(project, id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("作者关闭了第三方分发，请下载后选择本地更新包。");
            entry = new(url, Path.GetFileName(file.FileName), file.FileLength, file.Sha1, DownloadKind.ModpackFile);
        }
        if (!entry.RelativePath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) && !entry.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选文件不是受支持的整合包。");
        var directory = Path.Combine(gameRoot, ".cache", "modpacks", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(origin.Source + ":" + origin.ProjectId + ":" + origin.ReleaseId))));
        await downloader.DownloadAsync([entry], directory, ct: ct).ConfigureAwait(false);
        var path = Path.Combine(directory, entry.RelativePath);
        if (!File.Exists(path) || entry.Size > 0 && new FileInfo(path).Length != entry.Size || entry.Sha1 is {} sha && !sha.Equals(await ModSourceStore.HashAsync(path, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包下载校验失败。");
        return path;
    }

    public async Task<ModpackImportReport> InstallAsync(
        string gameRoot,
        string projectIdOrSlug,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default, string? gameVersion = null, string? loader = null, string? versionId = null)
    {
        // Only explicit browser filters apply; an unrelated current instance must not constrain a modpack.
        progress?.Report(new ModpackImportProgress("解析整合包", 0, projectIdOrSlug));

        var versions = await modrinth.GetVersionsAsync(projectIdOrSlug, gameVersion, loader, ct).ConfigureAwait(false);
        var version = (versionId is null ? versions.FirstOrDefault() : versions.FirstOrDefault(v=>v.Id==versionId))
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
        if (versionId is not null || !File.Exists(localPath) || new FileInfo(localPath).Length != file.Size)
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
        return await modpackInstaller.ImportAsync(source, gameRoot, progress, ct, new("Modrinth", version.ProjectId, version.Id)).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 CurseForge 直接装整合包：按项目 ID 查最新 zip，下到缓存，再交给同一个 <see cref="ModpackInstaller"/>。
    /// 和 Modrinth 那条路只差前两步——后面「原版 → 加载器 → 清单文件 → overrides」完全共用。
    /// </summary>
    public async Task<ModpackImportReport> InstallCurseForgeAsync(
        string gameRoot,
        int modId,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default, string? gameVersion = null, string? loader = null, string? versionId = null)
    {
        if (!curseForge.IsConfigured)
        {
            throw new CurseForgeNotConfiguredException(
                "CurseForge 整合包要先在设置里填 API Key，否则解析不出下载地址");
        }

        progress?.Report(new ModpackImportProgress("解析整合包", 0, $"CurseForge#{modId}"));

        var files = await curseForge.GetFilesAsync(modId, gameVersion, loader, ct).ConfigureAwait(false);
        var file = (versionId is null ? files.FirstOrDefault(f => f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault() : await curseForge.GetFileAsync(modId,int.Parse(versionId),ct).ConfigureAwait(false))
                   ?? throw new InvalidOperationException($"CurseForge 项目 {modId} 没有可下载的文件");

        if(versionId is not null && (file.ModId != modId || gameVersion is not null && !file.GameVersions.Contains(gameVersion)
            || loader is not null && !file.GameVersions.Contains(loader,StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("所选整合包文件与筛选条件不匹配。");
        var url = file.DownloadUrl
                  ?? await curseForge.GetDownloadUrlAsync(modId, file.Id, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException(
                      $"「{file.FileName}」没有直链（作者关闭了第三方分发），换一个文件或用「导入整合包」选本地文件");

        var cacheDirectory = Path.Combine(gameRoot, ".cache", "modpacks");
        Directory.CreateDirectory(cacheDirectory);

        var localPath = Path.Combine(cacheDirectory, Path.GetFileName(file.FileName));
        if (versionId is not null || !File.Exists(localPath) || new FileInfo(localPath).Length != file.FileLength)
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
        return await modpackInstaller.ImportAsync(source, gameRoot, progress, ct, new("CurseForge", modId.ToString(), file.Id.ToString())).ConfigureAwait(false);
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
