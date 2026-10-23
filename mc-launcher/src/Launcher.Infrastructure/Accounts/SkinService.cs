using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Launcher.Application.Accounts;
using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Accounts;

/// <summary>
/// 走 Mojang 的 sessionserver 取皮肤/披风。这里刻意不用签名版（<c>?unsigned=false</c>）：
/// 签名是给「服务端验证皮肤真伪」用的，启动器只是拿图显示，多要一份签名反而更慢更大。
/// </summary>
/// <remarks>
/// 三层行为：
/// <list type="number">
/// <item>内存缓存 10 分钟（进程内、按账号 ID，不区分 dataDirectory —— 皮肤是全局数据）。</item>
/// <item>落盘 <c>&lt;dataDirectory&gt;/skin-cache/&lt;uuid&gt;.json</c>，10 分钟内直接当新鲜数据用；</item>
/// <item>超过 10 分钟的旧盘缓存只在**网络失败**时兜底，并同时给出 Reason —— 头像不至于变空白，也不假装成功。</item>
/// </list>
/// 任何 IO / JSON 异常都静默忽略：皮肤是装饰品，绝不能因为它挡住启动。
/// </remarks>
public sealed class SkinService : ISkinService
{
    /// <summary>写进 User-Agent 的名字，方便 Mojang 侧识别调用方。</summary>
    public const string UserAgent = "MCLauncher-Skin/1.0";

    private const string ProfileEndpoint = "https://sessionserver.mojang.com/session/minecraft/profile/";

    private const string NoProfileReason = "这个账号没有公开的皮肤信息（离线账号或未设置皮肤）。";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // 进程内缓存。key 是去掉连字符的小写 uuid，value 带 FetchedAt，过期靠时间判断而不是定时清理。
    private static readonly ConcurrentDictionary<string, PlayerSkin> Memory = new(StringComparer.Ordinal);

    private readonly HttpClient _http;

    private readonly string _dataDirectory;

    /// <summary>用默认网络策略（<see cref="HttpClientFactory"/>，15 秒超时）建服务。</summary>
    public SkinService(string dataDirectory) : this(CreateHttpClient(), dataDirectory)
    {
    }

    /// <summary>注入式构造，便于自验时换成桩客户端。</summary>
    public SkinService(HttpClient http, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _dataDirectory = dataDirectory;
    }

    /// <summary>按项目统一的网络策略建客户端：带 User-Agent、15 秒超时、走用户配置的代理。</summary>
    public static HttpClient CreateHttpClient() =>
        HttpClientFactory.Create(UserAgent, TimeSpan.FromSeconds(15));

    /// <summary>缓存目录（<c>&lt;dataDirectory&gt;/skin-cache</c>）。</summary>
    public static string CacheDirectory(string dataDirectory) => Path.Combine(dataDirectory, "skin-cache");

    /// <summary>某个账号的缓存文件路径。</summary>
    public static string CachePath(string dataDirectory, string uuid) =>
        Path.Combine(CacheDirectory(dataDirectory), Normalize(uuid) + ".json");

    /// <inheritdoc />
    public async Task<SkinFetchResult> FetchAsync(string uuid, string name, CancellationToken ct = default)
    {
        var key = Normalize(uuid);
        if (key.Length != 32 || !IsHex(key))
            return SkinFetchResult.Failure("账号 ID 不合法，取不到皮肤。");

        var now = DateTimeOffset.UtcNow;

        if (Memory.TryGetValue(key, out var cached) && now - cached.FetchedAt < CacheLifetime)
            return SkinFetchResult.Success(cached);

        var disk = ReadCache(key);
        if (disk is not null && now - disk.FetchedAt < CacheLifetime)
        {
            Memory[key] = disk;
            return SkinFetchResult.Success(disk);
        }

        try
        {
            using var response = await _http.GetAsync(ProfileEndpoint + key, ct).ConfigureAwait(false);

            // 未注册 / 没公开皮肤：sessionserver 回 204（实测），历史版本回 404，两种都当"没有"
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                return SkinFetchResult.Failure(NoProfileReason, disk);

            if (!response.IsSuccessStatusCode)
                return SkinFetchResult.Failure($"取皮肤失败：服务器返回 HTTP {(int)response.StatusCode}。", disk);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var skin = BuildSkin(uuid.Trim(), name, body, now);

            if (skin is null || (skin.SkinUrl is null && skin.CapeUrl is null))
                return SkinFetchResult.Failure(NoProfileReason, disk);

            Memory[key] = skin;
            WriteCache(key, skin);
            return SkinFetchResult.Success(skin);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 调用方自己取消，不是"取不到皮肤"
        }
        catch (OperationCanceledException)
        {
            return SkinFetchResult.Failure("取皮肤失败：请求超时（15 秒）。", disk);
        }
        catch (HttpRequestException ex)
        {
            return SkinFetchResult.Failure("取皮肤失败：" + Describe(ex), disk);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                       or NotSupportedException or ArgumentException)
        {
            return SkinFetchResult.Failure("取皮肤失败：" + Describe(ex), disk);
        }
    }

    /// <summary>
    /// 把 textures 的 base64 解成 JSON 再取 SKIN 地址。等价于
    /// <c>ParseTextures(UTF8(base64decode(texturesBase64))).Skin</c>，失败返回 null 而不抛。
    /// </summary>
    public static string? DecodeSkinUrl(string? texturesBase64)
    {
        var json = DecodeTexturesJson(texturesBase64);
        return json is null ? null : ParseTextures(json).Skin;
    }

    /// <summary>
    /// 解析已解码的 textures JSON：<c>textures.SKIN.url</c> / <c>textures.CAPE.url</c> /
    /// <c>textures.SKIN.metadata.model</c>。字段缺失或 JSON 坏掉时对应项为 null，绝不抛异常。
    /// </summary>
    /// <remarks>
    /// Mojang 只在细手臂（Alex）皮肤上给 <c>metadata.model = "slim"</c>；classic 皮肤没有这个字段，
    /// 所以「有皮肤但没有 metadata」一律归成 <c>classic</c>。
    /// </remarks>
    public static (string? Skin, string? Cape, string? Model) ParseTextures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null, null);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 正常结构是 {"textures":{...}}；也有把 textures 直接当根的变体，两种都认
            var textures = Find(root, "textures") ?? root;

            var skinNode = Find(textures, "SKIN");
            var skinUrl = UpgradeScheme(ReadString(skinNode, "url"));
            var capeUrl = UpgradeScheme(ReadString(Find(textures, "CAPE"), "url"));
            var model = ReadString(Find(skinNode, "metadata"), "model");

            return (skinUrl, capeUrl, NormalizeModel(model, skinUrl));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>base64 → UTF-8 JSON 文本；兼容 URL-safe 变体与缺失的补位，坏输入返回 null。</summary>
    private static string? DecodeTexturesJson(string? texturesBase64)
    {
        if (string.IsNullOrWhiteSpace(texturesBase64)) return null;

        var text = texturesBase64.Trim().Replace('-', '+').Replace('_', '/');
        switch (text.Length % 4)
        {
            case 2: text += "=="; break;
            case 3: text += "="; break;
            case 1: return null; // 长度模 4 为 1 的 base64 不存在
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>把 profile 响应体拼成 <see cref="PlayerSkin"/>；没有 textures 属性时返回 null。</summary>
    private static PlayerSkin? BuildSkin(string uuid, string name, string body, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var displayName = ReadString(root, "name") is { Length: > 0 } serverName ? serverName : name;

            if (Find(root, "properties") is not { ValueKind: JsonValueKind.Array } properties) return null;

            foreach (var property in properties.EnumerateArray())
            {
                if (!string.Equals(ReadString(property, "name"), "textures", StringComparison.OrdinalIgnoreCase))
                    continue;

                var json = DecodeTexturesJson(ReadString(property, "value"));
                if (json is null) continue;

                var (skin, cape, model) = ParseTextures(json);
                return new PlayerSkin(uuid, displayName, skin, cape, model, now);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读内存/磁盘缓存里那份。任何异常都当没有。</summary>
    private PlayerSkin? ReadCache(string key)
    {
        try
        {
            var path = CachePath(_dataDirectory, key);
            if (!File.Exists(path)) return null;

            var skin = JsonSerializer.Deserialize<PlayerSkin>(File.ReadAllText(path), Options);

            // 空壳缓存（没皮肤也没披风）没有兜底价值，当没有
            return skin is { } value && (value.SkinUrl is not null || value.CapeUrl is not null) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>落盘。先写临时文件再整体替换，写坏不至于留下半份缓存。</summary>
    private void WriteCache(string key, PlayerSkin skin)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory(_dataDirectory));

            var target = CachePath(_dataDirectory, key);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(skin, Options));
                File.Move(temporary, target, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // 缓存写不进去不影响这次的结果
        }
    }

    /// <summary>Mojang 现在回 <c>http://textures.minecraft.net/...</c>，统一升成 https 更稳。</summary>
    private static string? UpgradeScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + trimmed["http://".Length..]
            : trimmed;
    }

    private static string? NormalizeModel(string? model, string? skinUrl)
    {
        if (!string.IsNullOrWhiteSpace(skinUrl) &&
            string.Equals(model?.Trim(), "slim", StringComparison.OrdinalIgnoreCase))
            return "slim";

        // 没有 metadata.model 就是 classic；连皮肤都没有就没有模型可言
        return string.IsNullOrWhiteSpace(skinUrl) ? null : "classic";
    }

    /// <summary>给 Reason 用的简短原因：取第一行、截断，避免把整段异常糊到界面上。</summary>
    private static string Describe(Exception ex)
    {
        var message = ex is HttpRequestException { InnerException: { } inner } ? inner.Message : ex.Message;

        var line = message.Split('\r', '\n')[0].Trim();
        if (line.Length == 0) line = ex.GetType().Name;

        return line.Length <= 120 ? line + "。" : line[..120] + "…";
    }

    private static JsonElement? Find(JsonElement? node, string name)
    {
        if (node is not { ValueKind: JsonValueKind.Object }) return null;

        foreach (var property in node.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        return null;
    }

    private static string? ReadString(JsonElement? node, string property) =>
        Find(node, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>去掉连字符并转小写：接口和缓存文件都用这个规范形式。</summary>
    private static string Normalize(string uuid) =>
        string.IsNullOrWhiteSpace(uuid) ? string.Empty : uuid.Trim().Replace("-", string.Empty).ToLowerInvariant();

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigitLower(c)) return false;
        }

        return true;
    }
}
