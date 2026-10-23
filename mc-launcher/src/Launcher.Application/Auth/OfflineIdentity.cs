using System.Text.RegularExpressions;
using Launcher.Domain.Launch;

namespace Launcher.Application.Auth;

/// <summary>
/// 不联网的身份：只用玩家自己起的名字，UUID 由名字推导。
/// 离线模式的服务端（server.properties: online-mode=false）就是按同一套规则认人的，
/// 所以这里算出来的 UUID 必须和 Java 侧逐位一致，否则换启动器就"变成另一个人"（背包、领地全丢）。
/// </summary>
/// <param name="Name">游戏内名，也就是拼 <c>--username</c> 的值。</param>
/// <param name="Uuid">由名字推导的 UUID（带连字符的 36 位形式）。</param>
public sealed record OfflineIdentity(string Name, string Uuid);

/// <summary>
/// 离线身份工厂：把名字变成"看起来像正版"的 UUID。
/// </summary>
public static partial class OfflineIdentityFactory
{
    /// <summary>没设置过名字时的默认值，沿用 CLI / App 里原来的硬编码。</summary>
    public const string DefaultName = "Developer";

    /// <summary>Minecraft 用户名上限 16；下限 3 是官方注册页的惯例，太短的名字原版也进不去。</summary>
    private const int MinNameLength = 3;

    private const int MaxNameLength = 16;

    /// <summary>
    /// 按名字生成离线身份。
    /// 算法本身放在 <see cref="LaunchContext.OfflineUuid"/>：启动参数那条链路已经用它，
    /// 两处各写一份迟早会漂移，这里只做校验 + 包装。
    /// </summary>
    public static OfflineIdentity Create(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException(
                $"离线用户名不合法：只能用 3~16 个字母、数字或下划线（当前为 \"{name}\"）。",
                nameof(name));

        return new OfflineIdentity(name, LaunchContext.OfflineUuid(name));
    }

    /// <summary>名字合法性判断，界面输入框和 <see cref="Create"/> 共用同一套规则。</summary>
    public static bool IsValidName(string? name)
    {
        if (name is null) return false;
        if (name.Length is < MinNameLength or > MaxNameLength) return false;
        return NamePattern().IsMatch(name);
    }

    // 只允许 ASCII：Java 的名字校验是 ^[A-Za-z0-9_]{3,16}$，中文名连原版服务端都进不去。
    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
