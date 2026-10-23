using System.Net;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Versions;

using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Meta;

/// <summary>Mojang 元数据客户端：manifest → 版本 JSON → 资源索引，带内存 + 磁盘缓存。</summary>
public sealed class MojangMetaClient : IVersionMetaClient
{
    public const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan VersionTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly IUrlRewriter _rewriter;
    private readonly string? _cacheDirectory;

    private VersionManifest? _manifest;
    private DateTimeOffset _manifestFetchedAt;

    /// <param name="useMirror">老开关，等价于传 <c>urlRewriter: MirrorUrlRewriter.Instance</c>；传了 urlRewriter 就以它为准。</param>
    public MojangMetaClient(
        HttpClient http,
        bool useMirror = false,
        string? cacheDirectory = null,
        IUrlRewriter? urlRewriter = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _rewriter = urlRewriter ?? (useMirror ? MirrorUrlRewriter.Instance : OfficialUrlRewriter.Instance);
    }

    public static HttpClient CreateHttpClient(string launcherName, string version)
    {
        return HttpClientFactory.Create(
            $"{launcherName}/{version} (self-hosted; +https://github.com/)",
            timeout: TimeSpan.FromSeconds(100));
    }

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct = default)
    {
        if (_manifest is not null && DateTimeOffset.UtcNow - _manifestFetchedAt < ManifestTtl)
            return _manifest;

        var cached = await ReadCacheAsync<VersionManifest>("manifest.json", ManifestTtl, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            _manifest = cached;
            _manifestFetchedAt = DateTimeOffset.UtcNow;
            return cached;
        }

        var json = await GetStringAsync(OfficialManifestUrl, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<VersionManifest>(json, JsonDefaults.Options)
                       ?? throw new InvalidOperationException("version_manifest 解析失败");

        _manifest = manifest;
        _manifestFetchedAt = DateTimeOffset.UtcNow;
        await WriteCacheAsync("manifest.json", json, ct).ConfigureAwait(false);
        return manifest;
    }

    public async Task<VersionJson> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        var safeName = $"version-{Sanitize(versionId)}.json";
        var cached = await ReadCacheAsync<VersionJson>(safeName, VersionTtl, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"版本清单里没有 {versionId}");

        var json = await GetStringAsync(entry.Url, ct).ConfigureAwait(false);
        var version = JsonSerializer.Deserialize<VersionJson>(json, JsonDefaults.Options)
                      ?? throw new InvalidOperationException($"{versionId} 的版本 JSON 解析失败");

        await WriteCacheAsync(safeName, json, ct).ConfigureAwait(false);
        return version;
    }

    public async Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference, CancellationToken ct = default)
    {
        var safeName = $"assets-{Sanitize(reference.Id)}.json";
        var cached = await ReadCacheAsync<AssetIndex>(safeName, VersionTtl, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var json = await GetStringAsync(reference.Url, ct).ConfigureAwait(false);
        var index = JsonSerializer.Deserialize<AssetIndex>(json, JsonDefaults.Options)
                    ?? throw new InvalidOperationException("asset index 解析失败");

        await WriteCacheAsync(safeName, json, ct).ConfigureAwait(false);
        return index;
    }

    /// <summary>
    /// 元数据一律「当前下载源先试、官方兜底」。镜像抖一下不该让整个安装失败。
    /// </summary>
    private Task<string> GetStringAsync(string officialUrl, CancellationToken ct) =>
        MirrorFallback.GetStringAsync(url => GetRawStringAsync(url, ct), officialUrl, _rewriter, HttpDiagnostics.Log, ct);

    /// <summary>真正发请求的地方：失败信息里必须带 URL 与状态码，否则用户只能看到一句 SSL 错误。</summary>
    private async Task<string> GetRawStringAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"元数据请求 {url} → {(int)response.StatusCode} {response.ReasonPhrase}",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<T?> ReadCacheAsync<T>(string fileName, TimeSpan ttl, CancellationToken ct) where T : class
    {
        if (_cacheDirectory is null) return null;

        var path = Path.Combine(_cacheDirectory, fileName);
        if (!File.Exists(path)) return null;
        if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) > ttl) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;   // 缓存损坏就当没有
        }
    }

    private async Task WriteCacheAsync(string fileName, string content, CancellationToken ct)
    {
        if (_cacheDirectory is null) return;

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = Path.Combine(_cacheDirectory, fileName);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, content, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            // 缓存写失败不影响主流程
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
