using Launcher.Application.Settings;

namespace Launcher.App.Appearance;

// Keep the persisted IDs compatible with the existing appearance store ("aurora" = Dark, "glacier" = Light).
internal static class DesktopThemes
{
    public const string LightId = "glacier";
    public const string DarkId = "aurora";

    public static ThemePreset Dark { get; } = new(DarkId, Create(light: false));
    public static ThemePreset Light { get; } = new(LightId, Create(light: true));

    public static IReadOnlyList<ThemePreset> Choices { get; } = [Dark, Light];

    public static ThemeDefinition ForVariant(bool light) => light ? Light.Definition : Dark.Definition;

    private static ThemeDefinition Create(bool light) => light
        ? new(1, "浅色 · 云母清灰", new()
        {
            ["BgBrush"] = "#F7F8FA",
            ["PanelBrush"] = "#F1F2F5",
            ["CardBrush"] = "#FFFFFF",
            ["FieldBrush"] = "#FAFAFC",
            ["HoverBrush"] = "#EBEDF2",
            ["LineBrush"] = "#E5E7ED",
            ["TextBrush"] = "#1D2028",
            ["MutedBrush"] = "#626A78",
            ["FaintBrush"] = "#89909D",
            ["AccentBrush"] = "#6E60DB",
            ["AccentHoverBrush"] = "#5F52CB",
            ["AccentFillBrush"] = "#6E60DB",
            ["AccentPressedBrush"] = "#5044B9",
            ["SelectedBrush"] = "#EEEBFF",
            ["SuccessBrush"] = "#269B65",
            ["WarnBrush"] = "#B98225",
            ["DangerBrush"] = "#D34F5D",
            ["DangerSoftBrush"] = "#1ED34F5D",
            ["OverlayBrush"] = "#7A12151C",
            ["WallpaperTintBrush"] = "#4DF7F8FA"
        })
        : new(1, "深色 · 星夜蓝灰", new()
        {
            ["BgBrush"] = "#101114",
            ["PanelBrush"] = "#14161B",
            ["CardBrush"] = "#191C22",
            ["FieldBrush"] = "#1F232B",
            ["HoverBrush"] = "#262B35",
            ["LineBrush"] = "#2A2E37",
            ["TextBrush"] = "#F1F2F5",
            ["MutedBrush"] = "#A0A4AF",
            ["FaintBrush"] = "#737987",
            ["AccentBrush"] = "#8B7CFF",
            ["AccentHoverBrush"] = "#9A8DFF",
            ["AccentFillBrush"] = "#6B5CE0",
            ["AccentPressedBrush"] = "#5D4ED1",
            ["SelectedBrush"] = "#292440",
            ["SuccessBrush"] = "#4FBF83",
            ["WarnBrush"] = "#E6B65C",
            ["DangerBrush"] = "#E06C75",
            ["DangerSoftBrush"] = "#24E06C75",
            ["OverlayBrush"] = "#A608090C",
            ["WallpaperTintBrush"] = "#52101114"
        });
}
