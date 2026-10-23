using System.Collections.Concurrent;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Meta;

/// <summary>Mojang 元数据：同一缓存键串行刷新，等待者复用完成结果；失败与取消不缓存。</summary>
public sealed class MojangMetaClient : IVersionMetaClient
{
    public const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan VersionTtl = TimeSpan.FromDays(7);
    private readonly HttpClient _http;
    private readonly IUrlRewriter _rewriter;
    private readonly string? _cacheDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, (object Value, DateTimeOffset At)> _memory = new();

    public MojangMetaClient(HttpClient http, bool useMirror = false, string? cacheDirectory = null, IUrlRewriter? urlRewriter = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _rewriter = urlRewriter ?? (useMirror ? MirrorUrlRewriter.Instance : OfficialUrlRewriter.Instance);
    }

    public static HttpClient CreateHttpClient(string launcherName, string version) => HttpClientFactory.Create(
        $"{launcherName}/{version} (self-hosted; +https://github.com/)", TimeSpan.FromSeconds(100));

    public Task<VersionManifest> GetManifestAsync(CancellationToken ct = default) => GetAsync<VersionManifest>(
        "manifest.json", ManifestTtl, _ => Task.FromResult(OfficialManifestUrl), ct);

    public Task<VersionJson> GetVersionAsync(string versionId, CancellationToken ct = default) => GetAsync<VersionJson>(
        $"version-{Sanitize(versionId)}.json", VersionTtl, async token =>
        {
            var manifest = await GetManifestAsync(token).ConfigureAwait(false);
            return manifest.Versions.FirstOrDefault(v => v.Id == versionId)?.Url
                ?? throw new InvalidOperationException($"找不到游戏版本：{versionId}");
        }, ct);

    public Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference, CancellationToken ct = default) => GetAsync<AssetIndex>(
        $"assets-{Sanitize(reference.Id)}.json", VersionTtl, _ => Task.FromResult(reference.Url), ct);

    private async Task<T> GetAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<string>> url, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_memory.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < ttl)
                return (T)cached.Value;
            var disk = await ReadCacheAsync<T>(key, ttl, ct).ConfigureAwait(false);
            if (disk is not null)
            {
                _memory[key] = (disk, DateTimeOffset.UtcNow);
                return disk;
            }
            var location = await url(ct).ConfigureAwait(false);
            var json = await MirrorFallback.GetStringAsync(u => GetRawStringAsync(u, ct), location, _rewriter, HttpDiagnostics.Log, ct).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)
                ?? throw new InvalidOperationException($"元数据解析失败：{key}");
            await WriteCacheAsync(key, json, ct).ConfigureAwait(false);
            _memory[key] = (value, DateTimeOffset.UtcNow);
            return value;
        }
        finally { gate.Release(); }
    }

    private async Task<string> GetRawStringAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"元数据请求 {url} → {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<T?> ReadCacheAsync<T>(string key, TimeSpan ttl, CancellationToken ct) where T : class
    {
        if (_cacheDirectory is null) return null;
        try
        {
            var path = Path.Combine(_cacheDirectory, key);
            if (!File.Exists(path) || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) > ttl) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task WriteCacheAsync(string key, string json, CancellationToken ct)
    {
        if (_cacheDirectory is null) return;
        var temporary = Path.Combine(_cacheDirectory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(_cacheDirectory, key), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
