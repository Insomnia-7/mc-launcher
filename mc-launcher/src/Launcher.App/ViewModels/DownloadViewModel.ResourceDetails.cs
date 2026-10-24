using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Mods;
using Launcher.Domain.Content;

namespace Launcher.App.ViewModels;

public sealed partial class DownloadViewModel
{
    private readonly IResourcePresentationClient _presentation;
    private CancellationTokenSource? _resourceExtrasCts, _resourceDetailCts, _resourceDependencyCts;
    private readonly List<ResourceReleaseItem> _resourceReleases=[];
    private int _cfFilesLoaded;
    private bool _settingResourceFilters;
    [ObservableProperty] private DownloadItem? _openedResource;
    [ObservableProperty] private ResourceReleaseItem? _selectedResourceRelease;
    [ObservableProperty] private bool _isLoadingResourceVersions;
    [ObservableProperty] private string _resourceVersionsStatus="";
    [ObservableProperty] private string _resourceDependencyHint="选择发布版本后显示必需前置。";
    [ObservableProperty] private string _resourceGameVersion="全部";
    [ObservableProperty] private string _resourceLoader="全部";
    [ObservableProperty] private string _resourceReleaseChannel="正式版";
    public IReadOnlyList<string> ResourceReleaseChannels {get;}=["正式版","测试版","开发版","全部版本"];
    [ObservableProperty] private bool _hasMoreResourceVersions;
    public ObservableCollection<string> ResourceGameVersions {get;}=["全部"];
    public ObservableCollection<ResourceReleaseGroup> ResourceReleaseGroups {get;}=[];
    public string ResourceDetailTitle=>OpenedResource?.DisplayTitle??"";
    public string ResourceOriginalTitle=>OpenedResource?.Title??"";
    public string ResourceDetailSummary=>OpenedResource?.SummaryText??"";
    public byte[]? ResourceDetailIcon=>OpenedResource?.IconBytes;
    public bool IsResourceDetail=>IsSearchMode && OpenedResource is not null;
    public bool IsResourceList=>IsSearchMode && !IsResourceDetail;
    public bool ShowsDiscoverHeading=>IsVersionMode;
    public bool ShowsDownloadFooter=>!IsResourceList;
    public bool ShowsResourceLoaderFilter=>OpenedResource?.Category.UsesLoader()==true;
    public bool ShowsResourceDependencies=>IsResourceDetail && OpenedResource?.Category==DownloadCategory.Mod;
    public string ResourceInstallButtonText=>SelectedResourceRelease is {ReleaseChannel:not "未标注类型"} release?"安装"+release.ReleaseChannel:"安装所选版本";
    public string ResourceReleaseSummary=>SelectedResourceRelease is { } release ? $"已选：{release.Name} · {release.ReleaseChannel} · {release.LoaderText}" : "请选择发布版本";

    private DownloadItem ModrinthItem(ModrinthProject hit,DownloadCategory category)=>new(ModrinthSource,category,hit.ProjectId,hit.Title,hit.Author??"未知作者","",hit.Description)
    {
        Slug=hit.Slug,IconUrl=hit.IconUrl,LoadersText=ResourceChinese.Loaders(hit.Categories,category.UsesLoader()),
        DownloadsText=ResourceChinese.Count(hit.Downloads)+" 次下载",UpdatedText="更新 "+ResourceChinese.Date(hit.DateModified),
        SupportedGameVersions=hit.Versions,VersionsText=VersionsText(hit.Versions),OpenCommand=OpenResourceCommand,
    };
    private DownloadItem CurseForgeItem(CfMod mod,DownloadCategory category)=>new(CurseForgeSource,category,mod.Id.ToString(),mod.Name,mod.Author,"",mod.Summary)
    {
        Slug=mod.Slug,IconUrl=mod.Logo?.ThumbnailUrl??mod.Logo?.Url,
        LoadersText=ResourceChinese.Loaders(mod.LatestFiles.SelectMany(f=>f.GameVersions),category.UsesLoader()),
        DownloadsText=ResourceChinese.Count(mod.DownloadCount)+" 次下载",UpdatedText="更新 "+ResourceChinese.Date(mod.DateModified),
        SupportedGameVersions=mod.LatestFiles.SelectMany(f=>f.GameVersions).Where(v=>v.Length>0&&char.IsDigit(v[0])).Distinct().ToArray(),
        VersionsText=VersionsText(mod.LatestFiles.SelectMany(f=>f.GameVersions).Where(v=>!ResourceChinese.IsLoader(v))),OpenCommand=OpenResourceCommand,
    };

    private async Task EnrichResultsAsync(DownloadItem[] items)
    {
        _resourceExtrasCts?.Cancel();
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20)); _resourceExtrasCts=cts;
        try
        {
            async Task LoadIcon(DownloadItem item)
            {
                if(item.IconUrl is not {Length:>0} url) return;
                try { var bytes=await _presentation.GetIconAsync(url,cts.Token); if(!cts.IsCancellationRequested) item.IconBytes=bytes; }
                catch(Exception ex) when(ex is not OutOfMemoryException) { }
            }
            async Task Translate()
            {
                if(items.Length==0) return;
                try
                {
                    var translations=await _presentation.GetTranslationsAsync(items[0].Source,items.Select(i=>i.Id).ToArray(),cts.Token);
                    foreach(var translated in translations)
                    {
                        if(cts.IsCancellationRequested) return;
                        var item=items.FirstOrDefault(i=>i.Id==translated.ProjectId);
                        // Do not apply stale translations to changed upstream descriptions.
                        if(item is not null && item.Description==translated.Original) item.TranslatedDescription=translated.Translated;
                    }
                }
                catch(Exception ex) when(ex is not OutOfMemoryException) { }
            }
            await Task.WhenAll(items.Select(LoadIcon).Append(Translate()));
        }
        finally { if(ReferenceEquals(_resourceExtrasCts,cts)) _resourceExtrasCts=null; }
    }

    partial void OnOpenedResourceChanged(DownloadItem? oldValue,DownloadItem? newValue)
    {
        if(oldValue is not null) oldValue.PropertyChanged-=OpenedResourcePropertyChanged;
        if(newValue is not null) newValue.PropertyChanged+=OpenedResourcePropertyChanged;
        NotifyResourceView();
    }
    private void OpenedResourcePropertyChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)=>NotifyResourceHeader();
    private void NotifyResourceHeader()
    {
        OnPropertyChanged(nameof(ResourceDetailTitle));OnPropertyChanged(nameof(ResourceOriginalTitle));
        OnPropertyChanged(nameof(ResourceDetailSummary));OnPropertyChanged(nameof(ResourceDetailIcon));
    }
    private void NotifyResourceView()
    {
        NotifyResourceHeader();
        OnPropertyChanged(nameof(IsResourceDetail));OnPropertyChanged(nameof(IsResourceList));
        OnPropertyChanged(nameof(ShowsBrowseControls));OnPropertyChanged(nameof(ShowsResourceDependencies));OnPropertyChanged(nameof(ShowsResourceLoaderFilter));
        OnPropertyChanged(nameof(ShowsDiscoverHeading));OnPropertyChanged(nameof(ShowsDownloadFooter));InstallCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenResourceAsync(DownloadItem? item)
    {
        if(IsBusy || item is null) return;
        CloseResourceDetails();
        SelectedResult=item;OpenedResource=item;
        _settingResourceFilters=true;
        ResourceReleaseChannel="正式版";
        Status=string.Empty;
        var preferredGame=FilterValue(GameVersionFilter)??(item.Category==DownloadCategory.Modpack?"全部":ResolveGameVersion()??"全部");
        if(!ResourceGameVersions.Contains(preferredGame)) ResourceGameVersions.Add(preferredGame);
        ResourceGameVersion=preferredGame;
        ResourceLoader=item.Category.UsesLoader()?FilterValue(LoaderFilter)??(item.Category!=DownloadCategory.Modpack?ResolveLoader()??"全部":"全部"):"全部";
        _settingResourceFilters=false;
        await LoadResourceVersionsAsync();
    }

    [RelayCommand]
    private void BackToResources() { if(!IsBusy) CloseResourceDetails(); }
    private void CloseResourceDetails()
    {
        _resourceDetailCts?.Cancel();_resourceDependencyCts?.Cancel();
        OpenedResource=null;SelectedResourceRelease=null;
        _resourceReleases.Clear();ResourceReleaseGroups.Clear();HasMoreResourceVersions=false;
        IsLoadingResourceVersions=false;NotifyResourceView();
    }
    partial void OnResourceGameVersionChanged(string value)=>ResourceFilterChanged();
    partial void OnResourceLoaderChanged(string value)=>ResourceFilterChanged();
    partial void OnResourceReleaseChannelChanged(string value) { if(!_settingResourceFilters && IsResourceDetail && !IsBusy) RebuildResourceGroups(); }
    private void ResourceFilterChanged()
    {
        if(_settingResourceFilters || !IsResourceDetail || IsBusy) return;
        _=LoadResourceVersionsAsync();
    }

    [RelayCommand]
    private Task RetryResourceVersionsAsync()=>LoadResourceVersionsAsync();
    [RelayCommand]
    private Task MoreResourceVersionsAsync()=>HasMoreResourceVersions && !IsLoadingResourceVersions?LoadResourceVersionsAsync(true):Task.CompletedTask;
    private async Task LoadResourceVersionsAsync(bool append=false)
    {
        if(OpenedResource is not { } item || IsBusy) return;
        _resourceDetailCts?.Cancel();
        using var cts=new CancellationTokenSource();_resourceDetailCts=cts;
        var game=FilterValue(ResourceGameVersion);var loader=item.Category.UsesLoader()?FilterValue(ResourceLoader):null;
        if(!append) { _resourceReleases.Clear();SelectedResourceRelease=null;ResourceReleaseGroups.Clear();_cfFilesLoaded=0; }
        IsLoadingResourceVersions=true;ResourceVersionsStatus="正在读取发布版本…";
        try
        {
            if(item.Source==CurseForgeSource)
            {
                var page=await _curseForge.GetFilesPageAsync(int.Parse(item.Id),game,loader,_cfFilesLoaded,cts.Token);
                if(cts.IsCancellationRequested) return;
                foreach(var file in page.Data.OrderByDescending(f=>f.FileDate))
                    if(_resourceReleases.All(r=>r.Id!=file.Id.ToString())) _resourceReleases.Add(CfRelease(file));
                _cfFilesLoaded+=page.Data.Count;
                HasMoreResourceVersions=page.Data.Count>0 && _cfFilesLoaded<Math.Min(10000,page.TotalCount);
            }
            else
            {
                var versions=await _modrinth.GetVersionsAsync(item.Id,game,loader,cts.Token);
                if(cts.IsCancellationRequested) return;
                _resourceReleases.AddRange(versions.Select(MrRelease));HasMoreResourceVersions=false;
            }
            UpdateResourceGameVersions();RebuildResourceGroups();
        }
        catch(OperationCanceledException) when(cts.IsCancellationRequested) { }
        catch(Exception ex)
        { if(!cts.IsCancellationRequested) ResourceVersionsStatus="读取版本失败，可重试："+Launcher.Application.Diagnostics.ErrorText.Describe(ex); }
        finally { if(ReferenceEquals(_resourceDetailCts,cts)) { _resourceDetailCts=null;IsLoadingResourceVersions=false; } }
    }

    private ResourceReleaseItem MrRelease(ModrinthVersion v)=>new()
    {
        Id=v.Id,Name=v.Name,FileName=v.PrimaryFile?.Filename??"没有可下载文件",GameVersions=v.GameVersions,
        Loaders=v.Loaders.Where(ResourceChinese.IsLoader).ToArray(),Modrinth=v,
        Metadata=$"{ResourceChinese.Date(v.DatePublished)} · {ResourceChinese.Count(v.Downloads)} 次下载 · {(v.PrimaryFile?.Size??0)/1024d/1024d:0.##} MB",
        SelectCommand=SelectResourceReleaseCommand,InstallCommand=InstallResourceReleaseCommand,
    };
    private ResourceReleaseItem CfRelease(CfFile f)=>new()
    {
        Id=f.Id.ToString(),Name=f.DisplayName,FileName=f.FileName,
        GameVersions=f.GameVersions.Where(v=>v.Length>0 && char.IsDigit(v[0])).ToArray(),
        Loaders=f.GameVersions.Where(ResourceChinese.IsLoader).Select(v=>v.ToLowerInvariant()).Distinct().ToArray(),CurseForge=f,
        Metadata=$"{ResourceChinese.Date(f.FileDate)} · {ResourceChinese.Count(f.DownloadCount)} 次下载 · {f.FileLength/1024d/1024d:0.##} MB",
        SelectCommand=SelectResourceReleaseCommand,InstallCommand=InstallResourceReleaseCommand,
    };
    private void UpdateResourceGameVersions()
    {
        _settingResourceFilters=true;
        var selected=ResourceGameVersion;ResourceGameVersions.Clear();ResourceGameVersions.Add("全部");
        var values=_resourceReleases.SelectMany(r=>r.GameVersions).Concat(OpenedResource?.SupportedGameVersions??[]);
        if(OpenedResource?.Source==CurseForgeSource) values=values.Concat(GameVersions.Where(v=>v!="全部"));
        foreach(var game in values.Prepend(selected).Where(v=>v!="全部").Distinct()) ResourceGameVersions.Add(game);
        ResourceGameVersion=selected;_settingResourceFilters=false;
    }
    private void RebuildResourceGroups()
    {
        var game=FilterValue(ResourceGameVersion);var loader=FilterValue(ResourceLoader);
        var visible=_resourceReleases.Where(r=>(game is null || r.GameVersions.Contains(game))
            && (loader is null || r.Loaders.Contains(loader,StringComparer.OrdinalIgnoreCase))
            && (ResourceReleaseChannel=="全部版本" || r.ReleaseChannel==ResourceReleaseChannel)).OrderBy(r=>r.ChannelOrder).ToArray();
        ResourceReleaseGroups.Clear();
        foreach(var group in visible.GroupBy(r=>r.Header)) ResourceReleaseGroups.Add(new(group.Key,group.ToArray()){IsExpanded=ResourceReleaseGroups.Count==0});
        SelectedResourceRelease=visible.Contains(SelectedResourceRelease)?SelectedResourceRelease:visible.FirstOrDefault();
        ResourceVersionsStatus=visible.Length==0?"没有匹配的"+(ResourceReleaseChannel=="全部版本"?"发布版本":ResourceReleaseChannel)+"，可切换版本类型、游戏版本或加载器。":$"已显示 {visible.Length} 个发布版本"+(HasMoreResourceVersions?"，下方可加载更多。":"。");
        RefreshResourceCompatibility();
    }
    private void RefreshResourceCompatibility()
    {
        foreach(var release in _resourceReleases)
        {
            var category=OpenedResource?.Category;var problem="";
            if(ResourceReleaseChannel!="全部版本" && release.ReleaseChannel!=ResourceReleaseChannel) problem="当前发布类型筛选未包含此版本";
            else if(release.Modrinth is {PrimaryFile:null}) problem="没有可下载文件";
            else if(category!=DownloadCategory.Modpack)
            {
                if(_currentInstance() is null) problem="请先在左侧选择目标游戏实例";
                else if(!release.GameVersions.Contains(ResolveGameVersion()??"")) problem="与当前游戏版本不匹配";
                else if(category==DownloadCategory.Mod && (ResolveLoader() is not { } target || !release.Loaders.Contains(target,StringComparer.OrdinalIgnoreCase))) problem="请先选择匹配加载器的游戏实例";
            }
            release.CanInstall=!IsBusy && problem.Length==0;
            release.CompatibilityHint=problem.Length==0?(category==DownloadCategory.Modpack?"安装为新的独立实例":"安装到当前游戏实例"):problem;
        }
        InstallCommand.NotifyCanExecuteChanged();OnPropertyChanged(nameof(ResourceReleaseSummary));OnPropertyChanged(nameof(ResourceInstallButtonText));
    }

    [RelayCommand]
    private void SelectResourceRelease(ResourceReleaseItem? release) { if(!IsBusy && release is not null && _resourceReleases.Contains(release)) SelectedResourceRelease=release; }
    partial void OnSelectedResourceReleaseChanged(ResourceReleaseItem? value)
    {
        foreach(var release in _resourceReleases) release.IsSelected=ReferenceEquals(release,value);
        OnPropertyChanged(nameof(ResourceReleaseSummary));OnPropertyChanged(nameof(ResourceInstallButtonText));InstallCommand.NotifyCanExecuteChanged();
        _=LoadResourceDependenciesAsync(value);
    }
    [RelayCommand]
    private async Task InstallResourceReleaseAsync(ResourceReleaseItem? release)
    {
        RefreshResourceCompatibility();
        if(IsBusy || release is not {CanInstall:true} || !_resourceReleases.Contains(release)) return;
        SelectedResourceRelease=release;
        await InstallCommand.ExecuteAsync(null);
    }
    private async Task LoadResourceDependenciesAsync(ResourceReleaseItem? release)
    {
        _resourceDependencyCts?.Cancel();
        if(release is null || OpenedResource?.Category!=DownloadCategory.Mod) { ResourceDependencyHint="选择发布版本后显示必需前置。"; return; }
        using var cts=new CancellationTokenSource();_resourceDependencyCts=cts;
        ResourceDependencyHint="正在读取所选版本的必需前置…";
        try
        {
            var names=new List<string>();
            if(release.Modrinth is { } mr)
            {
                foreach(var dep in mr.RequiredDependencies)
                {
                    var projectId=dep.ProjectId;
                    if(projectId is null && dep.VersionId is { } versionId) projectId=(await _modrinth.GetVersionAsync(versionId,cts.Token))?.ProjectId;
                    if(projectId is null) { names.Add("未标识的必需前置（请查看作者说明）");continue; }
                    var project=await _modrinth.GetProjectAsync(projectId,cts.Token);
                    var name=project is null?$"项目 {projectId}":DescribeChinese(project.Slug,project.Title);
                    names.Add(name+(dep.VersionId is null?"":"（指定版本 "+dep.VersionId+"）"));
                }
            }
            else if(release.CurseForge is { } cf)
                foreach(var dep in cf.RequiredDependencies)
                {
                    var project=await _curseForge.GetModAsync(dep.ModId,cts.Token);
                    names.Add(project is null?$"项目 {dep.ModId}":DescribeChinese(project.Slug,project.Name));
                }
            if(!cts.IsCancellationRequested) ResourceDependencyHint=names.Count==0?"此发布版本未声明必需前置。"
                :"必需前置："+string.Join("、",names.Distinct())+"。安装时会一并解析并下载兼容的前置；无法取得必需前置时会提示安装失败。";
        }
        catch(OperationCanceledException) when(cts.IsCancellationRequested) { }
        catch(Exception) { if(!cts.IsCancellationRequested) ResourceDependencyHint="前置名称暂时加载失败，请重选版本重试。安装时仍会校验必需前置。"; }
        finally { if(ReferenceEquals(_resourceDependencyCts,cts)) _resourceDependencyCts=null; }
    }
    private static string DescribeChinese(string slug,string title)=>ResourceChinese.Lookup(slug,title).Name is { } name && name!=title?$"{name}（{title}）":title;
}
