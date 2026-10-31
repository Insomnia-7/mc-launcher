using System.Text.Json.Serialization;

namespace Launcher.Application.Mods;

/// <summary>Modrinth 搜索结果里的一条项目。字段名对应 API 的 snake_case。</summary>
public sealed record ModrinthProject(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("icon_url")] string? IconUrl,
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("follows")] long Follows,
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("versions")] IReadOnlyList<string> Versions)
{
    [JsonPropertyName("project_type")] public string? ProjectType { get; init; }
    [JsonPropertyName("all_project_types")] public IReadOnlyList<string>? AllProjectTypes { get; init; }
    [JsonPropertyName("date_modified")] public DateTimeOffset? DateModified { get; init; }
    /// <summary>下载量的紧凑写法：85435625 → 85.4M。</summary>
    public string DownloadsText => Downloads switch
    {
        >= 100_000_000 => $"{Downloads / 100_000_000.0:F1} 亿",
        >= 10_000 => $"{Downloads / 10_000.0:F1} 万",
        _ => Downloads.ToString(),
    };
}

public sealed record ModrinthProjectInfo(
    [property:JsonPropertyName("id")] string Id,
    [property:JsonPropertyName("slug")] string Slug,
    [property:JsonPropertyName("title")] string Title,
    [property:JsonPropertyName("description")] string Description)
{
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("gallery")] public IReadOnlyList<ModrinthGalleryImage>? Gallery { get; init; }
}
public sealed record ModrinthGalleryImage(
    [property:JsonPropertyName("url")] string Url,
    [property:JsonPropertyName("featured")] bool Featured,
    [property:JsonPropertyName("title")] string? Title);

public sealed record ModrinthSearchResponse(
    [property: JsonPropertyName("hits")] IReadOnlyList<ModrinthProject> Hits,
    [property: JsonPropertyName("total_hits")] int TotalHits,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit);

public sealed record ModrinthFileHashes(
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("sha512")] string? Sha512);

public sealed record ModrinthFile(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("hashes")] ModrinthFileHashes? Hashes);

public sealed record ModrinthDependency(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("dependency_type")] string? DependencyType)
{
    public bool IsRequired => string.Equals(DependencyType, "required", StringComparison.Ordinal);
}

/// <summary>某个项目在某个游戏版本 / 加载器下的一个发布版本。</summary>
public sealed record ModrinthVersion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version_number")] string VersionNumber,
    [property: JsonPropertyName("game_versions")] IReadOnlyList<string> GameVersions,
    [property: JsonPropertyName("loaders")] IReadOnlyList<string> Loaders,
    [property: JsonPropertyName("date_published")] DateTimeOffset? DatePublished,
    [property: JsonPropertyName("files")] IReadOnlyList<ModrinthFile> Files,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<ModrinthDependency>? Dependencies)
{
    /// <summary>首选文件：primary=true 的那份，没有就取第一个。</summary>
    [JsonPropertyName("downloads")] public long Downloads { get; init; }
    [JsonPropertyName("version_type")] public string? VersionType { get; init; }
    [JsonPropertyName("changelog")] public string? Changelog { get; init; }
    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();

    public IReadOnlyList<ModrinthDependency> RequiredDependencies =>
        (Dependencies ?? []).Where(d => d.IsRequired).ToList();
}

/// <summary>一次 Mod 安装的结果。</summary>
public sealed record ModInstallReport(
    string ProjectTitle,
    string VersionNumber,
    string FileName,
    long FileBytes,
    string TargetDirectory,
    int DependencyFiles,
    int SkippedFiles = 0)
{
    public double Megabytes => FileBytes / 1024.0 / 1024.0;
}
