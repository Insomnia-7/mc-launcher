using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Loaders;
using Launcher.Domain.Versions;

namespace Launcher.Application.Install;

public sealed record LoaderInstallReport(
    string VersionId,
    LoaderKind Kind,
    string LoaderVersion,
    int LibraryCount,
    InstallReport Install);

/// <summary>
/// 装加载器：拉 profile（只含差异）→ 与父版本合并 → 落盘合并后的 version JSON → 下加载器自己的库。
/// Fabric/Quilt 只是 endpoint 不同，走同一套逻辑。
/// </summary>
public sealed class LoaderInstaller(
    ILoaderMetaClient loaders,
    IVersionMetaClient meta,
    GameInstaller installer)
{
    public async Task<LoaderInstallReport> InstallAsync(
        string gameVersion,
        LoaderKind kind,
        string gameRoot,
        string? loaderVersion = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (kind is LoaderKind.Vanilla) throw new ArgumentException("Vanilla 不需要装加载器", nameof(kind));
        if (kind is LoaderKind.Forge or LoaderKind.NeoForge)
            throw new NotSupportedException($"{kind} 走 installer jar 流程，M3 第二阶段再接");

        var available = await loaders.GetLoaderVersionsAsync(kind, gameVersion, ct).ConfigureAwait(false);
        if (available.Count == 0)
            throw new InvalidOperationException($"{kind} 没有适用于 {gameVersion} 的加载器版本");
        var version = loaderVersion ?? available[0];

        if (!available.Contains(version, StringComparer.Ordinal))
            throw new InvalidOperationException($"{kind} {version} 不适用于 {gameVersion}（可用：{string.Join(", ", available.Take(5))}…）");

        var profile = await loaders.GetProfileAsync(kind, gameVersion, version, ct).ConfigureAwait(false);
        var parent = await LoadParentAsync(gameVersion, gameRoot, ct).ConfigureAwait(false);
        var merged = VersionMerger.Merge(profile, parent);

        // assets 属于父版本，加载器不需要再下一遍
        var report = await installer.InstallVersionAsync(merged, gameRoot, includeAssets: false, progress, ct).ConfigureAwait(false);

        return new LoaderInstallReport(merged.Id, kind, version, merged.Libraries.Count, report);
    }

    /// <summary>父版本优先读磁盘（官方版本可能已经装过），没有再回源。</summary>
    private async Task<VersionJson> LoadParentAsync(string gameVersion, string gameRoot, CancellationToken ct)
    {
        var path = Path.Combine(gameRoot, "versions", gameVersion, gameVersion + ".json");
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var fromDisk = await JsonSerializer
                .DeserializeAsync<VersionJson>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                .ConfigureAwait(false);
            if (fromDisk is not null) return fromDisk;
        }

        return await meta.GetVersionAsync(gameVersion, ct).ConfigureAwait(false);
    }
}
