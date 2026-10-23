using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Application.Java;
using Launcher.Domain.Java;
using Launcher.Domain.Versions;
using Launcher.Infrastructure;
using Launcher.Application.Mods;
using Launcher.Domain.Content;
using Microsoft.Extensions.Logging.Abstractions;

internal static class UiCheck
{
    private static readonly SetupProxy State = new();
    private static int _checks;
    [STAThread]
    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcl-ui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MCLAUNCHER_HOME", Path.Combine(root, Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("MCLAUNCHER_GAME_ROOT", Path.Combine(Environment.GetEnvironmentVariable("MCLAUNCHER_HOME")!, "game"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App();
        app.InitializeComponent();
        var vm = (MainViewModel)Resolve(typeof(MainViewModel))!;
        var window = new MainWindow(vm);
        Check("首帧构造不进行资源检索", State.Searches == 0);
        Check("首次启动显示引导", vm.IsSetupOpen);
        Pump(vm.OpenSetupCommand.ExecuteAsync(null));
        Check("只显示正式版并选择元数据最新版本", vm.SetupVersions.Count == 2 && vm.SetupVersion?.Id == "setup-test");
        using (var held = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(vm.GameRoot, "跨页面验证"))
        {
            vm.Download.SelectedVersion = new VersionItem("setup-test", "release", DateTimeOffset.UtcNow);
            Pump(vm.Download.InstallVersionCommand.ExecuteAsync(null));
            Check("下载页受共享写任务互斥保护", vm.Download.Status.Contains("跨页面验证") && State.Downloads == 0);
            Pump(vm.InstallSetupCommand.ExecuteAsync(null));
            Check("安装向导显示共享任务冲突", vm.SetupError.Contains("跨页面验证") && State.Downloads == 0);
        }
        vm.SetupMemoryText = "bad";
        Pump(vm.InstallSetupCommand.ExecuteAsync(null));
        Check("无效内存留在引导中且不安装", vm.IsSetupOpen && vm.SetupError.Contains("内存") && State.Downloads == 0);
        vm.SetupMemoryText = "2048";
        vm.SetupOfflineName = "无效";
        Pump(vm.InstallSetupCommand.ExecuteAsync(null));
        Check("无效用户名不安装", vm.SetupError.Contains("用户名") && State.Downloads == 0);
        vm.SetupOfflineName = "Tester_01";
        Render(window, 1000, 640, "setup-1000.png");
        Render(window, 820, 560, "setup-820.png");
        State.FailJava = true;
        Pump(vm.InstallSetupCommand.ExecuteAsync(null));
        Check("失败后可编辑并重试", vm.IsSetupOpen && !vm.IsSetupBusy && vm.SetupError.Contains("安装失败") && vm.InstallSetupCommand.CanExecute(null));
        State.FailJava = false;
        State.CancelJava = () => vm.CancelCommand.Execute(null);
        Pump(vm.InstallSetupCommand.ExecuteAsync(null));
        Check("取消后保留引导并恢复按钮", vm.IsSetupOpen && !vm.IsSetupBusy && vm.SetupStatus.Contains("取消") && vm.InstallSetupCommand.CanExecute(null));
        State.CancelJava = null;
        vm.SetupLaunchAfterInstall = false;
        Pump(vm.InstallSetupCommand.ExecuteAsync(null));
        Check("成功后关闭引导并选中正确实例", !vm.IsSetupOpen && vm.SelectedInstance?.InstanceId == "setup-test");
        Check("内存与离线身份已保存", InstanceSettings.Load(vm.GameRoot, "setup-test").MemoryMb == 2048 && vm.OfflineName == "Tester_01");
        var existing = (MainViewModel)Resolve(typeof(MainViewModel))!;
        Check("已有版本不自动显示引导", !existing.IsSetupOpen);
        Pump(existing.OpenSetupCommand.ExecuteAsync(null));
        Check("已有版本可手动重新打开引导", existing.IsSetupOpen);
        existing.CloseSetupCommand.Execute(null);
        Check("可跳过引导", !existing.IsSetupOpen);
        Pump(existing.OpenSetupCommand.ExecuteAsync(null));
        existing.SetupLaunchAfterInstall = true;
        Pump(existing.InstallSetupCommand.ExecuteAsync(null));
        Check("向导完成后释放写权限再准备启动", !existing.IsSetupOpen && !existing.StatusText.Contains("正在安装游戏") && !string.IsNullOrEmpty(existing.DiagnosisText));
        var metadata = new Launcher.Application.Install.InstanceMetadata("second", null, null, null, "setup-test", null, "setup-test");
        metadata.Write(existing.GameRoot, "setup-test-2");
        Launcher.Application.Instances.InstanceRegistry.LoadOrMigrate(existing.GameRoot).Register("setup-test-2", "setup-test", metadata);
        var secondVm = (MainViewModel)Resolve(typeof(MainViewModel))!;
        secondVm.SelectedInstance = secondVm.Instances.Single(i => i.InstanceId == "setup-test-2");
        secondVm.Download.SelectedCategory = secondVm.Download.Categories.Single(c => c.Category == Launcher.Domain.Content.DownloadCategory.Mod);
        secondVm.Download.RefreshTarget();
        Check("下载资源使用所选实例目录而非共用版本 ID", secondVm.Download.TargetText.Contains("setup-test-2"));
        Browser(secondVm);
        Console.WriteLine($"[ok] {_checks} 项 WPF 流程检查通过");
        window.Close();
        app.Shutdown();
        Serilog.Log.CloseAndFlush();
        Directory.Delete(root, recursive: true);
        return 0;
    }
    private static void Browser(MainViewModel vm)
    {
        vm.CloseSetupCommand.Execute(null); vm.IsInstanceSection = false;
        var browser = vm.Download;
        Pump(browser.EnsureLoadedAsync());
        Check("首次进入资源页加载热门且不过滤当前实例", browser.Results.Count == 20 && State.LastRequest is { Query.Length:0, GameVersion:null, Sort:ResourceSort.Popularity });
        browser.Query = "sodium"; browser.GameVersionFilter = "1.21.4"; browser.LoaderFilter = "fabric";
        browser.SelectedTag = browser.ResourceTags.Single(t => t.Id == "optimization");
        browser.SelectedSort = browser.SortOptions.Single(s => s.Value == ResourceSort.Updated);
        Pump(browser.SearchCommand.ExecuteAsync(null));
        Check("页面所有筛选传入请求", State.LastRequest is { Query:"sodium",GameVersion:"1.21.4",Loader:"fabric",Tag:"optimization",Sort:ResourceSort.Updated,Offset:0 });
        browser.SelectedResult = browser.Results[0];
        var downloads = State.Downloads; Pump(browser.InstallCommand.ExecuteAsync(null));
        Check("筛选版本与目标实例不一致时不安装", State.Downloads == downloads && browser.Status.Contains("请切换实例"));
        Check("分页依据平台总量", browser.TotalPages == 3 && browser.NextPageCommand.CanExecute(null));
        Pump(browser.NextPageCommand.ExecuteAsync(null));
        Check("翻页保留筛选与排序", browser.PageNumber == 2 && State.LastRequest is { Offset:20,Sort:ResourceSort.Updated,Query:"sodium",GameVersion:"1.21.4" });
        browser.Query = "changed";
        Check("修改条件清除旧结果并回到首页", browser.Results.Count == 0 && browser.PageNumber == 1 && !browser.NextPageCommand.CanExecute(null));
        Pump(browser.ResetFiltersCommand.ExecuteAsync(null));
        Check("重置条件回到热门推荐", State.LastRequest is { Query.Length:0, GameVersion:null,Loader:null,Tag:null,Sort:ResourceSort.Popularity });
        var window = new MainWindow(vm);
        Render(window, 1000, 640, "browser-1000.png"); Render(window, 820, 560, "browser-820.png");
        foreach (var category in new[] { DownloadCategory.Mod, DownloadCategory.Modpack, DownloadCategory.ResourcePack, DownloadCategory.Datapack, DownloadCategory.Shader })
        {
            browser.SelectedCategory = browser.Categories.Single(c => !c.IsSeparator && c.Category == category);
            Pump(browser.SearchCommand.ExecuteAsync(null));
            Check("同一检索页适用于 " + category, State.LastRequest!.Category == category && browser.Results.All(r => r.Category == category) && browser.ShowsLoaderFilter == category.UsesLoader());
        }
        State.FailSearch = true; Pump(browser.SearchCommand.ExecuteAsync(null));
        Check("检索失败恢复按钮并禁用旧分页", !browser.IsBusy && browser.Status.Contains("失败") && browser.Results.Count == 0 && !browser.NextPageCommand.CanExecute(null));
        State.FailSearch = false;
        State.DelaySearch = true;
        var pending = browser.SearchCommand.ExecuteAsync(null);
        browser.CancelCommand.Execute(null); Pump(pending);
        Check("取消检索恢复空闲状态", !browser.IsBusy && browser.Status.Contains("取消") && browser.SearchCommand.CanExecute(null));
        State.DelaySearch = false;
        State.IgnoreCancellation = true; State.DelaySearch = true;
        pending = browser.SearchCommand.ExecuteAsync(null);
        State.DelaySearch = false; State.IgnoreCancellation = false;
        browser.SelectedCategory = browser.Categories.Single(c => c.Category == DownloadCategory.Mod);
        State.Pending!.SetResult(State.Page(State.LastRequest!)); Pump(pending);
        Check("切换分类丢弃延迟返回的旧结果", browser.Results.Count == 20 && browser.Results.All(r => r.Category == DownloadCategory.Mod));
        State.CfConfigured = false; browser.SelectedSource = DownloadViewModel.CurseForgeSource;
        Check("未配置 CF 可清楚提示并切源", browser.ShowsCurseForgeHint && browser.Status.Contains("Key") && !browser.IsBusy);
        State.CfConfigured = true; State.DenyCfSearch = true; browser.Query = "keyword";
        Pump(browser.SearchCommand.ExecuteAsync(null));
        Check("CF 无权限不伪造热门推荐", browser.Results.Count == 0 && browser.Status.Contains("不能调搜索"));
        browser.Query = "123"; Pump(browser.SearchCommand.ExecuteAsync(null));
        Check("CF 无搜索权限支持正确类型的项目 ID", browser.Results.Count == 1 && browser.Results[0].Id == "123");
        State.CfClassId = 12; Pump(browser.SearchCommand.ExecuteAsync(null));
        Check("CF ID 查询拒绝错误资源类型", browser.Results.Count == 0 && browser.Status.Contains("类型不匹配"));
        State.CfClassId = 6; State.DenyCfSearch = false;
        browser.SelectedSource = DownloadViewModel.ModrinthSource;
        browser.SelectedCategory = browser.Categories.Single(c => c.Category == DownloadCategory.MinecraftVersion);
        Pump(browser.EnsureLoadedAsync());
        Check("活动版与两个远古版分别归组", browser.VersionGroups.Any(g => g.Key == "活动版" && g.Versions.Single().Id == "24w14potato") && browser.VersionGroups.Any(g => g.Key == "远古 Beta") && browser.VersionGroups.Any(g => g.Key == "远古 Alpha"));
        Check("活动版本不会混入预览组", browser.VersionGroups.Single(g => g.Key == "预览版").Versions.All(v => v.Id != "24w14potato"));
        Render(window, 1000, 640, "versions-1000.png");
        window.Close();
    }
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception(name);
        _checks++; Console.WriteLine("[ok] " + name);
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher);
        task.ContinueWith(_ => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Render(MainWindow window, int width, int height, string name)
    {
        var grid = (Grid)window.Content;
        grid.Measure(new Size(width, height)); grid.Arrange(new Rect(0, 0, width, height)); grid.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
        Check($"{width}×{height} 界面可渲染", bitmap.PixelWidth == width);
    }
    private static object? Resolve(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            return typeof(NullLogger<>).MakeGenericType(type.GenericTypeArguments).GetField("Instance")!.GetValue(null);
        if (type == typeof(DownloadSourceProvider)) return new DownloadSourceProvider(AppPaths.DataDirectory);
        if (type.IsInterface)
        {
            var proxy = DispatchProxy.Create(type, typeof(SetupProxy));
            ((SetupProxy)proxy).Shared = State;
            return proxy;
        }
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : Resolve(p.ParameterType)).ToArray());
    }
}
public class SetupProxy : DispatchProxy
{
    public SetupProxy? Shared;
    public int Downloads;
    public bool FailJava;
    public int Searches;
    public ResourceSearchRequest? LastRequest;
    public bool FailSearch, DelaySearch, IgnoreCancellation, CfConfigured, DenyCfSearch;
    public int CfClassId = 6;
    public TaskCompletionSource<ModrinthSearchResponse>? Pending;
    public ModrinthSearchResponse Page(ResourceSearchRequest request) => new(
        Enumerable.Range(request.Offset, Math.Min(20, 45-request.Offset)).Select(i =>
            new ModrinthProject("project"+i,"slug"+i,"资源 "+i,"功能说明：支持指定的游戏版本和资源类型。","作者",null,1000+i,100,["optimization","fabric"],["1.21.4"]) { ProjectType = request.Category.ModrinthProjectType(), AllProjectTypes=[request.Category.ModrinthProjectType()!], DateModified=DateTimeOffset.UtcNow }).ToArray(),45,request.Offset,20);

    public Action? CancelJava;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var state = Shared ?? this;
        switch (method!.Name)
        {
            case "GetManifestAsync": return Task.FromResult(new VersionManifest { Latest = new LatestVersions { Release = "setup-test" }, Versions = [new() { Id="older", Type="release" }, new() { Id="snapshot", Type="snapshot" }, new() { Id="setup-test", Type="release" }, new() { Id="24w14potato",Type="snapshot" }, new() { Id="b1.7.3",Type="old_beta" }, new() { Id="a1.2.6",Type="old_alpha" }] });
            case "GetVersionAsync": return Task.FromResult(new VersionJson { Id = "setup-test", MainClass = "test.Main", JavaVersion = new JavaVersionRef { MajorVersion = 21 } });
            case "DownloadAsync": state.Downloads++; return Task.FromResult(new DownloadSummary(0,0,0,0,TimeSpan.Zero));
            case "EnsureAsync": if (state.FailJava) throw new IOException("模拟网络错误"); state.CancelJava?.Invoke(); return Task.FromResult(new JavaInstall("java.exe",21,"21","test","x64"));
            case "DiscoverAsync": return Task.FromResult<IReadOnlyList<JavaInstall>>([new("java.exe", 21, "21", "test", "x64")]);
            case "get_IsConfigured": return state.CfConfigured;
            case "GetCategoriesAsync": return Task.FromResult<IReadOnlyList<ResourceTag>>([new("optimization", "性能优化"), new("adventure", "冒险")]);
            case "SearchAsync":
                state.Searches++; state.LastRequest = (ResourceSearchRequest)args![0]!;
                if (method.ReturnType == typeof(Task<CfSearchResponse>))
                {
                    if (state.DenyCfSearch) return Task.FromException<CfSearchResponse>(new CurseForgeSearchUnavailableException("403"));
                    return Task.FromResult(new CfSearchResponse());
                }
                if (state.FailSearch) return Task.FromException<ModrinthSearchResponse>(new IOException("模拟断网"));
                if (state.DelaySearch)
                {
                    state.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!state.IgnoreCancellation) ((CancellationToken)args[1]!).Register(() => state.Pending.TrySetCanceled());
                    return state.Pending.Task;
                }
                return Task.FromResult(state.Page(state.LastRequest));
            case "GetModAsync": return Task.FromResult<CfMod?>(new CfMod { Id=123,ClassId=state.CfClassId,Name="CF fixture" });
            case "Protect": return System.Text.Encoding.UTF8.GetBytes((string)args![0]!);
            case "Rewrite": return args![0];
        }
        if (method.ReturnType == typeof(void)) return null;
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var t = method.ReturnType.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, [t.IsValueType ? Activator.CreateInstance(t) : null]);
        }
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
