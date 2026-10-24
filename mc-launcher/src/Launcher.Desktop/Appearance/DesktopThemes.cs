using Launcher.Application.Settings;

namespace Launcher.App.Appearance;

// Keep the persisted IDs compatible with the existing appearance store.
internal static class DesktopThemes
{
    public const string LightId = "glacier";
    public const string DarkId = "aurora";
    public static IReadOnlyList<ThemePreset> Choices { get; } =
        [new(LightId, Create(true)), new(DarkId, Create(false))];

    private static ThemeDefinition Create(bool light)
    {
        var text = light ? "#191919" : "#F0F0F0";
        var fill = light ? "#202020" : "#EEEEEE";
        return new(1, light ? "日间 · 白" : "夜间 · 黑", new()
        {
            ["BgBrush"] = light ? "#F2F2F2" : "#101010",
            ["PanelBrush"] = light ? "#F9F9F9" : "#191919",
            ["CardBrush"] = light ? "#FFFFFF" : "#222222",
            ["FieldBrush"] = light ? "#EBEBEB" : "#2A2A2A",
            ["HoverBrush"] = light ? "#E0E0E0" : "#363636",
            ["LineBrush"] = light ? "#24000000" : "#28FFFFFF",
            ["TextBrush"] = text,
            ["MutedBrush"] = light ? "#626262" : "#ACACAC",
            ["FaintBrush"] = light ? "#707070" : "#999999",
            ["AccentBrush"] = text,
            ["AccentHoverBrush"] = fill,
            ["AccentFillBrush"] = fill,
            ["AccentPressedBrush"] = fill,
            ["SelectedBrush"] = light ? "#12000000" : "#18FFFFFF",
            ["SuccessBrush"] = text,
            ["WarnBrush"] = text,
            ["DangerBrush"] = text,
            ["DangerSoftBrush"] = light ? "#10000000" : "#10FFFFFF",
            ["OverlayBrush"] = "#99000000",
            ["WallpaperTintBrush"] = light ? "#45FFFFFF" : "#45000000"
        });
    }
}
