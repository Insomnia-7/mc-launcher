using Launcher.Domain.Java;
using Launcher.Domain.Versions;

namespace Launcher.Application.Abstractions;

public interface IVersionMetaClient
{
    Task<VersionManifest> GetManifestAsync(CancellationToken ct = default);
    Task<VersionJson> GetVersionAsync(string versionId, CancellationToken ct = default);
    Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference, CancellationToken ct = default);
}

public interface IJavaLocator
{
    /// <summary>扫描已知安装位置，返回所有可用 Java（按主版本降序）。</summary>
    Task<IReadOnlyList<JavaInstall>> DiscoverAsync(CancellationToken ct = default);

    /// <summary>探测一个具体的 java 可执行文件。</summary>
    Task<JavaInstall?> ProbeAsync(string javaExePath, CancellationToken ct = default);
}
