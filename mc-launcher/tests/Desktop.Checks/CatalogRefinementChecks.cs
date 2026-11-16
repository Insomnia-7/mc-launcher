using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Mods;
using Launcher.Domain.Content;
using Launcher.Domain.Versions;
using Launcher.Desktop;

internal static class CatalogRefinementChecks
{
    public static void Run(MainViewModel model, MainWindow window, ResourceFixture resources, VersionInstallFixture versions, Action<int> pump, Action<string,bool> check, Action<string,bool> capture)
    {
        void Wait(Task task){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.ElapsedMilliseconds<12000)pump(10);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();pump(100);}
        var vm=model.Download;
        versions.Manifest=new(){Versions=[new(){Id="1.21.4",Type="release"},new(){Id="1.20.1",Type="release"},new(){Id="26.4-snapshot-8",Type="snapshot"},new(){Id="26w14a",Type="snapshot"},new(){Id="24w14potato",Type="snapshot"},new(){Id="b1.7.3",Type="old_beta"}]};
        model.ShowDownloadsCommand.Execute(null);Wait(vm.EnsureLoadedAsync());pump(120);
        var input=window.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="GameVersionQuery");
        check("游戏本体显示无占位字的版本搜索框",input.IsEffectivelyVisible&&string.IsNullOrEmpty(input.PlaceholderText));
        input.Text="1.20";pump(100);
        check("输入版本号即时筛选并展开匹配组",vm.VersionGroups.SelectMany(g=>g.Versions).Select(v=>v.Id).SequenceEqual(["1.20.1"])&&vm.VersionGroups.All(g=>g.IsExpanded)&&!vm.ShowsLatestVersions);
        input.Text="POTATO";pump(100);
        check("版本搜索忽略大小写",vm.VersionGroups.Single().Versions.Single().Id=="24w14potato");
        input.Text="愚人节";pump(100);
        check("愚人节版本独立归组且每项有简介",vm.VersionGroups.Single().Key=="愚人节版本"&&vm.VersionGroups.Single().Versions.All(v=>v.HasEventDescription));
        check("普通预览版不混入愚人节组",vm.VersionGroups.SelectMany(g=>g.Versions).All(v=>VersionClassification.IsEvent(v.Id)));
        capture("april-fools-versions",true);
        var headers=window.GetVisualDescendants().OfType<Expander>().Where(x=>x.IsEffectivelyVisible).ToArray();
        check("游戏版本分组条铺满内容宽度",headers.Length>0&&headers.All(x=>x.GetVisualDescendants().OfType<ToggleButton>().First(b=>b.Name=="HeaderToggle").Bounds.Width>=x.Bounds.Width-2));
        input.Text="no-such-version";pump(50);
        check("无匹配版本显示空结果而非最新推荐",vm.VersionGroups.Count==0&&vm.VersionSearchSummary=="没有匹配版本"&&!vm.ShowsLatestVersions);
        input.Text="";pump(50);check("清空搜索恢复全部类型与默认折叠",vm.VersionGroups.Sum(g=>g.Count)==6&&vm.ShowsLatestVersions&&vm.VersionGroups.All(g=>!g.IsExpanded));
        resources.CatalogCount=160;vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Mod);pump(100);
        vm.SelectedTag=vm.ResourceTags.First(t=>t.Id=="adventure");Wait(vm.SearchCommand.ExecuteAsync(null));
        check("选择中文分类仍使用平台原始标识",vm.SelectedTag?.Id=="adventure"&&vm.Results.Count==20);
        // A slow taxonomy response must not turn a transient ComboBox null selection into a filter edit.
        vm.SelectedSource=DownloadViewModel.CurseForgeSource;pump(120);vm.SelectedSource=DownloadViewModel.ModrinthSource;pump(120);
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.ResourcePack);pump(100);
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.MinecraftVersion);pump(100);
        var delayed=new TaskCompletionSource<IReadOnlyList<ResourceTag>>(TaskCreationOptions.RunContinuationsAsynchronously);resources.PendingCategories=delayed;
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Shader);pump(100);
        var selected=new ResourceTag("realistic","写实风格","类型");vm.ResourceTags.Add(selected);vm.SelectedTag=selected;
        // Request metadata again under the selected filter, then release it after results arrive.
        resources.PendingCategories=delayed;Wait(vm.SearchCommand.ExecuteAsync(null));var searches=resources.Searches;
        delayed.SetResult([new("realistic","写实风格","类型"),new("bloom","泛光","功能")]);pump(200);
        check("晚到的分类清单不清空结果或重复搜索",vm.Results.Count==20&&resources.Searches==searches&&vm.SelectedTag?.Id=="realistic");
        capture("localized-categories",true);
        vm.PageSize=50;for(var i=0;i<200&&vm.IsBusy;i++)pump(10);Wait(vm.SearchCommand.ExecuteAsync(null));
        var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(x=>x.Name=="ResourceResultsScroll");
        var row=scroll.GetVisualDescendants().OfType<Button>().First(x=>x.DataContext is DownloadItem);var point=row.TranslatePoint(new Point(140,22),window)!.Value;
        window.MouseMove(point);window.MouseWheel(point,new Vector(0,-4));pump(100);
        check("汉化后资源条仍能滚动",scroll.Offset.Y>0&&scroll.Extent.Height>scroll.Viewport.Height);
        var request=new TaskCompletionSource<ModrinthSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);resources.PendingSearch=request;var searching=vm.SearchCommand.ExecuteAsync(null);pump(80);var previous=scroll.Offset.Y;
        window.MouseWheel(point,new Vector(0,-4));pump(100);check("搜索等待时继续滚动已有结果",scroll.Offset.Y>previous&&vm.IsSearchingResources);
        vm.CancelCommand.Execute(null);Wait(searching);check("取消后保留资源列表",vm.Results.Count==50&&!vm.IsBusy);
        foreach(var width in new[]{900,1280}){window.Width=width;pump(100);check("窗口宽度 "+width+" 下滚动视口有界",scroll.Viewport.Height>0&&scroll.Viewport.Height<window.ClientSize.Height&&scroll.Extent.Height>scroll.Viewport.Height);}
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Modpack);pump(120);Wait(vm.SearchCommand.ExecuteAsync(null));Wait(vm.OpenResourceCommand.ExecuteAsync(vm.Results.First()));vm.ShowPackVersionsCommand.Execute(null);pump(100);
        var releases=window.GetVisualDescendants().OfType<ScrollViewer>().Single(x=>x.Name=="ResourceReleaseScroll");
        headers=releases.GetVisualDescendants().OfType<Expander>().Where(x=>x.IsEffectivelyVisible).ToArray();
        check("整合包发布版本分组条同样铺满宽度",headers.Length>0&&headers.All(x=>x.GetVisualDescendants().OfType<ToggleButton>().First(b=>b.Name=="HeaderToggle").Bounds.Width>=x.Bounds.Width-2));
        capture("full-width-pack-groups",true);
    }
}
