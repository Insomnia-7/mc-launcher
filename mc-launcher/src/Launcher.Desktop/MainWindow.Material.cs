using Avalonia.Controls;

namespace Launcher.Desktop;

public sealed partial class MainWindow
{
    private void UpdateShellMaterialPreference()
    {
        // A uses stable opaque surfaces; material effects must not tint text or instance content.
        WindowTransparencyLevel[] levels = [WindowTransparencyLevel.None];
        if (!TransparencyLevelHint.SequenceEqual(levels)) TransparencyLevelHint = levels;
        UpdateShellMaterialState();
    }

    private void UpdateShellMaterialState() => Classes.Set("mica",
        OperatingSystem.IsWindows() && _model?.HasCustomWallpaper != true &&
        (ActualTransparencyLevel == WindowTransparencyLevel.Mica || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur));
}
