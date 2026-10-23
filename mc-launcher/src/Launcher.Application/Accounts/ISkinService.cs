namespace Launcher.Application.Accounts;

/// <summary>
/// 取玩家皮肤/披风的数据能力。界面拿 <see cref="PlayerSkin.SkinUrl"/> 自己下图、裁头部方块。
/// </summary>
public interface ISkinService
{
    /// <summary>
    /// 按账号 ID 取皮肤/披风。实现内部会缓存（内存 + 落盘），重复调用通常不联网。
    /// </summary>
    /// <param name="uuid">账号 ID，带不带连字符都行（32 位十六进制）。</param>
    /// <param name="name">玩家名，仅作为接口没回名字时的兜底显示值。</param>
    /// <param name="ct">取消令牌；主动取消按 .NET 惯例抛出 <see cref="OperationCanceledException"/>，不算「失败」。</param>
    /// <remarks>
    /// 实现约定：网络失败、404/204、返回体异常一律**不抛异常**，用 <see cref="SkinFetchResult.Reason"/>
    /// 说明中文原因；只有调用方自己取消才抛。
    /// </remarks>
    Task<SkinFetchResult> FetchAsync(string uuid, string name, CancellationToken ct = default);
}
