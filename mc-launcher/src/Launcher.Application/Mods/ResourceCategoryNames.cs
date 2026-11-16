namespace Launcher.Application.Mods;

/// <summary>Display labels only; requests always retain the platform's original category ID.</summary>
public static class ResourceCategoryNames
{
    private static readonly Dictionary<string,string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure"]="冒险探索", ["cursed"]="趣味恶搞", ["decoration"]="建筑装饰",
        ["economy"]="经济交易", ["equipment"]="装备与工具", ["food"]="食物农业",
        ["game-mechanics"]="游戏机制", ["library"]="前置支持库", ["magic"]="魔法",
        ["management"]="管理辅助", ["minigame"]="小游戏", ["mobs"]="生物",
        ["optimization"]="性能优化", ["social"]="社交互动", ["storage"]="仓储整理",
        ["technology"]="科技机械", ["transportation"]="交通运输", ["utility"]="实用功能",
        ["worldgen"]="世界生成", ["challenging"]="高难挑战", ["combat"]="战斗",
        ["kitchen-sink"]="综合玩法", ["lightweight"]="轻量体验", ["multiplayer"]="多人联机",
        ["quests"]="任务引导", ["modded"]="模组适配", ["realistic"]="写实风格",
        ["simplistic"]="简约风格", ["themed"]="主题风格", ["tweaks"]="细节调整",
        ["vanilla-like"]="原版风格", ["audio"]="声音音效", ["blocks"]="方块材质",
        ["core-shaders"]="核心着色器", ["entities"]="实体外观", ["environment"]="环境景观",
        ["fonts"]="字体", ["gui"]="界面", ["items"]="物品材质", ["locale"]="语言汉化",
        ["models"]="模型", ["cartoon"]="卡通风格", ["fantasy"]="幻想风格",
        ["semi-realistic"]="半写实风格", ["atmosphere"]="大气效果", ["bloom"]="泛光",
        ["colored-lighting"]="彩色光照", ["foliage"]="植物动态", ["path-tracing"]="路径追踪",
        ["pbr"]="物理材质", ["reflections"]="反射", ["shadows"]="阴影",
        ["high"]="高负载", ["low"]="低负载", ["medium"]="中等负载",
        ["potato"]="入门配置", ["screenshot"]="截图画质",
        ["8x-"]="8×及以下", ["16x"]="16×", ["32x"]="32×", ["48x"]="48×",
        ["64x"]="64×", ["128x"]="128×", ["256x"]="256×", ["512x+"]="512×及以上",
        // CurseForge uses numeric IDs and its own English labels.
        ["Adventure and RPG"]="冒险与角色扮演", ["Adventure and Exploration"]="冒险探索",
        ["API and Library"]="前置支持库", ["Armor, Tools, and Weapons"]="装备与工具",
        ["Cosmetic"]="外观装饰", ["Education"]="教育",
        ["Farming"]="农业种植", ["World Gen"]="世界生成", ["Biomes"]="生物群系",
        ["Dimensions"]="维度", ["Ores and Resources"]="矿石与资源", ["Structures"]="建筑结构",
        ["Server Utility"]="服务器管理", ["Map and Information"]="地图与信息",
        ["Redstone"]="红石", ["Processing"]="资源处理", ["Player Transport"]="玩家运输",
        ["Energy"]="能源", ["Energy, Fluid, and Item Transport"]="能源与物品运输",
        ["Genetics"]="遗传育种", ["Automation"]="自动化", ["Twitch Integration"]="直播联动",
        ["MCreator"]="创作工具", ["Addons"]="扩展附属", ["Miscellaneous"]="其他功能",
        ["Applied Energistics 2"]="应用能源附属", ["Blood Magic"]="血魔法附属",
        ["Buildcraft"]="建筑工艺附属", ["Industrial Craft"]="工业时代附属",
        ["Tinker's Construct"]="匠魂附属", ["Thaumcraft"]="神秘时代附属",
        ["Thermal Expansion"]="热力膨胀附属", ["Forestry"]="林业附属",
        ["Skyblock"]="空岛生存", ["Small / Light"]="轻量整合", ["Extra Large"]="大型整合",
        ["Tech"]="科技", ["Exploration"]="探索", ["Hardcore"]="极限挑战",
        ["Sci-Fi"]="科幻", ["Combat / PvP"]="战斗竞技",
        ["Medieval"]="中世纪", ["Modern"]="现代风格", ["Photo Realistic"]="照片写实",
        ["Traditional"]="传统原版", ["Steampunk"]="蒸汽朋克", ["Animated"]="动态材质",
        ["Font Packs"]="字体", ["Mod Support"]="模组适配", ["Data Packs"]="数据包",
        ["512x and Higher"]="512×及以上"
    };

    public static string Translate(string name) => Names.GetValueOrDefault(name, name);
    public static string Group(string header) => header switch
    {
        "features" => "功能", "resolutions" => "分辨率", "performance impact" => "性能",
        _ => "类型"
    };
}
