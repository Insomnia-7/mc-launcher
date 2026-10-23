using Launcher.Domain.Modpacks;

namespace Launcher.Application.Abstractions;

/// <summary>
/// Forge 系（Forge / NeoForge）的安装入口。
/// 和 Fabric 的「拉 profile 再合并」不同：Forge 必须下载 installer jar 并跑 Java，
/// 由 installer 自己解包、打补丁、写 versions/&lt;id&gt;/。
/// </summary>
public interface IForgeInstaller
{
    /// <summary>
    /// 安装指定 Forge 版本，返回版本 id（如 1.20.1-forge-47.4.18）。
    /// 已存在且带 x-launcher-parent 的版本直接复用，不重复跑 installer。
    /// </summary>
    Task<string> InstallAsync(
        string minecraftVersion,
        string forgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default);
}
