using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Mods;
using Launcher.Domain.Content;
using Launcher.Domain.Install;

internal sealed class WorkbenchFixture:IModrinthClient,ICurseForgeClient,IDownloader
{
    public readonly Dictionary<string,byte[]> Files=new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string,IReadOnlyList<ModrinthVersion>> Versions=[];
    public readonly Dictionary<string,IReadOnlyList<CfFile>> CurseFiles=[];
    public TaskCompletionSource? PendingDownload;
    public int DownloadCalls;
    public int FailAfter=-1;
    public bool CorruptDownload {get;set;}
    public Action? AfterDownload {get;set;}
    public bool IsConfigured=>true;
    public ModChangePlans Plans=>new(new(this,this),new(this,this),this,this,this);
    public WorkbenchFixture()
    {
        Add("alpha","alpha-1","1.0","alpha-1.jar");
        Add("alpha","alpha-2","2.0","alpha-2.jar",new("shared","shared-1","required"));
        Add("alpha","alpha-beta","3.0-beta","alpha-beta.jar",new("shared","shared-1","required"),"beta");
        Add("beta","beta-1","1.0","beta-1.jar");
        Add("beta","beta-2","2.0","beta-2.jar",new("shared","shared-1","required"));
        Add("shared","shared-1","1.0","shared.jar");
        var cfBytes=Jar("cfmod","2.0");Files["cf-new.jar"]=cfBytes;
        CurseFiles["10"]=[new(){Id=101,ModId=10,DisplayName="CF 2.0",FileName="cf-new.jar",FileLength=cfBytes.Length,ReleaseType=1,GameVersions=["1.21.4","Fabric"],DownloadUrl="https://fixture.invalid/cf-new.jar",Hashes=[new(){Algo=1,Value=Hash(cfBytes)}]}];
    }
    public void Add(string project,string id,string number,string filename,ModrinthDependency? dependency=null,string channel="release")
    {
        var data=Jar(project,number);Files[filename]=data;
        var version=new ModrinthVersion(id,project,project+" "+number,number,["1.21.4"],["fabric"],DateTimeOffset.UtcNow,[new("https://fixture.invalid/"+filename,filename,data.Length,true,new(Hash(data),null))],dependency is null?[]:[dependency]){VersionType=channel,Changelog="## "+number+"\n修复兼容性问题，并改进模组行为。"};
        Versions[project]=new[]{version}.Concat(Versions.GetValueOrDefault(project)??[]).ToArray();
    }
    public static byte[] Jar(string id,string version)
    {
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
        {
            var entry=zip.CreateEntry("fabric.mod.json");using var text=new StreamWriter(entry.Open());
            text.Write(JsonSerializer.Serialize(new{schemaVersion=1,id,version,name=id.ToUpperInvariant()}));
        }
        return bytes.ToArray();
    }
    public static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA1.HashData(bytes));
    public static void MakeInstance(string root,string id="fabric-loader-0.16.9-1.21.4")
    {
        var vanilla=Path.Combine(root,"versions","1.21.4");Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla,"1.21.4.json"),"""{"id":"1.21.4","mainClass":"net.minecraft.client.main.Main","arguments":{"game":[],"jvm":[]},"libraries":[],"javaVersion":{"majorVersion":21}}""");
        File.WriteAllBytes(Path.Combine(vanilla,"1.21.4.jar"),[80,75,3,4]);
        var loader=Path.Combine(root,"versions",id);Directory.CreateDirectory(loader);
        File.WriteAllText(Path.Combine(loader,id+".json"),JsonSerializer.Serialize(new{id,inheritsFrom="1.21.4",mainClass="net.fabricmc.loader.impl.launch.knot.KnotClient",arguments=new{game=Array.Empty<string>(),jvm=Array.Empty<string>()},libraries=Array.Empty<object>()}));
        Directory.CreateDirectory(Path.Combine(root,"instances",id,".minecraft","mods"));
    }
    public async Task<ModUpdateCandidate> InstallOldAsync(string root,string instance,string project,string oldFile,string newVersion)
    {
        var directory=ModChangePlans.ModsDirectory(root,instance);Directory.CreateDirectory(directory);
        var version=Versions[project].First(v=>v.Files[0].Filename==oldFile);var data=Files[oldFile];await File.WriteAllBytesAsync(Path.Combine(directory,oldFile),data);
        var origins=ModSourceStore.Load(directory);origins[oldFile]=new("modrinth",project,version.Id,version.VersionNumber,Hash(data),data.Length);await ModSourceStore.SaveAsync(directory,origins);
        var next=Versions[project].Single(v=>v.Id==newVersion);
        return new(project,oldFile,version.VersionNumber,next.VersionNumber,true,next.PrimaryFile!.Url,next.PrimaryFile.Size,"modrinth"){ProjectId=project,VersionId=next.Id,CurrentSha1=Hash(data)};
    }
    public Task<ModrinthSearchResponse> SearchAsync(string q,DownloadCategory category,string? game=null,string? loader=null,int limit=20,int offset=0,CancellationToken ct=default)=>Task.FromResult(new ModrinthSearchResponse([],0,offset,limit));
    public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string id,string? game=null,string? loader=null,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ModrinthVersion>>((Versions.GetValueOrDefault(id)??[]).Where(v=>(game is null||v.GameVersions.Contains(game))&&(loader is null||v.Loaders.Contains(loader))).ToArray());
    public Task<ModrinthVersion?> GetVersionAsync(string id,CancellationToken ct=default)=>Task.FromResult(Versions.Values.SelectMany(v=>v).FirstOrDefault(v=>v.Id==id));
    public Task<ModrinthVersion?> GetVersionByHashAsync(string hash,CancellationToken ct=default)=>Task.FromResult(Versions.Values.SelectMany(v=>v).FirstOrDefault(v=>v.PrimaryFile!.Hashes!.Sha1==hash));
    public async Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries,string root,IProgress<DownloadProgress>? progress=null,CancellationToken ct=default)
    {
        DownloadCalls++;if(PendingDownload is {} pending)await pending.Task.WaitAsync(ct);
        var index=0;foreach(var entry in entries)
        {
            ct.ThrowIfCancellationRequested();if(FailAfter>=0&&index++>=FailAfter)throw new IOException("fixture interrupted download");
            var name=Uri.UnescapeDataString(new Uri(entry.Url).Segments.Last());var data=Files[name];if(CorruptDownload)data=[1,2,3];
            var path=Path.Combine(root,entry.RelativePath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,data,ct);
        }
        AfterDownload?.Invoke();return new(entries.Count,entries.Count,0,entries.Sum(e=>e.Size),TimeSpan.Zero);
    }
    Task<CfSearchResponse> ICurseForgeClient.SearchAsync(string q,DownloadCategory category,string? game,string? loader,int size,int offset,CancellationToken ct)=>Task.FromResult(new CfSearchResponse());
    public Task<CfMod?> GetModAsync(int id,CancellationToken ct=default)=>Task.FromResult<CfMod?>(new(){Id=id,Name="CF MOD"});
    public Task<IReadOnlyList<CfFile>> GetFilesAsync(int id,string? game=null,string? loader=null,CancellationToken ct=default)=>Task.FromResult(CurseFiles.GetValueOrDefault(id.ToString())??[]);
    public Task<string?> GetFileChangelogAsync(int id,int file,CancellationToken ct=default)=>Task.FromResult<string?>("<p>CurseForge 更新说明</p>");
    public Task<string?> GetDownloadUrlAsync(int id,int file,CancellationToken ct=default)=>Task.FromResult<string?>(null);
    public CfUrlRef? ParseUrl(string url)=>null;
}
