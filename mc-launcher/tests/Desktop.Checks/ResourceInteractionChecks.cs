using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;
using Launcher.Domain.Content;

internal static class ResourceInteractionChecks
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
    private static nint Pack(PixelPoint point)=>(nint)((point.X&0xffff)|((point.Y&0xffff)<<16));
    public static async Task RunNative(MainViewModel model,MainWindow window,ResourceFixture fixture,string root)
    {
        var passed=0;void Check(string name,bool value){if(!value)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
        model.SelectedMotion=model.MotionChoices.Single(x=>x.Id=="off");var vm=model.Download;
        vm.SelectedCategory=vm.Categories.First(x=>x.Category==DownloadCategory.ResourcePack);vm.PageSize=50;
        model.ShowDownloadsCommand.Execute(null);await Task.Delay(150);await vm.SearchCommand.ExecuteAsync(null);await Task.Delay(150);
        var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(x=>x.Name=="ResourceResultsScroll");
        var handle=window.TryGetPlatformHandle()!.Handle;var point=Pack(scroll.PointToScreen(new Point(120,100)));
        SendMessage(handle,0x20A,(nint)(-360<<16),point);await Task.Delay(120);
        Check("Windows 原生滚轮能移动资源列表",scroll.Offset.Y>0);
        Check("Windows 原生资源滚动条可见",scroll.GetVisualDescendants().OfType<ScrollBar>().Any(x=>x.IsVisible&&x.Maximum>0));
        fixture.PendingSearch=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=vm.SearchCommand.ExecuteAsync(null);await Task.Delay(50);var before=scroll.Offset.Y;
        SendMessage(handle,0x20A,(nint)(-360<<16),point);await Task.Delay(100);
        Check("Windows 搜索等待中仍可滚动已有资源",vm.IsSearchingResources&&scroll.Offset.Y>before);
        var progress=window.GetVisualDescendants().OfType<Border>().Single(x=>x.Name=="ResourceSearchProgress");
        Check("Windows 搜索进度不再覆盖资源列表",scroll.TranslatePoint(new Point(0,scroll.Bounds.Height),window)!.Value.Y<=progress.TranslatePoint(default,window)!.Value.Y+1);
        vm.CancelCommand.Execute(null);await pending;await vm.SearchCommand.ExecuteAsync(null);
        var count=fixture.Searches;model.ShowInstancesCommand.Execute(null);await Task.Delay(50);model.ShowDownloadsCommand.Execute(null);await Task.Delay(150);
        Check("Windows 返回资源页不触发重复请求",fixture.Searches==count);
        Check("Windows 搜索框为空白且可编辑",window.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="ResourceQuery") is {PlaceholderText:null,IsEffectivelyEnabled:true});
        window.UpdateLayout();using var image=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width,(int)window.ClientSize.Height),new Vector(96,96));image.Render(window);image.Save(Path.Combine(root,"resource-native.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine($"{passed} native resource checks passed");
    }
    public static void Run(MainViewModel model,MainWindow window,ResourceFixture fixture,string root,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        void Complete(Task task){var clock=Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<10000)pump(10);task.GetAwaiter().GetResult();pump(120);}
        var vm=model.Download;fixture.CatalogCount=160;vm.SelectedCategory=vm.Categories.First(x=>x.Category==DownloadCategory.ResourcePack);vm.PageSize=50;vm.IsInfiniteScroll=true;
        model.ShowDownloadsCommand.Execute(null);pump(180);Complete(vm.SearchCommand.ExecuteAsync(null));
        var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(x=>x.Name=="ResourceResultsScroll");
        var row=scroll.GetVisualDescendants().OfType<Button>().First(x=>x.DataContext is DownloadItem);
        var point=row.TranslatePoint(new Point(150,25),window)!.Value;
        var extent=scroll.Extent.Height;var viewport=scroll.Viewport.Height;
        window.MouseMove(point);window.MouseWheel(point,new Vector(0,-4));pump(150);var offset=scroll.Offset.Y;
        var searches=fixture.Searches;var first=vm.Results.First();model.ShowInstancesCommand.Execute(null);pump(80);
        var clock=Stopwatch.StartNew();model.ShowDownloadsCommand.Execute(null);var navigationMs=clock.Elapsed.TotalMilliseconds;pump(150);
        File.WriteAllText(Path.Combine(root,"resource-interaction.json"),JsonSerializer.Serialize(new{extent,viewport,offset,navigationMs,searchesBefore=searches,searchesAfter=fixture.Searches,realizedRows=scroll.GetVisualDescendants().OfType<Button>().Count(x=>x.DataContext is DownloadItem)},new JsonSerializerOptions{WriteIndented=true}));
        foreach(var theme in new[]{"light","dark"}){model.SelectThemeModeCommand.Execute(theme);pump(40);capture("resource-list-"+theme,true);}
        check("资源结果具有可滚动的高度",extent>viewport&&viewport>0);
        check("鼠标悬停资源条时滚轮可移动列表",offset>0);
        check("返回资源页不重复检索已有结果",fixture.Searches==searches&&ReferenceEquals(first,vm.Results.First()));
        check("资源列表恢复可见的竖向滚动条",scroll.VerticalScrollBarVisibility==ScrollBarVisibility.Auto&&scroll.GetVisualDescendants().OfType<ScrollBar>().Any(x=>x.IsVisible&&x.Maximum>0));
        check("资源搜索框没有提示文字",string.IsNullOrEmpty(window.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="ResourceQuery").PlaceholderText));
        var pending=new TaskCompletionSource<Launcher.Application.Mods.ModrinthSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);fixture.PendingSearch=pending;
        var search=vm.SearchCommand.ExecuteAsync(null);pump(80);var previousOffset=scroll.Offset.Y;
        window.MouseWheel(scroll.TranslatePoint(new Point(120,120),window)!.Value,new Vector(0,-3));pump(100);
        check("搜索进行中已有结果仍可滚动",vm.IsSearchingResources&&scroll.Offset.Y>previousOffset);
        var progress=window.GetVisualDescendants().OfType<Border>().Single(x=>x.Name=="ResourceSearchProgress");
        check("搜索进度不会挡住资源条或滚动条",scroll.TranslatePoint(new Point(0,scroll.Bounds.Height),window)!.Value.Y<=progress.TranslatePoint(default,window)!.Value.Y+1);
        foreach(var theme in new[]{"light","dark"}){model.SelectThemeModeCommand.Execute(theme);pump(40);capture("resource-search-"+theme,true);}
        vm.CancelCommand.Execute(null);Complete(search);
        check("取消搜索保留已有资源条",vm.Results.Count==50&&!vm.IsBusy);
        searches=fixture.Searches;Complete(vm.SearchCommand.ExecuteAsync(null));
        check("手动搜索仍会刷新结果",fixture.Searches==searches+1&&!ReferenceEquals(first,vm.Results.First()));
        model.ShowInstancesCommand.Execute(null);pump(80);
        check("实例搜索框也没有提示文字",string.IsNullOrEmpty(window.GetVisualDescendants().OfType<TextBox>().Single(x=>x.Name=="LibrarySearch").PlaceholderText));
    }
}
