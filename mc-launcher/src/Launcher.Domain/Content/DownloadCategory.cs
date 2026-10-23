namespace Launcher.Domain.Content;

/// <summary>
/// 下载中心里的资源类型。左边导航栏按它分组：
/// 游戏本体 → 模组 → 整合包 → 资源包 → 数据包 → 光影包。
/// </summary>
public enum DownloadCategory
{
    /// <summary>Minecraft 原版各版本（走 piston-meta）。</summary>
    MinecraftVersion,

    Mod,
    Modpack,
    ResourcePack,
    Datapack,
    Shader,
}

public static class DownloadCategoryExtensions
{
    public static string Title(this DownloadCategory category) => category switch
    {
        DownloadCategory.MinecraftVersion => "游戏本体",
        DownloadCategory.Mod => "模组",
        DownloadCategory.Modpack => "整合包",
        DownloadCategory.ResourcePack => "资源包",
        DownloadCategory.Datapack => "数据包",
        DownloadCategory.Shader => "光影包",
        _ => category.ToString(),
    };

    /// <summary>Modrinth 的 project_type 取值；原版版本没有。</summary>
    public static string? ModrinthProjectType(this DownloadCategory category) => category switch
    {
        DownloadCategory.Mod => "mod",
        DownloadCategory.Modpack => "modpack",
        DownloadCategory.ResourcePack => "resourcepack",
        DownloadCategory.Datapack => "datapack",
        DownloadCategory.Shader => "shader",
        _ => null,
    };

    /// <summary>CurseForge 的 classId；原版版本没有。</summary>
    public static int? CurseForgeClassId(this DownloadCategory category) => category switch
    {
        DownloadCategory.Mod => 6,
        DownloadCategory.Modpack => 4471,
        DownloadCategory.ResourcePack => 12,
        DownloadCategory.Datapack => 6945,
        DownloadCategory.Shader => 6552,
        _ => null,
    };

    /// <summary>
    /// 装进版本的哪个子目录。光影包必须落在 shaderpacks 才会被 Oculus/Iris 认出来；
    /// 数据包严格说应该进 saves/&lt;存档&gt;/datapacks，这里放在版本根让玩家自己拖。
    /// </summary>
    public static string? InstanceSubdirectory(this DownloadCategory category) => category switch
    {
        DownloadCategory.Mod => "mods",
        DownloadCategory.ResourcePack => "resourcepacks",
        DownloadCategory.Datapack => "datapacks",
        DownloadCategory.Shader => "shaderpacks",
        _ => null,
    };

    /// <summary>资源包 / 光影 / 数据包与加载器无关，只有模组和整合包要按 forge/fabric 过滤。</summary>
    public static bool UsesLoader(this DownloadCategory category) =>
        category is DownloadCategory.Mod or DownloadCategory.Modpack;

    /// <summary>模组才有"同一 mod 装两份会崩"的问题，别的类型按文件名判重就够。</summary>
    public static bool NeedsModIdGuard(this DownloadCategory category) =>
        category is DownloadCategory.Mod;
}
