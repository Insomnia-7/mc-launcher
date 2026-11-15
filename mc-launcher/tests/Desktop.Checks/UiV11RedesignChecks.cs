using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

internal static class UiV11RedesignChecks
{
    private static T Find<T>(MainWindow window,string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    private static bool Settled(AnimatedContentHost host) => host.Children.OfType<ContentPresenter>().Count(p=>p.Content is not null)==1 &&
        host.Children.OfType<ContentPresenter>().Where(p=>p.Content is not null).All(p=>p.Opacity==1 && p.RenderTransform!.Value.M32==0);

    private static void Layout(MainViewModel model,MainWindow window,Action<string,bool> check)
    {
        var banner=Find<Border>(window,"HomeHeroBanner");
        check("首页保留固定横幅文案",banner.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="专注于更好的 MC 联机体验"));
        check("首页横幅距窗口顶部不超过52px",banner.TranslatePoint(default,window)!.Value.Y<=52);
        foreach(var name in new[]{"HomeQuickLaunchCard","HomeNetworkStatusCard","HomeInstancesCard","HomeShortcutsCard","HomeFooter"})
        {
            var control=Find<Control>(window,name);var point=control.TranslatePoint(default,window)!.Value;
            check($"{window.Width}×{window.Height} {name}完整位于首屏",control.IsEffectivelyVisible && point.X>=0 && point.Y>=38 &&
                point.X+control.Bounds.Width<=window.ClientSize.Width+1 && point.Y+control.Bounds.Height<=window.ClientSize.Height+1);
        }
        check("首页主启动按钮可操作",Find<Button>(window,"HomeLaunchButton").IsEffectivelyEnabled && Find<Button>(window,"HomeLaunchButton").Command==model.LaunchCommand);
        var scroll=Find<ScrollViewer>(window,"HomeScroll");
        check("首屏没有横向溢出",scroll.Extent.Width<=scroll.Viewport.Width+1);
        var footer=Find<Grid>(window,"HomeFooter");var bottom=footer.TranslatePoint(new Point(0,footer.Bounds.Height),window)!.Value.Y;
        check("首页底部余白不超过40px",window.ClientSize.Height-bottom is >=0 and <=40);
    }

    public static void Run(MainViewModel model,MainWindow window,Action<int> pump,Action<string,bool> check,string root)
    {
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");model.ShowHomeCommand.Execute(null);pump(250);
        model.IsSidebarCollapsed=false;pump(30);
        check("展开侧栏240px并保留草方块图标",Find<Border>(window,"SidebarPanel").Width==240 && Find<Image>(window,"SidebarAppIcon") is {IsEffectivelyVisible:true,Source:not null});
        model.IsSidebarCollapsed=true;pump(30);
        check("收起侧栏72px并保留草方块图标",Find<Border>(window,"SidebarPanel").Width==72 && Find<Image>(window,"SidebarAppIconCollapsed") is {IsEffectivelyVisible:true,Source:not null});
        model.IsSidebarCollapsed=false;
        foreach(var (width,height) in new[]{(1366,768),(1440,900),(1920,1080)})
        {
            window.Width=width;window.Height=height;pump(120);Layout(model,window,check);
            foreach(var theme in new[]{ThemeMode.Light,ThemeMode.Dark})
            {
                model.SetThemeMode(theme);pump(100);
                using var image=new RenderTargetBitmap(new PixelSize(width,height),new Vector(96,96));image.Render(window);
                image.Save(Path.Combine(root,$"acceptance-home-{width}-{theme.ToString().ToLowerInvariant()}.png"),PngBitmapEncoderOptions.Default);
            }
        }
        window.Width=1366;window.Height=768;model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");pump(250);
        var host=Find<AnimatedContentHost>(window,"PageHost");
        for(var i=0;i<10;i++){if(i%2==0)model.ShowInstancesCommand.Execute(null);else model.ShowHomeCommand.Execute(null);pump(8);}
        for(var i=0;i<6;i++){model.SetThemeMode(i%2==0?ThemeMode.Light:ThemeMode.Dark);pump(8);}
        pump(500);
        check("十次切页和六次主题切换后仅保留可见当前页",model.IsHomePage && Settled(host) && host.Opacity==1);
        model.ShowInstancesCommand.Execute(null);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        check("减少动画立即挂载窗口类并清理过渡",window.Classes.Contains("reduced-motion") && Settled(host));
        model.ShowHomeCommand.Execute(null);pump(20);
        check("减少动画切页即时完成且启动命令不阻塞",model.IsHomePage && Settled(host) && Find<Button>(window,"HomeLaunchButton").IsEffectivelyEnabled);
        foreach(var mode in new[]{ThemeMode.System,ThemeMode.Light,ThemeMode.Dark})
        {model.SetThemeMode(mode);pump(20);check($"保留{mode}主题模式和两套预设",model.CurrentThemeMode==mode && model.ThemeChoices.Count==2);}
        foreach(var scale in new[]{1d,1.25,1.5,2})
        {
            using var image=new RenderTargetBitmap(new PixelSize((int)(1366*scale),(int)(768*scale)),new Vector(96*scale,96*scale));
            image.Render(window);image.Save(Path.Combine(root,$"acceptance-render-{scale*100:0}.png"),PngBitmapEncoderOptions.Default);
            check($"{scale*100:0}%离屏渲染保持逻辑尺寸",Find<Border>(window,"WindowHeader").Bounds.Height==38 && Find<Border>(window,"SidebarPanel").Width==240);
        }
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
    private static nint Pack(int x,int y)=>(nint)((x&0xffff)|((y&0xffff)<<16));

    public static async Task RunNativeAsync(MainViewModel model,MainWindow window,string root)
    {
        var passed=0;void Check(string name,bool okay){if(!okay)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
        await Task.Delay(450);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        foreach(var (width,height) in new[]{(1366,768),(1440,900),(1920,1080)})
        {window.Width=width;window.Height=height;await Task.Delay(180);Layout(model,window,Check);}
        window.Width=1180;window.Height=800;await Task.Delay(180);
        var handle=window.TryGetPlatformHandle()!.Handle;GetWindowRect(handle,out var rect);
        var middleX=(rect.Left+rect.Right)/2;var middleY=(rect.Top+rect.Bottom)/2;
        var hits=new Dictionary<string,int>();
        foreach(var (name,x,y,expected) in new[]{("left",rect.Left+1,middleY,10),("right",rect.Right-2,middleY,11),("top",middleX,rect.Top+1,12),("bottom",middleX,rect.Bottom-2,15),
            ("top-left",rect.Left+1,rect.Top+1,13),("top-right",rect.Right-2,rect.Top+1,14),("bottom-left",rect.Left+1,rect.Bottom-2,16),("bottom-right",rect.Right-2,rect.Bottom-2,17)})
        {var actual=(int)SendMessage(handle,0x84,0,Pack(x,y));hits[name]=actual;Check($"原生{name}缩放命中{expected}",actual==expected);}
        var drag=Find<Border>(window,"TitleDragArea").PointToScreen(new Point(100,18));
        Check("标题栏空白命中系统拖动区",SendMessage(handle,0x84,0,Pack(drag.X,drag.Y))==2);
        SendMessage(handle,0xA3,2,Pack(drag.X,drag.Y));await Task.Delay(200);Check("标题栏双击原生最大化",window.WindowState==WindowState.Maximized);
        drag=Find<Border>(window,"TitleDragArea").PointToScreen(new Point(100,18));SendMessage(handle,0xA3,2,Pack(drag.X,drag.Y));await Task.Delay(200);
        Check("再次双击原生还原",window.WindowState==WindowState.Normal);
        var maximize=Find<Button>(window,"MaximizeButton").PointToScreen(new Point(21,16));
        Check("最大化按钮保留Snap系统命中",SendMessage(handle,0x84,0,Pack(maximize.X,maximize.Y))==9);
        Check("当前Windows实际启用Mica",window.ActualTransparencyLevel==WindowTransparencyLevel.Mica && window.Classes.Contains("mica"));
        window.TransparencyLevelHint=[WindowTransparencyLevel.None];await Task.Delay(180);
        Check("主动关闭材质后退回可读实色",window.ActualTransparencyLevel==WindowTransparencyLevel.None && !window.Classes.Contains("mica") && window.Background is ISolidColorBrush{Color.A:255});
        model.SetThemeMode(ThemeMode.Light);await Task.Delay(100);model.SetThemeMode(ThemeMode.Dark);await Task.Delay(100);
        File.WriteAllText(Path.Combine(root,"native-acceptance.json"),JsonSerializer.Serialize(new{passed,os=RuntimeInformation.OSDescription,scaling=window.RenderScaling,edgeHits=hits,
            nativeGestures="double-click maximize and restore",snap="HTMAXBUTTON verified; visual hover flyout pending manual check",dpi="Current monitor only; exported 125/150/200% images are render checks, not OS DPI tests",
            pending=new[]{"Manual drag and all eight resize gestures","Snap hover flyout","Windows 10 device","125/150/200% real OS scaling"}},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{passed} native redesign checks passed");
    }
}
