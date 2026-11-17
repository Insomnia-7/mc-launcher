using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

internal static class PageSizeChecks
{
    public static async Task RunNative(MainViewModel model,MainWindow window,string root)
    {
        var passed=0;void Check(string name,bool okay){if(!okay)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
        var host=window.GetVisualDescendants().OfType<AnimatedContentHost>().Single();
        var layers=host.Children.OfType<ContentPresenter>().ToArray();var measurements=new List<object>();
        async Task Settle(){for(var i=0;i<200&&host.IsTransitioning;i++)await Task.Delay(10);window.UpdateLayout();}
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");window.Width=1280;window.Height=820;
        model.ShowHomeCommand.Execute(null);await Task.Delay(350);window.UpdateLayout();
        var ownerSize=window.ClientSize;model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="smooth");
        var pages=new (string,Action)[]{("实例",()=>model.ShowInstancesCommand.Execute(null)),("主页",()=>model.ShowHomeCommand.Execute(null)),("联机",()=>model.OpenMultiplayerCommand.Execute(null)),("网络",()=>model.OpenNetworkCommand.Execute(null)),("资源",()=>model.ShowDownloadsCommand.Execute(null)),("任务",()=>model.OpenTaskCenterCommand.Execute(null)),("设置",()=>model.OpenAppearanceCommand.Execute(null)),("Rust",()=>model.OpenHorizonCommand.Execute(null)),("返回Java",()=>model.ShowInstancesCommand.Execute(null)),("详情",()=>{model.SelectedInstance=model.Instances.First();model.OpenGameCommand.Execute(null);})};
        foreach(var (name,navigate) in pages)
        {
            var old=host.Content!;var before=old.Bounds.Size;navigate();var exited=false;
            for(var i=0;i<25;i++){await Task.Delay(10);if(host.IsTransitioning&&layers[0].RenderTransform!.Value.M11 is >0.985 and <1){exited=true;break;}}
            window.UpdateLayout();
            Check(name+"切换保留旧页布局尺寸",old.Bounds.Size==before);
            Check(name+"退场存在轻微缩放中间帧",exited);
            var entering=false;
            for(var i=0;i<65;i++){await Task.Delay(10);if(layers[0].Content is null&&layers[1].Opacity is >0 and <1&&layers[1].RenderTransform!.Value.M11 is >0.985 and <1){entering=true;break;}}
            Check(name+"新页按最终布局缩放淡入",entering);
            await Settle();Check(name+"结束归位且外层窗口尺寸不变",!host.IsTransitioning&&layers[0].Content is null&&layers[1].Opacity==1&&layers[1].RenderTransform!.Value.M11==1&&window.ClientSize==ownerSize);
            measurements.Add(new{name,oldWidth=before.Width,oldHeight=before.Height,newWidth=host.Content!.Bounds.Width,newHeight=host.Content.Bounds.Height});
        }
        var detail=host.Content!;var detailSize=detail.Bounds.Size;
        foreach(var tab in new[]{1,2,3,4,0}){model.DetailTab=tab;await Task.Delay(100);Check("详情子页"+tab+"保持详情窗口尺寸",detail.Bounds.Size==detailSize&&window.ClientSize==ownerSize);}
        foreach(var dialog in new[]{"AccountsDialog","ConfirmDialog","RestartDialog"})
        {
            model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");
            void Open(bool value){switch(dialog){case "AccountsDialog":model.IsAccountsOpen=value;break;case "ConfirmDialog":model.IsConfirmOpen=value;break;default:model.DataRestartRequired=value;break;}}
            Open(true);await Task.Delay(100);
            var surface=window.GetVisualDescendants().OfType<AnimatedDialog>().Single(x=>x.Name==dialog).Surface!;var size=surface.Bounds.Size;
            Open(false);await Task.Delay(50);model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="smooth");Open(true);await Task.Delay(70);
            Check(dialog+"入场不重排面板尺寸",surface.Bounds.Size==size&&window.ClientSize==ownerSize);
            await Task.Delay(350);Check(dialog+"结束保持面板尺寸",surface.Bounds.Size==size&&window.ClientSize==ownerSize);Open(false);await Task.Delay(250);
        }
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");model.ShowHomeCommand.Execute(null);await Task.Delay(120);
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="smooth");model.ShowInstancesCommand.Execute(null);await Task.Delay(60);
        window.Width=1000;window.Height=700;await Task.Delay(160);
        Check("实际缩放窗口中断转场后立即归位",!host.IsTransitioning&&layers[0].Content is null&&layers[1].Opacity==1&&layers[1].RenderTransform!.Value.M11==1);
        Check("紧凑窗口使用独立的正确页面边距",layers[1].Margin==new Thickness(20,20,20,16));
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");window.Width=1280;window.Height=820;model.ShowHomeCommand.Execute(null);await Task.Delay(180);
        var shown=host.Content;model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="smooth");model.ShowInstancesCommand.Execute(null);await Task.Delay(90);var alpha=layers[0].Opacity;
        model.OpenNetworkCommand.Execute(null);await Task.Delay(30);
        Check("快速改选继续当前可见页退场且不闪出隐藏页",ReferenceEquals(layers[0].Content,shown)&&layers[0].Opacity<=alpha+0.01);
        await Settle();
        File.WriteAllText(Path.Combine(root,"page-size-audit.json"),JsonSerializer.Serialize(new{passed,measurements,scope="Native current Windows monitor; all shell pages, detail tabs and three modal surfaces."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{passed} native page size checks passed");
    }
}
