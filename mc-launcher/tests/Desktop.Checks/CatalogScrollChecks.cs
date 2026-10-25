using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Domain.Content;
internal static class CatalogScrollChecks
{
    public static void Run(MainViewModel model,Window window,ResourceFixture fixture,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        var vm=model.Download;
        void Wait(Task task) {var clock=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<5000)pump(10);task.GetAwaiter().GetResult();pump(100);}
        void Settled() {for(var i=0;i<500&&vm.IsBusy;i++)pump(10);pump(100);}
        model.IsInstanceSection=false;fixture.CatalogCount=123;vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Mod);vm.SelectedSource=DownloadViewModel.ModrinthSource;vm.Query="catalog";vm.IsInfiniteScroll=true;
        Wait(vm.SearchCommand.ExecuteAsync(null));
        check("连续浏览首批为20项",vm.Results.Count==20&&fixture.LastLimit==20);
        var first=vm.Results[0];
        Wait(vm.LoadMoreResourcesCommand.ExecuteAsync(null));
        check("追加结果保留原项目且不重复",vm.Results.Count==40&&ReferenceEquals(first,vm.Results[0])&&vm.Results.Select(i=>i.Id).Distinct().Count()==40&&fixture.LastOffset==20);
        fixture.FailPage=true;Wait(vm.LoadMoreResourcesCommand.ExecuteAsync(null));
        check("加载失败保留已有结果并停止自动重试",vm.Results.Count==40&&!vm.CanAutoLoadMore&&vm.LoadMoreResourcesCommand.CanExecute(null));
        Wait(vm.LoadMoreResourcesCommand.ExecuteAsync(null));check("手动重试从失败位置继续",vm.Results.Count==60&&fixture.LastOffset==40);
        foreach(var size in new[]{10,20,35,50}) {vm.PageSize=size;Settled();check("每批数量 "+size,vm.Results.Count==size&&fixture.LastLimit==size);}
        var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(c=>c.Name=="ResourceResultsScroll");
        check("长列表仅实例化可见资源",window.GetVisualDescendants().OfType<Button>().Count(b=>b.DataContext is DownloadItem)<50);
        scroll.Offset=new Vector(0,scroll.Extent.Height);Settled();
        check("滚动接近底部自动追加",vm.Results.Count>=100);
        Wait(vm.LoadMoreResourcesCommand.ExecuteAsync(null));check("全部加载后停止",vm.Results.Count==123&&!vm.HasMoreResults&&!vm.LoadMoreResourcesCommand.CanExecute(null));
        capture("continuous-resources",true);
        check("分类按钮采用放大后的字号",window.GetVisualDescendants().OfType<Button>().Where(b=>b.Classes.Contains("category-tab")).All(b=>b.FontSize==14));
        window.Width=900;window.Height=640;pump(100);
        var batch=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="ResourcePageSize");
        var more=window.GetVisualDescendants().OfType<Button>().Single(c=>c.Name=="LoadMoreResourcesButton");
        check("最小窗口下每批数量和加载入口仍在窗口内",batch.TranslatePoint(new Point(),window) is {X:>=0,Y:>=0} && more.TranslatePoint(new Point(),window) is {} p && p.X+more.Bounds.Width<=window.ClientSize.Width+1);
        capture("continuous-compact",true);window.Width=1180;window.Height=800;pump(100);
        vm.IsInfiniteScroll=false;Settled();Wait(vm.NextPageCommand.ExecuteAsync(null));
        check("分页模式替换结果且偏移正确",vm.Results.Count==50&&fixture.LastOffset==50&&vm.PageText.Contains("页"));
        check("移除黑色选中条",!window.GetVisualDescendants().OfType<Border>().Any(b=>b.Name=="SelectionRail"));
        model.IsInstanceSection=true;pump(100);
        check("游戏库提供删除版本按钮",window.GetVisualDescendants().OfType<Button>().Any(b=>b.Name=="DeleteVersionButton"&&b.IsEffectivelyVisible));
        var count=model.Instances.Count;model.DeleteInstanceCommand.Execute(null);
        check("删除版本先提示存档与配置风险",model.IsConfirmOpen&&model.ConfirmMessage.Contains("存档"));model.ConfirmCancelCommand.Execute(null);
        check("取消确认保留版本",model.Instances.Count==count);
        vm.IsBusy=true;check("下载期间禁用删除",!model.DeleteInstanceCommand.CanExecute(null));vm.IsBusy=false;
        model.DeleteInstanceCommand.Execute(null);model.ConfirmOkCommand.Execute(null);pump(100);
        check("确认删除仅移除选中的测试实例",model.Instances.Count==count-1);
        fixture.CatalogCount=0;
    }
}
