using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Mods;
using Launcher.Domain.Content;
using Launcher.Domain.Install;
using Launcher.Infrastructure.Meta;

internal sealed class ResourceFixture : IModrinthClient, ICurseForgeClient, IResourcePresentationClient, IDownloader
{
    public readonly List<string> Downloaded=[];
    public int Icons,Translations,ExactCfFile;
    public bool FailVersions,FailTranslations,MixedReleases;
    public string? LastQuery;
    public int CatalogCount, LastLimit, LastOffset;
    public bool FailPage;
    public TaskCompletionSource<IReadOnlyList<ModrinthVersion>>? PendingVersions;
    public TaskCompletionSource<DownloadSummary>? PendingDownload;
    public TaskCompletionSource<ModrinthProjectInfo?>? PendingDependency;
    public static readonly byte[] Icon=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNQOpP2HwAEvAJUSd95QAAAAABJRU5ErkJggg==");
    public bool IsConfigured=>true;
    private static byte[] FileBytes(string name)
    {
        if(!name.EndsWith(".mrpack")) return Encoding.UTF8.GetBytes(name);
        using var stream=new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        using(var writer=new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
            writer.Write("""{"formatVersion":1,"game":"minecraft","versionId":"old","name":"Fixture Pack","files":[],"dependencies":{"minecraft":"1.21.4"}}""");
        return stream.ToArray();
    }
    private static ModrinthFile File(string name)
    { var data=FileBytes(name);return new("https://fixture.invalid/"+name,name,data.Length,true,new(Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(),null)); }
    public static ModrinthVersion Version(string id,string number,string game,string loader,string file,params ModrinthDependency[] deps)=>new(id,"sodium",number,number,[game],[loader],DateTimeOffset.Parse("2026-10-20T00:00:00Z"),[File(file)],deps){Downloads=15000,VersionType="release"};
    public Task<ModrinthSearchResponse> SearchAsync(string q,DownloadCategory category,string? game=null,string? loader=null,int limit=20,int offset=0,CancellationToken ct=default)
    {
        LastQuery=q;LastLimit=limit;LastOffset=offset;
        if(FailPage) {FailPage=false;throw new HttpRequestException("page failure");}
        var project=new ModrinthProject(category==DownloadCategory.Modpack?"pack":"sodium",category==DownloadCategory.Modpack?"fixture-pack":"sodium",category==DownloadCategory.Modpack?"Fixture Pack":"Sodium","fixture original description","Fixture Author","https://fixture.invalid/icon.png",253000000,10,["fabric","neoforge","optimization"],["1.21.4","1.20.1"]){ProjectType=category.ModrinthProjectType(),AllProjectTypes=[category.ModrinthProjectType()!],DateModified=DateTimeOffset.Parse("2026-10-20T00:00:00Z")};
        return Task.FromResult(CatalogCount>0 ? new ModrinthSearchResponse(Enumerable.Range(offset,Math.Min(limit,Math.Max(0,CatalogCount-offset))).Select(i=>project with{ProjectId="catalog-"+i,Title="Project "+i}).ToArray(),CatalogCount,offset,limit) : new ModrinthSearchResponse([project],1,offset,limit));
    }
    public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string id,string? game=null,string? loader=null,CancellationToken ct=default)
    {
        if(FailVersions) throw new HttpRequestException("fixture version failure");
        if(PendingVersions is {} pending) {PendingVersions=null;return pending.Task;}
        IReadOnlyList<ModrinthVersion> values=id=="dep"?[Version("dep-file","1.0","1.21.4","fabric","dependency.jar") with {ProjectId="dep"}]
            : id=="pack"?[Version("pack-new","new","1.21.4","fabric","pack-new.mrpack") with{ProjectId="pack"},Version("pack-old","old","1.21.4","fabric","pack-old.mrpack") with{ProjectId="pack"}]
            :[Version("new","3.0","1.21.4","neoforge","new.jar"),Version("old","2.0","1.21.4","fabric","old.jar",new(null,"dep-file","required"),new("optional",null,"optional")),Version("ancient","1.0","1.20.1","fabric","ancient.jar")];
        if(MixedReleases && id=="sodium") values=[
            Version("channel-alpha","3.0-alpha","1.21.4","fabric","alpha.jar") with{VersionType="alpha"},
            Version("channel-beta","2.0-beta","1.21.4","fabric","beta.jar") with{VersionType="beta"},
            Version("channel-release","1.0","1.21.4","fabric","stable.jar") with{VersionType="release"},
            Version("channel-unknown","4.0","1.21.4","fabric","unknown.jar") with{VersionType=null}];
        return Task.FromResult<IReadOnlyList<ModrinthVersion>>(values.Where(v=>(game is null||v.GameVersions.Contains(game))&&(loader is null||v.Loaders.Contains(loader))).ToArray());
    }
    public Task<ModrinthProjectInfo?> GetProjectAsync(string id,CancellationToken ct=default)
    {
        if(id=="pack") return Task.FromResult<ModrinthProjectInfo?>(new(id,"fixture-pack","Fixture Pack","整合包介绍") {Body="## 探索一个新的世界\n[![Install](https://fixture.invalid/badge.svg)](https://fixture.invalid/install)\n\n收集、建造，与朋友一起开启冒险。",Gallery=[new("https://fixture.invalid/cover.png",true,"封面")]});
        if(PendingDependency is {} pending) { PendingDependency=null;return pending.Task; }
        return Task.FromResult<ModrinthProjectInfo?>(new(id,"fabric-api","Fabric API","library"));
    }
    public Task<ModrinthVersion?> GetVersionAsync(string id,CancellationToken ct=default)=>Task.FromResult<ModrinthVersion?>(Version("dep-file","1.0","1.21.4","fabric","dependency.jar") with{ProjectId="dep"});
    public Task<IReadOnlyList<ResourceTranslation>> GetTranslationsAsync(string source,IReadOnlyList<string> ids,CancellationToken ct=default)
    { Translations++;if(FailTranslations) throw new HttpRequestException();return Task.FromResult<IReadOnlyList<ResourceTranslation>>(ids.Select(id=>new ResourceTranslation(id,"fixture original description","用于检查下载中心显示的中文简介。")).ToArray()); }
    public Task<byte[]?> GetIconAsync(string url,CancellationToken ct=default) {Icons++;return Task.FromResult<byte[]?>(Icon);}
    public Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries,string root,IProgress<DownloadProgress>? progress=null,CancellationToken ct=default)
    {
        if(PendingDownload is {} pending)return pending.Task.WaitAsync(ct);
        foreach(var entry in entries)
        {
            ct.ThrowIfCancellationRequested();Downloaded.Add(entry.RelativePath);
            var path=Path.Combine(root,entry.RelativePath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);System.IO.File.WriteAllBytes(path,FileBytes(Path.GetFileName(path)));
        }
        return Task.FromResult(new DownloadSummary(entries.Count,entries.Count,0,entries.Sum(e=>e.Size),TimeSpan.Zero));
    }
    Task<CfSearchResponse> ICurseForgeClient.SearchAsync(string q,DownloadCategory cat,string? game,string? loader,int pageSize,int index,CancellationToken ct)=>
        Task.FromResult(new CfSearchResponse{Data=[Mod(cat)]});
    private static CfMod Mod(DownloadCategory cat)=>new(){Id=100,Name="AppleSkin",Slug="appleskin",ClassId=cat.CurseForgeClassId()??6,Summary="fixture original description",DownloadCount=9800000,DateModified=DateTimeOffset.Parse("2026-10-19T00:00:00Z"),Logo=new(){Url="https://fixture.invalid/cf-icon.png"},LatestFiles=[Cf(101)]};
    private static CfFile Cf(int id)=>new(){Id=id,ModId=100,DisplayName=id==101?"Latest":"Older",FileName=$"cf-{id}.jar",FileLength=FileBytes($"cf-{id}.jar").Length,GameVersions=["1.21.4","Fabric"],DownloadCount=12345,FileDate=DateTimeOffset.Parse("2026-10-18T00:00:00Z"),ReleaseType=1,DownloadUrl=$"https://fixture.invalid/cf-{id}.jar",Dependencies=id==102?[new(){ModId=200,RelationType=3}]:[]};
    public Task<CfMod?> GetModAsync(int id,CancellationToken ct=default)=>Task.FromResult<CfMod?>(id==200?new(){Id=200,Name="Fabric API",Slug="fabric-api"}:Mod(DownloadCategory.Mod));
    public Task<IReadOnlyList<CfFile>> GetFilesAsync(int id,string? game=null,string? loader=null,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<CfFile>>(id==200?[new(){Id=201,ModId=200,DisplayName="API",FileName="cf-dep.jar",FileLength=FileBytes("cf-dep.jar").Length,GameVersions=["1.21.4","Fabric"],ReleaseType=1,DownloadUrl="https://fixture.invalid/cf-dep.jar"}]:[Cf(101),Cf(102)]);
    public Task<CfFileResponse> GetFilesPageAsync(int id,string? game=null,string? loader=null,int index=0,CancellationToken ct=default)=>Task.FromResult(new CfFileResponse{Data=index==0?[Cf(101)]:[Cf(102)],Pagination=new(){TotalCount=2}});
    public Task<CfFile?> GetFileAsync(int modId,int id,CancellationToken ct=default){ExactCfFile=id;return Task.FromResult<CfFile?>(Cf(id));}
    public Task<string?> GetDownloadUrlAsync(int mod,int file,CancellationToken ct=default)=>Task.FromResult<string?>("https://fixture.invalid/file.jar");
    public CfUrlRef? ParseUrl(string url)=>null;
}

internal static class ResourceCenterChecks
{
    public static void Run(MainViewModel model,Window window,ResourceFixture fixture,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,Action<Control> click)
    {
        var vm=model.Download;
        void Wait(Task task) {var clock=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<5000)pump(10);task.GetAwaiter().GetResult();pump(80);}
        model.SelectedInstance=model.Instances.First(i=>i.VersionId.StartsWith("fabric-loader-9.1.0"));
        model.IsInstanceSection=false;vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Mod);vm.SelectedSource=DownloadViewModel.ModrinthSource;
        vm.Query="钠";Wait(vm.SearchCommand.ExecuteAsync(null));
        var item=vm.Results.Single();
        check("资源列表显示加载器、下载量和更新时间",item.LoadersText.Contains("Fabric")&&item.LoadersText.Contains("NeoForge")&&item.DownloadsText.Contains("亿")&&item.UpdatedText.Contains("2026-10-20"));
        check("项目图标使用真实图片数据",item.IconBytes?.Length>0&&fixture.Icons>0);
        check("中文名称保留英文原名，中文检索映射原项目",item.DisplayTitle=="钠"&&item.HasOriginalTitle&&item.Title=="Sodium"&&fixture.LastQuery=="sodium");
        check("中文简介显示来源并保留原文",item.DescriptionText.Contains("中文简介")&&item.TranslationHint.Contains("MCIM")&&item.Description=="fixture original description");
        capture("resources-list",true);
        var resultsScroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(c=>c.Name=="ResourceResultsScroll");
        var searchPanel=window.GetVisualDescendants().OfType<Border>().Single(c=>c.Name=="ResourceSearchPanel");
        check("搜索区紧凑并给资源列表保留更大高度",searchPanel.Bounds.Height<=150 && resultsScroll.Bounds.Height>=350 && resultsScroll.TranslatePoint(new Point(),window) is {Y:<250});
        check("资源列表不再显示中文简介提示行",!window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible&&t.Text==item.TranslationHint));
        check("下载量与更新日期跟在介绍末尾，介绍字号略增",window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible&&t.Text==item.ActivityText) && window.GetVisualDescendants().OfType<TextBlock>().Any(t=>ReferenceEquals(t.DataContext,item)&&t.Text==item.DescriptionText&&t.FontSize==14&&t.MaxLines==2));
        foreach(var category in new[]{DownloadCategory.ResourcePack,DownloadCategory.Shader,DownloadCategory.Datapack})
        {
            vm.SelectedCategory=vm.Categories.First(c=>c.Category==category); Wait(vm.SearchCommand.ExecuteAsync(null));
            var pack=vm.Results.Single();
            check(category+"列表省去不适用的加载器标注",!pack.ShowsLoaders&&!pack.SummaryText.Contains("加载器")&&!window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible&&t.Text=="不限定加载器"));
            Wait(vm.OpenResourceCommand.ExecuteAsync(pack));
            check(category+"版本分组和已选说明也省去加载器",!vm.ResourceDetailSummary.Contains("加载器")&&!vm.ResourceReleaseSummary.Contains("加载器")&&vm.ResourceReleaseGroups.All(g=>!g.Header.Contains("Fabric")&&!g.Header.Contains("加载器")));
            vm.BackToResourcesCommand.Execute(null);
        }
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Mod); Wait(vm.SearchCommand.ExecuteAsync(null));item=vm.Results.Single();
        click(window.GetVisualDescendants().OfType<Button>().Single(b=>ReferenceEquals(b.DataContext,item)));pump(100);
        check("点击项目进入版本页面，默认匹配当前实例",vm.IsResourceDetail&&!vm.IsResourceList&&vm.ResourceGameVersion=="1.21.4"&&vm.ResourceLoader=="fabric"&&vm.SelectedResourceRelease?.Id=="old");
        check("顶部解析指定版本的必需依赖且排除可选依赖",vm.ResourceDependencyHint.Contains("Fabric API")&&!vm.ResourceDependencyHint.Contains("optional")&&vm.ResourceDependencyHint.Contains("dep-file"));
        capture("resource-versions",true);
        check("发布版本不显示英文文件名行",!window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible&&t.Text==vm.SelectedResourceRelease!.FileName));
        fixture.Downloaded.Clear();Wait(vm.InstallResourceReleaseCommand.ExecuteAsync(vm.SelectedResourceRelease));
        check("模组安装精确使用所选旧版并下载指定前置",vm.Status.Contains("已装到")&&fixture.Downloaded.Contains("old.jar")&&fixture.Downloaded.Contains("dependency.jar")&&!fixture.Downloaded.Contains("new.jar"));
        vm.ResourceLoader="全部";pump(50);vm.ResourceGameVersion="全部";pump(50);
        check("版本筛选可显示不同游戏版本和加载器",vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).Select(r=>r.Id).Distinct().Count()==3);
        var incompatible=vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).First(r=>r.Id=="new");var before=fixture.Downloaded.Count;
        Wait(vm.InstallResourceReleaseCommand.ExecuteAsync(incompatible));
        check("不匹配加载器的发布版本不能安装",!incompatible.CanInstall&&fixture.Downloaded.Count==before);
        vm.SelectResourceReleaseCommand.Execute(incompatible);
        var pending=new TaskCompletionSource<ModrinthProjectInfo?>();fixture.PendingDependency=pending;
        vm.SelectResourceReleaseCommand.Execute(vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).First(r=>r.Id=="old"));pump(20);
        vm.SelectResourceReleaseCommand.Execute(vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).First(r=>r.Id=="ancient"));pump(20);
        pending.SetResult(new("late","fabric-api","LATE",""));pump(60);
        check("快速切换版本不会被旧依赖提示覆盖",vm.ResourceDependencyHint.Contains("未声明")&&!vm.ResourceDependencyHint.Contains("LATE"));
        vm.BackToResourcesCommand.Execute(null);pump(30);
        check("返回资源列表保留列表与图标",vm.IsResourceList&&!vm.IsResourceDetail&&vm.Results.Single()==item&&item.IconBytes?.Length>0);
        fixture.FailVersions=true;Wait(vm.OpenResourceCommand.ExecuteAsync(item));
        check("发布版本查询失败显示重试入口",vm.ResourceVersionsStatus.Contains("重试")&&vm.SelectedResourceRelease is null);fixture.FailVersions=false;
        Wait(vm.RetryResourceVersionsCommand.ExecuteAsync(null));check("发布版本查询可以重试恢复",vm.ResourceReleaseGroups.Count>0);
        vm.BackToResourcesCommand.Execute(null);vm.SelectedSource=DownloadViewModel.CurseForgeSource;Wait(vm.SearchCommand.ExecuteAsync(null));
        item=vm.Results.Single();check("CurseForge 同样显示项目图片与元数据",item.IconBytes?.Length>0&&item.LoadersText.Contains("Fabric")&&item.UpdatedText.Contains("2026-10-19")&&item.DisplayTitle=="苹果皮");
        Wait(vm.OpenResourceCommand.ExecuteAsync(item));check("CurseForge 历史版本保留分页入口",vm.HasMoreResourceVersions);
        Wait(vm.MoreResourceVersionsCommand.ExecuteAsync(null));
        var old=vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).First(r=>r.Id=="102");vm.SelectResourceReleaseCommand.Execute(old);pump(50);
        check("CurseForge 历史文件显示必需前置名称",vm.ResourceDependencyHint.Contains("Fabric API"));
        fixture.Downloaded.Clear();Wait(vm.InstallResourceReleaseCommand.ExecuteAsync(old));
        check("CurseForge 安装精确文件，不回退最新版",fixture.ExactCfFile==102&&fixture.Downloaded.Contains("cf-102.jar")&&fixture.Downloaded.Contains("cf-dep.jar")&&!fixture.Downloaded.Contains("cf-101.jar"));
        vm.BackToResourcesCommand.Execute(null);vm.SelectedSource=DownloadViewModel.ModrinthSource;
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Modpack&&!c.IsSeparator);vm.Query="";Wait(vm.SearchCommand.ExecuteAsync(null));
        item=vm.Results.Single();Wait(vm.OpenResourceCommand.ExecuteAsync(item));
        check("整合包默认展示封面和介绍",vm.ShowsPackOverview&&vm.HasPackCover&&vm.PackDescription.Contains("探索")&&!vm.PackDescription.Contains("[]("));
        vm.ShowPackVersionsCommand.Execute(null);pump(50);check("整合包版本入口独立显示",vm.ShowsResourceVersions&&!vm.ShowsPackOverview);
        old=vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).First(r=>r.Id=="pack-old");fixture.Downloaded.Clear();
        Wait(vm.InstallResourceReleaseCommand.ExecuteAsync(old));
        check("整合包精确下载所选历史版本并建立独立实例",fixture.Downloaded.Contains("pack-old.mrpack")&&!fixture.Downloaded.Contains("pack-new.mrpack")&&model.Instances.Any(i=>i.DisplayName.Contains("Fixture Pack")&&i.DisplayName.Contains("old")));
        vm.BackToResourcesCommand.Execute(null);
        foreach(var cat in new[]{DownloadCategory.ResourcePack,DownloadCategory.Datapack,DownloadCategory.Shader})
        {
            vm.SelectedCategory=vm.Categories.First(c=>c.Category==cat);Wait(vm.SearchCommand.ExecuteAsync(null));
            check(cat+" 列表显示图标和下载信息",vm.Results.Single().IconBytes?.Length>0&&vm.Results.Single().LoadersText=="不限定加载器"&&vm.Results.Single().DownloadsText.Length>0);
            if(cat==DownloadCategory.ResourcePack)
            {
                var project=vm.Results.Single();vm.LoaderFilter="fabric";
                Wait(vm.OpenResourceCommand.ExecuteAsync(project));
                check("资源包版本不受上次模组加载器筛选影响",vm.ResourceLoader=="全部"&&!vm.ShowsResourceLoaderFilter&&vm.ResourceReleaseGroups.Count>0);
                vm.BackToResourcesCommand.Execute(null);vm.LoaderFilter="全部";
            }
        }
        vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.Mod);fixture.FailTranslations=true;Wait(vm.SearchCommand.ExecuteAsync(null));
        check("中文服务失败仍有原文或内置说明且可浏览",vm.Results.Count==1&&vm.Results[0].DescriptionText.Length>0);fixture.FailTranslations=false;
        Wait(vm.OpenResourceCommand.ExecuteAsync(vm.Results[0]));window.Width=900;window.Height=640;pump(50);capture("resource-versions-compact",true);
        check("最小窗口保留顶部依赖提示与安装入口",window.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Name=="ResourceDependencyNotice").IsEffectivelyVisible&&window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="InstallResourceRelease").IsEffectivelyVisible);
        window.Width=1180;window.Height=800;vm.BackToResourcesCommand.Execute(null);
        fixture.MixedReleases=true;
        Wait(vm.OpenResourceCommand.ExecuteAsync(vm.Results.Single()));
        check("默认仅显示正式版并选中正式发布",vm.ResourceReleaseChannel=="正式版"&&vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).Single().Id=="channel-release"&&vm.SelectedResourceRelease?.ReleaseChannel=="正式版");
        vm.ResourceReleaseChannel="测试版";pump(40);
        check("测试版单独筛选且按钮明确下载类型",vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).Single().Id=="channel-beta"&&vm.SelectedResourceRelease?.DownloadText=="下载测试版"&&vm.ResourceInstallButtonText=="安装测试版"&&vm.ResourceReleaseSummary.Contains("测试版"));
        vm.ResourceReleaseChannel="开发版";pump(40);
        check("开发版单独筛选且按钮明确下载类型",vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).Single().Id=="channel-alpha"&&vm.SelectedResourceRelease?.DownloadText=="下载开发版"&&vm.ResourceInstallButtonText=="安装开发版");
        vm.ResourceReleaseChannel="全部版本";pump(40);
        check("全部版本仍按正式测试开发分别分组",vm.ResourceReleaseGroups.Count==4&&vm.ResourceReleaseGroups[0].Header.StartsWith("正式版")&&vm.ResourceReleaseGroups.Any(g=>g.Header.StartsWith("测试版"))&&vm.ResourceReleaseGroups.Any(g=>g.Header.StartsWith("开发版")));
        check("未知发布类型不会误标为正式版",vm.ResourceReleaseGroups.SelectMany(g=>g.Releases).Single(r=>r.Id=="channel-unknown").ReleaseChannel=="未标注类型");
        capture("release-channels",true);
        fixture.MixedReleases=false;vm.BackToResourcesCommand.Execute(null);model.IsInstanceSection=true;pump(60);
    }

    public static async Task CheckPresentationCacheAsync(string path,Action<string,bool> check)
    {
        var handler=new PresentationHandler();var client=new ResourcePresentationClient(new HttpClient(handler),Path.Combine(path,"presentation-cache"));
        var first=await client.GetTranslationsAsync("Modrinth",["test"]);var second=await client.GetTranslationsAsync("Modrinth",["test"]);
        check("中文简介批量查询并复用磁盘缓存",first.Count==1&&second.Count==1&&handler.Requests==1);
        var icon=await client.GetIconAsync("https://fixture.invalid/icon.png");await client.GetIconAsync("https://fixture.invalid/icon.png");
        check("项目图标复用缓存，保留原始图片",icon!.SequenceEqual(ResourceFixture.Icon)&&handler.Requests==2);
    }
    private sealed class PresentationHandler:HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.Method==HttpMethod.Post?new StringContent("""[{"project_id":"test","original":"original","translated":"中文介绍"}]"""):new ByteArrayContent(ResourceFixture.Icon)});
        }
    }
}
