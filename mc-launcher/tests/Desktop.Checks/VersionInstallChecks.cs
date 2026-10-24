using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Application.Java;
using Launcher.Domain.Install;
using Launcher.Domain.Java;
using Launcher.Domain.Loaders;
using Launcher.Domain.Modpacks;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Meta;

internal sealed class VersionInstallFixture : IVersionMetaClient, ILoaderMetaClient, IDownloader, IJavaProvisioner, IForgeInstaller, INeoForgeInstaller
{
    public int Downloads, Profiles, JavaPreparations;
    public bool Empty, FailQuery, FailInstall, CancelInstall;
    public TaskCompletionSource<IReadOnlyList<string>>? Pending;
    public (LoaderKind Kind, string Game, string Version)? LastInstall;
    public Task<VersionManifest> GetManifestAsync(CancellationToken ct=default) => Task.FromResult(new VersionManifest { Versions=[new(){Id="1.21.4", Type="release"}] });
    public Task<VersionJson> GetVersionAsync(string id,CancellationToken ct=default) => Task.FromResult(BaseVersion(id));
    public Task<AssetIndex> GetAssetIndexAsync(AssetIndexRef reference,CancellationToken ct=default) => Task.FromResult(new AssetIndex());
    public static VersionJson BaseVersion(string id) => new() { Id=id, Type="release", MainClass="net.minecraft.client.main.Main", JavaVersion=new(){MajorVersion=21} };
    public Task<IReadOnlyList<string>> GetLoaderVersionsAsync(LoaderKind kind,string game,CancellationToken ct=default)
    {
        if(Pending is { } delayed) { Pending=null; return delayed.Task; } // Deliberately ignores cancellation to test stale results.
        if(FailQuery) throw new HttpRequestException("fixture unavailable");
        return Task.FromResult<IReadOnlyList<string>>(Empty ? [] : ["9.2.0","9.1.0"]);
    }
    public Task<VersionJson> GetProfileAsync(LoaderKind kind,string game,string version,CancellationToken ct=default)
    {
        Profiles++; LastInstall=(kind,game,version);
        if(FailInstall) throw new IOException("fixture loader failure");
        if(CancelInstall) throw new OperationCanceledException(ct);
        return Task.FromResult(new VersionJson { Id=$"{kind.ToString().ToLowerInvariant()}-loader-{version}-{game}", InheritsFrom=game, MainClass="fixture.Loader" });
    }
    public Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries,string root,IProgress<DownloadProgress>? progress=null,CancellationToken ct=default)
    {
        Downloads++; ct.ThrowIfCancellationRequested();
        return Task.FromResult(new DownloadSummary(entries.Count,0,entries.Count,0,TimeSpan.Zero));
    }
    public Task<JavaInstall> EnsureAsync(int major,string data,IProgress<JavaProvisionProgress>? progress=null,CancellationToken ct=default)
    { JavaPreparations++; return Task.FromResult(new JavaInstall("fixture-java",major,major.ToString(),"fixture","x64")); }
    Task<string> IForgeInstaller.InstallAsync(string game,string version,string root,IProgress<ModpackImportProgress>? progress,CancellationToken ct) => ForgeLike(LoaderKind.Forge,game,version,root,ct);
    Task<string> INeoForgeInstaller.InstallAsync(string game,string version,string root,IProgress<ModpackImportProgress>? progress,CancellationToken ct) => ForgeLike(LoaderKind.NeoForge,game,version,root,ct);
    private Task<string> ForgeLike(LoaderKind kind,string game,string version,string root,CancellationToken ct)
    {
        LastInstall=(kind,game,version); ct.ThrowIfCancellationRequested();
        var id=$"{game}-{kind.ToString().ToLowerInvariant()}-{version}";
        var dir=Path.Combine(root,"versions",id); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,id+".json"),JsonSerializer.Serialize(new VersionJson { Id=id, InheritsFrom=game, MainClass="fixture.Loader" }));
        return Task.FromResult(id);
    }
}

internal static class VersionInstallChecks
{
    public static void Run(MainViewModel model,Window window,VersionInstallFixture fixture,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,Action<Control> click)
    {
        window.Width=1180; window.Height=800; pump(50);
        var vm=model.Download;
        T Find<T>(string name) where T:Control => window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        void Wait(Task task) { var timer=System.Diagnostics.Stopwatch.StartNew(); while(!task.IsCompleted && timer.ElapsedMilliseconds<4000) pump(10); task.GetAwaiter().GetResult(); pump(50); }
        model.IsInstanceSection=false; pump(100);
        vm.SelectVersionCommand.Execute(new VersionItem("1.21.4","release",DateTimeOffset.UtcNow)); pump(100);
        check("选择游戏版本后进入加载器配置，默认原版",vm.IsVersionConfiguration && !vm.IsVersionList && vm.SelectedInstallLoader?.Kind==LoaderKind.Vanilla && vm.InstallVersionCommand.CanExecute(null));
        check("加载器选项使用有效的原色图标",vm.InstallLoaders.All(c=>Launcher.Desktop.Controls.IconCatalog.Items.ContainsKey(c.Icon)));
        capture("install-vanilla",true);
        foreach(var kind in new[]{LoaderKind.Fabric,LoaderKind.Quilt,LoaderKind.Forge,LoaderKind.NeoForge})
        {
            var choice=vm.InstallLoaders.Single(c=>c.Kind==kind);
            click(window.GetVisualDescendants().OfType<Button>().Single(b=>ReferenceEquals(b.DataContext,choice)));
            check(kind+" 互斥选中并加载可用版本",vm.InstallLoaders.Count(c=>c.IsSelected)==1 && vm.SelectedInstallLoader==choice && vm.InstallLoaderVersions.Count==2);
            Find<ComboBox>("InstallLoaderVersion").SelectedItem="9.1.0"; pump(40);
            check(kind+" 版本选择绑定安装摘要",vm.SelectedInstallLoaderVersion=="9.1.0" && vm.InstallSummary.Contains("9.1.0"));
            if(kind==LoaderKind.Fabric) capture("install-fabric",true);
            Wait(vm.InstallVersionCommand.ExecuteAsync(null));
            check(kind+" 安装使用用户所选版本并回到可操作状态",fixture.LastInstall==(kind,"1.21.4","9.1.0") && vm.Status.Contains("安装完成") && !vm.IsBusy && model.Instances.Any(i=>i.VersionId.Contains("9.1.0")));
        }
        check("Forge 与 NeoForge 安装前准备所需 Java",fixture.JavaPreparations==2);
        var before=fixture.Profiles; fixture.LastInstall=null;
        vm.ChooseInstallLoaderCommand.Execute(vm.InstallLoaders[0]); Wait(vm.InstallVersionCommand.ExecuteAsync(null));
        check("原版路径不安装加载器",fixture.Profiles==before && fixture.LastInstall is null && vm.Status.Contains("安装完成"));
        fixture.Empty=true;
        vm.ChooseInstallLoaderCommand.Execute(vm.InstallLoaders.Single(c=>c.Kind==LoaderKind.Fabric)); pump(50);
        check("无兼容版本时禁用安装并给出提示",vm.InstallLoaderVersions.Count==0 && !vm.InstallVersionCommand.CanExecute(null) && vm.LoaderVersionStatus.Contains("暂无"));
        fixture.Empty=false; fixture.FailQuery=true; Wait(vm.ReloadLoaderVersionsCommand.ExecuteAsync(null));
        check("查询失败可重试且不残留旧版本",!vm.InstallVersionCommand.CanExecute(null) && vm.SelectedInstallLoaderVersion is null && vm.LoaderVersionStatus.Contains("重试"));
        fixture.FailQuery=false; Wait(vm.ReloadLoaderVersionsCommand.ExecuteAsync(null));
        check("查询重试恢复选择和安装",vm.InstallVersionCommand.CanExecute(null) && vm.SelectedInstallLoaderVersion=="9.2.0");
        fixture.FailInstall=true; Wait(vm.InstallVersionCommand.ExecuteAsync(null));
        check("加载器失败不误报本体安装为全部成功",vm.Status.Contains("安装失败") && !vm.IsBusy); fixture.FailInstall=false;
        fixture.CancelInstall=true; Wait(vm.InstallVersionCommand.ExecuteAsync(null));
        check("加载器取消后恢复安装入口",vm.Status=="已取消" && vm.InstallVersionCommand.CanExecute(null)); fixture.CancelInstall=false;
        var pending=new TaskCompletionSource<IReadOnlyList<string>>(); fixture.Pending=pending;
        var query=vm.ReloadLoaderVersionsCommand.ExecuteAsync(null); pump(40);
        check("查询期间禁止确认安装",vm.IsLoadingLoaderVersions && !vm.InstallVersionCommand.CanExecute(null));
        vm.ChooseInstallLoaderCommand.Execute(vm.InstallLoaders.Single(c=>c.Kind==LoaderKind.Quilt)); pump(40);
        pending.SetResult(["obsolete"]); Wait(query);
        check("快速切换加载器不被迟到结果覆盖",vm.SelectedInstallLoader?.Kind==LoaderKind.Quilt && !vm.InstallLoaderVersions.Contains("obsolete") && vm.SelectedInstallLoaderVersion=="9.2.0");
        pending=new(); fixture.Pending=pending; query=vm.ReloadLoaderVersionsCommand.ExecuteAsync(null); pump(30);
        click(Find<Button>("BackToVersions")); pending.SetResult(["obsolete"]); Wait(query);
        check("返回版本列表会清除加载器与旧请求",vm.IsVersionList && !vm.IsVersionConfiguration && vm.SelectedVersion is null && vm.SelectedInstallLoader?.Kind==LoaderKind.Vanilla && vm.InstallLoaderVersions.Count==0);
        vm.SelectVersionCommand.Execute(new VersionItem("26.3","release",DateTimeOffset.UtcNow)); pump(40);
        check("更换游戏版本重新选择安装方式",vm.SelectedInstallLoader?.Kind==LoaderKind.Vanilla && vm.SelectedInstallLoaderVersion is null && vm.InstallSummary.Contains("26.3"));
        window.Width=900;window.Height=640;pump(50);capture("install-compact",true);
        var button=Find<Button>("InstallGameVersion");
        check("最小窗口仍可确认安装",button.IsEffectivelyVisible && button.TranslatePoint(new Avalonia.Point(),window) is {Y:>=0} point && point.Y+button.Bounds.Height<=window.ClientSize.Height);
        window.Width=1180;window.Height=800;vm.BackToVersionsCommand.Execute(null);model.IsInstanceSection=true;pump(100);
    }

    public static async Task CheckMetadataAsync(Action<string,bool> check)
    {
        var meta=new LoaderMetaClient(new HttpClient(new MetadataHandler()));
        var forge=await meta.GetLoaderVersionsAsync(LoaderKind.Forge,"1.21.4");
        check("Forge 按游戏版本筛选并按数值降序",forge.SequenceEqual(new[]{"54.0.10","54.0.9"}));
        var neo=await meta.GetLoaderVersionsAsync(LoaderKind.NeoForge,"1.21.1");
        check("NeoForge 精确区分 1.21.1 与 1.21.10",neo.SequenceEqual(new[]{"21.1.10","21.1.9"}));
        neo=await meta.GetLoaderVersionsAsync(LoaderKind.NeoForge,"26.3");
        check("NeoForge 支持 26.x 新版编号",neo.SequenceEqual(new[]{"26.3.0.10","26.3.0.9-beta"}));
        neo=await meta.GetLoaderVersionsAsync(LoaderKind.NeoForge,"1.20.1");
        check("NeoForge 1.20.1 使用旧 Maven 布局",neo.SequenceEqual(new[]{"47.1.100","47.1.99"}));
        var quilt=await meta.GetLoaderVersionsAsync(LoaderKind.Quilt,"1.21.4");
        check("Quilt 嵌套响应去重并按版本降序",quilt.SequenceEqual(new[]{"0.28.1","0.27.0"}));
        check("不存在的 Fabric 游戏版本返回空列表",(await meta.GetLoaderVersionsAsync(LoaderKind.Fabric,"unsupported")).Count==0);
    }
    private sealed class MetadataHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var url=request.RequestUri!.AbsoluteUri;
            if(url.Contains("unsupported")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var values=url.Contains("minecraftforge") ? new[]{"1.21.4-54.0.9","1.21.4-54.0.10","1.21.1-52.0.1"}
                : url.Contains("/forge/") ? new[]{"1.20.1-47.1.99","1.20.1-47.1.100"}
                : new[]{"21.1.9","21.1.10","21.10.1","26.3.0.9-beta","26.3.0.10","26.3.1.3"};
            var body=url.Contains("quilt") ? """[{"loader":{"version":"0.27.0"}},{"loader":{"version":"0.28.1"}},{"loader":{"version":"0.28.1"}}]"""
                : "<metadata><versioning><versions>"+string.Concat(values.Select(v=>"<version>"+v+"</version>"))+"</versions></versioning></metadata>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }
}
