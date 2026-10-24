using System.Text.Json;
using System.Xml.Linq;
using Launcher.Application.Abstractions;
using Launcher.Domain.Loaders;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Meta;

/// <summary>Fabric / Quilt 的 meta 客户端。两家 API 形状不同，但都落到同一个 VersionJson。</summary>
public sealed class LoaderMetaClient : ILoaderMetaClient
{
    private const string FabricBase = "https://meta.fabricmc.net/v2";
    private const string QuiltBase = "https://meta.quiltmc.org/v3";

    private readonly HttpClient _http;
    private readonly IUrlRewriter _rewriter;

    /// <param name="urlRewriter">下载源；不传就是官方源。BMCLAPI 只镜像了 Fabric 的 meta，Quilt 会原样走官方。</param>
    public LoaderMetaClient(HttpClient http, IUrlRewriter? urlRewriter = null)
    {
        _http = http;
        _rewriter = urlRewriter ?? OfficialUrlRewriter.Instance;
    }

    public async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(
        LoaderKind kind, string gameVersion, CancellationToken ct = default)
    {
        if (kind is LoaderKind.Forge or LoaderKind.NeoForge)
            return await GetMavenVersionsAsync(kind, gameVersion, ct).ConfigureAwait(false);
        var game = Uri.EscapeDataString(gameVersion);
        var url = kind switch
        {
            LoaderKind.Fabric => $"{FabricBase}/versions/loader/{game}",
            LoaderKind.Quilt => $"{QuiltBase}/versions/loader/{game}",
            _ => throw new NotSupportedException($"{kind} 不提供加载器版本列表"),
        };
        string json;
        try { json = await GetStringAsync(url, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return []; }
        using var document = JsonDocument.Parse(json);
        var versions = new List<string>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var item = element.TryGetProperty("loader", out var loader) ? loader : element;
            if (item.TryGetProperty("version", out var version) && version.GetString() is { Length: > 0 } value)
                versions.Add(value);
        }
        return NewestFirst(versions);
    }

    private async Task<IReadOnlyList<string>> GetMavenVersionsAsync(LoaderKind kind, string game, CancellationToken ct)
    {
        string prefix;
        string url;
        var stripPrefix = false;
        if (kind == LoaderKind.Forge)
        {
            prefix = game + "-";
            stripPrefix = true;
            url = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        }
        else if (game == "1.20.1")
        {
            prefix = game + "-";
            stripPrefix = true;
            url = "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml";
        }
        else
        {
            // NeoForge 21.4.x -> MC 1.21.4; 26.3.0.x -> MC 26.3.
            // Exact component boundaries prevent 1.21.1 from matching 1.21.10.
            if (!Version.TryParse(game, out var parsed)) return [];
            if (parsed.Major == 1 && parsed.Minor >= 20)
                prefix = $"{parsed.Minor}.{Math.Max(0, parsed.Build)}.";
            else if (parsed.Major >= 26)
                prefix = $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}.";
            else return [];
            url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
        }
        var xml = await GetStringAsync(url, ct).ConfigureAwait(false);
        var versions = XDocument.Parse(xml).Descendants("version").Select(v => v.Value)
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => stripPrefix ? v[prefix.Length..] : v);
        return NewestFirst(versions);
    }

    private static IReadOnlyList<string> NewestFirst(IEnumerable<string> versions) => versions
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var version) ? version : new Version())
        .ThenBy(v => v.Contains('-'))
        .ThenByDescending(v => v, StringComparer.Ordinal)
        .ToArray();

    public async Task<VersionJson> GetProfileAsync(
        LoaderKind kind, string gameVersion, string loaderVersion, CancellationToken ct = default)
    {
        var game = Uri.EscapeDataString(gameVersion);
        var version = Uri.EscapeDataString(loaderVersion);
        var url = kind switch
        {
            LoaderKind.Fabric => $"{FabricBase}/versions/loader/{game}/{version}/profile/json",
            LoaderKind.Quilt => $"{QuiltBase}/versions/loader/{game}/{version}/profile/json",
            _ => throw new NotSupportedException($"{kind} 的 profile 还没接入"),
        };

        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<VersionJson>(json, JsonDefaults.Options)
                      ?? throw new InvalidOperationException($"{kind} profile 解析失败");

        if (string.IsNullOrWhiteSpace(profile.InheritsFrom))
            throw new InvalidOperationException($"{kind} profile 缺少 inheritsFrom，合并逻辑会失效");

        return profile;
    }

    /// <summary>
    /// 和 <see cref="MojangMetaClient"/> 一样：下载源先试、官方兜底。
    /// BMCLAPI 的 fabric-meta 镜像不全（缺某些 loader 版本的 profile 会 404），不该因此装不了。
    /// </summary>
    private Task<string> GetStringAsync(string officialUrl, CancellationToken ct) =>
        MirrorFallback.GetStringAsync(url => GetRawStringAsync(url, ct), officialUrl, _rewriter, HttpDiagnostics.Log, ct);

    private async Task<string> GetRawStringAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"加载器元数据请求 {url} → {(int)response.StatusCode} {response.ReasonPhrase}",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
