using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Auth;

using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Auth;

/// <summary>
/// 微软账号登录：设备码授权 + Xbox / Minecraft 换证。
/// 选设备码而不是内嵌浏览器，是因为它不需要 client secret、不碰账号密码，
/// 用户在一台设备上授权、启动器在另一台设备上取 token，天然符合 OAuth 的推荐姿势。
/// </summary>
public sealed class MicrosoftAuthService(HttpClient http, MinecraftProfileClient profiles) : IMicrosoftAuthService
{
    /// <summary>
    /// Minecraft 官方启动器沿用多年的公共 client id（MSA 命名空间应用，不是我们自己注册的 AAD 应用）。
    /// 它只被 login.live.com 的消费者端点认可；拿去打 AAD 的 consumers/oauth2/v2.0 会被
    /// AADSTS700016 判成「应用不存在」——这是实测结论，不是猜的，改端点前先看 DeviceCodeUrl 的注释。
    /// </summary>
    private const string ClientId = "00000000402b5328";

    /// <summary>offline_access 才能拿到 refresh_token，没有它就每次都得让用户重登。</summary>
    private const string Scope = "XboxLive.signin offline_access";

    /// <summary>
    /// 设备码端点用 login.live.com，不用 login.microsoftonline.com/consumers/oauth2/v2.0/devicecode。
    /// 后者配 402b5328 现在直接 400 unauthorized_client（AADSTS700016，consumers 目录里找不到该应用），
    /// 因为 0000000040xxxxxx 这类 MSA 老应用压根没注册进那个 AAD 目录；
    /// 而 live.com 这一对端点会真的校验 client_id（随便编个 id 立刻 invalid_client），
    /// 并且返回 user_code / device_code / verification_uri=https://www.microsoft.com/link / interval=5 / expires_in=900。
    /// </summary>
    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";

    /// <summary>设备码轮询和 refresh_token 续期共用同一个 token 端点（两者 grant_type 不同）。</summary>
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    /// <summary>微软没给 expires_in 时的保守兜底（官方实际给 900 秒）。</summary>
    private const int DefaultDeviceCodeLifetimeSeconds = 900;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 便捷构造：换证客户端本来就要用同一套 HTTP 配置，DI 只注册一个 HttpClient 时走这条。
    /// </summary>
    public MicrosoftAuthService(HttpClient http) : this(http, new MinecraftProfileClient(http))
    {
    }

    public static HttpClient CreateHttpClient(string userAgent)
    {
        // 不设 BaseAddress：这里是绝对地址，注入方换不换 HttpMessageHandler 都不受影响。
        var client = HttpClientFactory.Create(userAgent, TimeSpan.FromSeconds(30));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<DeviceCodeSession> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>(3)
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            // v2 端点靠端点名区分流程，live.com 的 oauth20_* 是老式端点，必须自己在 body 里声明要设备码。
            ["response_type"] = "device_code",
        };

        var (status, body) = await PostFormAsync(DeviceCodeUrl, form, ct).ConfigureAwait(false);
        if (status is not HttpStatusCode.OK)
        {
            // invalid_client 是老应用被停用的典型信号，跟「网络抖了一下」不是一回事，单独给句话。
            if (ReadErrorCode(body) == "invalid_client")
                throw new InvalidOperationException("启动器内置的登录应用已被微软停用，暂时无法登录微软账号。");

            throw new InvalidOperationException($"申请登录代码失败（HTTP {(int)status}{TokenErrorSuffix(body)}）。");
        }

        var dto = Deserialize<DeviceCodeResponse>(body, "设备码响应");
        if (string.IsNullOrWhiteSpace(dto.UserCode) || string.IsNullOrWhiteSpace(dto.DeviceCode))
            throw new InvalidOperationException("微软返回的登录代码不完整，请重试。");

        // 实测 live.com 回的就是 https://www.microsoft.com/link；万一哪天缺失，退回同一个官方入口而不是崩掉。
        var verificationUri = string.IsNullOrWhiteSpace(dto.VerificationUri)
            ? "https://www.microsoft.com/link"
            : dto.VerificationUri;

        var lifetime = dto.ExpiresIn > 0 ? dto.ExpiresIn : DefaultDeviceCodeLifetimeSeconds;

        return new DeviceCodeSession(
            dto.UserCode,
            verificationUri,
            dto.DeviceCode,
            Math.Max(1, dto.Interval),
            DateTimeOffset.UtcNow.AddSeconds(lifetime));
    }

    public async Task<MicrosoftAccount> WaitForLoginAsync(
        DeviceCodeSession session,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.DeviceCode))
            throw new ArgumentException("登录会话缺少设备码，请重新申请。", nameof(session));

        status?.Report($"请在浏览器打开 {session.VerificationUri}，输入代码 {session.UserCode} 完成登录…");

        var interval = TimeSpan.FromSeconds(Math.Max(1, session.IntervalSeconds));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // 先等到下一个轮询点再问：设备码刚下发时用户不可能已经授权，
            // 抢跑只会拿到 authorization_pending 并白挨一次 slow_down。
            await Task.Delay(interval, ct).ConfigureAwait(false);

            if (session.IsExpired)
                throw new InvalidOperationException("登录代码已过期，请重新发起登录。");

            var form = new Dictionary<string, string>(3)
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = session.DeviceCode,
            };

            var (_, body) = await PostFormAsync(TokenUrl, form, ct).ConfigureAwait(false);
            var token = Deserialize<TokenResponse>(body, "登录令牌响应");

            if (!string.IsNullOrWhiteSpace(token.AccessToken))
            {
                status?.Report("登录成功，正在换取 Minecraft 令牌…");
                var refreshToken = token.RefreshToken ?? "";
                return await CompleteSignInAsync(token.AccessToken, refreshToken, status, ct).ConfigureAwait(false);
            }

            switch (token.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    // 微软要求轮询间隔至少加 5 秒，否则会一路拖到会话过期。
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new InvalidOperationException("登录代码已过期，请重新发起登录。");
                case "authorization_declined" or "access_denied":
                    throw new InvalidOperationException("你已在登录页面上取消了授权。");
                case "bad_verification_code":
                    throw new InvalidOperationException("登录代码无效，请重新发起登录。");
                case "invalid_client":
                    throw new InvalidOperationException(
                        "启动器内置的登录应用已被微软停用，暂时无法登录微软账号。");
                case "invalid_grant":
                    throw new InvalidOperationException("登录代码已失效，请重新发起登录。");
                default:
                    throw new InvalidOperationException(
                        $"微软登录失败{TokenErrorSuffix(body)}，请重试。");
            }
        }
    }

    public async Task<MicrosoftAccount> RefreshAsync(MicrosoftAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            throw new InvalidOperationException("本地没有 refresh_token，无法静默续期，请重新登录。");

        var form = new Dictionary<string, string>(4)
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken,
            ["scope"] = Scope,
        };

        var (_, body) = await PostFormAsync(TokenUrl, form, ct).ConfigureAwait(false);
        var token = Deserialize<TokenResponse>(body, "续期响应");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            if (token.Error == "invalid_client")
                throw new InvalidOperationException("启动器内置的登录应用已被微软停用，暂时无法续期。");

            if (token.Error is "invalid_grant" or "interaction_required")
                throw new InvalidOperationException("登录已过期（refresh_token 失效），请重新登录。");

            throw new InvalidOperationException($"续期失败{TokenErrorSuffix(body)}，请重新登录。");
        }

        // 微软的 refresh_token 是一次性的：每次换证都会签新的，旧的立刻作废。
        // 对方偶尔不返回新值，那就沿用旧的，别把它清空。
        var refreshToken = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? account.RefreshToken
            : token.RefreshToken;

        return await CompleteSignInAsync(token.AccessToken, refreshToken, null, ct).ConfigureAwait(false);
    }

    /// <summary>微软 token 到手之后的公共尾巴：Xbox → Minecraft 换证 + 拼账号。</summary>
    private async Task<MicrosoftAccount> CompleteSignInAsync(
        string microsoftAccessToken,
        string refreshToken,
        IProgress<string>? status,
        CancellationToken ct)
    {
        status?.Report("正在向 Xbox Live 验证账号…");
        var signIn = await profiles.SignInAsync(microsoftAccessToken, ct).ConfigureAwait(false);

        return new MicrosoftAccount(
            signIn.Profile.Uuid,
            signIn.Profile.Name,
            signIn.AccessToken,
            refreshToken,
            signIn.ExpiresAt);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostFormAsync(
        string url, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"连接微软登录服务器失败：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("微软登录服务器响应超时，请检查网络或代理后重试。", ex);
        }
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

    /// <summary>只取 OAuth 错误体里的 error 码，用来区分「应用被停用」和普通失败。</summary>
    private static string? ReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return null;

            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind is JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>OAuth 的错误体是 400 + JSON，把 error_description 摘出来给用户看。</summary>
    private static string TokenErrorSuffix(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return "";

            var code = document.RootElement.TryGetProperty("error", out var error) &&
                       error.ValueKind is JsonValueKind.String
                ? error.GetString()
                : null;

            var description = document.RootElement.TryGetProperty("error_description", out var desc) &&
                              desc.ValueKind is JsonValueKind.String
                ? desc.GetString()
                : null;

            // 微软的 error_description 末尾带一串 trace id，界面上只留前两句。
            if (!string.IsNullOrWhiteSpace(description))
            {
                var firstLine = description.Split('\r', '\n')[0].Trim();
                return code is null ? $"：{firstLine}" : $"：{firstLine}（{code}）";
            }

            return code is null ? "" : $"：{code}";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
