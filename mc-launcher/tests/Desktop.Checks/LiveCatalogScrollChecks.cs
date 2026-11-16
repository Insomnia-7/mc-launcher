using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Domain.Content;

internal static class LiveCatalogScrollChecks
{
    public static void Run(MainViewModel model,MainWindow window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        void Until(Func<bool> ready){var timer=Stopwatch.StartNew();while(!ready()&&timer.ElapsedMilliseconds<35000)pump(20);if(!ready())throw new TimeoutException("Live catalog: "+model.Download.Status);pump(150);}
        var vm=model.Download;model.ShowDownloadsCommand.Execute(null);pump(100);vm.PageSize=20;vm.Query="";
        foreach(var category in new[]{DownloadCategory.Mod,DownloadCategory.Modpack,DownloadCategory.ResourcePack,DownloadCategory.Datapack,DownloadCategory.Shader})
        {
            vm.SelectedCategory=vm.Categories.First(c=>c.Category==category);Until(()=>!vm.IsBusy);
            if(vm.Results.Count==0){var task=vm.SearchCommand.ExecuteAsync(null);Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
            Until(()=>vm.ResourceTags.Count>1);
            check("真实平台 "+category+" 返回可浏览结果",vm.Results.Count>=10);
            check("真实平台 "+category+" 分类中文并区分类型",vm.ResourceTags.Skip(1).All(t=>t.Name!=t.Id&&t.Group.Length>0));
            var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(x=>x.Name=="ResourceResultsScroll");
            scroll.Offset=default;pump(100);var point=scroll.TranslatePoint(new Point(140,100),window)!.Value;
            window.MouseMove(point);window.MouseWheel(point,new Vector(0,-4));pump(150);
            check("真实平台 "+category+" 鼠标滚轮移动资源条",scroll.Offset.Y>0&&scroll.Extent.Height>scroll.Viewport.Height);
            var first=vm.Results[0];var count=vm.Results.Count;scroll.Offset=new Vector(0,scroll.Extent.Height);
            Until(()=>vm.Results.Count>count||!vm.HasMoreResults);
            check("真实平台 "+category+" 滚动追加且保留首条",vm.Results.Count>count&&ReferenceEquals(first,vm.Results[0])&&vm.Results.Select(r=>r.Id).Distinct().Count()==vm.Results.Count);
            Until(()=>!vm.IsBusy);var total=vm.Results.Count;
            model.ShowInstancesCommand.Execute(null);pump(120);model.ShowDownloadsCommand.Execute(null);pump(200);
            check("真实平台 "+category+" 返回页面保留可滚动结果",vm.Results.Count==total&&ReferenceEquals(first,vm.Results[0])&&scroll.IsEffectivelyVisible&&scroll.Viewport.Height>0);
            capture("live-scroll-"+category,true);
        }
    }
}
