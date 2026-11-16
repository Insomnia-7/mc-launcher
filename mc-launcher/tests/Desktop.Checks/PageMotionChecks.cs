using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Launcher.Desktop.Views;

internal static class PageMotionChecks
{
    private static ContentPresenter[] Layers(AnimatedContentHost host) => host.Children.OfType<ContentPresenter>().ToArray();
    private static bool Settled(AnimatedContentHost host) => !host.IsTransitioning && Layers(host).Count(x => x.Content is not null) == 1 &&
        Layers(host).Where(x => x.Content is not null).All(x => x.Opacity == 1 && x.RenderTransform!.Value.M32 == 0);

    public static void Run(MainViewModel model, MainWindow window, Action<int> pump, Action<string,bool> check, Func<int> requests)
    {
        var host = window.GetVisualDescendants().OfType<AnimatedContentHost>().Single();
        bool EndSettled()
        {
            for(var i=0;i<150 && !Settled(host);i++)pump(10);
            return Settled(host);
        }
        var library = (LibraryView)host.Content!;
        model.OpenAppearanceCommand.Execute(null);pump(250);var preferences=host.Content;model.ShowInstancesCommand.Execute(null);pump(250);
        model.SelectedMotion = model.MotionChoices.Single(m => m.Id == "smooth"); pump(380);
        var search = library.FindControl<TextBox>("LibrarySearch")!;
        search.Text = "1.21"; pump(100);
        model.SetLibraryLayoutCommand.Execute("list");pump(40);
        var scroll = library.FindControl<ListBox>("LibraryList")!.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, Math.Min(100, Math.Max(0,scroll.Extent.Height-scroll.Viewport.Height))); pump(30);
        var offset = scroll.Offset;
        var before = requests();
        // Inspect ownership synchronously: software rendering a full settings page can exceed 110ms.
        host.Content=preferences;
        check("淡出淡入最多挂载相邻两页", Layers(host).Count(x => x.Content is not null) == 2 && preferences is PreferencesView);
        check("旧卡片淡出时两页均不接受误点击", !Layers(host)[0].IsHitTestVisible && !Layers(host)[1].IsHitTestVisible);
        check("宿主实色背景不随页面淡出", host.Background is ISolidColorBrush {Color.A:255} && host.Opacity == 1);
        model.OpenAppearanceCommand.Execute(null);pump(380); check("过渡结束清空旧层并归位当前页", EndSettled());
        model.ShowInstancesCommand.Execute(null); pump(380);
        check("游戏库复用同一视图且保留筛选", ReferenceEquals(host.Content,library) && search.Text=="1.21" && model.LibraryQuery=="1.21");
        check("游戏库往返保留滚动位置", scroll.Offset == offset);
        check("视觉切页不增加网络请求", requests()==before);
        for (var i=0;i<30;i++)
        {
            if(i%2==0)model.OpenAppearanceCommand.Execute(null);else model.ShowInstancesCommand.Execute(null);
            pump(5);
            if(Layers(host).Count(x=>x.Content is not null)>2)throw new InvalidOperationException("More than two pages attached");
        }
        pump(380); check("30 次快速往返无重复父级且只保留当前页", ReferenceEquals(host.Content, library) && EndSettled());
        model.OpenAppearanceCommand.Execute(null);pump(20);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        check("中途减少动画立即移除旧层、透明度与位移", Settled(host));
        model.ShowInstancesCommand.Execute(null);pump(20);
        check("减少动画切页瞬时完成", ReferenceEquals(host.Content,library) && EndSettled());
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");pump(20);
        model.OpenAppearanceCommand.Execute(null);pump(10);
        var parent=(Panel)host.Parent!;parent.Children.Remove(host);
        check("宿主卸载取消过渡并恢复可见状态", Settled(host));
        parent.Children.Add(host);pump(380);
        check("宿主重挂载仍保留当前缓存页", ReferenceEquals(preferences,host.Content) && EndSettled());
        model.ShowInstancesCommand.Execute(null);pump(380);
        search.Text="";pump(50);

        var stages=Enumerable.Range(0,7).Select(i=>(Control)new Button {Content=$"stage {i}",Height=32}).ToArray();
        var stack=new StackPanel();foreach(var stage in stages)stack.Children.Add(stage);
        var probe=new Window{Content=stack,Width=400,Height=300};probe.Show();pump(50);
        try
        {
            using var cancel=new CancellationTokenSource();
            var entrance=Motion.PlayStaggerSequenceAsync(stages,cancel.Token);
            check("七段入场保留交互且末段等待错峰",stages.All(x=>x.IsHitTestVisible && x.IsEffectivelyEnabled) && stages[6].Opacity==0);
            pump(100);
            check("七段按顺序推进而非同时出现",stages[0].Opacity>stages[6].Opacity);
            pump(500);
            check("七段完整结束并恢复所有模块",entrance.IsCompletedSuccessfully && stages.All(x=>x.Opacity==1 && x.RenderTransform!.Value.M32==0));
            var interrupted=Motion.PlayStaggerSequenceAsync(stages,cancel.Token);pump(30);cancel.Cancel();pump(10);
            check("交错入场中断归位包括尚未开始的段",interrupted.IsCompletedSuccessfully && stages.All(x=>x.Opacity==1 && x.RenderTransform!.Value.M32==0));
            var reduced=Motion.PlayStaggerSequenceAsync(stages,CancellationToken.None);pump(20);
            model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
            check("关闭动画立即显示全部入场段",stages.All(x=>x.Opacity==1 && x.RenderTransform!.Value.M32==0));
            pump(10); check("关闭动画结束所有交错等待",reduced.IsCompletedSuccessfully);
            var instant=Motion.PlayStaggerSequenceAsync(stages,CancellationToken.None);
            check("减少动画没有错峰等待或位移",instant.IsCompletedSuccessfully && stages.All(x=>x.Opacity==1 && x.RenderTransform!.Value.M32==0));
        }
        finally{probe.Close();model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");}
    }

    public static async Task RunNativeAsync(MainViewModel model, MainWindow window, string root)
    {
        var passed=0;
        void Check(string text,bool okay){if(!okay)throw new InvalidOperationException(text);passed++;Console.WriteLine("PASS "+text);}
        var host=window.GetVisualDescendants().OfType<AnimatedContentHost>().Single();
        async Task WaitSettled()
        {
            for(var i=0;i<150 && !Settled(host);i++)await Task.Delay(10);
        }
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        model.OpenAppearanceCommand.Execute(null);await Task.Delay(250);model.ShowInstancesCommand.Execute(null);await Task.Delay(250);
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");await Task.Delay(380);
        var library=host.Content;
        model.OpenAppearanceCommand.Execute(null);
        var layers=Layers(host);
        Control[] cards=[];var observed=false;
        for(var i=0;i<20;i++)
        {
            await Task.Delay(12);
            if(layers[0].Content is Control old){cards=AnimatedContentHost.FadeTargets(old);observed=cards.Any(c=>c.Opacity>0.01&&c.Opacity<0.99);}
            if(observed)break;
        }
        Check("真实窗口旧卡片存在淡出中间帧",observed);
        Check("淡出期间新页完全隐藏不叠印",layers[1].Opacity==0&&layers[0].IsVisible);
        Check("卡片淡入淡出不发生垂直位移",layers.All(x=>x.RenderTransform!.Value.M32==0));
        Check("宿主实色背景保留且退场不接受点击",host.Background is ISolidColorBrush {Color.A:255}&&!layers[0].IsHitTestVisible&&!layers[1].IsHitTestVisible);
        var entered=false;
        for(var i=0;i<45;i++){await Task.Delay(12);if(layers[0].Content is null&&layers[1].Content is Control page&&AnimatedContentHost.FadeTargets(page).Any(c=>c.Opacity>0.01&&c.Opacity<0.99)){entered=true;break;}}
        Check("真实窗口新卡片存在淡入中间帧",entered);
        Check("新卡片淡入前旧页已从渲染树移除",layers[0].Content is null&&library!.GetVisualAncestors().OfType<Window>().Any()==false);
        using(var picture=new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96)))
        {picture.Render(window);picture.Save(Path.Combine(root,"page-transition.png"),PngBitmapEncoderOptions.Default);}
        await Task.Delay(380);await WaitSettled();Check("真实过渡结束不残留退场页",Settled(host));
        for(var i=0;i<12;i++){if(i%2==0)model.ShowInstancesCommand.Execute(null);else model.OpenAppearanceCommand.Execute(null);await Task.Delay(10);}
        model.ShowInstancesCommand.Execute(null);await Task.Delay(380);await WaitSettled();
        Check("真实窗口连续切页复用游戏库并归位",ReferenceEquals(library,host.Content) && Settled(host));
        model.OpenAppearanceCommand.Execute(null);await Task.Delay(380);await WaitSettled();
        model.ShowInstancesCommand.Execute(null);await Task.Delay(40);
        Check("反向导航同样先淡出且不移动页面",host.IsTransitioning&&layers[1].Opacity==0&&layers.All(x=>x.RenderTransform!.Value.M32==0));
        await Task.Delay(380);await WaitSettled();
        model.OpenAppearanceCommand.Execute(null);await Task.Delay(420);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        Check("真实窗口淡入中途关闭动画恢复所有卡片",Settled(host)&&AnimatedContentHost.FadeTargets((Control)host.Content!).All(c=>c.Opacity>0.99));
        model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");model.ShowInstancesCommand.Execute(null);await Task.Delay(20);
        window.WindowState=WindowState.Minimized;await Task.Delay(380);window.WindowState=WindowState.Normal;await Task.Delay(100);
        Check("最小化中断后还原页面仍可见",Settled(host));
        Console.WriteLine($"{passed} native page motion checks passed");
    }
}
