using Launcher.Application.Abstractions;
using Launcher.Domain.Versions;

namespace Launcher.Application.Versions;

/// <summary>解析 inheritsFrom 链，产出可直接用于安装/启动的完整版本 JSON。</summary>
public sealed class VersionResolver(IVersionMetaClient meta)
{
    public Task<VersionJson> ResolveAsync(string versionId, CancellationToken ct = default)
        => ResolveCoreAsync(versionId, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);

    private async Task<VersionJson> ResolveCoreAsync(string versionId, HashSet<string> chain, CancellationToken ct)
    {
        if (!chain.Add(versionId))
            throw new InvalidOperationException($"版本继承出现环：{string.Join(" -> ", chain)} -> {versionId}");

        var json = await meta.GetVersionAsync(versionId, ct).ConfigureAwait(false);

        if (json.InheritsFrom is not { } parentId)
            return json;

        var parent = await ResolveCoreAsync(parentId, chain, ct).ConfigureAwait(false);
        return VersionMerger.Merge(json, parent);
    }
}
