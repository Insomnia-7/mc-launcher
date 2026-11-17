using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Operations;
using Launcher.Desktop.Controls;

internal static class LibraryStorageUiChecks
{
    public static void Run(MainViewModel model, Window window, Action<int> pump, Action<string,bool> check, Action<string,bool> capture, string root, ResourceFixture fixture)
    {
        void Wait(Task task) { while(!task.IsCompleted) pump(10); task.GetAwaiter().GetResult(); pump(100); }
        model.ShowInstancesCommand.Execute(null); pump(100); capture("library-light",true);
        var info=window.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Classes.Contains("page-title")&&t.IsEffectivelyVisible);
        FontManager.Current.TryGetGlyphTypeface(new Typeface(info.FontFamily),out var glyph);
        File.WriteAllText(Path.Combine(root,"font-info.txt"),$"Family={info.FontFamily}; resolved={glyph?.FamilyName}; size={info.FontSize}; scaling={window.RenderScaling}");
        check("侧栏实例计数不再称为世界",model.InstanceCountText.Contains("游戏实例"));
        Wait(model.OpenStorageSettingsCommand.ExecuteAsync(null));
        check("存储页展示游戏和启动器数据真实位置",model.LocationSummary.Contains(model.GameRoot) && model.LocationSummary.Contains(model.DataRootPath));
        check("所有分类都有实际路径", model.StorageAreas.Count>0 && model.StorageAreas.All(a=>Path.IsPathFullyQualified(a.Path)));
        capture("storage-light",true);
        window.Width=900; window.Height=640;pump(100);capture("storage-small",true);
        check("小窗口存储页可滚动",window.GetVisualDescendants().OfType<ScrollViewer>().Any(s=>s.IsEffectivelyVisible && s.Extent.Height>s.Viewport.Height));
        window.Width=1180;window.Height=800;model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
        model.ShowDownloadsCommand.Execute(null);pump(100);
        model.Download.SelectedVersion=model.Download.LatestVersions.First();
        Wait(model.Download.InstallVersionCommand.ExecuteAsync(null));
        model.OpenTaskCenterCommand.Execute(null);pump(100);capture("tasks-active",true);
        check("完成任务自动移出进行中",model.DurableTasks.Any(t=>t.IsHistory) && model.VisibleDurableTasks.All(t=>!t.IsHistory));
        model.ShowTaskHistoryCommand.Execute(null);pump(100);capture("tasks-history",true);
        check("历史页展示完成任务",model.VisibleDurableTasks.Count>0 && model.VisibleDurableTasks.All(t=>t.IsHistory));
        model.ClearTaskHistoryCommand.Execute(null);pump(50);
        check("界面清空记录后游戏仍存在",model.VisibleDurableTasks.Count==0 && Directory.Exists(Path.Combine(model.GameRoot,"versions","1.21.4")));
        model.ShowInstancesCommand.Execute(null);capture("meadow-library-light-2",true);
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");pump(100);capture("meadow-library-light",true);
        foreach(var factor in new[]{1d,1.25,1.5})
        {
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)(window.ClientSize.Width*factor),(int)(window.ClientSize.Height*factor)),new Vector(96*factor,96*factor));
            bitmap.Render(window);bitmap.Save(Path.Combine(root,$"library-render-{factor*100:0}.png"));
        }
        var vm=model.Download; fixture.CatalogCount=63;
        model.ShowDownloadsCommand.Execute(null); vm.SelectedCategory=vm.Categories.First(c=>c.Category==Launcher.Domain.Content.DownloadCategory.Mod);vm.Query="catalog";vm.IsInfiniteScroll=false;
        Wait(vm.SearchCommand.ExecuteAsync(null));
        var first=vm.Results.First();
        var response=fixture.SearchAsync("catalog",Launcher.Domain.Content.DownloadCategory.Mod,limit:20,offset:20).GetAwaiter().GetResult();
        var pending=new TaskCompletionSource<Launcher.Application.Mods.ModrinthSearchResponse>();fixture.PendingSearch=pending;
        var loading=vm.NextPageCommand.ExecuteAsync(null);pump(60);
        check("翻页搜索保留旧内容并显示加载状态",vm.IsSearchingResources && ReferenceEquals(first,vm.Results.First()));capture("search-transition",true);
        pending.SetResult(response);Wait(loading);check("新结果到达后切换且结束加载状态",!vm.IsSearchingResources && vm.PageNumber==2 && !ReferenceEquals(first,vm.Results.First()));
        fixture.PendingSearch=new();loading=vm.NextPageCommand.ExecuteAsync(null);pump(30);vm.CancelCommand.Execute(null);Wait(loading);
        check("取消翻页保留上页结果和分页位置",vm.PageNumber==2 && vm.Results.Count==20 && vm.Status.Contains("取消"));
        fixture.PendingSearch=new();loading=vm.NextPageCommand.ExecuteAsync(null);pump(30);
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==Launcher.Domain.Content.DownloadCategory.Shader);Wait(loading);pump(100);
        check("搜索期间可切换分类且丢弃旧请求",vm.SelectedCategory.Category==Launcher.Domain.Content.DownloadCategory.Shader && vm.Results.All(r=>r.Category==Launcher.Domain.Content.DownloadCategory.Shader));
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==Launcher.Domain.Content.DownloadCategory.MinecraftVersion);
        vm.BackToVersionsCommand.Execute(null);pump(100);capture("versions-type",true);
        check("版本页底栏不残留Modrinth搜索状态",!model.Download.FooterStatus.Contains("Modrinth"));
        var expander=window.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e=>e.IsEffectivelyVisible);
        if(expander is not null)
        {
            expander.IsExpanded=true;pump(100);
            var header=expander.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(c=>c.Name=="HeaderContent");
            check("展开标题不使用Fluent主题默认命名的内容部件",header is not null && header.Background is ISolidColorBrush { Color.A: 0 });
        }
    }
}
