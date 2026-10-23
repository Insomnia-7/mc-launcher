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
        var facets = BuildFacets(category, gameVersion, loader);
        var url = $"search?query={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}" +
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
            filters.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        if (!string.IsNullOrWhiteSpace(loader))
            filters.Add($"loaders={Uri.EscapeDataString($"[\"{loader}\"]")}");

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
    private static string? BuildFacets(DownloadCategory category, string? gameVersion, string? loader)
    {
        var groups = new List<string>(3);

        if (category.ModrinthProjectType() is { Length: > 0 } type)
            groups.Add($"[\"project_type:{type}\"]");

        if (!string.IsNullOrWhiteSpace(gameVersion))
            groups.Add($"[\"versions:{gameVersion}\"]");

        // 只有模组/整合包按加载器过滤；资源包、光影、数据包跟加载器无关
        if (!string.IsNullOrWhiteSpace(loader) && category.UsesLoader())
            groups.Add($"[\"categories:{loader}\"]");

        return groups.Count == 0 ? null : "[" + string.Join(",", groups) + "]";
    }
}
