using System.Net.Http.Json;
using System.Text.Json;
using Launcher.Application.Mods;
using Launcher.Domain.Content;

using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Modrinth;

/// <summary>
/// Modrinth v2 API 的只读封装。只用公开端点，不需要 token。
/// 官方要求带可识别 UA，否则会被限流。
/// </summary>
public sealed class ModrinthClient(HttpClient http) : IModrinthClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = HttpClientFactory.Create(userAgent, TimeSpan.FromSeconds(30));
        client.BaseAddress = new Uri("https://api.modrinth.com/v2/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<ModrinthVersion?> GetVersionByHashAsync(string sha1, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"version_file/{Uri.EscapeDataString(sha1)}?algorithm=sha1", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModrinthVersion>(Json, ct).ConfigureAwait(false);
    }

    public async Task<ModrinthSearchResponse> SearchAsync(
        string query,
        DownloadCategory category,
        string? gameVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default)
    {
        return await SearchAsync(new ResourceSearchRequest(query, category, gameVersion, loader,
            Sort: ResourceSort.Relevance, Limit: limit, Offset: offset), ct).ConfigureAwait(false);
    }

    public async Task<ModrinthSearchResponse> SearchAsync(ResourceSearchRequest request, CancellationToken ct = default)
    {
        var (query, category, gameVersion, loader, tag, sort, limit, offset) = request;
        var facets = BuildFacets(category, gameVersion, loader, tag);
        var index = sort switch { ResourceSort.Downloads => "downloads", ResourceSort.Popularity => "follows",
            ResourceSort.Updated => "updated", ResourceSort.Newest => "newest", _ => "relevance" };
        limit = Math.Clamp(limit, 1, 100); offset = Math.Max(0, offset);
        var url = $"search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&index={index}" +
                  (facets is null ? "" : $"&facets={Uri.EscapeDataString(facets)}");

        var response = await http.GetFromJsonAsync<ModrinthSearchResponse>(url, Json, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Modrinth 搜索返回空响应");
        return response;
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var filters = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(gameVersion))
            filters.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (!string.IsNullOrWhiteSpace(loader))
            filters.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");

        var url = $"project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        if (filters.Count > 0) url += "?" + string.Join("&", filters);

        var versions = await http.GetFromJsonAsync<List<ModrinthVersion>>(url, Json, ct).ConfigureAwait(false);
        return versions is null
            ? []
            : versions.OrderByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>
    /// facets 是 JSON 数组套数组：[[ "project_type:mod" ], [ "versions:1.21.4" ], [ "categories:fabric" ]]
    /// 多个内层数组是 AND，同一个内层数组里多个值是 OR。
    /// </summary>
    private static string BuildFacets(DownloadCategory category, string? gameVersion, string? loader, string? tag)
    {
        var groups = new List<string[]>();
        if (category.ModrinthProjectType() is { } type)
            groups.Add([category == DownloadCategory.Datapack ? "all_project_types:datapack" : "project_type:" + type]);
        if (!string.IsNullOrWhiteSpace(gameVersion)) groups.Add(["versions:" + gameVersion]);
        if (!string.IsNullOrWhiteSpace(loader) && category.UsesLoader()) groups.Add(["categories:" + loader]);
        // A datapack must provide a datapack file, even when the primary project type is mod.
        if (category == DownloadCategory.Datapack) groups.Add(["categories:datapack"]);
        if (!string.IsNullOrWhiteSpace(tag)) groups.Add(["categories:" + tag]);
        return JsonSerializer.Serialize(groups);
    }

    private List<ModrinthTag>? _tags;
    public async Task<IReadOnlyList<ResourceTag>> GetCategoriesAsync(DownloadCategory category, CancellationToken ct = default)
    {
        var tags = _tags ??= await http.GetFromJsonAsync<List<ModrinthTag>>("tag/category", Json, ct).ConfigureAwait(false) ?? [];
        var type = category == DownloadCategory.Datapack ? "mod" : category.ModrinthProjectType();
        return tags.Where(t => t.ProjectType == type && t.Header != "loaders")
            .Select(t => new ResourceTag(t.Name, t.Name)).DistinctBy(t => t.Id).OrderBy(t => t.Name).ToArray();
    }

    private sealed record ModrinthTag(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("project_type")] string ProjectType,
        [property: System.Text.Json.Serialization.JsonPropertyName("header")] string Header);
}
