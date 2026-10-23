using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Launcher.Application.Mods;

/// <summary>一个 jar 声明的 mod 元数据：modId（可能有多个）、显示名、版本、来自哪套加载器。</summary>
public sealed record ModMetadata(
    IReadOnlyList<string> ModIds,
    string? DisplayName,
    string? Version,
    string? LoaderKind)
{
    /// <summary>第一个 modId（`[[mods]]` 的首块 / json 的 id），拿来做上游查询最常用。</summary>
    public string? PrimaryModId => ModIds.Count > 0 ? ModIds[0] : null;
}

/// <summary>
/// 判断 mods 目录里是否已经装过某个 mod。整合包作者常给 jar 加「[苹果皮] 」这类中文装饰前缀，
/// 也常见整合包自带旧版本、Modrinth 上是新版本 —— 只按文件名比对会漏判，同一 mod 装两份
/// 会让 Forge 抛 duplicate mod 直接起不来。
///
/// 所以判定分两级：先按「去前缀 + 去扩展名」的名字比，再读 jar 里的 mod 元数据（Forge 的
/// META-INF/mods.toml、Fabric 的 fabric.mod.json）按 modId 比。
/// 元数据读取结果按「路径 + 大小 + 修改时间」缓存，重复安装时不再解 jar。
/// </summary>
public static partial class ModFileGuard
{
    private static readonly ConcurrentDictionary<string, ModMetadata?> MetadataCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>元数据清单文件 → 对应加载器名（同时决定 <see cref="ModMetadata.LoaderKind"/>）。</summary>
    private static readonly (string Entry, string LoaderKind)[] MetadataEntries =
    [
        ("META-INF/mods.toml", "forge"),
        ("META-INF/neoforge.mods.toml", "neoforge"),
        ("fabric.mod.json", "fabric"),
        ("quilt.mod.json", "quilt")
    ];

    /// <summary>返回已存在的完整路径，没有则 null。</summary>
    public static string? FindExisting(string modsDirectory, string fileName)
    {
        if (!Directory.Exists(modsDirectory))
        {
            return null;
        }

        var target = Normalize(fileName);

        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            if (string.Equals(Normalize(Path.GetFileName(path)), target, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>按 jar 内的 modId 找同 mod 的既有文件（可能是别的版本、带中文前缀的名字）。</summary>
    public static string? FindExistingByModId(string modsDirectory, params string?[] candidateModIds)
    {
        if (!Directory.Exists(modsDirectory))
        {
            return null;
        }

        var wanted = candidateModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (wanted.Count == 0)
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.jar"))
        {
            foreach (var modId in ReadModIds(path))
            {
                if (wanted.Contains(modId))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>读一个 jar 声明了哪些 modId；读不出来就返回空数组。</summary>
    public static string[] ReadModIds(string jarPath)
    {
        var metadata = ReadMetadata(jarPath);
        return metadata is null ? [] : metadata.ModIds.ToArray();
    }

    /// <summary>
    /// 读一个 jar 的 mod 元数据。四个候选清单按 Forge → NeoForge → Fabric → Quilt 的顺序取第一个存在的，
    /// 与判重、扫描用的是同一套来源。坏包 / 不是 mod 的 jar 返回 null，绝不抛。
    /// </summary>
    public static ModMetadata? ReadMetadata(string jarPath)
    {
        var info = new FileInfo(jarPath);
        var key = $"{jarPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        if (MetadataCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ModMetadata? metadata = null;

        try
        {
            using var zip = ZipFile.OpenRead(jarPath);

            foreach (var (entryName, loaderKind) in MetadataEntries)
            {
                var entry = zip.GetEntry(entryName)
                            ?? zip.Entries.FirstOrDefault(e =>
                                e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    continue;
                }

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();

                metadata = entryName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                    ? ParseToml(text, loaderKind)
                    : ParseJson(text, loaderKind);

                break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // jar 坏了或被占用：当作读不出元数据，退回按文件名判断。
            metadata = null;
        }

        MetadataCache[key] = metadata;
        return metadata;
    }

    private static ModMetadata ParseToml(string text, string loaderKind)
    {
        var ids = TomlModId()
            .Matches(text)
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct()
            .ToArray();

        return new ModMetadata(
            ids,
            FirstTomlString(text, TomlDisplayName()),
            FirstTomlString(text, TomlVersion()),
            loaderKind);
    }

    private static ModMetadata ParseJson(string text, string loaderKind)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ModMetadata([], null, null, loaderKind);
            }

            // quilt.mod.json 把 id/version/name 放在 quilt_loader 下面，兼容写法仍可能放在顶层。
            var scope = document.RootElement;
            if (!HasString(scope, "id")
                && scope.TryGetProperty("quilt_loader", out var quiltLoader)
                && quiltLoader.ValueKind == JsonValueKind.Object)
            {
                scope = quiltLoader;
            }

            var id = GetString(scope, "id");
            var name = GetString(scope, "name");
            if (name is null
                && scope.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                name = GetString(metadata, "name");
            }

            return new ModMetadata(
                id is null ? [] : [id.ToLowerInvariant()],
                name,
                GetString(scope, "version"),
                loaderKind);
        }
        catch (JsonException)
        {
            // 手工改坏的 json：退回正则，至少把 id 摸出来。
            var ids = JsonModId()
                .Matches(text)
                .Select(m => m.Groups[1].Value.ToLowerInvariant())
                .Distinct()
                .ToArray();

            return new ModMetadata(ids, null, null, loaderKind);
        }
    }

    private static bool HasString(JsonElement element, string name) => GetString(element, name) is not null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FirstTomlString(string text, Regex regex)
    {
        var match = regex.Match(text);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    /// <summary>去掉扩展名与「[...] 」前缀，统一小写，用于比对。</summary>
    public static string Normalize(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();

        if (stem.StartsWith('[') && stem.IndexOf(']') is var close and > 0)
        {
            stem = stem[(close + 1)..].Trim();
        }

        return stem.ToLowerInvariant();
    }

    [GeneratedRegex("(?m)^\\s*modId\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex TomlModId();

    [GeneratedRegex("(?m)^\\s*\"id\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex JsonModId();

    [GeneratedRegex("(?m)^\\s*displayName\\s*=\\s*[\"']([^\"']*)[\"']")]
    private static partial Regex TomlDisplayName();

    // loaderVersion / versionRange 之类靠 ^\s*version 锚住，不会误命中。
    [GeneratedRegex("(?m)^\\s*version\\s*=\\s*[\"']([^\"']*)[\"']")]
    private static partial Regex TomlVersion();
}
