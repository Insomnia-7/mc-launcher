using System.Text.Json;

namespace Launcher.Domain.Modpacks;

/// <summary>manifest.json 里的一条文件引用：CurseForge 只给项目 / 文件 ID，下载地址和落位路径都要现查。</summary>
public sealed record CurseForgeFileRef(int ProjectId, int FileId, bool Required);

/// <summary>
/// CurseForge 整合包的 manifest.json 投影。典型结构：
/// <code>{ "minecraft": { "version": "1.20.1", "modLoaders": [{ "id": "forge-47.2.0", "primary": true }] },
///   "name": "...", "version": "1.0.0", "author": "...",
///   "files": [{ "projectID": 238222, "fileID": 4567890, "required": true }],
///   "overrides": "overrides" }</code>
/// 注意 modLoaders[].id 形如 forge-47.2.0 / fabric-0.15.11 / neoforge-21.1.0 / quilt-0.20.0。
/// </summary>
public sealed class CurseForgeManifest
{
    public string Name { get; init; } = string.Empty;

    public string VersionId { get; init; } = string.Empty;

    public string? Author { get; init; }

    public string? MinecraftVersion { get; init; }

    /// <summary>overrides 目录名，默认 overrides，少数包会写成别的。</summary>
    public string OverridesDirectory { get; init; } = "overrides";

    public IReadOnlyList<CurseForgeFileRef> Files { get; init; } = [];

    /// <summary>原始加载器 ID（未拆分），纯原版包为 null。</summary>
    public string? LoaderId { get; init; }

    /// <summary>
    /// 投影成启动器内部统一的 <see cref="ModrinthPack"/>。
    /// Files 留空——CurseForge 不给 URL，要等 <c>ResolveFilesAsync</c> 查完才填。
    /// </summary>
    public ModrinthPack ToPack()
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (MinecraftVersion is { Length: > 0 } minecraft)
            dependencies["minecraft"] = minecraft;

        var (key, version) = SplitLoaderId(LoaderId);
        if (key is not null) dependencies[key] = version!;

        return new ModrinthPack
        {
            Name = Name,
            VersionId = VersionId,
            Summary = Author is { Length: > 0 } author ? $"整合包作者：{author}" : null,
            SourceLabel = "CurseForge",
            Files = [],
            Dependencies = dependencies,
        };
    }

    /// <summary>
    /// 把 forge-47.2.0 拆成 (forge, 47.2.0)。fabric / quilt 在 Modrinth 规范里叫
    /// fabric-loader / quilt-loader，这里顺手映射掉，下游不用再认 CurseForge 的写法。
    /// </summary>
    public static (string? Key, string? Version) SplitLoaderId(string? loaderId)
    {
        if (string.IsNullOrWhiteSpace(loaderId)) return (null, null);

        var index = loaderId.IndexOf('-');
        if (index <= 0 || index == loaderId.Length - 1) return (null, null);

        var prefix = loaderId[..index].ToLowerInvariant();
        var version = loaderId[(index + 1)..];
        return prefix switch
        {
            "fabric" => ("fabric-loader", version),
            "quilt" => ("quilt-loader", version),
            "forge" => ("forge", version),
            "neoforge" => ("neoforge", version),
            _ => (null, null),
        };
    }

    /// <summary>解析 manifest.json。缺字段按默认值补，但 JSON 语法错误照抛。</summary>
    public static CurseForgeManifest Parse(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("manifest.json 根节点不是对象");

        string? minecraftVersion = null;
        string? loaderId = null;

        if (root.TryGetProperty("minecraft", out var minecraft) && minecraft.ValueKind == JsonValueKind.Object)
        {
            minecraftVersion = GetString(minecraft, "version");

            if (minecraft.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
            {
                // 有 primary 就取 primary，否则取第一个——绝大多数包只写一个加载器
                foreach (var loader in loaders.EnumerateArray())
                {
                    if (loader.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(loader, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var primary = loader.TryGetProperty("primary", out var primaryElement) &&
                                  primaryElement.ValueKind == JsonValueKind.True;

                    if (primary)
                    {
                        loaderId = id;
                        break;
                    }

                    loaderId ??= id;
                }
            }
        }

        var files = new List<CurseForgeFileRef>();
        if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var projectId = GetInt32(item, "projectID");
                var fileId = GetInt32(item, "fileID");
                if (projectId <= 0 || fileId <= 0) continue;

                // required 缺省视为必需；显式 false 才是可选
                var required = !(item.TryGetProperty("required", out var requiredElement) &&
                                 requiredElement.ValueKind == JsonValueKind.False);

                files.Add(new CurseForgeFileRef(projectId, fileId, required));
            }
        }

        return new CurseForgeManifest
        {
            Name = GetString(root, "name") is { Length: > 0 } name ? name : "未命名整合包",
            VersionId = GetString(root, "version") is { Length: > 0 } version ? version : "0",
            Author = GetString(root, "author"),
            MinecraftVersion = minecraftVersion,
            OverridesDirectory = GetString(root, "overrides") is { Length: > 0 } overrides ? overrides : "overrides",
            Files = files,
            LoaderId = loaderId,
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
}
