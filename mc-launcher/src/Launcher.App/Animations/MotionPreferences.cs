using System.Windows;

namespace Launcher.App.Animations;

public static class MotionPreferences
{
    public static string Mode { get; set; } = "smooth";
    public static bool Enabled => Mode == "smooth" || (Mode == "system" && SystemParameters.ClientAreaAnimation);
}
