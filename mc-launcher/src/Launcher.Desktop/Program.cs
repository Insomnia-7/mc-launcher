using Avalonia;

namespace Launcher.Desktop;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--launcher-update")
        { Environment.ExitCode = Launcher.Application.Update.LauncherUpdateRunner.RunAsync(args[1..]).GetAwaiter().GetResult(); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
