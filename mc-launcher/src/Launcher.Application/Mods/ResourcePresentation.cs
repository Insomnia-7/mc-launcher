namespace Launcher.Application.Mods;

public sealed record ResourceTranslation(string ProjectId, string Original, string Translated);

public interface IResourcePresentationClient
{
    Task<IReadOnlyList<ResourceTranslation>> GetTranslationsAsync(string source, IReadOnlyList<string> projectIds, CancellationToken ct=default);
    Task<byte[]?> GetIconAsync(string url, CancellationToken ct=default);
}

/// <summary>UI labels and a small built-in glossary; unknown projects always keep their original names.</summary>
public static class ResourceChinese
{
    private static readonly Dictionary<string,(string Name,string Summary)> Glossary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sodium"]=("钠","优化游戏渲染，提高帧率并减少卡顿。"),
        ["iris"]=("Iris 光影","为游戏提供光影包加载支持。"),
        ["iris-shaders"]=("Iris 光影","为游戏提供光影包加载支持。"),
        ["lithium"]=("锂","优化游戏逻辑，减少运算开销。"),
        ["ferrite-core"]=("铁氧体磁芯","减少游戏与模组的内存占用。"),
        ["ferritecore"]=("铁氧体磁芯","减少游戏与模组的内存占用。"),
        ["entityculling"]=("实体渲染机制优化","跳过被遮挡实体的渲染，提高帧率。"),
        ["entity-culling"]=("实体渲染机制优化","跳过被遮挡实体的渲染，提高帧率。"),
        ["modmenu"]=("模组菜单","在游戏中查看模组列表与配置入口。"),
        ["mod-menu"]=("模组菜单","在游戏中查看模组列表与配置入口。"),
        ["appleskin"]=("苹果皮","显示食物、饥饿值与饱和度信息。"),
        ["fabric-api"]=("Fabric API","许多 Fabric 模组需要的基础接口库。"),
        ["cloth-config"]=("Cloth 配置库","为模组提供配置界面，常作为必需前置。"),
        ["cloth-config-api"]=("Cloth 配置库","为模组提供配置界面，常作为必需前置。"),
        ["architectury-api"]=("Architectury API","帮助模组适配不同加载器的基础接口库。"),
        ["jei"]=("JEI 物品管理器","查询物品、配方和用途。"),
        ["just-enough-items"]=("JEI 物品管理器","查询物品、配方和用途。"),
        ["rei"]=("REI 物品管理器","查询物品、配方和用途。"),
        ["roughly-enough-items"]=("REI 物品管理器","查询物品、配方和用途。"),
        ["emi"]=("EMI 物品与配方管理器","浏览物品与配方，规划合成材料。"),
        ["jade"]=("玉","显示准星指向的方块或实体信息。"),
        ["wthit"]=("这是什么","显示方块与实体的信息。"),
        ["journeymap"]=("旅行地图","提供小地图与世界地图。"),
        ["xaeros-minimap"]=("Xaero 的小地图","提供小地图与路径点。"),
        ["xaeros-world-map"]=("Xaero 的世界地图","记录探索过的区域，显示世界地图。"),
        ["create"]=("机械动力","使用旋转动力搭建机械与自动化设施。"),
        ["waystones"]=("传送石碑","在已激活的传送石碑之间快速移动。"),
        ["mouse-tweaks"]=("鼠标手势","改善背包中的物品拖动与整理操作。"),
        ["inventory-profiles-next"]=("背包整理","提供物品整理与背包操作功能。"),
        ["modernfix"]=("现代化修复","优化加载速度与内存使用，修复部分兼容问题。"),
        ["immediatelyfast"]=("立即优化","优化游戏中的即时渲染。"),
        ["dynamic-fps"]=("动态帧率","在游戏处于后台时降低资源占用。"),
        ["no-chat-reports"]=("聊天举报禁用","提供聊天签名与举报相关设置。"),
        ["continuity"]=("连续纹理","为兼容的资源包提供连续纹理支持。"),
        ["indium"]=("铟","为部分钠版本补充 Fabric 渲染接口兼容支持。"),
        ["yacl"]=("YetAnotherConfigLib 配置库","为模组提供配置界面。"),
        ["yet-another-config-lib"]=("YetAnotherConfigLib 配置库","为模组提供配置界面。"),
    };
    public static (string? Name,string? Summary) Lookup(string slug,string original)
    {
        if(Glossary.TryGetValue(slug,out var found)) return found;
        var normalized=original.ToLowerInvariant().Replace(' ','-');
        return Glossary.TryGetValue(normalized,out found) ? found : (null,null);
    }
    public static string SearchQuery(string query)
    {
        var hit=Glossary.FirstOrDefault(p=>p.Value.Name==query);
        return hit.Key ?? query;
    }
    public static bool HasChinese(string value)=>value.Any(c=>c is >= '\u3400' and <= '\u9fff');
    public static string Loader(string value)=>value.ToLowerInvariant() switch
    { "fabric"=>"Fabric", "quilt"=>"Quilt", "forge"=>"Forge", "neoforge"=>"NeoForge", "liteloader"=>"LiteLoader", "datapack"=>"数据包", "minecraft"=>"原版", "iris"=>"Iris", "optifine"=>"OptiFine", _=>value };
    public static bool IsLoader(string value)=>value.ToLowerInvariant() is "fabric" or "quilt" or "forge" or "neoforge" or "liteloader";
    public static string Count(double value)=>value switch { >=100_000_000=>$"{value/100_000_000:0.#} 亿", >=10_000=>$"{value/10_000:0.#} 万", _=>$"{value:0}" };
    public static string Date(DateTimeOffset? value)=>value?.ToLocalTime().ToString("yyyy-MM-dd")??"未标注";
    public static string Loaders(IEnumerable<string> values,bool usesLoader)
    {
        if(!usesLoader) return "不限定加载器";
        var names=values.Where(IsLoader).Distinct(StringComparer.OrdinalIgnoreCase).Select(Loader).ToArray();
        return names.Length>0 ? string.Join(" / ",names) : usesLoader ? "加载器未标注" : "不限定加载器";
    }
}
