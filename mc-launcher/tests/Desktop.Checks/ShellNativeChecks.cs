using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;
using Microsoft.Extensions.DependencyInjection;

internal static class ShellNativeChecks
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private static nint Pack(PixelPoint point) => (nint)((point.X & 0xffff) | ((point.Y & 0xffff) << 16));

    public static int Start(string root, bool sidebar = false, bool pageMotion = false, bool home = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This check uses Windows native hit testing.");
        Environment.SetEnvironmentVariable("MCLAUNCHER_HOME", Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable("MCLAUNCHER_GAME_ROOT", Path.Combine(root, "game"));
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        AppPaths.EnsureCreated();
        if (home)
        {
            foreach (var number in Enumerable.Range(1,12))
            {
                var id="1.21."+number;var directory=Path.Combine(AppPaths.GameRoot,"versions",id);Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory,id+".json"),JsonSerializer.Serialize(new{id,type="release",mainClass="net.minecraft.client.main.Main",libraries=Array.Empty<object>()}));
                File.WriteAllBytes(Path.Combine(directory,id+".jar"),[80,75,3,4]);
            }
        }
        using var services = LauncherServices.Create();
        var model = services.GetRequiredService<MainViewModel>();
        using var stop = new CancellationTokenSource();
        var window = new MainWindow { DataContext = model, Width = 1180, Height = 800 };
        PlatformUi.Window = window;
        var passed = 0; var exit = 1;
        void Check(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("PASS " + name);
        }
        T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
        if (sidebar || pageMotion) model.ShowInstancesCommand.Execute(null);
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (home) { await HomeNativeChecks.RunAsync(model,window,root); exit=0; return; }
                await Task.Delay(350);
                if (pageMotion) { await PageMotionChecks.RunNativeAsync(model, window, root); exit = 0; return; }
                if (sidebar) { await SidebarUiChecks.RunNativeAsync(model, window, root); exit = 0; return; }
                var handle = window.TryGetPlatformHandle()!.Handle;
                nint Hit(Control control) => SendMessage(handle, 0x84, 0, Pack(control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2))));
                Check("真实 Windows 标题栏高 38px", Find<Border>("WindowHeader").Bounds.Height == 38);
                Check("标题栏原生拖动命中 HTCAPTION", Hit(Find<Border>("TitleDragArea")) == 2);
                Check("主题按钮命中 HTCLIENT，不触发拖动", Hit(Find<Button>("ThemeSwitchButton")) == 1);
                Check("最大化按钮命中 HTMAXBUTTON，保留 Snap Layouts", Hit(Find<Button>("MaximizeButton")) == 9);
                GetWindowRect(handle, out var frame);
                var edgeHits = Enumerable.Range(0, 10).Select(offset => SendMessage(handle, 0x84, 0, Pack(new PixelPoint(frame.Left + offset, (frame.Top + frame.Bottom) / 2)))).ToArray();
                Console.WriteLine("Native frame edge hits: " + string.Join(",", edgeHits));
                Check("窗口左边缘保留原生缩放命中", edgeHits.Contains((nint)10));
                Check("实际材质启用后窗口底色透明", !window.Classes.Contains("mica") || window.Background is ISolidColorBrush { Color.A: 0 });
                Check("实际材质仅影响外壳，主内容仍为实色", Find<Grid>("PageContent").Background is ISolidColorBrush { Color.A: 255 } && (!window.Classes.Contains("mica") || Find<Border>("WindowHeader").Background is ISolidColorBrush { Color.A: < 255 }));
                var selectedPage = model.ShellPage;
                foreach (var mode in new[] { ThemeMode.Light, ThemeMode.System, ThemeMode.Dark })
                {
                    model.SetThemeMode(mode); await Task.Delay(250);
                    Check($"原生窗口 {mode} 模式切换保留页面", model.CurrentThemeMode == mode && model.ShellPage == selectedPage && window.TryGetPlatformHandle()!.Handle == handle);
                }
                using (var screenshot = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)))
                { screenshot.Render(window); screenshot.Save(Path.Combine(root, "native-shell-dark.png"), PngBitmapEncoderOptions.Default); }
                var dragPoint = Find<Border>("TitleDragArea").PointToScreen(new Point(180, 18));
                SendMessage(handle, 0xA3, 2, Pack(dragPoint)); await Task.Delay(250);
                Check("双击标题栏由系统最大化", window.WindowState == WindowState.Maximized);
                Check("最大化隐藏内边框并保留 OffScreenMargin", !Find<Border>("WindowOutline").IsVisible && Find<Grid>("Shell").Margin == window.OffScreenMargin);
                var bounds = Find<Button>("CloseWindowButton").TranslatePoint(new Point(42, 32), window)!.Value;
                Check("最大化窗口控制按钮未超出可见客户区", bounds.X <= window.ClientSize.Width + 1 && bounds.Y <= window.ClientSize.Height + 1);
                window.WindowState = WindowState.Normal; await Task.Delay(200);
                Check("还原恢复细边框和窗口控制入口", Find<Border>("WindowOutline").IsVisible && Avalonia.Automation.AutomationProperties.GetName(Find<Button>("MaximizeButton")) == "最大化");
                window.WindowState = WindowState.Minimized; await Task.Delay(120);
                Check("原生窗口可最小化", window.WindowState == WindowState.Minimized);
                window.WindowState = WindowState.Normal; await Task.Delay(150);
                window.Width = 760; window.Height = 620; await Task.Delay(150);
                Check("760px 原生窗口仍有可拖动区", Find<Border>("TitleDragArea").Bounds.Width > 200);
                File.WriteAllText(Path.Combine(root, "native-shell.json"), JsonSerializer.Serialize(new
                {
                    passed, scaling = window.RenderScaling, actualTransparency = window.ActualTransparencyLevel.ToString(),
                    materialApplied = window.Classes.Contains("mica"), scope = "Native Windows hit testing, theme changes, double-click maximize, restore, minimize and 760px layout; current monitor DPI only."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"{passed} native shell checks passed"); exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                window.Close(); model.FlushAppearance(); model.Multiplayer?.Dispose(); PlatformUi.Window = null; stop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return exit;
    }
}
