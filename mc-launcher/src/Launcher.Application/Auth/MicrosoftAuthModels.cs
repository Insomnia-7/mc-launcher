namespace Launcher.Application.Auth;

/// <summary>
/// 设备码登录会话。用户在浏览器里输一次短码，启动器在后台按固定间隔轮询换 token，
/// 全程不需要启动器自己接密码，也不需要带 client secret。
/// </summary>
/// <param name="UserCode">展示给用户的短码（形如 ABCD1234），只用于人工抄写。</param>
/// <param name="VerificationUri">用户要打开的登录页；微软以后会改域名，所以不硬编码。</param>
/// <param name="DeviceCode">轮询用的长码。它等同于临时口令，不要写进日志或界面。</param>
/// <param name="IntervalSeconds">官方要求的最小轮询间隔；调快会收到 <c>slow_down</c>。</param>
/// <param name="ExpiresAt">会话截止时刻，过了必须重新申请（典型 15 分钟）。</param>
public sealed record DeviceCodeSession(
    string UserCode,
    string VerificationUri,
    string DeviceCode,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}

/// <summary>
/// 已登录的正版账号。<see cref="AccessToken"/> 是 Minecraft 服务端 token（拼启动参数用的那个），
/// <see cref="RefreshToken"/> 是微软侧 refresh_token（用于静默续期），两者的有效期不是一回事。
/// </summary>
/// <param name="Uuid">Minecraft 档案 id，无连字符的 32 位十六进制。</param>
/// <param name="Name">游戏内名，拼 <c>--username</c> 用。</param>
/// <param name="AccessToken">Minecraft 访问令牌，有效期 24 小时。</param>
/// <param name="RefreshToken">微软 refresh_token，长期有效但每用一次就换新。</param>
/// <param name="ExpiresAt">上面 Minecraft 令牌的过期时刻（UTC）。</param>
public sealed record MicrosoftAccount(
    string Uuid,
    string Name,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt)
{
    /// <summary>续期余量：压着过期点启动容易在加载途中失效，留 5 分钟。</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>该考虑先续期了。启动前用这个判断，不要用 <see cref="IsExpired"/>。</summary>
    public bool NeedsRefresh => DateTimeOffset.UtcNow >= ExpiresAt - RefreshMargin;

    /// <summary>默认的 record ToString 会把两个 token 全部打出来，落到日志里就是凭据泄漏。</summary>
    public override string ToString() => $"{Name} ({Uuid})";
}

/// <summary>Minecraft 侧能拿到的最小身份：uuid + 游戏内名。</summary>
public sealed record MinecraftProfile(string Uuid, string Name);

/// <summary>
/// 一次完整的 Xbox → Minecraft 换证结果。调用方拿到它再加上微软的 refresh_token，
/// 才能拼成可持久化的 <see cref="MicrosoftAccount"/>。
/// </summary>
public sealed record MinecraftSignIn(MinecraftProfile Profile, string AccessToken, DateTimeOffset ExpiresAt);
