using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Experience;

namespace Launcher.Application.Instances;

/// <summary>Verified library copy with root-keyed backup associations. Originals are retained.</summary>
public static class GameLibraryMigration
{
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Normalized(string root) => OperatingSystem.IsWindows() ? root.TrimEnd('\\','/').ToUpperInvariant() : root.TrimEnd('/');
    public static async Task CopyAsync(string source, string destination, string dataRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (source.Equals(destination,comparison) || source.StartsWith(destination.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,comparison) || destination.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,comparison))
            throw new IOException("来源与目标必须互不包含。");
        ExperienceFiles.RejectLinks(source); ExperienceFiles.RejectLinks(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("目标文件夹必须为空。");
        Directory.CreateDirectory(destination);
        var stage = ExperienceFiles.Child(destination,".fangyu-library-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var moved = new List<(string Path,string Owner)>();
        var associations = new List<(string Source,string Target)>();
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                ct.ThrowIfCancellationRequested(); var name=Path.GetFileName(entry);
                if (name==".cache") continue;
                ExperienceFiles.RejectLinks(entry);
                if (Directory.Exists(entry)) await ExternalGameImport.CopyTreeAsync(entry,Path.Combine(stage,name),progress,ct);
                else await ExternalGameImport.CopyFileAsync(entry,Path.Combine(stage,name),ct);
            }
            associations.Add((Path.Combine(dataRoot,"snapshots",Hash(source)),Path.Combine(dataRoot,"snapshots",Hash(destination))));
            foreach (var instance in InstanceRegistry.Load(source).Instances)
                associations.Add((Path.Combine(dataRoot,"world-backups",Hash(source+"\n"+instance.Id)),Path.Combine(dataRoot,"world-backups",Hash(destination+"\n"+instance.Id))));
            associations.Add((Path.Combine(dataRoot,"settings-sync",Hash(Normalized(source))+".json"),Path.Combine(dataRoot,"settings-sync",Hash(Normalized(destination))+".json")));
            associations.Add((Path.Combine(dataRoot,"library",Hash(Normalized(source))[..24].ToUpperInvariant()+".json"),Path.Combine(dataRoot,"library",Hash(Normalized(destination))[..24].ToUpperInvariant()+".json")));
            var index=0; var stagedAssociations=new List<(string Staged,string Target)>();
            foreach(var pair in associations.Where(p=>Directory.Exists(p.Source)||File.Exists(p.Source)))
            {
                if(Directory.Exists(pair.Target)||File.Exists(pair.Target)) throw new IOException("目标位置已有备份关联，请另选空游戏目录。");
                var temp=Path.Combine(stage,".associations",(index++).ToString());
                if(Directory.Exists(pair.Source))await ExternalGameImport.CopyTreeAsync(pair.Source,temp,progress,ct);
                else await ExternalGameImport.CopyFileAsync(pair.Source,temp,ct);
                stagedAssociations.Add((temp,pair.Target));
            }
            ct.ThrowIfCancellationRequested();
            foreach(var pair in stagedAssociations)
            {
                ExperienceFiles.RejectLinks(pair.Target);Directory.CreateDirectory(Path.GetDirectoryName(pair.Target)!);
                if(Directory.Exists(pair.Staged))Directory.Move(pair.Staged,pair.Target);else File.Move(pair.Staged,pair.Target);
                moved.Add((pair.Target,dataRoot));
            }
            if(Directory.Exists(Path.Combine(stage,".associations")))ExperienceFiles.DeleteTree(Path.Combine(stage,".associations"),stage);
            foreach(var entry in Directory.GetFileSystemEntries(stage))
            {
                var target=Path.Combine(destination,Path.GetFileName(entry));ExperienceFiles.RejectLinks(target);
                if(Directory.Exists(entry))Directory.Move(entry,target);else File.Move(entry,target);
                moved.Add((target,destination));
            }
        }
        catch
        {
            foreach(var pair in moved.AsEnumerable().Reverse())
            { if(Directory.Exists(pair.Path))ExperienceFiles.DeleteTree(pair.Path,pair.Owner);else{ExperienceFiles.RejectLinks(pair.Path);File.Delete(pair.Path);} }
            throw;
        }
        finally{ExperienceFiles.DeleteTree(stage,destination);}
    }
}
