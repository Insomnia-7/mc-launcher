using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;

internal static class HomeNativeChecks
{
    public static async Task RunAsync(MainViewModel model, MainWindow window, string root)
    {
        var passed=0;
        void Check(string text,bool okay){if(!okay)throw new InvalidOperationException(text);passed++;Console.WriteLine("PASS "+text);}
        T Find<T>(string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        void Capture(string name)
        {
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));
            bitmap.Render(window);bitmap.Save(Path.Combine(root,name+".png"),PngBitmapEncoderOptions.Default);
        }
        await Task.Delay(600);
        Check("原生窗口默认显示首页和真实12个实例",model.IsHomePage && model.HomeEntries.Count==12 && Find<TextBlock>("TitleEdition").Text=="主页");
        Check("原生首页保留实色背景",Find<AnimatedContentHost>("PageHost").Background is ISolidColorBrush{Color.A:255});
        foreach(var (width,height,minimum,name,theme) in new[]{(1366,768,5,"native-home-1366-light",ThemeMode.Light),(1440,900,8,"native-home-1440-dark",ThemeMode.Dark)})
        {
            window.Width=width;window.Height=height;model.SetThemeMode(theme);await Task.Delay(300);
            var page=Find<Grid>("PageContent");
            Check($"原生{width} 横幅高88且顶部间距8",Find<Border>("HomeHeroBanner").Bounds.Height==88 && Math.Abs(Find<Border>("HomeHeroBanner").TranslatePoint(default,page)!.Value.Y-8)<1);
            Check($"原生{width} 首屏显示至少{minimum}条完整实例",Math.Floor(Find<ScrollViewer>("HomeInstancesScroll").Viewport.Height/50)>=minimum);
            Check($"原生{width} 六个快捷入口与底栏完整显示",Find<Grid>("HomeShortcuts").GetVisualDescendants().OfType<Button>().Count()==6 && Find<Grid>("HomeFooter").TranslatePoint(new Point(0,28),page)!.Value.Y<=page.Bounds.Height+1);
            Capture(name);
        }
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");
        var host=Find<AnimatedContentHost>("PageHost");
        var first=new HomeView{DataContext=model};var loaded=false;var initial=false;var interactive=false;
        first.Loaded+=(_,_)=>
        {
            loaded=true;
            initial=first.FindControl<Grid>("HomeFooter")!.Opacity==0 && first.FindControl<Border>("HomeHeroBanner")!.RenderTransform!.Value.M32==8;
            interactive=first.FindControl<Button>("HomeLaunchButton")!.IsEffectivelyEnabled && first.FindControl<Button>("HomeLaunchButton")!.IsHitTestVisible && first.FindControl<Border>("HomeQuickLaunchCard")!.IsHitTestVisible;
        };
        host.Content=first;
        for(var i=0;i<20 && !loaded;i++)await Task.Delay(15);
        Check("真实首页按七段顺序等待首次入场",loaded && initial);
        Check("首次入场等待期间启动入口可立即交互",interactive);
        var footer=first.FindControl<Grid>("HomeFooter")!;var intermediate=false;
        for(var i=0;i<35;i++){await Task.Delay(16);if(footer.Opacity>0 && footer.Opacity<1 && footer.RenderTransform!.Value.M32>0){intermediate=true;break;}}
        Check("真实首页底部段出现淡入和位移中间帧",intermediate);
        await Task.Delay(500);
        Check("七段结束后首页与页标全部归位",footer.Opacity==1 && footer.RenderTransform!.Value.M32==0 && Find<TextBlock>("TitleEdition").Opacity==1);
        var second=new HomeView{DataContext=model};second.Loaded+=(_,_)=>model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");host.Content=second;
        await Task.Delay(120);
        Check("首页入场中途减少动画立即显示全部模块",second.FindControl<Grid>("HomeFooter")!.Opacity==1 && second.FindControl<Grid>("HomeFooter")!.RenderTransform!.Value.M32==0);
        model.ShowInstancesCommand.Execute(null);await Task.Delay(80);model.ShowHomeCommand.Execute(null);await Task.Delay(160);
        Check("快速往返复用正式首页并不重播入场",host.Content is HomeView && !ReferenceEquals(host.Content,second) && Find<Grid>("HomeFooter").Opacity==1 && host.Children.OfType<Avalonia.Controls.Presenters.ContentPresenter>().Count(p=>p.Content is not null)==1);
        window.Width=760;window.Height=620;await Task.Delay(250);
        Check("原生最小窗口提供滚动且没有横向溢出",Find<ScrollViewer>("HomeScroll").Extent.Width<=Find<ScrollViewer>("HomeScroll").Viewport.Width+1);
        Capture("native-home-760");
        Console.WriteLine($"{passed} native home checks passed");
    }
}
