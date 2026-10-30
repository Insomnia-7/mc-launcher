using System.IO.Compression;
using System.Security.Cryptography;
using Launcher.Application.Update;

namespace Launcher.Application.Experience;

public enum SnapshotRestoreScope { Configuration, Worlds, All }
public sealed record SnapshotFile(string Path, long Bytes, string Sha256);
public sealed record InstanceSnapshot(string Id, string Name, DateTimeOffset CreatedAt, bool IncludesWorlds, IReadOnlyList<SnapshotFile> Files)
{
    public string Summary => $"{CreatedAt.LocalDateTime:MM-dd HH:mm} · {Files.Count} 文件 · {Files.Sum(f=>f.Bytes)/1048576d:0.#} MiB" + (IncludesWorlds ? " · 含存档" : " · 模组与配置");
}

/// <summary>Snapshots contain instance-private files only. Restore verifies every file before making any change.</summary>
public sealed class InstanceSnapshots(string dataRoot, string gameRoot, string instanceId)
{
    private readonly string _instance = ExperienceFiles.Child(Path.Combine(gameRoot, "instances"), instanceId);
    private readonly string _root = Path.Combine(dataRoot, "snapshots", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot)))), Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instanceId))));
    private static readonly string[] Configuration = ["settings.json", "instance.json", "mod-update-policy.json", ".minecraft/CustomSkinLoader/LocalSkin", ".minecraft/CustomSkinLoader/CustomSkinLoader.json", ".minecraft/CustomSkinLoader/ExtraList/Fangyu.json", ".minecraft/mods", ".minecraft/config", ".minecraft/resourcepacks", ".minecraft/shaderpacks", ".minecraft/options.txt", ".minecraft/servers.dat"];
    private static readonly string[] Worlds = [".minecraft/saves"];
    public IReadOnlyList<InstanceSnapshot> List()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateFiles(_root,"*.json").Select(p=>ExperienceFiles.Read<InstanceSnapshot>(p)).OfType<InstanceSnapshot>().OrderByDescending(x=>x.CreatedAt).ToArray();
    }
    private IEnumerable<string> SelectFiles(IEnumerable<string> groups)
    {
        foreach(var group in groups)
        {
            var path=ExperienceFiles.Child(_instance,group);ExperienceFiles.RejectLinks(path);
            if(File.Exists(path)) yield return path;
            else foreach(var file in ExperienceFiles.Files(path)) yield return file;
        }
    }
    public async Task<InstanceSnapshot> CaptureAsync(string name, bool includeWorlds=false, CancellationToken ct=default)
    {
        if(!Directory.Exists(_instance)) throw new DirectoryNotFoundException("实例目录不存在。");
        ExperienceFiles.RejectLinks(_instance);Directory.CreateDirectory(_root);
        var id=Guid.NewGuid().ToString("N");var archive=Path.Combine(_root,id+".zip");var temporary=archive+".tmp";
        var records=new List<SnapshotFile>();
        try
        {
            await using(var stream=File.Create(temporary))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
                foreach(var file in SelectFiles(includeWorlds?Configuration.Concat(Worlds):Configuration).Distinct())
                {
                    ct.ThrowIfCancellationRequested();
                    var relative=Path.GetRelativePath(_instance,file).Replace('\\','/');
                    var entry=zip.CreateEntry(relative,CompressionLevel.Fastest);
                    await using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
                    await using var output=entry.Open();using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer=new byte[65536];int read;long bytes=0;
                    while((read=await input.ReadAsync(buffer,ct))>0) {hash.AppendData(buffer,0,read);await output.WriteAsync(buffer.AsMemory(0,read),ct);bytes+=read;}
                    records.Add(new(relative,bytes,Convert.ToHexStringLower(hash.GetHashAndReset())));
                }
            ct.ThrowIfCancellationRequested();File.Move(temporary,archive);
            var snapshot=new InstanceSnapshot(id,string.IsNullOrWhiteSpace(name)?"手动快照":name.Trim(),DateTimeOffset.UtcNow,includeWorlds,records);
            ExperienceFiles.Write(Path.Combine(_root,id+".json"),snapshot);return snapshot;
        }
        catch {File.Delete(temporary);File.Delete(archive);throw;}
    }
    private static bool InGroups(string relative,IEnumerable<string> groups) => groups.Any(g=>relative==g||relative.StartsWith(g+"/",StringComparison.Ordinal));
    public async Task RestoreAsync(InstanceSnapshot snapshot,SnapshotRestoreScope scope,CancellationToken ct=default)
    {
        if(scope!=SnapshotRestoreScope.Configuration&&!snapshot.IncludesWorlds) throw new InvalidOperationException("该快照未备份存档。");
        if(!List().Any(x=>x.Id==snapshot.Id)) throw new InvalidDataException("快照不存在。");
        var groups=scope==SnapshotRestoreScope.Configuration?Configuration:scope==SnapshotRestoreScope.Worlds?Worlds:Configuration.Concat(Worlds).ToArray();
        var files=snapshot.Files.Where(f=>InGroups(f.Path,groups)).ToArray();
        if(snapshot.Files.Any(f=>!InGroups(f.Path,Configuration.Concat(Worlds)))) throw new InvalidDataException("快照包含未授权的路径。");
        var stage=Path.Combine(_root,"restore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            using(var zip=ZipFile.OpenRead(ExperienceFiles.Child(_root,snapshot.Id+".zip")))
            foreach(var item in files)
            {
                ct.ThrowIfCancellationRequested();var entry=zip.GetEntry(item.Path)??throw new InvalidDataException("快照缺少文件："+item.Path);
                if(entry.Length!=item.Bytes) throw new InvalidDataException("快照文件大小异常："+item.Path);
                var path=ExperienceFiles.Child(stage,item.Path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using(var input=entry.Open()) await using(var output=File.Create(path)) await input.CopyToAsync(output,ct);
                await using var check=File.OpenRead(path);
                if(!Convert.ToHexStringLower(await SHA256.HashDataAsync(check,ct)).Equals(item.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("快照校验失败："+item.Path);
            }
            var wanted=files.Select(x=>x.Path).ToHashSet(StringComparer.Ordinal);
            var removals=SelectFiles(groups).Select(p=>Path.GetRelativePath(_instance,p).Replace('\\','/')).Where(p=>!wanted.Contains(p)).ToArray();
            foreach(var file in files) ExperienceFiles.RejectLinks(ExperienceFiles.Child(_instance,file.Path));
            var backup=Path.Combine(_root,"recovery-"+Guid.NewGuid().ToString("N"));
            await UpdateFileTransaction.ApplyAsync(files.Select(f=>new UpdateFile(f.Path,"",f.Sha256,f.Bytes)).ToArray(),stage,_instance,backup,ct:ct,removeFiles:removals);
            ExperienceFiles.DeleteTree(backup,_root);
        }
        finally {ExperienceFiles.DeleteTree(stage,_root);}
    }
    public void Delete(InstanceSnapshot snapshot)
    {
        if(!Guid.TryParseExact(snapshot.Id,"N",out _)) throw new InvalidDataException("无效快照编号。");
        File.Delete(ExperienceFiles.Child(_root,snapshot.Id+".zip"));File.Delete(ExperienceFiles.Child(_root,snapshot.Id+".json"));
    }
}
