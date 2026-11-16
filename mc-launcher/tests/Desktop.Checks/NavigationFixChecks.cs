using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

internal static class NavigationFixChecks
{
    public static void Run(MainViewModel model,MainWindow window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        var host=Find<AnimatedContentHost>("PageHost");
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");window.Width=1280;window.Height=800;pump(300);
        var pages=new Action[]{()=>model.ShowHomeCommand.Execute(null),()=>model.ShowInstancesCommand.Execute(null),()=>model.OpenMultiplayerCommand.Execute(null),()=>model.OpenNetworkCommand.Execute(null),()=>model.ShowDownloadsCommand.Execute(null),()=>model.OpenTaskCenterCommand.Execute(null),()=>model.OpenAppearanceCommand.Execute(null)};
        foreach(var page in pages)
        {
            var previous=host.Content;page();pump(380);
            // Both fade phases and deferred visual detachment must finish before checking ownership.
            for(var attempt=0;attempt<150 && (host.IsTransitioning || host.Children.OfType<ContentPresenter>().Count(c=>c.Content is not null)>1 || !ReferenceEquals(previous,host.Content) && previous!.GetVisualAncestors().OfType<Window>().Any());attempt++)pump(10);
            check("转场结束不保留上一个页面的视觉父级",ReferenceEquals(previous,host.Content) || previous!.GetVisualAncestors().OfType<Window>().Any()==false);
            check("转场结束仅当前栏目有内容",host.Children.OfType<ContentPresenter>().Count(c=>c.Content is not null)==1);
        }
        model.IsSidebarCollapsed=true;pump(400);model.OpenTaskCenterCommand.Execute(null);pump(250);model.IsSidebarCollapsed=false;pump(600);
        var nav=Find<Button>("TaskCenterEntry");var list=Find<DockPanel>("NavList");var indicator=Find<Border>("NavIndicator");var transform=(TranslateTransform)indicator.RenderTransform!;
        var position=nav.TranslatePoint(default,list)!.Value;
        check("收起切页再展开指示条横向准确落在新宽度右缘",Math.Abs(transform.X-(position.X+nav.Bounds.Width-3))<1);
        check("收起切页再展开指示条纵向对准当前栏目",Math.Abs(transform.Y-(position.Y+(nav.Bounds.Height-indicator.Height)/2))<1);
        capture("navigation-expanded-corrected",true);
        for(var i=0;i<10;i++){model.IsSidebarCollapsed=!model.IsSidebarCollapsed;pages[i%pages.Length]();pump(15);}
        model.IsSidebarCollapsed=false;model.ShowInstancesCommand.Execute(null);pump(650);
        nav=Find<Button>("LibraryNav");position=nav.TranslatePoint(default,list)!.Value;
        check("快速折叠和切页后高亮与按钮位置一致",Math.Abs(transform.X-(position.X+nav.Bounds.Width-3))<1 && Math.Abs(transform.Y-(position.Y+(nav.Bounds.Height-indicator.Height)/2))<1);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");model.IsSidebarCollapsed=true;model.IsSidebarCollapsed=false;pump(50);
        check("关闭动画时导航条立即归位",Math.Abs(transform.X-(nav.Bounds.Width-3))<1);
    }

    public static async Task RunNative(MainViewModel model,MainWindow window,string root)
    {
        var passed=0;void Check(string text,bool value){if(!value)throw new InvalidOperationException(text);passed++;Console.WriteLine("PASS "+text);}
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        window.Width=1280;window.Height=800;model.IsSidebarCollapsed=false;model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");await Task.Delay(350);
        var host=Find<AnimatedContentHost>("PageHost");var previous=host.Content;model.ShowInstancesCommand.Execute(null);await Task.Delay(380);
        for(var i=0;i<150 && (ReferenceEquals(previous,host.Content) || host.IsTransitioning || host.Children.OfType<ContentPresenter>().Count(c=>c.Content is not null)>1 || previous!.GetVisualAncestors().OfType<Window>().Any());i++)await Task.Delay(10);
        Check("原生转场结束旧栏目脱离渲染树",previous!.GetVisualAncestors().OfType<Window>().Any()==false && host.Children.OfType<ContentPresenter>().Count(c=>c.Content is not null)==1);
        model.IsSidebarCollapsed=true;await Task.Delay(450);model.OpenTaskCenterCommand.Execute(null);await Task.Delay(250);
        var indicator=Find<Border>("NavIndicator");var transform=(TranslateTransform)indicator.RenderTransform!;var start=transform.X;
        model.IsSidebarCollapsed=false;var intermediate=false;
        for(var i=0;i<30;i++){await Task.Delay(12);window.UpdateLayout();if(transform.X>start+1 && transform.X<180)intermediate=true;}
        Check("展开导航条横向滑动存在真实中间帧",intermediate);
        await Task.Delay(300);window.UpdateLayout();var button=Find<Button>("TaskCenterEntry");var position=button.TranslatePoint(default,Find<DockPanel>("NavList"))!.Value;
        Check("原生收起切页展开后的指示条横坐标准确",Math.Abs(transform.X-(position.X+button.Bounds.Width-3))<1);
        Check("原生收起切页展开后的指示条纵坐标准确",Math.Abs(transform.Y-(position.Y+(button.Bounds.Height-indicator.Height)/2))<1);
        using(var image=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96))){image.Render(window);image.Save(Path.Combine(root,"native-navigation-corrected.png"),PngBitmapEncoderOptions.Default);}
        for(var i=0;i<8;i++){model.IsSidebarCollapsed=!model.IsSidebarCollapsed;if(i%2==0)model.ShowInstancesCommand.Execute(null);else model.OpenTaskCenterCommand.Execute(null);await Task.Delay(18);}
        model.IsSidebarCollapsed=false;model.OpenTaskCenterCommand.Execute(null);await Task.Delay(650);window.UpdateLayout();position=button.TranslatePoint(default,Find<DockPanel>("NavList"))!.Value;
        Check("原生快速折叠切页后正确落位",Math.Abs(transform.X-(position.X+button.Bounds.Width-3))<1 && Math.Abs(transform.Y-(position.Y+(button.Bounds.Height-indicator.Height)/2))<1);
        Console.WriteLine($"{passed} native navigation fix checks passed");
    }
}
