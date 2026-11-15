using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

internal static class SidebarUiChecks
{
    public static void Run(MainViewModel model, MainWindow window, Action<int> pump,
        Action<string,bool> check, Action<string,bool> capture, Action<Control> click, string data)
    {
        T Find<T>(string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        var sidebar=Find<Border>("SidebarPanel");var text=Find<StackPanel>("SidebarNavText");
        var nav=Find<Button>("LibraryNav");var navText=nav.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Text=="游戏实例");
        var indicator=Find<Border>("NavIndicator");var selection=Find<Border>("NavSelection");
        double Y()=>((TranslateTransform)indicator.RenderTransform!).Y;
        double Target(string name)=>Find<Button>(name).TranslatePoint(new Point(0,(Find<Button>(name).Bounds.Height-20)/2),Find<StackPanel>("NavList"))!.Value.Y;
        bool Sample(Func<bool> predicate,int maxMs=180){for(var i=0;i<maxMs/10;i++){pump(10);if(predicate())return true;}return false;}
        check("保留全部六个真实业务导航",new[]{"LibraryNav","DiscoverNav","MultiplayerNav","WardrobeEntry","TaskCenterEntry","SettingsEntry"}.All(n=>Find<Button>(n).Command is not null));
        check("展开态严格 240px 且显示品牌",sidebar.Width==240&&Find<Image>("SidebarAppIcon").IsEffectivelyVisible);
        check("展开态抑制重复模块提示",!ToolTip.GetServiceEnabled(nav));
        check("唯一共享指示条为 3×20",window.GetVisualDescendants().Count(c=>c.Name=="NavIndicator")==1&&indicator.Width==3&&indicator.Height==20);
        check("选中胶囊独立共享且不拦截点击",window.GetVisualDescendants().Count(c=>c.Name=="NavSelection")==1&&!selection.IsHitTestVisible&&selection.IsVisible);
        check("空闲任务角标不伪造数量",model.SidebarTaskCount==0&&!Find<Border>("SidebarTaskBadge").IsVisible);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");pump(50);
        var originalNavY=nav.TranslatePoint(default,sidebar)!.Value.Y;
        model.IsSidebarCollapsed=true;
        check("收起开始保留文字布局",text.IsEffectivelyVisible&&text.Opacity>0.9);
        check("收起文字实际经过中间透明度",Sample(()=>text.Opacity>0.05&&text.Opacity<0.95));
        check("导航文字也在渐隐",navText.Opacity<1&&navText.Opacity>=0);
        pump(280);
        check("收起结束为 72px，文字隐藏",sidebar.Width==72&&!navText.IsEffectivelyVisible&&text.Opacity==0);
        check("收起前后导航纵向位置稳定",Math.Abs(nav.TranslatePoint(default,sidebar)!.Value.Y-originalNavY)<1);
        var icon=nav.GetVisualDescendants().OfType<UiIcon>().Single();
        check("收起态图标水平居中",Math.Abs(icon.TranslatePoint(new Point(icon.Bounds.Width/2,0),sidebar)!.Value.X-sidebar.Bounds.Width/2)<2);
        check("收起态保留原始品牌和独立折叠键",Find<Image>("SidebarAppIconCollapsed").IsEffectivelyVisible&&Find<ToggleButton>("SidebarToggleCollapsed").IsEffectivelyVisible);
        check("收起态模块提示延迟 450ms",ToolTip.GetServiceEnabled(nav)&&ToolTip.GetShowDelay(nav)==450&&ToolTip.GetTip(nav) as string=="游戏实例");
        check("收起态账号入口为 32×32 且可见",Find<Button>("SidebarAccountCompact").IsEffectivelyVisible&&Find<Button>("SidebarAccountCompact").Bounds.Size==new Size(32,32));
        click(Find<Button>("SidebarAccountCompact"));check("紧凑账号入口连接原账号管理",model.IsAccountsOpen);model.CloseAccountsCommand.Execute(null);pump(40);
        capture("sidebar-compact",true);
        model.IsSidebarCollapsed=false;
        check("展开先恢复布局，文字等待扩宽后显示",text.IsVisible&&text.Opacity<0.05);pump(35);
        check("展开文字实际经过中间透明度",Sample(()=>text.Opacity>0.05&&text.Opacity<0.95));pump(300);
        check("展开结束文字恢复且紧凑账号隐藏",sidebar.Width==240&&text.Opacity==1&&!Find<Button>("SidebarAccountCompact").IsEffectivelyVisible);
        check("展开后导航文字位移归零",navText.RenderTransform is TranslateTransform {X:0});
        model.IsSidebarCollapsed=true;pump(35);var reversingWidth=sidebar.Width;
        model.IsSidebarCollapsed=false;
        check("反向点击从当前宽度衔接，不跳回端点",Math.Abs(sidebar.Width-reversingWidth)<2);pump(300);
        for(var i=0;i<13;i++){model.IsSidebarCollapsed=!model.IsSidebarCollapsed;pump(12);}
        model.IsSidebarCollapsed=false;pump(350);
        check("连续反向切换最终展开且无残留透明文字",sidebar.Width==240&&text.Opacity==1&&navText.Opacity==1&&navText.IsEffectivelyVisible);
        check("最终展开偏好已写盘",!AppearanceStore.Read(data).SidebarCollapsed);
        var before=Y();model.OpenTaskCenterCommand.Execute(null);
        check("导航指示条实际滑过中间位置",Sample(()=>Y()>before+1&&Y()<Target("TaskCenterEntry")-1));pump(230);
        check("指示条与胶囊落在同一任务入口",Math.Abs(Y()-Target("TaskCenterEntry"))<1&&Math.Abs(((TranslateTransform)selection.RenderTransform!).Y-Find<Button>("TaskCenterEntry").Bounds.Y)<1);
        model.OpenWardrobeCommand.Execute(null);pump(20);model.OpenAppearanceCommand.Execute(null);pump(20);model.ShowInstancesCommand.Execute(null);pump(300);
        check("快速导航后唯一指示条对准最终页面",model.IsLibraryPage&&Math.Abs(Y()-Target("LibraryNav"))<1);
        model.IsSidebarCollapsed=true;pump(35);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");pump(10);
        check("动画途中关闭动效立即落到紧凑终态",sidebar.Width==72&&text.Opacity==0&&sidebar.Transitions is null);
        model.IsSidebarCollapsed=false;pump(10);
        check("减少动画时展开立即完成",sidebar.Width==240&&text.Opacity==1);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");model.IsSidebarCollapsed=true;pump(30);window.Width=860;pump(30);
        check("收起中缩为窄窗口不会残留侧栏",!sidebar.IsEffectivelyVisible&&Find<Button>("SidebarHamburger").IsEffectivelyVisible);
        click(Find<Button>("SidebarHamburger"));pump(250);
        check("窄窗口浮层保留完整导航与文字",sidebar.IsEffectivelyVisible&&sidebar.Width==240&&navText.IsEffectivelyVisible&&navText.Opacity==1&&Find<Border>("SidebarScrim").IsVisible);
        capture("sidebar-overlay",true);
        click(Find<Button>("WardrobeEntry"));pump(100);
        check("浮层选择页面后自动收起",model.IsRolePage&&!sidebar.IsEffectivelyVisible&&!Find<Border>("SidebarScrim").IsVisible);
        window.Width=1180;pump(100);
        check("回到宽窗口保留用户收起偏好",sidebar.Width==72&&AppearanceStore.Read(data).SidebarCollapsed);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");model.IsSidebarCollapsed=false;model.ShowInstancesCommand.Execute(null);pump(80);
        model.SetThemeMode(ThemeMode.Dark);pump(100);capture("sidebar-expanded-dark",true);
        model.SetThemeMode(ThemeMode.Light);pump(100);capture("sidebar-expanded-light",true);
        check("侧栏主题切换保持导航与唯一高亮",selection.IsVisible&&indicator.IsVisible&&model.IsLibraryPage);
    }

    public static async Task RunNativeAsync(MainViewModel model,MainWindow window,string root)
    {
        T Find<T>(string name) where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        var passed=0;
        void Check(string name,bool value){if(!value)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");model.IsSidebarCollapsed=false;await Task.Delay(300);
        var text=Find<StackPanel>("SidebarNavText");var sidebar=Find<Border>("SidebarPanel");
        model.IsSidebarCollapsed=true;await Task.Delay(45);
        Check("原生窗口收起文字存在渐隐中间帧",text.Opacity is >0 and <1);
        await Task.Delay(300);
        Check("原生窗口收起后 72px 且账号可见",sidebar.Width==72&&Find<Button>("SidebarAccountCompact").IsEffectivelyVisible);
        model.IsSidebarCollapsed=false;await Task.Delay(35);Check("原生展开文字先等待扩宽",text.Opacity<0.05);
        await Task.Delay(80);Check("原生窗口展开文字实际渐入",text.Opacity is >0 and <1);await Task.Delay(250);
        var indicator=Find<Border>("NavIndicator");var from=((TranslateTransform)indicator.RenderTransform!).Y;
        model.OpenTaskCenterCommand.Execute(null);await Task.Delay(35);
        var target=Find<Button>("TaskCenterEntry").Bounds.Y+(Find<Button>("TaskCenterEntry").Bounds.Height-20)/2;
        Check("原生选中条经过两入口间的中间位置",((TranslateTransform)indicator.RenderTransform!).Y>from&&((TranslateTransform)indicator.RenderTransform!).Y<target);
        await Task.Delay(240);Check("原生选中条准确落位",Math.Abs(((TranslateTransform)indicator.RenderTransform!).Y-target)<1);
        for(var i=0;i<12;i++){model.IsSidebarCollapsed=!model.IsSidebarCollapsed;await Task.Delay(15);}
        model.IsSidebarCollapsed=false;await Task.Delay(350);Check("原生连续折叠反向后文字与宽度恢复",text.Opacity==1&&sidebar.Width==240);
        model.IsSidebarCollapsed=true;await Task.Delay(30);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        Check("原生动画中停用动效即时归位",text.Opacity==0&&sidebar.Width==72);
        using var image=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));
        image.Render(window);image.Save(Path.Combine(root,"native-sidebar-compact.png"),PngBitmapEncoderOptions.Default);
        File.WriteAllText(Path.Combine(root,"native-sidebar.json"),System.Text.Json.JsonSerializer.Serialize(new{passed,scaling=window.RenderScaling}));
        Console.WriteLine($"{passed} native sidebar checks passed");
    }
}
