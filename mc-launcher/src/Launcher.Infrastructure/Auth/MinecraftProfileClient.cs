using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Auth;

using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Auth;

/// <summary>
/// 微软 token → Xbox Live → XSTS → Minecraft 服务端 token → 玩家档案，这条链是固定的四跳，
/// 每一跳都需要上一跳签出来的票据，没有捷径。单独成一个类是因为它是纯换证逻辑：
/// 不碰设备码、不管持久化，也就更容易单独排查「卡在哪一跳」。
/// </summary>
public sealed class MinecraftProfileClient(HttpClient http)
{
    private const string XboxUserAuthenticateUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthorizeUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>XSTS 要求显式声明依赖方；写错这一行 XSTS 会签出一张 Minecraft 不认的票据。</summary>
    private const string MinecraftRelyingParty = "rp://api.minecraftservices.com/";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// XBL 的 XErr 是数字，用 long 接；NumberHandling 允许字符串形式，避免对方某天改成字面量就解析炸。
    /// </summary>
    private static readonly string[] JsonBodyKeys = ["errorMessage", "error", "Message"];

    public static HttpClient CreateHttpClient(string userAgent)
    {
        // 这里不设 BaseAddress：四个端点跨三个域名，用绝对地址比来回切 client 清楚。
        var client = HttpClientFactory.Create(userAgent, TimeSpan.FromSeconds(30));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 走完 Xbox → Minecraft 的全链路，返回 Minecraft 令牌与档案。
    /// 这里拿到的令牌和 refresh_token 组合起来才是完整账号，refresh_token 由调用方补。
    /// </summary>
    public async Task<MinecraftSignIn> SignInAsync(string microsoftAccessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(microsoftAccessToken))
            throw new ArgumentException("微软访问令牌为空，无法换取 Minecraft 令牌。", nameof(microsoftAccessToken));

        var (xboxToken, userHash) = await AuthenticateXboxAsync(microsoftAccessToken, ct).ConfigureAwait(false);
        var xstsToken = await AuthorizeXstsAsync(xboxToken, ct).ConfigureAwait(false);

        // Minecraft 只认这个拼法：XBL3.0 + 空格 + x=<userhash> + 分号 + XSTS token。
        var identityToken = $"XBL3.0 x={userHash};{xstsToken}";
        var (minecraftToken, expiresAt) = await LoginWithXboxAsync(identityToken, ct).ConfigureAwait(false);
        var profile = await GetProfileAsync(minecraftToken, ct).ConfigureAwait(false);

        return new MinecraftSignIn(profile, minecraftToken, expiresAt);
    }

    /// <summary>查一次玩家档案。用来在启动前确认令牌还能用，也用来给用户看当前登录的是谁。</summary>
    public async Task<MinecraftProfile> GetProfileAsync(string minecraftAccessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftAccessToken))
            throw new ArgumentException("Minecraft 访问令牌为空。", nameof(minecraftAccessToken));

        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        var (status, body) = await SendAsync(request, ct).ConfigureAwait(false);

        if (status is HttpStatusCode.NotFound)
            throw new InvalidOperationException("这个账号没有购买 Minecraft: Java 版，拿不到玩家档案。");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Minecraft 访问令牌已失效，请重新登录。");
        if (status is not HttpStatusCode.OK)
            throw new InvalidOperationException($"获取 Minecraft 档案失败（HTTP {(int)status}{ErrorSuffix(body)}）。");

        var dto = Deserialize<MinecraftProfileResponse>(body, "Minecraft 档案");
        if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name))
            throw new InvalidOperationException("Minecraft 档案缺少 id 或 name 字段。");

        return new MinecraftProfile(dto.Id, dto.Name);
    }

    /// <summary>
    /// 给「账号现在到底行不行」这类问题用的自测/诊断入口：真去查一次档案，
    /// 查得动就回一句人话，查不动就把中文原因抛出去。
    /// </summary>
    public async Task<string> DescribeAccountAsync(MicrosoftAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var profile = await GetProfileAsync(account.AccessToken, ct).ConfigureAwait(false);
        var remaining = account.ExpiresAt - DateTimeOffset.UtcNow;
        var lifetime = remaining <= TimeSpan.Zero
            ? "令牌已过期"
            : $"令牌剩余 {remaining.TotalHours:F1} 小时";

        return $"{profile.Name}（{profile.Uuid}），{lifetime}。";
    }

    private async Task<(string Token, string UserHash)> AuthenticateXboxAsync(
        string microsoftAccessToken, CancellationToken ct)
    {
        // RpsTicket 必须带 d= 前缀：不带会被当成旧版 MSA 票据直接拒掉，
        // 而报错信息只有一句 "Invalid token" 之类，排查起来非常费劲。
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={microsoftAccessToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        var (status, body) = await PostJsonAsync(XboxUserAuthenticateUrl, payload, ct).ConfigureAwait(false);
        if (status is not HttpStatusCode.OK)
            throw TranslateXboxError(body, $"Xbox Live 认证失败（HTTP {(int)status}）");

        var dto = Deserialize<XboxTokenResponse>(body, "Xbox Live 认证响应");
        var userHash = dto.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs;
        if (string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(userHash))
            throw new InvalidOperationException("Xbox Live 认证响应缺少 Token 或 userhash。");

        return (dto.Token, userHash);
    }

    private async Task<string> AuthorizeXstsAsync(string xboxToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxToken },
            },
            RelyingParty = MinecraftRelyingParty,
            TokenType = "JWT",
        };

        var (status, body) = await PostJsonAsync(XstsAuthorizeUrl, payload, ct).ConfigureAwait(false);
        if (status is not HttpStatusCode.OK)
            throw TranslateXboxError(body, $"Xbox 身份验证失败（HTTP {(int)status}）");

        var dto = Deserialize<XboxTokenResponse>(body, "XSTS 响应");
        if (string.IsNullOrWhiteSpace(dto.Token))
            throw new InvalidOperationException("XSTS 响应缺少 Token。");

        return dto.Token;
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> LoginWithXboxAsync(
        string identityToken, CancellationToken ct)
    {
        var (status, body) = await PostJsonAsync(
            MinecraftLoginUrl, new { identityToken }, ct).ConfigureAwait(false);

        if (status is not HttpStatusCode.OK)
            throw new InvalidOperationException($"Minecraft 登录失败（HTTP {(int)status}{ErrorSuffix(body)}）。");

        var dto = Deserialize<MinecraftLoginResponse>(body, "Minecraft 登录响应");
        if (string.IsNullOrWhiteSpace(dto.AccessToken))
            throw new InvalidOperationException("Minecraft 登录响应缺少 access_token。");

        // 官方给 86400 秒；对方万一漏发 expires_in，按 24 小时兜底而不是当成永不过期。
        var seconds = dto.ExpiresIn > 0 ? dto.ExpiresIn : 86_400;
        return (dto.AccessToken, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private async Task<(HttpStatusCode Status, string Body)> PostJsonAsync(
        string url, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        // XBL / XSTS 是契约式 API，缺这个版本头会返回 400 且不解释原因。
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "1");
        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"连接 Xbox / Minecraft 服务器失败：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Xbox / Minecraft 服务器响应超时，请检查网络后重试。", ex);
        }
    }

    /// <summary>
    /// XSTS 的失败原因全在 XErr 里，HTTP 状态码只能看出「401」。
    /// 这几个码是用户能自己解决的高频原因，值得单独翻译；其余的连同原始码一起抛出去便于反馈。
    /// </summary>
    private static InvalidOperationException TranslateXboxError(string body, string fallback)
    {
        long? xerr = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty("XErr", out var element))
            {
                xerr = element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetInt64(out var value) => value,
                    JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就当没有错误码，走兜底文案
        }

        return xerr switch
        {
            2148916233 => new InvalidOperationException(
                "这个微软账号还没有 Xbox 账号。请先到 https://www.xbox.com 免费创建一个，再回来登录。"),
            2148916235 => new InvalidOperationException(
                "该账号所在国家/地区不支持 Xbox Live，无法登录。"),
            2148916236 or 2148916237 => new InvalidOperationException(
                "该账号需要先在 Xbox 上完成成人验证（韩国地区的合规要求）。"),
            2148916238 => new InvalidOperationException(
                "这是未成年的微软账号，需要家长在 Microsoft 家庭组里同意后才能登录。"),
            2148916227 => new InvalidOperationException(
                "该 Xbox 账号已被封禁，无法登录。"),
            null => new InvalidOperationException($"{fallback}{ErrorSuffix(body)}。"),
            _ => new InvalidOperationException($"Xbox 身份验证被拒绝（错误码 {xerr}）。"),
        };
    }

    private static T Deserialize<T>(string body, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json)
                   ?? throw new InvalidOperationException($"{what}解析失败：返回了空对象。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{what}解析失败：{ex.Message}", ex);
        }
    }

    /// <summary>把对方的错误体压成一句「：原因」，解析不出就返回空串。</summary>
    private static string ErrorSuffix(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return "";

            foreach (var key in JsonBodyKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var element) &&
                    element.ValueKind is JsonValueKind.String &&
                    element.GetString() is { Length: > 0 } text)
                {
                    return $"：{text}";
                }
            }
        }
        catch (JsonException)
        {
            // HTML 错误页之类，直接不给原因
        }

        return "";
    }

    private sealed record MinecraftProfileResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record MinecraftLoginResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record XboxTokenResponse(
        [property: JsonPropertyName("Token")] string? Token,
        [property: JsonPropertyName("DisplayClaims")] XboxDisplayClaims? DisplayClaims);

    private sealed record XboxDisplayClaims(
        [property: JsonPropertyName("xui")] List<XboxUserClaim>? Xui);

    private sealed record XboxUserClaim(
        [property: JsonPropertyName("uhs")] string? Uhs);
}
