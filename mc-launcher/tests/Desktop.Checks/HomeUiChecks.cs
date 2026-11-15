using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;

internal static class HomeUiChecks
{
    public static void Run(MainViewModel model, MainWindow window, MultiplayerFixture multiplayer, TestLauncher launcher,
        Action<int> pump, Action<string,bool> check, Action<string,bool> capture, Action<Control> click, Func<int> requests, string root)
    {
        T Find<T>(string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        void Wait(Task task){var clock=Stopwatch.StartNew();while(!task.IsCompleted && clock.ElapsedMilliseconds<15000)pump(10);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();pump(100);}
        var host=Find<AnimatedContentHost>("PageHost");var home=host.Content;
        check("默认进入真实首页并显示主页标题",model.IsHomePage && home is HomeView && Find<TextBlock>("TitleEdition").Text=="主页");
        check("主页导航连接命令并成为唯一选中入口",Find<Button>("HomeNav").Command==model.ShowHomeCommand && Find<Button>("HomeNav").Classes.Contains("active") && !Find<Button>("LibraryNav").Classes.Contains("active"));
        check("首页不主动扫描联机或发起联网请求",requests()==0 && multiplayer.Scans==0 && multiplayer.Queries==0);
        check("未检测时如实显示未测速",model.HomeLatencyText=="未测速" && model.Multiplayer.LastPingMs is null && !model.HomeHasLatency);
        check("未安装组件不显示在线成功",model.HomeMultiplayerStateText=="待安装组件" && !model.Multiplayer.HasSession);
        check("实例下拉和首页列表来自完整真实库",model.HomeEntries.Count==12 && ReferenceEquals(Find<ComboBox>("QuickInstanceSelector").ItemsSource,model.HomeEntries) && ReferenceEquals(Find<ItemsControl>("HomeInstancesList").ItemsSource,model.HomeEntries));
        var selected=model.HomeEntries.Last();Find<ComboBox>("QuickInstanceSelector").SelectedItem=selected;pump(60);
        check("下拉选择同步主启动实例",model.SelectedInstance?.InstanceId==selected.Instance.InstanceId && ReferenceEquals(model.SelectedHomeEntry,selected));
        model.LibraryQuery="不会命中的搜索条件";pump(80);
        check("实例页搜索不隐藏首页可启动实例",model.LibraryEntries.Count==0 && model.HomeEntries.Count==12 && Find<ComboBox>("QuickInstanceSelector").ItemCount==12);
        click(Find<Button>("HomeAllInstances"));
        check("全部实例进入原完整管理页并保留其筛选",model.IsLibraryPage && model.LibraryEntries.Count==0 && host.Content is LibraryView);
        model.ShowHomeCommand.Execute(null);pump(250);
        check("返回复用首页并保留所选实例",ReferenceEquals(home,host.Content) && model.SelectedInstance?.InstanceId==selected.Instance.InstanceId);
        model.LibraryQuery="";pump(60);
        foreach(var (width,height,minimum,name) in new[]{(1366,768,5,"home-1366-light"),(1440,900,8,"home-1440-dark")})
        {
            window.Width=width;window.Height=height;model.SetThemeMode(name.Contains("dark")?ThemeMode.Dark:ThemeMode.Light);pump(250);
            var hero=Find<Border>("HomeHeroBanner");var page=Find<Grid>("PageContent");
            check($"{width}×{height} 横幅定高88且距内容顶部8",hero.Bounds.Height==88 && Math.Abs(hero.TranslatePoint(default,page)!.Value.Y-8)<1);
            check($"{width}×{height} 首行定高144、双栏比例1.65",Find<Grid>("HomeTopRow").Bounds.Height==144 && Math.Abs(Find<Border>("HomeQuickLaunchCard").Bounds.Width/Find<Border>("HomeNetworkStatusCard").Bounds.Width-1.65)<0.02);
            var view=Find<ScrollViewer>("HomeInstancesScroll");
            check($"{width}×{height} 首屏至少完整显示{minimum}条实例",Math.Floor(view.Viewport.Height/50)>=minimum);
            check($"{width}×{height} 六个快捷按钮与底栏完整可见",Find<Grid>("HomeShortcuts").GetVisualDescendants().OfType<Button>().Count()==6 && Find<Grid>("HomeFooter").TranslatePoint(new Point(0,28),page)!.Value.Y<=page.Bounds.Height+1 && Find<ScrollViewer>("HomeScroll").ScrollBarMaximum.Y<1);
            capture(name,true);
        }
        window.Width=760;window.Height=620;pump(200);
        check("最小窗口提供纵向滚动且不横向溢出",Find<ScrollViewer>("HomeScroll").Extent.Width<=Find<ScrollViewer>("HomeScroll").Viewport.Width+1 && Find<Grid>("HomeShortcuts").GetVisualDescendants().OfType<Button>().All(b=>b.Bounds.Height>=42));
        capture("home-760",true);
        window.Width=1366;window.Height=768;pump(150);
        var scrolling=Find<ScrollViewer>("HomeInstancesScroll");scrolling.Offset=new Vector(0,100);pump(30);var offset=scrolling.Offset;
        model.ShowInstancesCommand.Execute(null);pump(100);model.ShowHomeCommand.Execute(null);pump(120);
        check("首页实例列表往返保留滚动",Find<ScrollViewer>("HomeInstancesScroll").Offset==offset);
        scrolling.Offset=default;pump(30);
        check("启动与取消使用既有业务命令",Find<Button>("HomeLaunchButton").Command==model.LaunchCommand && Find<Button>("HomeCancelLaunch").Command==model.CancelCommand);
        model.IsBusy=true;model.ProgressPercent=37;model.StatusText="正在准备测试环境";pump(30);
        check("准备中显示真实状态与进度",Find<Grid>("HomeLaunchProgress").IsVisible && Find<Grid>("HomeLaunchProgress").GetVisualDescendants().OfType<ProgressBar>().Single().Value==37 && model.LaunchTitle=="正在准备…");
        model.IsBusy=false;pump(30);
        var starts=launcher.Starts;var launchEntry=model.HomeEntries.First();Wait(model.QuickLaunchEntryCommand.ExecuteAsync(launchEntry));
        check("行内启动实际委托启动链路并选择该实例",launcher.Starts==starts+1 && model.SelectedInstance?.InstanceId==launchEntry.Instance.InstanceId);
        check("真实启动后首页最近游玩记录更新",model.HomeLastPlayedText.StartsWith("最近游玩") && model.HomeEntries.Any(e=>e.Instance.InstanceId==launchEntry.Instance.InstanceId && e.LastPlayed is not null));
        var entry=model.HomeEntries[1];var more=Find<ItemsControl>("HomeInstancesList").GetVisualDescendants().OfType<Button>().First(b=>ReferenceEquals(b.DataContext,entry) && b.Content as string=="•••");
        more.BringIntoView();pump(120);
        var hit=window.InputHitTest(more.TranslatePoint(new Point(more.Bounds.Width/2,more.Bounds.Height/2),window)!.Value) as Visual;
        check("列表滚动条不遮挡右侧实例操作按钮",ReferenceEquals(hit,more) || hit?.GetVisualAncestors().Contains(more)==true);
        click(more);var menu=(MenuFlyout)FlyoutBase.GetAttachedFlyout(more)!;var items=menu.Items.OfType<MenuItem>().ToArray();
        check("实例菜单提供六项真实操作并携带当前行实例",items.Length==6 && items.All(i=>ReferenceEquals(i.CommandParameter,entry)) && items.All(i=>i.Command is not null));
        menu.Hide();items.Single(i=>i.Header as string=="删除实例").Command!.Execute(entry);pump(60);
        check("删除操作先选择目标行并进入原确认流程",model.IsConfirmOpen && model.SelectedInstance?.InstanceId==entry.Instance.InstanceId && model.Instances.Count==12);
        model.ConfirmCancelCommand.Execute(null);pump(50);
        model.CloneHomeEntryCommand.Execute(entry);pump(150);
        check("创建副本进入既有副本编辑器",model.IsCloneEditorOpen && model.SelectedInstance?.InstanceId==entry.Instance.InstanceId);
        model.CloseCloneEditorCommand.Execute(null);model.ShowHomeCommand.Execute(null);pump(200);
        click(Find<Button>("HomeManageMods"));check("管理模组打开所选实例模组页",model.IsDetailOpen && model.IsModsDetailTab && model.SelectedInstance?.InstanceId==entry.Instance.InstanceId);
        model.ShowHomeCommand.Execute(null);pump(180);click(Find<Button>("HomeDiagnostics"));check("日志与诊断打开所选实例诊断页",model.IsDetailOpen && model.IsDiagnosticsDetailTab);
        model.ShowHomeCommand.Execute(null);pump(150);
        check("资源包、目录和导入快捷入口连接现有命令",Find<Button>("HomeManageResources").Command==model.OpenGameResourceFolderCommand && Find<Button>("HomeManageResources").CommandParameter as string=="resourcepacks" && Find<Button>("HomeOpenGameDirectory").Command==model.OpenGameDirectoryCommand && Find<Button>("HomeImportModpack").Command==model.ImportModpackCommand);
        click(Find<Button>("HomeNewInstance"));check("新建实例打开已有安装向导",model.IsSetupOpen);model.CloseSetupCommand.Execute(null);pump(60);
        click(Find<Button>("HomeNetworkEntry"));check("网络状态直达设置网络页",model.IsNetworkOpen && model.IsPreferencesOpen);model.ShowHomeCommand.Execute(null);pump(120);
        var runtimeSummary=model.HomeNetworkSummary;model.ProxyModeIndex=2;model.ProxyAddress="http://127.0.0.1:45678";model.SaveNetworkCommand.Execute(null);
        check("保存待重启代理不冒充运行中的网络模式",model.HomeNetworkSummary==runtimeSummary);
        model.DownloadSourceIndex=model.DownloadSourceIndex==1?0:1;pump(20);
        check("下载源切换立即同步真实首页状态",model.HomeNetworkSummary.Contains(model.DownloadSourceIndex==1?"BMCLAPI":"官方源"));
        Wait(model.Multiplayer.InstallEngineCommand.ExecuteAsync(null));check("组件安装完成自动更新首页状态",model.HomeMultiplayerStateText=="组件已就绪");
        model.Multiplayer.ConnectAddress="127.0.0.1:25565";Wait(model.Multiplayer.CheckConnectionCommand.ExecuteAsync(null));
        check("连接检测回传实测16ms且首页同步",model.Multiplayer.LastPingMs==16 && model.HomeLatencyText=="优秀 (16 ms)");
        model.Multiplayer.ConnectAddress="127.0.0.1:25566";check("更换检测目标立即清除旧延迟",model.Multiplayer.LastPingMs is null && model.HomeLatencyText=="未测速");
        multiplayer.FailQuery=true;Wait(model.Multiplayer.CheckConnectionCommand.ExecuteAsync(null));multiplayer.FailQuery=false;
        check("检测失败不保留成功延迟",model.Multiplayer.LastPingMs is null && !model.HomeHasLatency);
        model.Multiplayer.HostPort="25565";model.Multiplayer.PeerAddress="tcp://127.0.0.1:11010";Wait(model.Multiplayer.CreateRoomCommand.ExecuteAsync(null));
        check("真实房间会话建立后首页显示共享中",model.Multiplayer.HasSession && model.HomeMultiplayerStateText=="房间共享中");
        Wait(model.Multiplayer.DisconnectCommand.ExecuteAsync(null));check("断开房间同步清空延迟并恢复就绪",!model.Multiplayer.HasSession && model.Multiplayer.LastPingMs is null && model.HomeMultiplayerStateText=="组件已就绪");
        var saved=model.Instances.ToArray();model.Instances.Clear();model.SelectedInstance=null;pump(120);
        check("空库显示真实引导和新建主操作",Find<StackPanel>("HomeEmptyLibrary").IsVisible && Find<Button>("HomeCreateFirstButton").IsVisible && !Find<Button>("HomeLaunchButton").IsVisible);
        check("空库禁用资源目录操作而保留新建和导入",!Find<Button>("HomeManageResources").IsEffectivelyEnabled && !Find<Button>("HomeOpenGameDirectory").IsEffectivelyEnabled && Find<Button>("HomeNewInstance").IsEffectivelyEnabled && Find<Button>("HomeImportModpack").IsEffectivelyEnabled);
        capture("home-empty",true);click(Find<Button>("HomeManageMods"));check("空库管理模组进入下载中心模组分类",model.IsDiscoverPage && model.Download.SelectedCategory?.Category==Launcher.Domain.Content.DownloadCategory.Mod);
        foreach(var instance in saved)model.Instances.Add(instance);model.SelectedInstance=saved[0];model.ShowHomeCommand.Execute(null);pump(180);
        check("恢复实例后首页下拉、列表和总数一起更新",model.HomeEntries.Count==12 && Find<ComboBox>("QuickInstanceSelector").ItemCount==12 && model.AllInstancesLinkText=="全部实例 (12) ›");
    }
}
