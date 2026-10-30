using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;
using Launcher.Domain.Content;
using Launcher.Domain.Loaders;

namespace Launcher.App.ViewModels;

public sealed partial class DownloadViewModel
{
    public Func<DurableTaskRequest,Task>? QueueTask {get;set;}
    public Func<bool>? CancelPersistentTask {get;set;}
    public async Task<string> ResolveOfflineSkinVersionAsync(string instanceId)
    {
        var environment=InstanceEnvironment.Resolve(_gameRoot(),instanceId);
        var versions=await _modrinth.GetVersionsAsync("customskinloader",environment.GameVersion,environment.LoaderSlug);
        return (versions.FirstOrDefault(v=>v.VersionType=="release")??versions.FirstOrDefault())?.Id??throw new InvalidOperationException("没有适配此游戏版本和加载器的皮肤组件。");
    }
    private async Task<bool> QueueSelectedResourceAsync()
    {
        if(QueueTask is null||SelectedResult is not {} item||!CanInstall())return false;
        RefreshResourceCompatibility();
        if(IsResourceDetail&&SelectedResourceRelease is not {CanInstall:true})return true;
        var root=_gameRoot();var instance=_currentInstance();
        if(item.Category!=DownloadCategory.Modpack&&instance is null){Status="先在游戏库选择目标版本。";return true;}
        if(item.Category!=DownloadCategory.Modpack&&_installRequest is {} filters)
        {
            if(filters.GameVersion is {} game&&game!=ResolveGameVersion()||item.Category.UsesLoader()&&filters.Loader is {} loader&&loader!=ResolveLoader())
            {Status="检索条件与目标实例不一致，请切换实例或重置筛选。";return true;}
        }
        var release=IsResourceDetail?SelectedResourceRelease?.Id:null;
        if(release is null)
        {
            Status="正在固定本次下载版本…";
            if(item.Source==CurseForgeSource)
            {
                var files=await _curseForge.GetFilesAsync(int.Parse(item.Id),item.Category==DownloadCategory.Modpack?_installRequest?.GameVersion:ResolveGameVersion(),item.Category==DownloadCategory.Modpack?_installRequest?.Loader:item.Category.UsesLoader()?ResolveLoader():null);
                release=files.FirstOrDefault(f=>f.ReleaseType==1)?.Id.ToString()??files.FirstOrDefault()?.Id.ToString();
            }
            else
            {
                var versions=await _modrinth.GetVersionsAsync(item.Id,item.Category==DownloadCategory.Modpack?_installRequest?.GameVersion:ResolveGameVersion(),item.Category==DownloadCategory.Modpack?_installRequest?.Loader:item.Category.UsesLoader()?ResolveLoader():null);
                release=(versions.FirstOrDefault(v=>v.VersionType=="release")??versions.FirstOrDefault())?.Id;
            }
        }
        if(release is null){Status="找不到兼容的发布版本。";return true;}
        if(root!=_gameRoot()||instance!=_currentInstance()){Status="安装目标已变化，请重新确认。";return true;}
        await QueueTask(new("resource",item.DisplayTitle,root,instance,new(){{"source",item.Source},{"project",item.Id},{"category",item.Category.ToString()},{"release",release}}));return true;
    }
    private async Task<bool> QueueSelectedVersionAsync()
    {
        if(QueueTask is null||!CanInstallVersion())return false;
        await QueueTask(new("version",SelectedVersion!.Id,_gameRoot(),null,new(){{"game",SelectedVersion.Id},{"loader",SelectedInstallLoader!.Kind.ToString()},{"loaderVersion",SelectedInstallLoaderVersion??""}}));return true;
    }
    public async Task ExecutePersistentInstallAsync(DurableTaskRequest task,IProgress<TaskProgress> progress,CancellationToken ct)
    {
        string Arg(string key)=>task.Arguments.GetValueOrDefault(key)??"";
        var dp=new ImmediateProgress<DownloadProgress>(p=>progress.Report(new(p.Percent,$"下载 {p.CompletedBytes/1048576d:0.#}/{p.TotalBytes/1048576d:0.#} MiB · {p.CurrentFile}")));
        var mp=new ImmediateProgress<ModInstallProgress>(p=>progress.Report(new(p.Percent,p.Stage+" · "+p.Detail)));
        var pp=new ImmediateProgress<ModpackImportProgress>(p=>progress.Report(new(p.Percent,p.Stage+" · "+p.Detail)));
        if(task.Kind=="version")
        {
            var game=Arg("game");
            if(Launcher.Application.Diagnostics.DiskSpaceProbe.Describe(task.GameRoot,VersionInstallReserveBytes) is {} disk)throw new IOException(disk);
            await _gameInstaller.InstallAsync(game,task.GameRoot,true,dp,ct);
            var loader=Enum.Parse<LoaderKind>(Arg("loader"));
            if(loader!=LoaderKind.Vanilla)await InstallSelectedLoaderAsync(game,task.GameRoot,loader,Arg("loaderVersion"),dp,ct);
        }
        else
        {
            var category=Enum.Parse<DownloadCategory>(Arg("category"));var cf=Arg("source")==CurseForgeSource;
            if(category==DownloadCategory.Modpack)
            {
                if(cf)await _modpackDownloader.InstallCurseForgeAsync(task.GameRoot,int.Parse(Arg("project")),pp,ct,versionId:Arg("release"));
                else await _modpackDownloader.InstallAsync(task.GameRoot,Arg("project"),pp,ct,versionId:Arg("release"));
            }
            else
            {
                if(task.InstanceId is null)throw new InvalidOperationException("任务未指定实例。");
                if(Directory.Exists(Path.Combine(task.GameRoot,"instances",task.InstanceId)))await new InstanceSnapshots(AppPaths.DataDirectory,task.GameRoot,task.InstanceId).CaptureAsync("安装资源前："+task.Title,ct:ct);
                if(cf)await _cfInstaller.InstallAsync(task.GameRoot,task.InstanceId,category,int.Parse(Arg("project")),mp,ct,Arg("release"));
                else await _installer.InstallAsync(task.GameRoot,task.InstanceId,category,Arg("project"),mp,ct,Arg("release"));
            }
        }
        await PlatformUi.RunOnUiAsync(()=>InstancesChanged?.Invoke());
    }
    private sealed class ImmediateProgress<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
}
