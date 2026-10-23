using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Application.Abstractions;
using Launcher.Domain.Versions;

namespace Launcher.Application.Versions;

/// <summary>
/// 按 <c>inheritsFrom</c> 链把版本 JSON 合并成完整版本。磁盘优先（加载器装出来的增量 json
/// 只在磁盘上），没有再回源。
/// </summary>
/// <remarks>
/// Forge / NeoForge 的版本 json 只有增量：没有 mainClass、没有游戏 classpath、没有原版库。
/// 直接拿它去启动会得到一条 classpath 为空的命令，java 只会报「找不到主类」。
/// 所有启动路径都必须走这里。
/// </remarks>
public sealed class VersionLoader(IVersionMetaClient meta)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<VersionJson> LoadAsync(string gameRoot, string versionId, CancellationToken ct = default) =>
        LoadMergedAsync(gameRoot, versionId, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);

    private async Task<VersionJson> LoadMergedAsync(
        string gameRoot,
        string versionId,
        HashSet<string> seen,
        CancellationToken ct)
    {
        if (!seen.Add(versionId))
            throw new InvalidOperationException($"版本继承出现环：{versionId}");

        var json = await LoadSingleAsync(gameRoot, versionId, ct).ConfigureAwait(false);
        if (json.InheritsFrom is not { } parentId) return json;

        var parent = await LoadMergedAsync(gameRoot, parentId, seen, ct).ConfigureAwait(false);
        return VersionMerger.Merge(json, parent);
    }

    private async Task<VersionJson> LoadSingleAsync(string gameRoot, string versionId, CancellationToken ct)
    {
        var path = Path.Combine(gameRoot, "versions", versionId, versionId + ".json");
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var fromDisk = await JsonSerializer
                .DeserializeAsync<VersionJson>(stream, SerializerOptions, ct)
                .ConfigureAwait(false);
            if (fromDisk is not null) return fromDisk;
        }

        return await meta.GetVersionAsync(versionId).ConfigureAwait(false);
    }
}
