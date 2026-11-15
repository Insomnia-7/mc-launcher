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
        ? new(1, "浅色 · 云白", new()
        {
            ["BgBrush"] = "#F4F5FA",
            ["PanelBrush"] = "#F1F2F7",
            ["CardBrush"] = "#FFFFFF",
            ["FieldBrush"] = "#F7F7FB",
            ["HoverBrush"] = "#E9EAF3",
            ["LineBrush"] = "#E5E5EF",
            ["TextBrush"] = "#202131",
            ["MutedBrush"] = "#666779",
            ["FaintBrush"] = "#89909D",
            ["AccentBrush"] = "#6554D9",
            ["AccentHoverBrush"] = "#5B49CE",
            ["AccentFillBrush"] = "#6554D9",
            ["AccentPressedBrush"] = "#5140BD",
            ["SelectedBrush"] = "#EEEBFF",
            ["SuccessBrush"] = "#269B65",
            ["WarnBrush"] = "#B98225",
            ["DangerBrush"] = "#D34F5D",
            ["DangerSoftBrush"] = "#1ED34F5D",
            ["OverlayBrush"] = "#7A12151C",
            ["WallpaperTintBrush"] = "#4DF7F8FA"
        })
        : new(1, "深色 · 静夜", new()
        {
            ["BgBrush"] = "#14151B",
            ["PanelBrush"] = "#191B23",
            ["CardBrush"] = "#1E2029",
            ["FieldBrush"] = "#252733",
            ["HoverBrush"] = "#2D303E",
            ["LineBrush"] = "#30323F",
            ["TextBrush"] = "#F1F2F7",
            ["MutedBrush"] = "#A5A7BA",
            ["FaintBrush"] = "#737987",
            ["AccentBrush"] = "#9B8CFF",
            ["AccentHoverBrush"] = "#AFA2FF",
            ["AccentFillBrush"] = "#9B8CFF",
            ["AccentPressedBrush"] = "#8777ED",
            ["SelectedBrush"] = "#292440",
            ["SuccessBrush"] = "#4FBF83",
            ["WarnBrush"] = "#E6B65C",
            ["DangerBrush"] = "#E06C75",
            ["DangerSoftBrush"] = "#24E06C75",
            ["OverlayBrush"] = "#A608090C",
            ["WallpaperTintBrush"] = "#52101114"
        });
}
