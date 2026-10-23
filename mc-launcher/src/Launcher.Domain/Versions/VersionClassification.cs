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
    public static string Group(string id, string type) => IsEvent(id) ? "活动版" : type switch
    {
        "release" => "正式版", "snapshot" => "预览版", "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha", _ => "其他"
    };
    public static IReadOnlyList<string> Groups { get; } = ["正式版", "预览版", "活动版", "远古 Beta", "远古 Alpha", "其他"];
}
