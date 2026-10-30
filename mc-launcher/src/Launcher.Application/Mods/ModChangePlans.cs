using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Application.Operations;
using Launcher.Application.Update;
using Launcher.Domain.Content;

namespace Launcher.Application.Mods;

public enum ModBatchAction { Enable, Disable, Delete, Update, Reinstall }
public sealed record ModFileSelection(string FileName,string Sha1);
public sealed record ModReleaseOption(string Id,string Number,string Channel,string? Changelog)
{
    public string Label=>$"{Number} · {ChannelText}";
    public string ChannelText=>Channel switch {"release"=>"正式版","beta"=>"测试版","alpha"=>"开发版",_=>"未标注频道"};
    public override string ToString()=>Label;
}
public sealed record ModPlannedChange(string Kind,string Name,string Before,string After,string Channel,string Changelog)
{
    public string ChannelText=>Channel switch{"release"=>"正式版","beta"=>"测试版","alpha"=>"开发版",_=>""};
}
public sealed record ModPlanFile(PlannedModFile File,string? CopyFrom=null);
public sealed record ModChangePlan(string Id,string GameRoot,string InstanceId,ModBatchAction Action,
    InstanceEnvironment Environment,string Fingerprint,IReadOnlyList<ModPlannedChange> Changes,
    IReadOnlyList<ModPlanFile> Files,IReadOnlyList<string> Removals)
{
    public long DownloadBytes=>Files.Where(f=>f.CopyFrom is null).Sum(f=>f.File.Entry.Size);
    public string DownloadSize=>DownloadBytes>=1048576?$"{DownloadBytes/1048576d:0.##} MB":DownloadBytes>=1024?$"{DownloadBytes/1024d:0.#} KB":$"{DownloadBytes} B";
    public string Summary=>$"{Changes.Count} 项变更 · 下载 {DownloadSize} · 执行前自动备份";
}

/// <summary>Prepare an exact file set before approval. All files are staged and verified before a single transaction.</summary>
public sealed class ModChangePlans(ModInstallService modrinth,CurseForgeInstallService curseForge,
    IModrinthClient modrinthApi,ICurseForgeClient curseForgeApi,IDownloader downloader)
{
    public static string ModsDirectory(string root,string id)
    {
        var instance=ExperienceFiles.Child(Path.Combine(root,"instances"),id);
        ExperienceFiles.RejectLinks(instance);
        return Path.Combine(instance,".minecraft","mods");
    }
    private static string Name(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Any(c=>char.IsControl(c)||"<>:\"|?*/\\".Contains(c))||Path.GetFileName(name)!=name||
            !(name.EndsWith(".jar",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".jar.disabled",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("无效的模组文件名："+name);
        return name;
    }
    private static string FilePath(string directory,string name)
    {var path=ExperienceFiles.Child(directory,Name(name));ExperienceFiles.RejectLinks(path);return path;}
    public static async Task<string> FingerprintAsync(string directory,CancellationToken ct=default)
    {
        var rows=new List<string>();
        if(Directory.Exists(directory))foreach(var path in Directory.EnumerateFiles(directory).Where(p=>
            p.EndsWith(".jar",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".jar.disabled",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(p)==ModSourceStore.FileName).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();ExperienceFiles.RejectLinks(path);
            rows.Add(Path.GetFileName(path)+"\0"+await ModSourceStore.HashAsync(path,ct).ConfigureAwait(false));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",rows))));
    }
    public async Task<IReadOnlyList<ModReleaseOption>> ReleasesAsync(string root,string instanceId,ModUpdateCandidate candidate,CancellationToken ct=default)
    {
        var environment=InstanceEnvironment.Resolve(root,instanceId);
        var project=candidate.ProjectId??throw new InvalidOperationException("无法确认项目来源，请重新检查更新。");
        if(candidate.Source==ModUpdateCandidate.SourceModrinth)
            return (await modrinthApi.GetVersionsAsync(project,environment.GameVersion,environment.LoaderSlug,ct).ConfigureAwait(false))
                .Select(v=>new ModReleaseOption(v.Id,v.VersionNumber,v.VersionType??"",v.Changelog)).ToArray();
        if(candidate.Source==ModUpdateCandidate.SourceCurseForge&&int.TryParse(project,out var id))
            return (await curseForgeApi.GetFilesAsync(id,environment.GameVersion,environment.LoaderSlug,ct).ConfigureAwait(false))
                .Select(v=>new ModReleaseOption(v.Id.ToString(),v.DisplayName,v.ReleaseType switch{1=>"release",2=>"beta",3=>"alpha",_=>""},null)).ToArray();
        throw new InvalidOperationException("该项目的下载来源不可用。");
    }
    public async Task<IReadOnlyList<ModUpdateCandidate>> ReinstallCandidatesAsync(string root,string instanceId,IReadOnlyList<ModFileSelection> selected,CancellationToken ct=default)
    {
        var directory=ModsDirectory(root,instanceId);var origins=ModSourceStore.Load(directory);var result=new List<ModUpdateCandidate>();
        foreach(var item in selected)
        {
            var path=FilePath(directory,item.FileName);var hash=await ModSourceStore.HashAsync(path,ct).ConfigureAwait(false);
            if(!hash.Equals(item.Sha1,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("文件已改变，请刷新后重选："+item.FileName);
            if(!origins.TryGetValue(item.FileName,out var origin)||!hash.Equals(origin.Sha1,StringComparison.OrdinalIgnoreCase)||origin.ProjectId is null||origin.VersionId is null)
                throw new InvalidOperationException("无法确认原发布版本，不能自动重装："+item.FileName+"。请从发现资源选择对应项目。");
            result.Add(new(item.FileName,item.FileName,origin.VersionNumber,origin.VersionNumber,true,null,origin.Size,origin.Source)
                {ProjectId=origin.ProjectId,VersionId=origin.VersionId,CurrentSha1=hash});
        }
        return result;
    }
    public async Task<ModChangePlan> PrepareLocalAsync(string root,string id,ModBatchAction action,IReadOnlyList<ModFileSelection> selected,CancellationToken ct=default)
    {
        if(action is not (ModBatchAction.Enable or ModBatchAction.Disable or ModBatchAction.Delete))throw new ArgumentException("需要本地模组操作。");
        if(selected.Count==0)throw new InvalidOperationException("请先选择模组。");
        root=Path.GetFullPath(root);var directory=ModsDirectory(root,id);var fingerprint=await FingerprintAsync(directory,ct).ConfigureAwait(false);
        var files=new List<ModPlanFile>();var removals=new List<string>();var changes=new List<ModPlannedChange>();var origins=ModSourceStore.Load(directory);
        foreach(var item in selected.DistinctBy(x=>x.FileName,StringComparer.OrdinalIgnoreCase))
        {
            var path=FilePath(directory,item.FileName);var hash=await ModSourceStore.HashAsync(path,ct).ConfigureAwait(false);
            if(!hash.Equals(item.Sha1,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("文件已改变，请刷新后重选："+item.FileName);
            var disabled=item.FileName.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase);
            if(action==ModBatchAction.Enable&&!disabled||action==ModBatchAction.Disable&&disabled)continue;
            var next=action==ModBatchAction.Delete?"":disabled?item.FileName[..^9]:item.FileName+".disabled";
            if(next.Length>0)
            {
                var destination=FilePath(directory,next);
                if(File.Exists(destination))throw new InvalidOperationException("目标文件已存在，已停止整批操作："+next);
                var origin=origins.GetValueOrDefault(item.FileName);
                if(origin?.Sha1.Equals(hash,StringComparison.OrdinalIgnoreCase)!=true)origin=new("local",null,null,null,hash,new FileInfo(path).Length);
                files.Add(new(new(new("",next,new FileInfo(path).Length,hash,Launcher.Domain.Install.DownloadKind.Mod),origin),item.FileName));
            }
            removals.Add(item.FileName);
            changes.Add(new(action switch{ModBatchAction.Enable=>"启用",ModBatchAction.Disable=>"停用",_=>"删除"},item.FileName,item.FileName,next.Length>0?next:"移入备份", "",""));
        }
        if(changes.Count==0)throw new InvalidOperationException("所选模组已处于目标状态，无需修改。");
        return new(Guid.NewGuid().ToString("N"),root,id,action,InstanceEnvironment.Resolve(root,id),fingerprint,changes,files,removals);
    }
    public async Task<ModChangePlan> PrepareUpdatesAsync(string root,string id,IReadOnlyList<ModUpdateCandidate> candidates,bool reinstall=false,CancellationToken ct=default)
    {
        if(candidates.Count==0)throw new InvalidOperationException("请先选择需要更新的模组。");
        root=Path.GetFullPath(root);var directory=ModsDirectory(root,id);var fingerprint=await FingerprintAsync(directory,ct).ConfigureAwait(false);
        var oldNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared=new List<(ModUpdateCandidate Candidate,PreparedModInstall Plan)>();
        foreach(var candidate in candidates)
        {
            if(!oldNames.Add(Name(candidate.FileName)))throw new InvalidOperationException("同一模组文件被重复选择。");
            var path=FilePath(directory,candidate.FileName);var hash=await ModSourceStore.HashAsync(path,ct).ConfigureAwait(false);
            if(candidate.CurrentSha1 is null||!hash.Equals(candidate.CurrentSha1,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("原文件已变化，请重新检查更新："+candidate.FileName);
            var project=candidate.ProjectId??throw new InvalidOperationException("项目缺少可确认的来源。");
            if(string.IsNullOrWhiteSpace(candidate.VersionId))throw new InvalidOperationException("请选择明确的发布版本。");
            var plan=candidate.Source switch
            {
                "modrinth"=>await modrinth.PrepareAsync(root,id,DownloadCategory.Mod,project,ct:ct,strict:true,versionId:candidate.VersionId).ConfigureAwait(false),
                "curseforge" when int.TryParse(project,out var cf)=>await curseForge.PrepareAsync(root,id,DownloadCategory.Mod,cf,ct:ct,strict:true,versionId:candidate.VersionId).ConfigureAwait(false),
                _=>throw new InvalidOperationException("无法识别更新来源。")
            };
            prepared.Add((candidate,plan));
        }
        var roots=new Dictionary<string,PlannedModFile>(StringComparer.OrdinalIgnoreCase);
        foreach(var (candidate,plan) in prepared)
        {
            var file=plan.Files[0];var key=file.Origin.Source+":"+file.Origin.ProjectId;
            if(roots.ContainsKey(key))throw new InvalidOperationException("同一项目安装了多份，请先解决重复文件："+candidate.FileName);
            if(candidate.FileName.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase))file=file with{Entry=file.Entry with{RelativePath=file.Entry.RelativePath+".disabled"}};
            roots[key]=file;
        }
        var output=new Dictionary<string,ModPlanFile>(StringComparer.OrdinalIgnoreCase);
        var projects=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var changes=new List<ModPlannedChange>();
        void Add(PlannedModFile file)
        {
            Name(file.Entry.RelativePath);var key=file.Origin.Source+":"+file.Origin.ProjectId;
            if(projects.TryGetValue(key,out var version)&&version!=file.Origin.VersionId)throw new InvalidOperationException("前置依赖要求不同版本，请分开处理："+file.Origin.ProjectId);
            projects[key]=file.Origin.VersionId??"";
            if(output.TryGetValue(file.Entry.RelativePath,out var existing))
            {if(existing.File.Origin!=file.Origin)throw new InvalidOperationException("下载文件名冲突："+file.Entry.RelativePath);return;}
            output[file.Entry.RelativePath]=new(file);
        }
        foreach(var file in roots.Values)Add(file);
        foreach(var (candidate,plan) in prepared)
        {
            var release=(await ReleasesAsync(root,id,candidate,ct).ConfigureAwait(false)).FirstOrDefault(v=>v.Id==candidate.VersionId);
            var log=release?.Changelog;
            if(candidate.Source=="curseforge"&&int.TryParse(candidate.ProjectId,out var projectId)&&int.TryParse(candidate.VersionId,out var fileId))
                try{log=await curseForgeApi.GetFileChangelogAsync(projectId,fileId,ct).ConfigureAwait(false);}catch(HttpRequestException){ }
            changes.Add(new(reinstall?"重装":"更新",plan.Title,candidate.CurrentVersion??"未知",plan.Version,release?.Channel??"",string.IsNullOrWhiteSpace(log)?"暂无可显示的更新日志。":log));
            foreach(var dependency in plan.Files.Skip(1))
            {
                var key=dependency.Origin.Source+":"+dependency.Origin.ProjectId;
                if(roots.TryGetValue(key,out var chosen))
                {
                    if(dependency.RequiresExactVersion&&dependency.Origin.VersionId!=chosen.Origin.VersionId)throw new InvalidOperationException("所选版本不满足另一模组的指定前置版本："+dependency.Origin.ProjectId);
                    if(!candidate.FileName.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)&&chosen.Entry.RelativePath.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("必需前置已停用，请先启用："+chosen.Entry.RelativePath);
                    continue;
                }
                var existing=await ModInstallExecutor.FindExistingAsync(plan,dependency,DownloadCategory.Mod,ct).ConfigureAwait(false);
                if(existing is not null)
                {
                    if(existing.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase)&&!candidate.FileName.EndsWith(".disabled",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("必需前置已停用，请先启用："+Path.GetFileName(existing));
                    if(dependency.RequiresExactVersion)
                    {
                        var hash=await ModSourceStore.HashAsync(existing,ct).ConfigureAwait(false);var origin=ModSourceStore.Load(directory).GetValueOrDefault(Path.GetFileName(existing));
                        var matches=dependency.Entry.Sha1 is {Length:>0} expected?hash.Equals(expected,StringComparison.OrdinalIgnoreCase):origin is not null&&origin.VersionId==dependency.Origin.VersionId&&origin.Sha1.Equals(hash,StringComparison.OrdinalIgnoreCase);
                        if(!matches)throw new InvalidOperationException("已安装前置不满足指定版本，请一并选择更新："+Path.GetFileName(existing));
                    }
                    continue;
                }
                var first=!output.ContainsKey(dependency.Entry.RelativePath);Add(dependency);
                if(first)changes.Add(new("补充前置",dependency.Origin.ProjectId??dependency.Entry.RelativePath,"未安装",dependency.Origin.VersionNumber??dependency.Origin.VersionId??"", "",dependency.Entry.RelativePath));
            }
        }
        foreach(var item in output.Values)
        {
            var path=FilePath(directory,item.File.Entry.RelativePath);
            if(File.Exists(path)&&!oldNames.Contains(item.File.Entry.RelativePath))throw new InvalidOperationException("目标文件与其他已安装内容冲突："+item.File.Entry.RelativePath);
        }
        if(await FingerprintAsync(directory,ct).ConfigureAwait(false)!=fingerprint)throw new InvalidOperationException("生成预览期间文件已改变，请刷新后重试。");
        return new(Guid.NewGuid().ToString("N"),root,id,reinstall?ModBatchAction.Reinstall:ModBatchAction.Update,InstanceEnvironment.Resolve(root,id),fingerprint,changes,output.Values.ToArray(),oldNames.Where(n=>!output.ContainsKey(n)).ToArray());
    }
    public async Task ValidateAsync(ModChangePlan plan,CancellationToken ct=default)
    {
        var directory=ModsDirectory(plan.GameRoot,plan.InstanceId);
        foreach(var item in plan.Files){FilePath(directory,item.File.Entry.RelativePath);if(item.CopyFrom is {} name)FilePath(directory,name);}
        foreach(var name in plan.Removals)FilePath(directory,name);
        if(InstanceEnvironment.Resolve(plan.GameRoot,plan.InstanceId)!=plan.Environment||await FingerprintAsync(directory,ct).ConfigureAwait(false)!=plan.Fingerprint)
            throw new InvalidOperationException("游戏环境或模组文件已改变，旧预览已失效，请重新生成变更预览。");
    }
    public async Task ApplyAsync(ModChangePlan plan,IProgress<ModInstallProgress>? progress=null,CancellationToken ct=default)
    {
        await ValidateAsync(plan,ct).ConfigureAwait(false);
        var directory=ModsDirectory(plan.GameRoot,plan.InstanceId);
        var owned=DurableExecution.Current?.Directory??Path.Combine(plan.GameRoot,".mod-changes",Guid.NewGuid().ToString("N"));
        var stage=Path.Combine(owned,"files");var backup=Path.Combine(owned,"backup");Directory.CreateDirectory(stage);
        var preserveRecovery=false;
        try
        {
            foreach(var item in plan.Files.Where(f=>f.CopyFrom is not null))
            {
                ct.ThrowIfCancellationRequested();var path=FilePath(stage,item.File.Entry.RelativePath);
                await using var source=File.OpenRead(FilePath(directory,item.CopyFrom!));await using var target=File.Create(path);await source.CopyToAsync(target,ct).ConfigureAwait(false);
            }
            var downloads=plan.Files.Where(f=>f.CopyFrom is null).Select(f=>f.File.Entry).ToArray();
            if(downloads.Length>0)await downloader.DownloadAsync(downloads,stage,new ProgressAdapter(progress),ct).ConfigureAwait(false);
            var origins=ModSourceStore.Load(directory);
            foreach(var name in plan.Removals)origins.Remove(name);
            foreach(var item in plan.Files)
            {
                var captured=await ModSourceStore.CaptureAsync(stage,item.File,true,ct).ConfigureAwait(false);
                if(captured!.Source=="local")origins.Remove(item.File.Entry.RelativePath);else origins[item.File.Entry.RelativePath]=captured;
            }
            await ModSourceStore.SaveAsync(stage,origins,ct).ConfigureAwait(false);
            await ValidateAsync(plan,ct).ConfigureAwait(false);
            var replacements=plan.Files.Select(f=>new UpdateFile(f.File.Entry.RelativePath,"","",f.File.Entry.Size)).Append(new(ModSourceStore.FileName,"","",0)).ToArray();
            await UpdateFileTransaction.ApplyAsync(replacements,stage,directory,backup,ct:ct,removeFiles:plan.Removals).ConfigureAwait(false);
            progress?.Report(ModInstallProgress.Of("完成",100,$"已完成 {plan.Changes.Count} 项变更"));
        }
        catch(AggregateException){preserveRecovery=true;throw;}
        finally
        {
            if(!preserveRecovery&&DurableExecution.Current is null&&Directory.Exists(owned))ExperienceFiles.DeleteTree(owned,Path.Combine(plan.GameRoot,".mod-changes"));
        }
    }
    private sealed class ProgressAdapter(IProgress<ModInstallProgress>? progress):IProgress<DownloadProgress>
    {public void Report(DownloadProgress value)=>progress?.Report(ModInstallProgress.Of("准备文件",value.Percent,value.CurrentFile));}
}
