using Launcher.Domain.Loaders;
using Launcher.Domain.Versions;

namespace Launcher.Application.Abstractions;

/// <summary>Fabric/Quilt 的 meta 服务。返回的 profile 只含差异，必须与父版本合并。</summary>
public interface ILoaderMetaClient
{
    /// <summary>可用加载器版本（新的在前）。</summary>
    Task<IReadOnlyList<string>> GetLoaderVersionsAsync(LoaderKind kind, string gameVersion, CancellationToken ct = default);

    /// <summary>某个加载器版本针对某个游戏版本生成的 version JSON（带 inheritsFrom）。</summary>
    Task<VersionJson> GetProfileAsync(LoaderKind kind, string gameVersion, string loaderVersion, CancellationToken ct = default);
}
