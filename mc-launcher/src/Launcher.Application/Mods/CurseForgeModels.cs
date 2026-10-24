using System.Text.Json.Serialization;

namespace Launcher.Application.Mods;

// CurseForge 官方 API（https://api.curseforge.com/v1）的响应模型。
// 与 Modrinth 不同，这里的字段名大多是 camelCase，直接用默认命名策略即可。

public sealed class CfSearchResponse
{
    [JsonPropertyName("data")]
    public List<CfMod> Data { get; init; } = [];

    [JsonPropertyName("pagination")]
    public CfPagination? Pagination { get; init; }

    public long TotalCount => Pagination?.TotalCount ?? Data.Count;
}

public sealed class CfPagination
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }

    [JsonPropertyName("totalCount")]
    public long TotalCount { get; init; }
}

/// <summary>/mods/{id}/files 的响应，data 是文件数组。</summary>
public sealed class CfFileResponse
{
    [JsonPropertyName("data")]
    public List<CfFile> Data { get; init; } = [];

    [JsonPropertyName("pagination")]
    public CfPagination? Pagination { get; init; }

    public long TotalCount => Pagination?.TotalCount ?? Data.Count;
}

/// <summary>单个 mod 的响应包装。</summary>
public sealed class CfSingleFileResponse
{
    [JsonPropertyName("data")] public CfFile? Data {get;init;}
}

public sealed class CfModResponse
{
    [JsonPropertyName("data")]
    public CfMod? Data { get; init; }
}

/// <summary>字符串型 data 的响应包装（download-url 用）。</summary>
public sealed class CfStringResponse
{
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

public sealed class CfMod
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>项目分类：6=mod、12=资源包、6552=光影、17=世界。整合包靠它决定文件落到哪个子目录。</summary>
    [JsonPropertyName("classId")]
    public int ClassId { get; init; }

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("downloadCount")]
    public double DownloadCount { get; init; }

    [JsonPropertyName("logo")]
    public CfLogo? Logo { get; init; }

    [JsonPropertyName("latestFiles")]
    public List<CfFile> LatestFiles { get; init; } = [];

    [JsonPropertyName("categories")]
    public List<CfCategory> Categories { get; init; } = [];

    [JsonPropertyName("authors")]
    public List<CfAuthor> Authors { get; init; } = [];

    [JsonPropertyName("dateModified")] public DateTimeOffset? DateModified { get; init; }
    public string Author => Authors.Count > 0 ? Authors[0].Name : string.Empty;

    public string DownloadsText => FormatCount(DownloadCount);

    public string CategoryText => string.Join(' ', Categories.Take(3).Select(c => c.Name));

    private static string FormatCount(double value) => value switch
    {
        >= 100_000_000 => (value / 100_000_000).ToString("0.#") + " 亿",
        >= 10_000 => (value / 10_000).ToString("0.#") + " 万",
        _ => ((long)value).ToString()
    };
}

public sealed class CfLogo
{
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class CfCategory
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class CfAuthor
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class CfFile
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("modId")]
    public int ModId { get; init; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>1 = release, 2 = beta, 3 = alpha。</summary>
    [JsonPropertyName("releaseType")]
    public int ReleaseType { get; init; }

    [JsonPropertyName("fileLength")]
    public long FileLength { get; init; }

    [JsonPropertyName("downloadCount")]
    public double DownloadCount { get; init; }

    /// <summary>作者关闭第三方分发时为 null，必须再请求 /download-url。</summary>
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    /// <summary>形如 ["1.20.1", "1.20.2", "Forge", "Client"]。</summary>
    [JsonPropertyName("gameVersions")]
    public List<string> GameVersions { get; init; } = [];

    /// <summary>哈希数组：[{value, algo}]，algo 1 = sha1，2 = md5。</summary>
    [JsonPropertyName("hashes")]
    public List<CfHash> Hashes { get; init; } = [];

    /// <summary>文件级依赖：relationType 3 表示必需。</summary>
    [JsonPropertyName("dependencies")]
    public List<CfDependency> Dependencies { get; init; } = [];

    [JsonPropertyName("fileDate")]
    public DateTimeOffset FileDate { get; init; }

    public bool IsRelease => ReleaseType == 1;

    public string? Sha1 => Hashes.FirstOrDefault(h => h.Algo == 1)?.Value;

    public IReadOnlyList<CfDependency> RequiredDependencies =>
        Dependencies.Where(d => d.RelationType == CfRelationType.RequiredDependency).ToList();
}

public sealed class CfHash
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    /// <summary>1 = sha1，2 = md5。</summary>
    [JsonPropertyName("algo")]
    public int Algo { get; init; }
}

public sealed class CfDependency
{
    [JsonPropertyName("modId")]
    public int ModId { get; init; }

    [JsonPropertyName("relationType")]
    public int RelationType { get; init; }
}

/// <summary>CurseForge 的依赖关系类型，只用到必需依赖。</summary>
public static class CfRelationType
{
    public const int EmbeddedLibrary = 1;
    public const int OptionalDependency = 2;
    public const int RequiredDependency = 3;
    public const int Tool = 4;
    public const int Incompatible = 5;
    public const int Include = 6;
}

/// <summary>CurseForge 的 modLoaderType 取值。</summary>
public static class CfModLoaderType
{
    public const int Any = 0;
    public const int Forge = 1;
    public const int Cauldron = 2;
    public const int LiteLoader = 3;
    public const int Fabric = 4;
    public const int Quilt = 5;
    public const int NeoForge = 6;

    public static int FromSlug(string? loaderSlug) => loaderSlug?.ToLowerInvariant() switch
    {
        "forge" => Forge,
        "fabric" => Fabric,
        "quilt" => Quilt,
        "neoforge" => NeoForge,
        _ => Any
    };
}

public sealed record CfInstallReport(
    string ProjectName,
    string FileName,
    long FileBytes,
    string TargetDirectory,
    int DependencyFiles,
    int SkippedFiles = 0)
{
    public double Megabytes => FileBytes / 1024d / 1024d;
}
