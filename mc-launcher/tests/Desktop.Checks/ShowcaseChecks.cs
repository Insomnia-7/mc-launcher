using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Settings;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views.Dev;

internal static class ShowcaseChecks
{
    private static T Find<T>(Control root,string name) where T:Control=>root.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    private static UiShowcaseView View(Control root)=>root.GetVisualDescendants().OfType<UiShowcaseView>().Single();
    private static void Shortcut(Window window){window.KeyPress(Key.D,RawInputModifiers.Control|RawInputModifiers.Shift,PhysicalKey.D,null);window.KeyRelease(Key.D,RawInputModifiers.Control|RawInputModifiers.Shift,PhysicalKey.D,null);}
    public static void Disabled(MainViewModel model,MainWindow window,Action<int> pump,Action<string,bool> check)
    {
        check("普通模式隐藏开发入口",!model.IsDevShowcaseEnabled&&!Find<Button>(window,"ShowcaseNav").IsEffectivelyVisible);
        check("普通模式不注册开发快捷键",!window.KeyBindings.Any(k=>k.Command==model.OpenShowcaseCommand));
        check("普通模式开发命令不可执行",!model.OpenShowcaseCommand.CanExecute(null));
        var page=model.ShellPage;model.OpenShowcaseCommand.Execute(null);Shortcut(window);pump(40);
        check("普通模式调用命令与快捷键均不改变页面",model.ShellPage==page&&!model.IsShowcasePage);
        model.IsShowcaseOpen=true;pump(40);
        check("普通模式即使修改开关也不挂载预览页",model.ShellPage==page&&!window.GetVisualDescendants().OfType<UiShowcaseView>().Any());
        model.IsShowcaseOpen=false;
        check("普通模式保留七个业务入口",Find<StackPanel>(window,"NavList").Children.OfType<Button>().Count(b=>b.IsEffectivelyVisible)==7);
    }
    public static void Run(MainViewModel model,MainWindow window,MultiplayerFixture network,TestLauncher launcher,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,Action<Control> click,Func<int> requests)
    {
        void Wait(Task task){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.ElapsedMilliseconds<5000)pump(10);if(!task.IsCompleted)throw new TimeoutException("预览操作超时");task.GetAwaiter().GetResult();pump(30);}
        var networkBefore=requests();var starts=launcher.Starts;var instance=model.SelectedInstance;
        check("开发模式显示独立入口",model.IsDevShowcaseEnabled&&Find<Button>(window,"ShowcaseNav").IsEffectivelyVisible);
        check("开发模式注册 Ctrl+Shift+D",window.KeyBindings.Any(k=>k.Command==model.OpenShowcaseCommand&&k.Gesture?.Key==Key.D&&k.Gesture.KeyModifiers==(KeyModifiers.Control|KeyModifiers.Shift)));
        click(Find<Button>(window,"ShowcaseNav"));pump(100);var view=View(window);var vm=view.Model!;
        check("开发入口挂载独立预览页并更新标题",model.IsShowcasePage&&model.ShellPageTitle=="UI 预览 · Dev"&&ReferenceEquals(vm.Host,model));
        check("预览页成为唯一选中导航",Find<StackPanel>(window,"NavList").Children.OfType<Button>().Count(b=>b.Classes.Contains("active"))==1&&Find<Button>(window,"ShowcaseNav").Classes.Contains("active"));
        check("八态样本实际使用控件状态",Find<Button>(view,"ShowcaseHover").Classes.Contains(":pointerover")&&Find<Button>(view,"ShowcasePressed").Classes.Contains(":pressed")&&Find<Button>(view,"ShowcaseFocused").Classes.Contains(":focus-visible"));
        check("焦点样本显示 2px 焦点环",Find<Border>(Find<Button>(view,"ShowcaseFocused"),"FocusRing") is {IsVisible:true,BorderThickness.Left:2});
        check("禁用和按下样本复用实际视觉",Find<Button>(view,"ShowcaseDisabled").Opacity==0.45&&Math.Abs(Find<Button>(view,"ShowcasePressed").RenderTransform!.Value.M11-0.98)<0.001);
        check("加载成功错误样本复用反馈图标",ButtonFeedback.GetState(Find<Button>(view,"ShowcaseLoading"))==FeedbackState.Loading&&ButtonFeedback.GetState(Find<Button>(view,"ShowcaseSuccess"))==FeedbackState.Success&&ButtonFeedback.GetState(Find<Button>(view,"ShowcaseError"))==FeedbackState.Error);
        check("Tooltip 保持实际 450ms 延迟",ToolTip.GetShowDelay(Find<Button>(view,"ShowcasePrimary"))==450&&ToolTip.GetTip(Find<Button>(view,"ShowcasePrimary")) is string);
        click(Find<Button>(view,"ShowcaseDark"));check("深色按钮联动实际外壳",model.CurrentThemeMode==ThemeMode.Dark&&!model.IsLightTheme);
        click(Find<Button>(view,"ShowcaseLight"));check("浅色按钮联动实际外壳",model.CurrentThemeMode==ThemeMode.Light&&model.IsLightTheme);
        click(Find<Button>(view,"ShowcaseSystem"));check("跟随系统按钮联动真实主题模式",model.CurrentThemeMode==ThemeMode.System);
        model.SetThemeMode(ThemeMode.Light);window.Width=1440;window.Height=900;pump(100);capture("showcase-light",true);
        model.SetThemeMode(ThemeMode.Dark);pump(50);capture("showcase-dark",true);
        var original=model.IsSidebarCollapsed;click(Find<Button>(view,"ShowcaseSidebar"));pump(60);
        check("预览侧栏控制切换真实 72px 外壳",model.IsSidebarCollapsed!=original&&Find<Border>(window,"SidebarPanel").Width==72);
        check("收起时预览导航保留图标与 Tooltip",Find<Button>(window,"ShowcaseNav").IsEffectivelyVisible&&ToolTip.GetServiceEnabled(Find<Button>(window,"ShowcaseNav")));
        click(Find<Button>(view,"ShowcaseSidebar"));pump(50);check("预览侧栏可恢复 240px",Find<Border>(window,"SidebarPanel").Width==240);
        Find<ComboBox>(view,"ShowcaseMotion").SelectedItem=model.MotionChoices.Single(m=>m.Id=="off");pump(20);
        check("预览动效选择绑定实际减少动画偏好",!MotionPreferences.Enabled&&window.Classes.Contains("reduced-motion"));
        var demo=vm.RunDemoCommand.ExecuteAsync(null);pump(30);
        check("交互演示由实际异步命令触发加载态",vm.RunDemoCommand.IsRunning&&!Find<Button>(view,"ShowcaseRun").IsEffectivelyEnabled&&Find<Button>(view,"ShowcaseRun").Classes.Contains("is-loading"));
        Wait(demo);check("预览操作完成显示成功反馈",vm.DemoSucceeded&&!vm.DemoFailed&&Find<Button>(view,"ShowcaseRun").Classes.Contains("is-success"));
        vm.SimulateFailure=true;Wait(vm.RunDemoCommand.ExecuteAsync(null));
        check("模拟失败显示可重试的错误反馈",vm.DemoFailed&&!vm.DemoSucceeded&&Find<Button>(view,"ShowcaseRun").Classes.Contains("is-error"));
        vm.ShowAllToastsCommand.Execute(null);pump(30);
        check("四态演示通知独立于业务通知",vm.Toasts.Items.Count==4&&vm.Toasts.Items.Select(t=>t.Level).Distinct().Count()==4&&model.Toasts.Items.Count==0);
        var first=vm.Toasts.Items[0];first.InvokeActionCommand.Execute(null);first.InvokeActionCommand.Execute(null);pump(20);
        check("预览通知动作可查看并关闭单条",vm.DemoStatus=="已查看预览通知。"&&vm.Toasts.Items.Count==3&&!vm.Toasts.Items.Contains(first));
        vm.Toasts.Clear();vm.OpenDialogCommand.Execute(null);pump(50);
        check("预览弹窗使用统一 AnimatedDialog 且不打开业务向导",Find<AnimatedDialog>(view,"ShowcaseDialog").IsVisible&&!model.HasOverlay&&vm.IsDialogOpen);
        check("预览弹窗把焦点移入输入框",Find<TextBox>(view,"ShowcaseDialogInput").IsFocused);
        vm.DialogText="只用于预览";model.SetThemeMode(ThemeMode.Light);capture("showcase-dialog",true);
        vm.ConfirmDialogCommand.Execute(null);pump(30);
        check("确认预览只关闭示例弹窗并发出示例通知",!vm.IsDialogOpen&&vm.Toasts.Items.Count==1&&vm.Toasts.Items[0].IsSuccess&&!model.IsSetupOpen);
        vm.Toasts.Clear();Wait(view.ReplayEntranceAsync());
        check("减少动画下七段重播立即归位",view.Stages.Count==7&&view.Stages.All(c=>c.Opacity==1&&(c.RenderTransform?.Value.M32??0)==0));
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");var one=view.ReplayEntranceAsync();pump(10);var two=view.ReplayEntranceAsync();pump(10);var three=view.ReplayEntranceAsync();model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");Wait(Task.WhenAll(one,two,three));
        check("快速重播和中途停用动画不会残留透明阶段",view.Stages.All(c=>c.Opacity==1&&(c.RenderTransform?.Value.M32??0)==0));
        var scroll=Find<ScrollViewer>(view,"ShowcaseScroll");scroll.Offset=new Vector(0,scroll.Extent.Height);pump(60);
        var offset=scroll.Offset.Y;check("预览内容可滚动访问所有模块",offset>0&&Find<Button>(view,"ShowcaseReplay").IsEffectivelyVisible);
        window.Width=760;window.Height=620;pump(70);scroll.ScrollToEnd();pump(50);capture("showcase-compact",true);
        check("760px 预览页没有横向滚动溢出",scroll.Extent.Width<=scroll.Viewport.Width+1&&Find<Button>(view,"ShowcaseReplay").TranslatePoint(default,window) is {X:>=0} p&&p.X+Find<Button>(view,"ShowcaseReplay").Bounds.Width<=window.ClientSize.Width);
        window.Width=1180;window.Height=800;pump(50);vm.ShowAllToastsCommand.Execute(null);vm.OpenDialogCommand.Execute(null);var pending=vm.RunDemoCommand.ExecuteAsync(null);pump(20);
        model.OpenAppearanceCommand.Execute(null);pump(70);Wait(pending);
        check("离开预览取消模拟操作并清理通知和弹窗",!vm.CanPreview&&!vm.RunDemoCommand.IsRunning&&!vm.IsDialogOpen&&vm.Toasts.Items.Count==0&&!vm.DemoSucceeded&&!vm.DemoFailed);
        check("离页后示例命令不能再触发",!vm.ShowToastCommand.CanExecute("info")&&!vm.OpenDialogCommand.CanExecute(null));
        Shortcut(window);pump(100);
        check("Ctrl+Shift+D 可从业务页面返回缓存预览",model.IsShowcasePage&&ReferenceEquals(view,View(window))&&vm.CanPreview);
        check("返回缓存页面保留滚动位置和输入",Find<ScrollViewer>(view,"ShowcaseScroll").Offset.Y>0&&vm.DialogText=="只用于预览");
        check("演示全过程没有启动游戏或发起网络动作",launcher.Starts==starts&&requests()==networkBefore&&network.Scans==0&&network.Starts==0&&network.Queries==0&&ReferenceEquals(instance,model.SelectedInstance));
        model.ShowHomeCommand.Execute(null);pump(70);check("主页导航可正常退出开发预览",model.IsHomePage&&!model.IsShowcaseOpen&&!vm.CanPreview);
    }
    public static async Task RunNativeAsync(MainViewModel model,MainWindow window,string root)
    {
        var passed=0;void Check(string text,bool condition){if(!condition)throw new InvalidOperationException(text);passed++;Console.WriteLine("PASS "+text);}
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");model.OpenShowcaseCommand.Execute(null);await Task.Delay(350);
        var view=View(window);var vm=view.Model!;var scroll=Find<ScrollViewer>(view,"ShowcaseScroll");scroll.Offset=new Vector(0,scroll.Extent.Height);await Task.Delay(100);
        Check("原生窗口挂载开发预览页",model.IsShowcasePage&&vm.CanPreview&&Find<Button>(window,"ShowcaseNav").Classes.Contains("active"));
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");var replay=view.ReplayEntranceAsync();
        for(var i=0;i<12;i++){await Task.Delay(10);if(view.Stages[0].Opacity>0&&view.Stages[0].Opacity<1)break;}
        Check("原生七段存在透明度与位移中间帧",view.Stages[0].Opacity>0&&view.Stages[0].Opacity<1&&view.Stages[0].RenderTransform!.Value.M32>0);
        Check("原生七段按 40ms 错峰进入",view.Stages[0].Opacity>view.Stages[6].Opacity);
        await replay;Check("重播结束七段都归位",view.Stages.All(c=>c.Opacity==1&&(c.RenderTransform?.Value.M32??0)==0));
        var old=view.ReplayEntranceAsync();await Task.Delay(15);var next=view.ReplayEntranceAsync();await Task.Delay(15);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");await Task.WhenAll(old,next);
        Check("原生重播中减少动画立即归位",view.Stages.All(c=>c.Opacity==1&&(c.RenderTransform?.Value.M32??0)==0));
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");vm.OpenDialogCommand.Execute(null);var dialog=Find<AnimatedDialog>(view,"ShowcaseDialog");
        for(var i=0;i<15;i++){await Task.Delay(10);if(dialog.Surface!.Opacity>0&&dialog.Surface.Opacity<1)break;}
        Check("原生预览弹窗复用缩放淡入",dialog.IsVisible&&dialog.Surface!.Opacity>0&&dialog.Surface.Opacity<1&&dialog.Surface.RenderTransform!.Value.M11<1);
        vm.CloseDialogCommand.Execute(null);Check("原生预览弹窗等待退场",dialog.IsVisible&&!dialog.IsOpen);await Task.Delay(200);Check("原生预览弹窗退场后移除",!dialog.IsVisible);
        vm.ToggleSidebarCommand.Execute(null);await Task.Delay(35);var width=Find<Border>(window,"SidebarPanel").Width;
        Check("原生侧栏实际经过 240 到 72 中间宽度",width>72&&width<240);await Task.Delay(350);Check("原生侧栏最终为 72px",Find<Border>(window,"SidebarPanel").Width==72);
        var pending=vm.RunDemoCommand.ExecuteAsync(null);await Task.Delay(30);model.ShowHomeCommand.Execute(null);await Task.Delay(300);await pending;
        Check("原生离页取消异步演示并清理预览",!vm.CanPreview&&!vm.RunDemoCommand.IsRunning&&vm.Toasts.Items.Count==0&&!vm.IsDialogOpen);
        model.OpenShowcaseCommand.Execute(null);await Task.Delay(300);Check("原生返回复用页面且仍能操作",ReferenceEquals(view,View(window))&&vm.CanPreview);
        File.WriteAllText(Path.Combine(root,"showcase-native.json"),System.Text.Json.JsonSerializer.Serialize(new{passed,window.RenderScaling,scope="Development showcase navigation, native motion frames and lifecycle; current monitor DPI only."}));
        Console.WriteLine($"{passed} native showcase checks passed");
    }
}
