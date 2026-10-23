using System.Text.Json;

namespace Launcher.Domain.Modpacks;

/// <summary>Modrinth 整合包里的一条待下载文件。Path 相对实例根（即 .minecraft/）。</summary>
public sealed record ModpackFile(
    string Path,
    long FileSize,
    string? Sha1,
    string? Sha512,
    IReadOnlyList<string> Downloads,
    bool ClientRequired)
{
    public string? PrimaryUrl => Downloads.Count > 0 ? Downloads[0] : null;

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// modrinth.index.json 的只读投影。
/// 规范：formatVersion=1，game=minecraft，files[].path 相对实例根，
/// dependencies 里是 minecraft + 加载器（fabric-loader / forge / neoforge / quilt-loader）。
/// </summary>
public sealed class ModrinthPack
{
    public int FormatVersion { get; init; } = 1;

    public string Game { get; init; } = "minecraft";

    public string VersionId { get; init; } = "";

    public string Name { get; init; } = "";

    public string? Summary { get; init; }

    /// <summary>来源标签（Modrinth / CurseForge），写进实例元数据用。</summary>
    public string SourceLabel { get; init; } = "Modrinth";

    public IReadOnlyList<ModpackFile> Files { get; init; } = [];

    public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? MinecraftVersion => GetDependency("minecraft");

    public string? ForgeVersion => GetDependency("forge");

    public string? NeoForgeVersion => GetDependency("neoforge");

    public string? FabricVersion => GetDependency("fabric-loader");

    public string? QuiltVersion => GetDependency("quilt-loader");

    public long TotalSize => Files.Sum(f => f.FileSize);

    public string? GetDependency(string key) =>
        Dependencies.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>解析 modrinth.index.json。缺字段按规范给默认值，但不放过 JSON 语法错误。</summary>
    public static ModrinthPack Parse(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("modrinth.index.json 根节点不是对象");

        var files = new List<ModpackFile>();
        if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesElement.EnumerateArray())
            {
                var path = GetString(item, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;

                var downloads = new List<string>();
                if (item.TryGetProperty("downloads", out var downloadsElement) && downloadsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var url in downloadsElement.EnumerateArray())
                    {
                        var text = url.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) downloads.Add(text);
                    }
                }

                string? sha1 = null;
                string? sha512 = null;
                if (item.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                {
                    sha1 = GetString(hashes, "sha1");
                    sha512 = GetString(hashes, "sha512");
                }

                // env.client 可能是 "required" / "optional" / "unsupported" / "" / 缺省。
                // 只有明确 unsupported 才跳过，其余都算要装（整合包导出经常留空）。
                var clientEnv = item.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                    ? GetString(env, "client")
                    : null;
                var clientRequired = !string.Equals(clientEnv, "unsupported", StringComparison.OrdinalIgnoreCase);

                files.Add(new ModpackFile(
                    path.Replace('\\', '/'),
                    GetInt64(item, "fileSize"),
                    sha1,
                    sha512,
                    downloads,
                    clientRequired));
            }
        }

        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in deps.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } value)
                    dependencies[property.Name] = value;
            }
        }

        return new ModrinthPack
        {
            FormatVersion = (int)GetInt64(root, "formatVersion", 1),
            Game = GetString(root, "game") ?? "minecraft",
            VersionId = GetString(root, "versionId") ?? "",
            Name = GetString(root, "name") ?? "",
            Summary = GetString(root, "summary"),
            Files = files,
            Dependencies = dependencies,
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long GetInt64(JsonElement element, string property, long fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : fallback;
}
