using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;

internal static class ComponentChecks
{
    private static T Part<T>(Control control,string name) where T:Control => control.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    public static void Run(MainViewModel model,Window main,MultiplayerFixture network,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        void Wait(Task task){var clock=Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<5000)pump(10);task.GetAwaiter().GetResult();pump(30);}
        main.Hide();
        var button=new Button{Content="执行操作",Width=180};
        var disabled=new Button{Content="不可用",IsEnabled=false};
        var success=new Button{Content="已完成"};success.Classes.Add("is-success");
        var error=new Button{Content="重试操作"};error.Classes.Add("is-error");
        var tip=new ToolTip{Content="组件提示"};ToolTip.SetTip(button,tip);
        var stack=new StackPanel{Spacing=12,Margin=new Thickness(24)};
        foreach(var item in new[]{button,disabled,success,error})stack.Children.Add(item);
        foreach(var variant in new[]{"primary","ghost","icon","danger-solid"}){var item=new Button{Content=variant};item.Classes.Add(variant);stack.Children.Add(item);}
        var close=new Button{Content="关闭弹窗"};
        var surface=new Border{Classes={"modal-surface"},Child=close,Width=300,Height=160};
        var dialog=new AnimatedDialog{ScrimBrush=Brushes.Black};dialog.Children.Add(surface);
        var grid=new Grid();grid.Children.Add(stack);grid.Children.Add(dialog);
        var fixture=new Window{Width=500,Height=600,Content=grid};fixture.Show();pump(80);
        check("四种按钮变体共用反馈模板",stack.Children.OfType<Button>().All(b=>Part<FeedbackIcon>(b,"ActionFeedback") is not null));
        check("禁用态立即为 0.45 且无过渡",disabled.Opacity==0.45&&disabled.Transitions?.Count==0);
        check("成功和错误使用独立图标与语义色",ButtonFeedback.GetState(success)==FeedbackState.Success&&ButtonFeedback.GetState(error)==FeedbackState.Error&&Part<FeedbackIcon>(success,"ActionFeedback").Foreground!=Part<FeedbackIcon>(error,"ActionFeedback").Foreground);
        button.Focus(NavigationMethod.Tab);pump(10);
        check("键盘焦点瞬时显示 2px 焦点环",Part<Border>(button,"FocusRing") is {IsVisible:true,BorderThickness.Left:2});
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");pump(30);
        var point=button.TranslatePoint(new Point(60,18),fixture)!.Value;
        fixture.MouseMove(point);fixture.MouseDown(point,MouseButton.Left);pump(15);
        check("按下使用 85ms 缩放过渡",button.Transitions?.OfType<TransformOperationsTransition>().Single().Duration==MotionTokens.Pressed);
        pump(100);check("按下实际缩放为 0.98",Math.Abs(button.RenderTransform!.Value.M11-0.98)<0.002);
        fixture.MouseUp(point,MouseButton.Left);pump(170);
        check("松开使用 140ms 并恢复比例",button.Transitions?.OfType<TransformOperationsTransition>().Single().Duration==MotionTokens.Release&&Math.Abs(button.RenderTransform!.Value.M11-1)<0.001);
        var gate=new TaskCompletionSource();var calls=0;
        var command=new AsyncRelayCommand(async()=>{calls++;await gate.Task;});button.Command=command;
        fixture.MouseDown(point,MouseButton.Left);fixture.MouseUp(point,MouseButton.Left);pump(25);
        var spinner=Part<FeedbackIcon>(button,"ActionFeedback");
        check("真实异步命令驱动加载图标并禁用按钮",command.IsRunning&&ButtonFeedback.GetState(button)==FeedbackState.Loading&&!button.IsEffectivelyEnabled&&spinner.IsVisible&&spinner.SpinnerRunning);
        fixture.MouseDown(point,MouseButton.Left);fixture.MouseUp(point,MouseButton.Left);fixture.KeyPress(Key.Space,RawInputModifiers.None,PhysicalKey.Space,null);fixture.KeyRelease(Key.Space,RawInputModifiers.None,PhysicalKey.Space,null);pump(20);
        check("加载中鼠标和键盘不会重复执行",calls==1);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");pump(10);
        check("减少动画保留加载图标但停止旋转",spinner.IsVisible&&!spinner.SpinnerRunning);
        gate.SetResult();Wait(command.ExecutionTask!);
        check("命令完成恢复可用且不冒充业务成功",button.IsEffectivelyEnabled&&!button.Classes.Contains("is-loading")&&ButtonFeedback.GetState(button)==FeedbackState.None);
        button.Focus();dialog.IsOpen=true;pump(25);
        check("减少动画弹窗直接显示且把焦点移入",dialog.IsVisible&&surface.Opacity==1&&surface.RenderTransform!.Value.IsIdentity&&close.IsFocused);
        dialog.IsOpen=false;pump(10);
        check("减少动画关闭立即隐藏并恢复原焦点",!dialog.IsVisible&&button.IsFocused);
        fixture.Close();main.Show();pump(100);
        model.Toasts.Clear();
        foreach(var level in Enum.GetValues<ToastLevel>())model.Toasts.Show($"{level} · 隔离测试通知",level);
        pump(50);
        var views=main.GetVisualDescendants().OfType<ToastView>().ToArray();
        check("四条通知同时展示且各有图标和语义文字",views.Length==4&&model.Toasts.Items.Select(i=>i.IconKind).Distinct().Count()==4&&model.Toasts.Items.Select(i=>i.Title).Distinct().Count()==4);
        var host=Part<ToastHost>(main,"Notifications");var hostPoint=host.TranslatePoint(default,main)!.Value;
        check("通知锚定右下角 20px 不打开遮罩",Math.Abs(main.ClientSize.Width-hostPoint.X-host.Bounds.Width-20)<2&&Math.Abs(main.ClientSize.Height-hostPoint.Y-host.Bounds.Height-20)<2&&!model.HasOverlay);
        var oldest=model.Toasts.Items[0];model.Toasts.Show("第五条");pump(30);
        check("第五条淘汰最早通知且总数不超过四条",model.Toasts.Items.Count==4&&!model.Toasts.Items.Contains(oldest));
        var middle=model.Toasts.Items[1];middle.CloseCommand.Execute(null);pump(20);
        check("单条关闭只移除选中通知",model.Toasts.Items.Count==3&&!model.Toasts.Items.Contains(middle));
        model.Toasts.Clear();var actions=0;model.Toasts.Show("查看结果",action:()=>actions++);var actionable=model.Toasts.Items[0];actionable.InvokeActionCommand.Execute(null);actionable.InvokeActionCommand.Execute(null);
        check("通知动作只执行一次并立即取消过期计时",actions==1&&model.Toasts.Items.Count==0&&!actionable.HasAction);


        var posting=Task.Run(()=>model.Toasts.Show("后台任务结果"));Wait(posting);
        check("后台通知通过 UI 线程入队",model.Toasts.Items.Count==1&&model.Toasts.Items[0].Message=="后台任务结果");
        model.Toasts.Clear();model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");model.Toasts.Show("关闭动画中的通知");pump(160);var closing=model.Toasts.Items[0];model.Toasts.Dismiss(closing);
        check("通知退场时保留条目以完成淡出",closing.IsClosing&&model.Toasts.Items.Contains(closing));
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");pump(10);
        check("退场中关闭动画立即清理队列",model.Toasts.Items.Count==0);
        using(var manager=new ToastManager()){manager.Show("不再回调",action:()=>actions++);var item=manager.Items[0];manager.Dispose();item.InvokeActionCommand.Execute(null);manager.Show("已释放");check("释放通知服务取消计时与动作",manager.Items.Count==0&&actions==1);}
        model.OpenMultiplayerCommand.Execute(null);model.Multiplayer.ShowRoomsCommand.Execute(null);pump(80);
        Wait(model.Multiplayer.InstallEngineCommand.ExecuteAsync(null));
        check("安装成功反馈来自已确认业务结果",model.Multiplayer.InstallEngineSucceeded&&Part<Button>(main,"InstallRoomEngineButton").Classes.Contains("is-success"));
        model.Multiplayer.InviteToJoin="invalid";Wait(model.Multiplayer.JoinRoomCommand.ExecuteAsync(null));
        check("业务失败即使命令完成仍显示警示态",model.Multiplayer.JoinRoomFailed&&Part<Button>(main,"JoinRoomButton").Classes.Contains("is-error")&&!model.Multiplayer.JoinRoomSucceeded&&!model.Multiplayer.InstallEngineSucceeded);
        model.OpenSetupCommand.Execute(null);pump(30);
        var actual=Part<AnimatedDialog>(main,"SetupDialog");
        check("安装向导已接入统一弹窗组件",actual.IsOpen&&actual.IsVisible&&actual.Surface is not null);
        var setupFocus=main.FocusManager?.GetFocusedElement();model.OpenAccountsCommand.Execute(null);pump(30);model.CloseAccountsCommand.Execute(null);pump(30);
        check("子弹窗关闭后恢复到仍打开的父弹窗",actual.IsOpen&&model.HasOverlay&&setupFocus is not null&&ReferenceEquals(setupFocus,main.FocusManager?.GetFocusedElement()));
        model.CloseSetupCommand.Execute(null);pump(30);
        check("实际弹窗关闭后宿主不再拦截输入",!Part<ContentControl>(main,"OverlayHost").IsHitTestVisible&&!actual.IsVisible);
        model.ShowHomeCommand.Execute(null);pump(150);
        foreach(var level in Enum.GetValues<ToastLevel>())model.Toasts.Show(level switch{ToastLevel.Success=>"共享设置已保存。",ToastLevel.Warning=>"世界暂时不可连接，请稍后重试。",ToastLevel.Error=>"测试下载未完成，可在任务中心重试。",_=>"组件展示使用隔离的测试数据。"},level);
        capture("components-toasts-light",true);
        model.SetThemeMode(Launcher.Application.Settings.ThemeMode.Dark);pump(60);capture("components-toasts-dark",true);
        main.Width=760;main.Height=620;pump(50);hostPoint=host.TranslatePoint(default,main)!.Value;
        check("760px 窗口通知不横向溢出",hostPoint.X>=0&&hostPoint.X+host.Bounds.Width<=main.ClientSize.Width&&hostPoint.Y>=38);
        capture("components-compact",true);
        model.Toasts.Clear();main.Width=1180;main.Height=800;model.SetThemeMode(Launcher.Application.Settings.ThemeMode.Light);model.OpenAccountsCommand.Execute(null);pump(50);capture("components-dialog-light",true);model.CloseAccountsCommand.Execute(null);pump(30);
    }

    public static async Task RunNativeAsync(MainViewModel model,Window main,string root)
    {
        var passed=0;void Check(string name,bool value){if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS "+name);passed++;}
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");model.OpenAccountsCommand.Execute(null);await Task.Delay(250);model.CloseAccountsCommand.Execute(null);await Task.Delay(50);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");await Task.Delay(100);
        model.OpenAccountsCommand.Execute(null);
        await Task.Delay(35);
        var dialog=Part<AnimatedDialog>(main,"AccountsDialog");var surface=dialog.Surface!;
        Console.WriteLine($"dialog open={dialog.IsOpen} visible={dialog.IsVisible} opacity={surface.Opacity} matrix={surface.RenderTransform?.Value} scrim={dialog.ScrimOpacity} motion={MotionPreferences.Enabled}");
        Check("原生弹窗主体存在透明度和缩放中间帧",dialog.IsVisible&&surface.Opacity>0&&surface.Opacity<1&&surface.RenderTransform!.Value.M11>0.97&&surface.RenderTransform.Value.M11<1);
        Check("原生遮罩正在独立淡入",dialog.ScrimOpacity>0&&dialog.ScrimOpacity<=1);
        await Task.Delay(240);
        Check("原生弹窗完成后清理过渡并归位",surface.Opacity==1&&surface.RenderTransform!.Value.IsIdentity&&surface.Transitions is null);
        model.CloseAccountsCommand.Execute(null);
        Check("关闭保留可见层和输入屏障",dialog.IsVisible&&Part<ContentControl>(main,"OverlayHost").IsHitTestVisible&&!surface.IsHitTestVisible);
        await Task.Delay(40);model.OpenAccountsCommand.Execute(null);await Task.Delay(250);
        Check("快速反向以最新打开状态为准",dialog.IsOpen&&dialog.IsVisible&&surface.Opacity==1);
        model.CloseAccountsCommand.Execute(null);await Task.Delay(190);
        Check("150ms 退场后清除遮罩与输入屏障",!dialog.IsVisible&&!Part<ContentControl>(main,"OverlayHost").IsHitTestVisible);
        model.OpenAccountsCommand.Execute(null);await Task.Delay(20);model.CloseAccountsCommand.Execute(null);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");await Task.Delay(20);
        Check("关闭动画偏好立即终止原生弹窗退场",!dialog.IsVisible&&surface.Opacity==1&&surface.RenderTransform!.Value.IsIdentity);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");
        var tip=new ToolTip{Content="450ms 提示 / 140ms 入场"};var trigger=new Button{Content="提示"};ToolTip.SetTip(trigger,tip);
        var popupWindow=new Window{Width=350,Height=200,Content=trigger};popupWindow.Show();await Task.Delay(100);
        Check("Tooltip 全局延迟为 450ms",ToolTip.GetShowDelay(trigger)==450);
        ToolTip.SetIsOpen(trigger,true);await Task.Delay(30);
        Check("原生 Tooltip 使用淡入和 4px 位移",tip.Opacity>0&&tip.Opacity<1&&tip.RenderTransform!.Value.M32>0&&tip.RenderTransform.Value.M32<4);
        await Task.Delay(180);Check("Tooltip 140ms 后完全可见",tip.Opacity==1&&Math.Abs(tip.RenderTransform!.Value.M32)<0.001);
        ToolTip.SetIsOpen(trigger,false);await Task.Delay(30);Check("Tooltip 卸载清理过渡",tip.Transitions is null&&tip.RenderTransform is null&&tip.Opacity==1);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");ToolTip.SetIsOpen(trigger,true);await Task.Delay(20);Check("减少动画 Tooltip 直接呈现",tip.Opacity==1&&tip.RenderTransform is null);ToolTip.SetIsOpen(trigger,false);popupWindow.Close();
        model.Toasts.Clear();model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");model.Toasts.Show("通知入场和手动退场");await Task.Delay(35);
        var toast=main.GetVisualDescendants().OfType<ToastView>().Single();Check("原生通知存在淡入与位移中间帧",toast.Opacity>0&&toast.Opacity<1&&toast.RenderTransform!.Value.M32>0);
        await Task.Delay(180);model.Toasts.Items[0].CloseCommand.Execute(null);await Task.Delay(40);Check("原生通知保留 150ms 淡出帧",model.Toasts.Items.Count==1&&model.Toasts.Items[0].IsClosing&&toast.Opacity>0&&toast.Opacity<1);
        await Task.Delay(180);Check("手动退场后移除通知",model.Toasts.Items.Count==0);model.Toasts.Show("默认五秒自动消失");
        await Task.Delay(4750);Check("默认通知五秒前保留",model.Toasts.Items.Count==1&&!model.Toasts.Items[0].IsClosing);
        await Task.Delay(500);Check("默认五秒计时和淡出后移除",model.Toasts.Items.Count==0);
        File.WriteAllText(Path.Combine(root,"components-native.json"),System.Text.Json.JsonSerializer.Serialize(new{passed,main.RenderScaling,dialogOpenMs=220,scrimMs=180,exitMs=150,tooltipMs=140,toastSeconds=5}));
        Console.WriteLine($"{passed} native component checks passed");
    }
}
