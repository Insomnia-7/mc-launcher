using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;

internal static class WorkbenchNativeChecks
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
    private static nint Pack(PixelPoint point)=>(nint)((point.X&0xffff)|((point.Y&0xffff)<<16));
    public static async Task Run(MainViewModel model,MainWindow window,string root)
    {
        var passed=0;
        void Check(string message,bool result){if(!result)throw new InvalidOperationException(message);passed++;Console.WriteLine("PASS "+message);}
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        var handle=window.TryGetPlatformHandle()!.Handle;
        nint Hit(Control control)=>SendMessage(handle,0x84,0,Pack(control.PointToScreen(new Point(control.Bounds.Width/2,control.Bounds.Height/2))));
        void Capture(string name,double scale=1)
        {
            window.UpdateLayout();
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width*scale),(int)(window.ClientSize.Height*scale)),new Vector(96*scale,96*scale));
            bitmap.Render(window);bitmap.Save(Path.Combine(root,name+".png"),PngBitmapEncoderOptions.Default);
        }
        await Task.Delay(350);window.Width=1440;window.Height=900;model.ShowHomeCommand.Execute(null);await Task.Delay(250);
        Check("M6 原生标题栏52 DIP",Find<Border>("WindowHeader").Bounds.Height==52);
        Check("M6 拖动区命中HTCAPTION",Hit(Find<Border>("TitleDragArea"))==2);
        Check("M6 账号与主题按钮命中客户区",Hit(Find<Button>("HeaderAccountButton"))==1 && Hit(Find<Button>("ThemeSwitchButton"))==1);
        Check("M6 最大化按钮保留系统命中",Hit(Find<Button>("MaximizeButton"))==9);
        GetWindowRect(handle,out var frame);
        var left=Enumerable.Range(0,10).Select(x=>SendMessage(handle,0x84,0,Pack(new PixelPoint(frame.Left+x,(frame.Top+frame.Bottom)/2))));
        Check("M6 原生边缘可缩放",left.Contains((nint)10));
        model.SelectThemeModeCommand.Execute("dark");await Task.Delay(250);Capture("a-home-1440-dark");
        model.SelectThemeModeCommand.Execute("light");await Task.Delay(250);Capture("a-home-1440-light");
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");
        model.IsSidebarCollapsed=true;await Task.Delay(70);window.UpdateLayout();
        var folding=Find<Border>("SidebarPanel");
        Check("M6 侧栏收起具有真实中间帧",folding.Bounds.Width>64 && folding.Bounds.Width<216);
        Check("M6 内容跟随侧栏的实际过渡宽度",Math.Abs(Find<Grid>("PageContent").TranslatePoint(default,window)!.Value.X-folding.Bounds.Width)<2);
        await Task.Delay(240);window.UpdateLayout();Check("M6 收起结束释放全部可用宽度",Math.Abs(Find<Grid>("PageContent").TranslatePoint(default,window)!.Value.X-64)<2);
        model.IsSidebarCollapsed=false;await Task.Delay(300);
        var drag=Find<Border>("TitleDragArea").PointToScreen(new Point(180,20));SendMessage(handle,0xA3,2,Pack(drag));await Task.Delay(250);
        Check("M6 标题栏双击最大化",window.WindowState==WindowState.Maximized);
        Check("M6 最大化处理外边界",!Find<Border>("WindowOutline").IsVisible && Find<Grid>("Shell").Margin==window.OffScreenMargin);
        window.WindowState=WindowState.Normal;await Task.Delay(200);Check("M6 还原恢复边界",Find<Border>("WindowOutline").IsVisible);
        window.WindowState=WindowState.Minimized;await Task.Delay(120);Check("M6 最小化状态正确",window.WindowState==WindowState.Minimized);window.WindowState=WindowState.Normal;await Task.Delay(120);
        foreach(var size in new[]{new Size(900,560),new Size(1366,768),new Size(1920,1080)})
        {
            window.Width=size.Width;window.Height=size.Height;await Task.Delay(250);
            window.UpdateLayout();
            if(size.Width==900)
            {
                var sidebar=Find<Border>("SidebarPanel");
                var content=Find<Grid>("PageContent");
                Check("M6 原生紧凑侧栏贴合左边界",sidebar.TranslatePoint(default,window) is {X:>=0 and <2});
                Check("M6 原生收起导航释放内容宽度",content.Bounds.Width>=window.ClientSize.Width-66);
            }
            var launch=Find<Button>("HomeLaunchButton");launch.BringIntoView();await Task.Delay(60);
            var pos=launch.TranslatePoint(default,window)!.Value;
            Check($"M6 原生{size.Width}启动入口可达",pos.X>=0 && pos.X+launch.Bounds.Width<=window.ClientSize.Width && pos.Y+launch.Bounds.Height<=window.ClientSize.Height);
            Capture("a-home-"+size.Width);
        }
        window.Width=1440;window.Height=900;await Task.Delay(150);
        foreach(var scale in new[]{1.25,1.5,2.0}){Capture("a-render-"+(int)(scale*100),scale);Check($"M6 {scale}倍渲染生成",File.Exists(Path.Combine(root,"a-render-"+(int)(scale*100)+".png")));}
        File.WriteAllText(Path.Combine(root,"native-a-acceptance.json"),JsonSerializer.Serialize(new{passed,os=RuntimeInformation.OSDescription,actualOsScaling=window.RenderScaling,scope="Current Windows monitor native hit testing and window behavior; client sizes and render scales are not physical display / OS DPI tests",pending=new[]{"Windows 10","125/150/200% real OS scaling","Manual drag/eight resize gestures and Snap hover flyout","Real remote multiplayer session"}},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{passed} native A checks passed");
    }
}
