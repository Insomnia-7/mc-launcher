using System.Security.Cryptography;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Operations;

namespace Launcher.Application.Instances;

public sealed record InstanceCloneRequest(string SourceId,string DestinationId,string Name,bool Mods=true,bool Settings=true,
    bool ResourcePacks=true,bool ShaderPacks=true,bool Worlds=false,bool Screenshots=false,bool Servers=true);
public sealed record CloneFile(string Path,long Size,string Sha256);
public sealed record InstanceCloneManifest(InstanceCloneRequest Request,string VersionId,InstanceMetadata Metadata,IReadOnlyList<CloneFile> Files);
public sealed record InstanceCloneResult(string InstanceId,string VersionId,string Name,int Files,long Bytes);
public sealed record InstanceCloneProvenance(InstanceCloneRequest Request,string VersionId,string? OperationId,int Files,long Bytes);

/// <summary>Copy private files into an unpublished directory; publish the independent instance only after validation.</summary>
public sealed class InstanceCloner
{
    private static IEnumerable<string> Groups(InstanceCloneRequest request)
    {
        if(request.Mods)yield return ".minecraft/mods";
        if(request.Settings)
            foreach(var group in new[]{"settings.json","mod-update-policy.json",".minecraft/config",".minecraft/options.txt",".minecraft/CustomSkinLoader/LocalSkin",".minecraft/CustomSkinLoader/CustomSkinLoader.json",".minecraft/CustomSkinLoader/ExtraList/Fangyu.json"})yield return group;
        if(request.ResourcePacks)yield return ".minecraft/resourcepacks";
        if(request.ShaderPacks)yield return ".minecraft/shaderpacks";
        if(request.Worlds)yield return ".minecraft/saves";
        if(request.Screenshots)yield return ".minecraft/screenshots";
        if(request.Servers)yield return ".minecraft/servers.dat";
    }
    private static IEnumerable<string> SelectedFiles(string source,InstanceCloneRequest request)
    {
        foreach(var group in Groups(request))
        {
            var path=ExperienceFiles.Child(source,group);ExperienceFiles.RejectLinks(path);
            if(File.Exists(path))yield return path;
            else foreach(var file in ExperienceFiles.Files(path))yield return file;
        }
    }
    private static async Task<string> HashAsync(string path,CancellationToken ct)
    {await using var stream=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(stream,ct).ConfigureAwait(false));}
    public async Task<InstanceCloneResult> CloneAsync(string gameRoot,InstanceCloneRequest request,IProgress<TaskProgress>? progress=null,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80)throw new InvalidOperationException("请填写 1–80 字的副本名称。");
        var instances=Path.Combine(gameRoot,"instances");var source=ExperienceFiles.Child(instances,request.SourceId);
        var destination=ExperienceFiles.Child(instances,request.DestinationId);
        ExperienceFiles.RejectLinks(source);ExperienceFiles.RejectLinks(destination);
        if(!Directory.Exists(source))throw new DirectoryNotFoundException("原实例目录不存在。");
        if(Directory.Exists(destination))
        {
            if(OwnedClone(gameRoot,request) is {} completed)return new(request.DestinationId,completed.VersionId,request.Name,completed.Files,completed.Bytes);
            throw new InvalidOperationException("副本目录已存在，请重新创建副本。");
        }
        var owned=DurableExecution.Current?.Directory??Path.Combine(gameRoot,".clone-staging",Guid.NewGuid().ToString("N"));
        var stage=Path.Combine(owned,"instance");var manifestPath=Path.Combine(owned,"clone-manifest.json");
        Directory.CreateDirectory(stage);
        try
        {
            var manifest=ExperienceFiles.Read<InstanceCloneManifest>(manifestPath);
            if(manifest is not null&&manifest.Request!=request)throw new InvalidOperationException("恢复任务的副本参数已改变。");
            if(manifest is null)
            {
                var version=InstanceRegistry.ResolveVersionId(gameRoot,request.SourceId);
                var old=InstanceMetadata.TryRead(gameRoot,request.SourceId);
                var environment=Launcher.Application.Mods.InstanceEnvironment.Resolve(gameRoot,request.SourceId);
                var metadata=new InstanceMetadata(request.Name,null,old?.Source,old?.Loader??environment.Loader,environment.GameVersion,DateTimeOffset.UtcNow.ToString("O"),version,old?.IsModpack==true);
                var files=new List<CloneFile>();
                foreach(var path in SelectedFiles(source,request).Distinct())
                {
                    ct.ThrowIfCancellationRequested();var relative=Path.GetRelativePath(source,path).Replace('\\','/');
                    progress?.Report(new(0,"检查原实例："+relative));
                    files.Add(new(relative,new FileInfo(path).Length,await HashAsync(path,ct).ConfigureAwait(false)));
                }
                manifest=new(request,version,metadata,files);ExperienceFiles.Write(manifestPath,manifest);
            }
            var selected=SelectedFiles(source,request).Select(p=>Path.GetRelativePath(source,p).Replace('\\','/')).Order().ToArray();
            if(!selected.SequenceEqual(manifest.Files.Select(f=>f.Path).Order()))throw new InvalidOperationException("原实例内容已变化，请取消旧副本任务后重新创建。");
            var total=manifest.Files.Sum(f=>f.Size);long completed=0;
            foreach(var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();var from=ExperienceFiles.Child(source,file.Path);var to=ExperienceFiles.Child(stage,file.Path);
                ExperienceFiles.RejectLinks(from);ExperienceFiles.RejectLinks(to);
                if(!File.Exists(from)||new FileInfo(from).Length!=file.Size||await HashAsync(from,ct).ConfigureAwait(false)!=file.Sha256)
                    throw new InvalidOperationException("原实例文件已改变，请重新创建副本："+file.Path);
                var ready=File.Exists(to)&&new FileInfo(to).Length==file.Size&&await HashAsync(to,ct).ConfigureAwait(false)==file.Sha256;
                if(!ready)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    await using(var input=File.OpenRead(from))await using(var output=File.Create(to))await input.CopyToAsync(output,ct).ConfigureAwait(false);
                    if(await HashAsync(to,ct).ConfigureAwait(false)!=file.Sha256)throw new IOException("副本校验失败："+file.Path);
                }
                completed+=file.Size;progress?.Report(new(total>0?completed*95d/total:95,"复制并校验："+file.Path));
            }
            // The original must still match after a long copy, including selected world files.
            foreach(var file in manifest.Files)
                if(await HashAsync(ExperienceFiles.Child(source,file.Path),ct).ConfigureAwait(false)!=file.Sha256)
                    throw new InvalidOperationException("复制期间原实例已变化，请重新创建副本。");
            if(!SelectedFiles(source,request).Select(p=>Path.GetRelativePath(source,p).Replace('\\','/')).Order().SequenceEqual(selected))
                throw new InvalidOperationException("复制期间原实例新增或移除了文件，请重新创建副本。");
            ExperienceFiles.Write(Path.Combine(stage,InstanceMetadata.FileName),manifest.Metadata);
            ExperienceFiles.Write(Path.Combine(stage,"clone-info.json"),new InstanceCloneProvenance(request,manifest.VersionId,DurableExecution.Current is {} context?Path.GetFileName(context.Directory):null,manifest.Files.Count,total));
            Directory.CreateDirectory(Path.Combine(stage,".minecraft"));
            ct.ThrowIfCancellationRequested();Directory.Move(stage,destination);
            // The metadata is the source of truth; a missing index is rebuilt on next load.
            try{InstanceRegistry.Load(gameRoot).Register(request.DestinationId,manifest.VersionId,manifest.Metadata);}catch(IOException){ }
            progress?.Report(new(100,"试装副本已创建；原实例保持不变。"));
            return new(request.DestinationId,manifest.VersionId,request.Name,manifest.Files.Count,total);
        }
        finally
        {
            if(DurableExecution.Current is null&&Directory.Exists(owned))ExperienceFiles.DeleteTree(owned,Path.Combine(gameRoot,".clone-staging"));
        }
    }
    private static InstanceCloneProvenance? OwnedClone(string root,InstanceCloneRequest request)
    {
        if(DurableExecution.Current is not {} context)return null;
        var path=ExperienceFiles.Child(Path.Combine(root,"instances"),request.DestinationId);ExperienceFiles.RejectLinks(path);
        var info=ExperienceFiles.Read<InstanceCloneProvenance>(Path.Combine(path,"clone-info.json"));
        return info?.Request==request&&info.OperationId==Path.GetFileName(context.Directory)?info:null;
    }
}
