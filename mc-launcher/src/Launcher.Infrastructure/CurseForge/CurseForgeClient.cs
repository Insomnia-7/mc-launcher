using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Application.Mods;
using Launcher.Domain.Content;

using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.CurseForge;

/// <summary>
/// CurseForge 官方 API 客户端。<c>/v1</c> 下的每个请求都必须带 <c>x-api-key</c>，
/// Key 从构造时传入的委托里实时读取，用户改配置后不用重建对象。
/// </summary>
public sealed partial class CurseForgeClient : ICurseForgeClient
{
    private const int MinecraftGameId = 432;
    private const int ModsClassId = 6;
    private const int SortPopularity = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public CurseForgeClient(HttpClient http, Func<string?> apiKeyProvider)
    {
        _http = http;
        _apiKey = apiKeyProvider;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey());

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = HttpClientFactory.Create(userAgent, TimeSpan.FromSeconds(30));
        client.BaseAddress = new Uri("https://api.curseforge.com/v1/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<CfSearchResponse> SearchAsync(
        string query,
        DownloadCategory category,
        string? gameVersion = null,
        string? loaderSlug = null,
        int pageSize = 20,
        int index = 0,
        CancellationToken ct = default)
    {
        return await SearchAsync(new ResourceSearchRequest(query, category, gameVersion, loaderSlug,
            Limit: pageSize, Offset: index), ct).ConfigureAwait(false);
    }

    public async Task<CfSearchResponse> SearchAsync(ResourceSearchRequest request, CancellationToken ct = default)
    {
        var (query, category, gameVersion, loaderSlug, tag, sort, pageSize, index) = request;
        var classId = category.CurseForgeClassId() ?? ModsClassId;
        var sortField = sort switch { ResourceSort.Downloads => 6, ResourceSort.Updated => 3,
            ResourceSort.Newest => 11, _ => SortPopularity };

        var query_string = new List<string>
        {
            $"gameId={MinecraftGameId}",
            $"classId={classId}",
            $"searchFilter={Uri.EscapeDataString(query)}",
            $"sortField={sortField}",
            "sortOrder=desc",
            $"index={Math.Clamp(index, 0, 9999)}",
            $"pageSize={Math.Clamp(pageSize, 1, 50)}"
        };

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query_string.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");
        }

        if (int.TryParse(tag, out var categoryId) && categoryId > 0)
            query_string.Add($"categoryId={categoryId}");

        // 资源包 / 光影 / 数据包没有加载器概念，带上会搜不到东西
        var loader = category.UsesLoader() ? CfModLoaderType.FromSlug(loaderSlug) : CfModLoaderType.Any;
        if (loader != CfModLoaderType.Any)
        {
            query_string.Add($"modLoaderType={loader}");
        }

        try
        {
            return await GetAsync<CfSearchResponse>("mods/search?" + string.Join('&', query_string), ct)
                .ConfigureAwait(false) ?? new CfSearchResponse();
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("403", StringComparison.Ordinal))
        {
            // 个人 Core Key 常见：读单个项目没问题，搜索端点一律 403。
            throw new CurseForgeSearchUnavailableException(
                "这个 API Key 不能调搜索端点（403）。可以直接输入 CurseForge 项目 ID 或链接安装。", ex);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<DownloadCategory, IReadOnlyList<ResourceTag>> _categoryCache = new();
    public async Task<IReadOnlyList<ResourceTag>> GetCategoriesAsync(DownloadCategory category, CancellationToken ct = default)
    {
        if (_categoryCache.TryGetValue(category, out var cached)) return cached;
        var response = await GetAsync<CfCategoryResponse>($"categories?gameId={MinecraftGameId}&classId={category.CurseForgeClassId()}", ct).ConfigureAwait(false);
        var tags = (response?.Data ?? []).Select(t => new ResourceTag(t.Id.ToString(), t.Name)).OrderBy(t => t.Name).ToArray();
        _categoryCache[category] = tags;
        return tags;
    }
    private sealed class CfCategoryResponse { public List<CfCategory> Data { get; init; } = []; }

    public async Task<CfMod?> GetModAsync(int modId, CancellationToken ct = default)
        => (await GetAsync<CfModResponse>($"mods/{modId}", ct).ConfigureAwait(false))?.Data;

    public async Task<IReadOnlyList<CfFile>> GetFilesAsync(
        int modId,
        string? gameVersion = null,
        string? loaderSlug = null,
        CancellationToken ct = default)
    {
        var query_string = new List<string> { "pageSize=50", "index=0" };

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query_string.Add($"gameVersion={Uri.EscapeDataString(gameVersion)}");
        }

        var loader = CfModLoaderType.FromSlug(loaderSlug);
        if (loader != CfModLoaderType.Any)
        {
            query_string.Add($"modLoaderType={loader}");
        }

        var response = await GetAsync<CfFileResponse>(
            $"mods/{modId}/files?" + string.Join('&', query_string), ct).ConfigureAwait(false);

        if (response is null)
        {
            return [];
        }

        return response.Data
            .OrderByDescending(f => f.IsRelease)
            .ThenByDescending(f => f.FileDate)
            .ToList();
    }

    public async Task<CfFile?> GetFileAsync(int modId,int fileId,CancellationToken ct=default) =>
        (await GetAsync<CfSingleFileResponse>($"mods/{modId}/files/{fileId}",ct).ConfigureAwait(false))?.Data;

    public async Task<CfFileResponse> GetFilesPageAsync(int modId,string? gameVersion=null,string? loaderSlug=null,int index=0,CancellationToken ct=default)
    {
        var query=new List<string>{"pageSize=50",$"index={Math.Max(0,index)}"};
        if(!string.IsNullOrWhiteSpace(gameVersion)) query.Add("gameVersion="+Uri.EscapeDataString(gameVersion));
        var loader=CfModLoaderType.FromSlug(loaderSlug);
        if(loader!=CfModLoaderType.Any) query.Add($"modLoaderType={loader}");
        return await GetAsync<CfFileResponse>($"mods/{modId}/files?"+string.Join('&',query),ct).ConfigureAwait(false) ?? new();
    }

    public async Task<string?> GetDownloadUrlAsync(int modId, int fileId, CancellationToken ct = default)
        => (await GetAsync<CfStringResponse>($"mods/{modId}/files/{fileId}/download-url", ct)
            .ConfigureAwait(false))?.Data;

    public CfUrlRef? ParseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("curseforge.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileMatch = FileIdPattern().Match(uri.AbsolutePath);
        var modMatch = ModIdPattern().Match(uri.AbsolutePath);

        int? fileId = fileMatch.Success && int.TryParse(fileMatch.Groups[1].Value, out var f) ? f : null;
        int? modId = modMatch.Success && int.TryParse(modMatch.Groups[1].Value, out var m) ? m : null;

        return modId is null && fileId is null ? null : new CfUrlRef(modId, fileId);
    }

    [GeneratedRegex(@"/files/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileIdPattern();

    [GeneratedRegex(@"/(?:mc-mods|mods|texture-packs|worlds|bukkit-plugins|shaders)/(\d+)(?:[-/]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ModIdPattern();

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new CurseForgeNotConfiguredException(
                "未配置 CurseForge API Key。到 https://console.curseforge.com/ 免费申请后填入设置，" +
                "或设置环境变量 CURSEFORGE_API_KEY。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Add("x-api-key", key);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"CurseForge API 返回 {(int)response.StatusCode}：{Head(body)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException($"解析 CurseForge 响应失败：{ex.Message}", ex);
        }
    }

    private static string Head(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
