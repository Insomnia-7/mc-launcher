namespace Launcher.Application.Abstractions;

/// <summary>
/// NeoForge 的安装入口。和 Forge 一样是「下载 installer jar 再跑 java -jar」，
/// 但 installer 的 maven 布局分两套（新版 <c>net/neoforged/neoforge</c>、1.20.1 旧版
/// <c>net/neoforged/forge</c>），最终版本 id 也只能从 installer 的
/// <c>install_profile.json</c> 的 <c>version</c> 字段读（如 <c>neoforge-21.4.123</c>），不能自己拼。
/// </summary>
public interface INeoForgeInstaller
{
    /// <summary>
    /// 安装指定 NeoForge 版本，返回 installer 写出的版本 id。
    /// 已存在 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 时直接复用，不重复跑 installer。
    /// </summary>
    Task<string> InstallAsync(
        string minecraftVersion,
        string neoForgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default);
}
