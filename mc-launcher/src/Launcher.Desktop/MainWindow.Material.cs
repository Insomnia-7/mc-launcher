using Avalonia.Controls;

namespace Launcher.Desktop;

public sealed partial class MainWindow
{
    private void UpdateShellMaterialPreference()
    {
        WindowTransparencyLevel[] levels = OperatingSystem.IsWindows() && _model?.HasCustomWallpaper != true
            ? [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        if (!TransparencyLevelHint.SequenceEqual(levels)) TransparencyLevelHint = levels;
        UpdateShellMaterialState();
    }

    private void UpdateShellMaterialState() => Classes.Set("mica",
        OperatingSystem.IsWindows() && _model?.HasCustomWallpaper != true &&
        (ActualTransparencyLevel == WindowTransparencyLevel.Mica || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur));
}
