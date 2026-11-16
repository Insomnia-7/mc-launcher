namespace Launcher.Domain.Versions;

/// <summary>Explicit official event IDs; ordinary April snapshots must not be classified by date alone.</summary>
public static class VersionClassification
{
    private static readonly HashSet<string> EventIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "2.0", "2.0_red", "2.0_blue", "2.0_purple", "15w14a", "1.RV-Pre1",
        "3D Shareware v1.34", "20w14infinite", "22w13oneblockatatime", "23w13a_or_b",
        "24w14potato", "25w14craftmine", "26w14a"
    };
    public static bool IsEvent(string id) => EventIds.Contains(id);
    public static string Group(string id, string type) => IsEvent(id) ? "愚人节版本" : type switch
    {
        "release" => "正式版", "snapshot" => "预览版", "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha", _ => "其他"
    };
    public static IReadOnlyList<string> Groups { get; } = ["正式版", "预览版", "愚人节版本", "远古 Beta", "远古 Alpha", "其他"];
    public static string EventDescription(string id) => id.ToLowerInvariant() switch
    {
        "2.0" or "2.0_red" or "2.0_blue" or "2.0_purple" => "2013 · 玩笑版 2.0：体验超级敌对模式等夸张的实验玩法。",
        "15w14a" => "2015 · 爱与拥抱：收集爱心，在一个更和平的世界中冒险。",
        "1.rv-pre1" => "2016 · 潮流科技：体验智能手表、现实眼镜与悬浮滑板。",
        "3d shareware v1.34" => "2019 · 复古共享软件：回到像素迷宫与怀旧射击游戏的年代。",
        "20w14infinite" => "2020 · 无限维度：将书投入传送门，探索由文字生成的奇异世界。",
        "22w13oneblockatatime" => "2022 · 一次一个方块：告别传统物品栏，用双手搬运、投掷与合成。",
        "23w13a_or_b" => "2023 · 投票更新：用一次次投票改变规则，体验意想不到的世界。",
        "24w14potato" => "2024 · 毒马铃薯：前往马铃薯维度，探索新生态、工具与生物。",
        "25w14craftmine" => "2025 · Craftmine：将资源组合成矿境，收集材料并寻找出口。",
        "26w14a" => "2026 · Herdcraft：让方块与物品跟随你，在地面合成并指挥武器战斗。",
        _ => ""
    };
}
