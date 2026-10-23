using System.Text.Json;
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
        var url = kind switch
        {
            LoaderKind.Fabric => $"{FabricBase}/versions/loader/{gameVersion}",
            LoaderKind.Quilt => $"{QuiltBase}/versions/loader/{gameVersion}",
            _ => throw new NotSupportedException($"{kind} 的 meta 还没接入"),
        };

        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var versions = new List<string>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // Fabric: { "loader": { "version": "0.16.9", "stable": true } }
            // Quilt : { "version": "0.27.1", ... }
            if (element.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("version", out var fabricVersion))
            {
                versions.Add(fabricVersion.GetString()!);
            }
            else if (element.TryGetProperty("version", out var quiltVersion))
            {
                versions.Add(quiltVersion.GetString()!);
            }
        }

        if (versions.Count == 0) throw new InvalidOperationException($"{kind} 没有适用于 {gameVersion} 的加载器版本");
        return versions;
    }

    public async Task<VersionJson> GetProfileAsync(
        LoaderKind kind, string gameVersion, string loaderVersion, CancellationToken ct = default)
    {
        var url = kind switch
        {
            LoaderKind.Fabric => $"{FabricBase}/versions/loader/{gameVersion}/{loaderVersion}/profile/json",
            LoaderKind.Quilt => $"{QuiltBase}/versions/loader/{gameVersion}/{loaderVersion}/profile/json",
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
