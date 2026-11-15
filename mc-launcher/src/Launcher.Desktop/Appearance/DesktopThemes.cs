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
            ["BgBrush"] = "#EFEEFB",
            ["PanelBrush"] = "#F1F1FA",
            ["CardBrush"] = "#FAF9FF",
            ["FieldBrush"] = "#EFEDFC",
            ["HoverBrush"] = "#E6E2FA",
            ["LineBrush"] = "#E5E1F6",
            ["TextBrush"] = "#202035",
            ["MutedBrush"] = "#656180",
            ["FaintBrush"] = "#89909D",
            ["AccentBrush"] = "#6456E8",
            ["AccentHoverBrush"] = "#786AF2",
            ["AccentFillBrush"] = "#6456E8",
            ["AccentPressedBrush"] = "#5143CA",
            ["SelectedBrush"] = "#E2DDFB",
            ["SuccessBrush"] = "#269B65",
            ["WarnBrush"] = "#B98225",
            ["DangerBrush"] = "#D34F5D",
            ["DangerSoftBrush"] = "#1ED34F5D",
            ["OverlayBrush"] = "#7A12151C",
            ["WallpaperTintBrush"] = "#4DF7F8FA"
        })
        : new(1, "深色 · 静夜", new()
        {
            ["BgBrush"] = "#1D1F2B",
            ["PanelBrush"] = "#232532",
            ["CardBrush"] = "#2B2D3F",
            ["FieldBrush"] = "#34364B",
            ["HoverBrush"] = "#3B3D56",
            ["LineBrush"] = "#3B3D55",
            ["TextBrush"] = "#F1F1FA",
            ["MutedBrush"] = "#B9B8CD",
            ["FaintBrush"] = "#737987",
            ["AccentBrush"] = "#A99BFF",
            ["AccentHoverBrush"] = "#7E70F3",
            ["AccentFillBrush"] = "#6758E8",
            ["AccentPressedBrush"] = "#5143CA",
            ["SelectedBrush"] = "#3B375E",
            ["SuccessBrush"] = "#4FBF83",
            ["WarnBrush"] = "#E6B65C",
            ["DangerBrush"] = "#E06C75",
            ["DangerSoftBrush"] = "#24E06C75",
            ["OverlayBrush"] = "#A608090C",
            ["WallpaperTintBrush"] = "#52101114"
        });
}
