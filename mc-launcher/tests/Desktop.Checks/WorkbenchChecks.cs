using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;

internal static class WorkbenchChecks
{
    public static void Shell(MainViewModel model, MainWindow window, Action<int> pump, Action<string,bool> check, Action<string,bool> capture, Action<Control> click)
    {
        T Find<T>(string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        window.Width=1440; window.Height=900; model.IsSidebarCollapsed=false; model.ShowHomeCommand.Execute(null); pump(250);
        check("M1 主导航宽度为216 DIP", Math.Abs(Find<Border>("SidebarPanel").Bounds.Width-216)<1);
        check("M1 标题栏高度52且有独立账号入口", Find<Border>("WindowHeader").Bounds.Height==52 && Find<Button>("HeaderAccountButton").IsEffectivelyVisible);
        check("M1 角色入口从普通侧栏移出", !Find<Button>("WardrobeEntry").IsVisible);
        check("M1 下载和设置紧随发现资源", Find<Button>("TaskCenterEntry").TranslatePoint(default,window)!.Value.Y > Find<Button>("DiscoverNav").TranslatePoint(default,window)!.Value.Y);
        click(Find<Button>("LibraryNav")); check("M1 实例导航连接真实页面",model.IsLibraryPage);
        click(Find<Button>("MultiplayerNav")); check("M1 联机导航连接真实页面",model.IsMultiplayerPage);
        click(Find<Button>("TaskCenterEntry")); check("M1 下载导航连接任务页",model.IsTaskPage);
        click(Find<Button>("SettingsEntry")); check("M1 设置导航连接设置页",model.IsSettingsPage);
        model.ShowHomeCommand.Execute(null); model.IsSidebarCollapsed=true; pump(300);
        check("M1 用户收起后为64 DIP",Math.Abs(Find<Border>("SidebarPanel").Bounds.Width-64)<1);
        window.Width=900; window.Height=560; pump(250);
        check("M1 紧凑窗口导航保持可用", Find<Button>("HomeNav").IsEffectivelyVisible && Find<Button>("SettingsEntry").IsEffectivelyVisible);
        model.IsSidebarCollapsed=false;window.Width=1440;window.Height=900;pump(200);
        model.SelectThemeModeCommand.Execute("dark");pump(250);capture("m1-shell-dark",true);
        var dark=(SolidColorBrush)Avalonia.Application.Current!.FindResource("BgBrush")!;
        check("M1 A方案深色基础色应用",dark.Color==Color.Parse("#1D1F2B"));
        model.SelectThemeModeCommand.Execute("light");pump(250);capture("m1-shell-light",true);
        var light=(SolidColorBrush)Avalonia.Application.Current!.FindResource("BgBrush")!;
        check("M1 A方案浅色基础色应用",light.Color==Color.Parse("#EFEEFB"));
        Find<Button>("HomeNav").Focus();pump(30);check("M1 导航可获得键盘焦点", Find<Button>("HomeNav").IsFocused);
    }
    public static void Home(MainViewModel model, MainWindow window, TestLauncher launcher, Action<int> pump, Action<string,bool> check, Action<string,bool> capture, Action<Control> click, string root)
    {
        T Find<T>(string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        model.ShowHomeCommand.Execute(null);window.Width=1440;window.Height=900;pump(250);
        check("M2 主页独立于实例过滤器", model.HomeEntries.Count==12 && model.RecentHomeEntries.Count==4);
        model.LibraryQuery="missing-query";check("M2 实例过滤不影响主页选择器",model.LibraryEntries.Count==0 && Find<ComboBox>("QuickInstanceSelector").ItemCount==12);
        var selected=model.HomeEntries[4];Find<ComboBox>("QuickInstanceSelector").SelectedItem=selected;pump(60);
        check("M2 选择实例更新启动目标且不触发启动",model.SelectedInstance?.InstanceId==selected.Instance.InstanceId && launcher.Starts==0);
        check("M2 当前名称与账号在启动区可见",Find<TextBlock>("HomeSelectedName").Text==selected.Name && Find<Button>("HomeAccountEntry").IsEffectivelyVisible);
        model.LibraryQuery="";click(Find<Button>("HomeLaunchButton"));pump(600);
        check("M2 主按钮调用现有启动链路",launcher.Starts==1);
        check("M2 启动后更新真实最近记录",model.HomeLastPlayedText.StartsWith("最近游玩"));
        click(Find<Button>("HomeRuntimeToggle"));check("M2 运行环境按需展开",Find<Border>("HomeRuntimePanel").IsVisible);
        model.SelectThemeModeCommand.Execute("dark");pump(200);capture("m2-home-1440-dark",true);
        model.SelectThemeModeCommand.Execute("light");pump(200);capture("m2-home-1440-light",true);
        foreach(var size in new[]{new Size(1366,768),new Size(900,560),new Size(1920,1080)})
        {
            window.Width=size.Width;window.Height=size.Height;pump(200);
            var button=Find<Button>("HomeLaunchButton");button.BringIntoView();pump(80);
            var pos=button.TranslatePoint(default,window)!.Value;
            check($"M2 {size.Width}宽度启动按钮可见可点击",button.Bounds.Width>=110 && pos.X>=0 && pos.X+button.Bounds.Width<=window.ClientSize.Width && pos.Y+button.Bounds.Height<=window.ClientSize.Height);
            capture($"m2-home-{size.Width}",true);
        }
        window.Width=1440;window.Height=900;model.ShowInstancesCommand.Execute(null);model.SetLibraryLayoutCommand.Execute("list");pump(250);
        check("M2 实例页使用列表与按需详情结构",Find<ListBox>("LibraryList").IsVisible && Find<Border>("LibraryInspector").IsVisible);
        var starts=launcher.Starts;var entry=model.LibraryEntries[2];Find<ListBox>("LibraryList").SelectedItem=entry;pump(50);
        check("M2 列表选择只改变当前实例",model.SelectedInstance?.InstanceId==entry.Instance.InstanceId && launcher.Starts==starts && !model.IsDetailOpen);
        model.LibraryQuery="1.21.12";pump(60);check("M2 搜索匹配Minecraft版本",model.LibraryEntries.Count==1);model.LibraryQuery="nothing";pump(60);check("M2 无结果状态清楚区分于无实例",model.LibraryHasNoResults && model.HasInstances);model.LibraryQuery="";pump(100);capture("m2-library",true);
        model.OpenGameCommand.Execute(entry);pump(200);check("M2 管理动作打开当前实例详情",model.IsDetailOpen && model.SelectedInstance?.InstanceId==entry.Instance.InstanceId);
        model.ShowHomeCommand.Execute(null);pump(180);click(Find<Button>("HomeNewInstance"));check("M2 安装入口直达发现资源",model.IsDiscoverPage&&model.Download.IsVersionMode&&!model.HasOverlay);model.ShowHomeCommand.Execute(null);
        model.ShowHomeCommand.Execute(null);var instances=model.Instances.ToArray();model.Instances.Clear();pump(200);
        check("M2 无实例出现首次安装和导入引导",Find<Button>("HomeCreateFirstButton").IsEffectivelyVisible && Find<Button>("HomeImportFirstButton").IsEffectivelyVisible);
        capture("m2-first-use",true);foreach(var instance in instances)model.Instances.Add(instance);pump(200);
        model.ShowInstancesCommand.Execute(null);pump(180);check("M2 恢复实例后详情侧板随数据重新可见",Find<Border>("LibraryInspector").IsVisible);
    }
    public static void Consistency(MainViewModel model,MainWindow window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        var pages=new (string Name,Action Open)[]{("home",()=>model.ShowHomeCommand.Execute(null)),("library",()=>model.ShowInstancesCommand.Execute(null)),("detail",()=>model.OpenGameCommand.Execute(model.HomeEntries[0])),("multiplayer",()=>model.OpenMultiplayerCommand.Execute(null)),("resources",()=>model.ShowDownloadsCommand.Execute(null)),("tasks",()=>model.OpenTaskCenterCommand.Execute(null)),("wardrobe",()=>model.OpenWardrobeCommand.Execute(null)),("settings",()=>model.OpenAppearanceCommand.Execute(null))};
        foreach(var theme in new[]{"light","dark"})
        {
            model.SelectThemeModeCommand.Execute(theme);window.Width=1440;window.Height=900;pump(250);
            foreach(var page in pages)
            {
                page.Open();pump(220);var host=Find<Launcher.Desktop.Controls.AnimatedContentHost>("PageHost");
                check($"M5 {theme} {page.Name}页面挂载可见",host.Content is Control content && content.IsEffectivelyVisible && content.Bounds.Width>100);
                if(page.Name=="wardrobe")check($"M5 {theme}角色页不显示隐藏侧栏项的指示条",!Find<Border>("NavIndicator").IsVisible);
                if(page.Name=="detail")
                {
                    var button=Find<Button>("SaveInstancePresentationButton");
                    var text=button.GetVisualDescendants().OfType<TextBlock>().First();
                    check($"M5 {theme}字符串按钮文字继承主按钮前景",text.Foreground is ISolidColorBrush foreground && button.Foreground is ISolidColorBrush actual && foreground.Color==actual.Color);
                }
                model.Toasts.Clear();capture("m5-"+page.Name+"-"+theme,true);
            }
            double L(Color c){double C(byte value){var v=value/255d;return v<=0.04045?v/12.92:Math.Pow((v+0.055)/1.055,2.4);}return 0.2126*C(c.R)+0.7152*C(c.G)+0.0722*C(c.B);}
            double Contrast(string a,string b){var x=L(((SolidColorBrush)Avalonia.Application.Current!.FindResource(a)!).Color);var y=L(((SolidColorBrush)Avalonia.Application.Current!.FindResource(b)!).Color);return (Math.Max(x,y)+0.05)/(Math.Min(x,y)+0.05);}
            check($"M5 {theme}正文与次要文字对比度",Contrast("TextBrush","CardBrush")>=4.5 && Contrast("MutedBrush","CardBrush")>=4.5);
            check($"M5 {theme}主按钮对比度",Contrast("PrimaryTextBrush","PrimaryFillBrush")>=4.5);
        }
        model.ShowHomeCommand.Execute(null);model.OpenAccountsCommand.Execute(null);pump(200);check("M5 账号弹层保留底层页面",model.IsAccountsOpen && model.IsHomePage);capture("m5-accounts",true);model.CloseAccountsCommand.Execute(null);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");
        for(var i=0;i<8;i++){pages[i%pages.Length].Open();pump(35);}
        model.ShowHomeCommand.Execute(null);pump(400);check("M5 快速切页最终启动区可交互",Find<Button>("HomeLaunchButton").IsEffectivelyVisible && Find<Button>("HomeLaunchButton").IsEffectivelyEnabled);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");check("M5 减少动画立即生效",!MotionPreferences.Enabled && window.Classes.Contains("reduced-motion"));
        window.Width=900;window.Height=560;
        foreach(var page in pages){page.Open();pump(160);check($"M5 紧凑{page.Name}标题栏与导航在客户区内",Find<Button>("CloseWindowButton").TranslatePoint(default,window) is {X:>=0,Y:>=0} p && p.X+Find<Button>("CloseWindowButton").Bounds.Width<=window.ClientSize.Width && Find<Button>("HomeNav").IsEffectivelyVisible);capture("m5-compact-"+page.Name,true);}
        model.ShowHomeCommand.Execute(null);
    }
}
