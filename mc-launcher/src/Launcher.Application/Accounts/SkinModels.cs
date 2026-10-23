using System.Text.Json.Serialization;

namespace Launcher.Application.Accounts;

/// <summary>
/// 一个玩家的皮肤/披风信息，取自 Mojang 的 sessionserver（无签名版）。
/// 只描述「去哪拿图」，不含图片字节：界面自己下载并裁头部，避免启动器替 UI 决定解码方式。
/// </summary>
/// <param name="Uuid">调用方传入的账号 ID（原样保留，含连字符与否都行）。</param>
/// <param name="Name">玩家名，优先用接口返回的（大小写以服务端为准）。</param>
/// <param name="SkinUrl">皮肤 PNG 的 https 地址；账号没设皮肤时为 null。</param>
/// <param name="CapeUrl">披风 PNG 的 https 地址；没披风时为 null。</param>
/// <param name="Model">皮肤模型：<c>slim</c>（细手臂）或 <c>classic</c>；连皮肤都没有时为 null。</param>
/// <param name="FetchedAt">取到这份数据的时间，缓存新鲜度按它算。</param>
public sealed record PlayerSkin(
    string Uuid,
    string Name,
    string? SkinUrl,
    string? CapeUrl,
    string? Model,
    DateTimeOffset FetchedAt)
{
    /// <summary>有没有披风（界面据此决定要不要留披风位）。</summary>
    [JsonIgnore]
    public bool HasCape => !string.IsNullOrWhiteSpace(CapeUrl);

    /// <summary>是不是细手臂模型（Alex 型）。</summary>
    [JsonIgnore]
    public bool IsSlim => string.Equals(Model, "slim", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 取皮肤的结果。<see cref="Reason"/> 为 null 表示「干净的成功」。
/// 注意 <see cref="Skin"/> 与 <see cref="Reason"/> 可以同时有值：网络挂了但本地有旧缓存时，
/// 我们既把旧皮肤给你（头像不至于变空白），也用 Reason 告诉你这次没取到新的。
/// </summary>
/// <param name="Skin">拿到的皮肤；一次都没成功过时为 null。</param>
/// <param name="Reason">给人看的中文失败原因；成功时为 null。</param>
public sealed record SkinFetchResult(PlayerSkin? Skin, string? Reason)
{
    /// <summary>取到了新数据，且没有任何问题。</summary>
    public bool IsSuccess => Skin is not null && Reason is null;

    /// <summary>有图可显示（可能是旧缓存）。</summary>
    public bool HasSkin => Skin is not null;

    public static SkinFetchResult Success(PlayerSkin skin) => new(skin, null);

    /// <summary>失败。<paramref name="fallback"/> 传上次缓存时，界面仍能显示头像。</summary>
    public static SkinFetchResult Failure(string reason, PlayerSkin? fallback = null) => new(fallback, reason);
}
