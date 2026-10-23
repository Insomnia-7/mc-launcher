namespace Launcher.Domain.Launch;

/// <summary>
/// 一次启动所需的全部外部输入。刻意不含任何平台类型：
/// 路径由调用方按平台拼好传进来（ADR-001：Domain 不碰 System.IO 的平台差异）。
/// </summary>
public sealed record LaunchContext(
    string PlayerName,
    string Uuid,
    string? AccessToken,
    string UserType,              // msa / legacy / mojang
    string LauncherName,
    string LauncherVersion,
    string GameDirectory,         // 实例 .minecraft
    string AssetsRoot,            // <root>/assets
    string LibrariesRoot,         // <root>/libraries
    string ClientJarPath,         // <root>/versions/<id>/<id>.jar
    int MemoryMb,
    string? ExtraJvmArgs,
    string? ExtraGameArgs,
    int WindowWidth,
    int WindowHeight,
    bool FullScreen,
    // 追加的 JVM 调优档位。放在末尾且带默认值：老调用方不用改，语义也确实是「可选加料」
    JvmPreset JvmPreset = JvmPreset.Default,
    // natives 的实际落盘目录。默认 null = 沿用 versions/<id>/natives；
    // 填了就以它为准（调用方用 NativesProvisioner 解压到 <gameRoot>/natives/<versionId>/<指纹>/ 再传进来）。
    // 带默认值，所以 App / CLI / 测试里那些按命名参数构造的调用点都不用改。
    string? NativesDirectory = null)
{
    /// <summary>离线账户的 UUID：MD5("OfflinePlayer:"+name) 按 UUID v3 改写版本位。</summary>
    public static string OfflineUuid(string playerName)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);   // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // variant RFC 4122

        var hex = Convert.ToHexStringLower(hash);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
