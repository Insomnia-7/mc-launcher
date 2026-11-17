using System.Globalization;
using System.Text.Json;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Instances;

namespace Launcher.Application.Java;

public sealed record ManagedJavaRuntime(int Major,string Directory,string Executable);

/// <summary>Only launcher-managed runtimes may be offered for instance removal.</summary>
public static class ManagedJavaCleanup
{
    private static string LibrariesFile(string dataRoot)=>Path.Combine(dataRoot,"java","game-libraries.json");
    public static void RememberLibrary(string dataRoot,string gameRoot)
    {
        var roots=ExperienceFiles.Read<List<string>>(LibrariesFile(dataRoot))??[];
        var full=Path.GetFullPath(gameRoot);
        if(roots.Contains(full,StringComparer.OrdinalIgnoreCase))return;
        roots.Add(full);ExperienceFiles.Write(LibrariesFile(dataRoot),roots);
    }

    public static ManagedJavaRuntime? ForInstance(string dataRoot,string gameRoot,string instanceId,string versionId)
    {
        var root=Path.Combine(Path.GetFullPath(dataRoot),"java");
        if(!System.IO.Directory.Exists(root))return null;
        var pinned=InstanceSettings.Load(gameRoot,instanceId).JavaPath;
        var required=RequiredMajor(gameRoot,versionId,new(StringComparer.OrdinalIgnoreCase));
        return System.IO.Directory.EnumerateDirectories(root)
            .Select(path=>new{Path=path,Major=int.TryParse(Path.GetFileName(path),NumberStyles.None,CultureInfo.InvariantCulture,out var n)?n:0})
            .Where(x=>x.Major>0).Select(x=>new ManagedJavaRuntime(x.Major,x.Path,Path.Combine(x.Path,"bin",OperatingSystem.IsWindows()?"java.exe":"java")))
            .Where(x=>File.Exists(x.Executable)&&(pinned is {Length:>0}
                ?Path.GetFullPath(pinned).Equals(x.Executable,StringComparison.OrdinalIgnoreCase):x.Major>=required))
            .OrderBy(x=>x.Major).FirstOrDefault();
    }

    private static int RequiredMajor(string root,string id,HashSet<string> seen)
    {
        if(!seen.Add(id))throw new InvalidDataException("Java 要求的版本继承出现环。");
        var path=ExperienceFiles.Child(Path.Combine(root,"versions"),Path.Combine(id,id+".json"));
        if(!File.Exists(path))throw new IOException("无法读取实例的 Java 要求。");
        using var json=JsonDocument.Parse(File.ReadAllText(path));
        foreach(var property in json.RootElement.EnumerateObject())
            if(property.Name.Equals("javaVersion",StringComparison.OrdinalIgnoreCase)&&property.Value.ValueKind==JsonValueKind.Object
                &&property.Value.TryGetProperty("majorVersion",out var major)&&major.TryGetInt32(out var n))return n;
        return InstanceRegistry.Parents(root,id).Select(p=>RequiredMajor(root,p,seen)).DefaultIfEmpty(8).Max();
    }

    public static string RemoveIfUnused(string dataRoot,string gameRoot,ManagedJavaRuntime runtime,Func<string,bool> isRunning)
    {
        var expected=ExperienceFiles.Child(Path.Combine(dataRoot,"java"),runtime.Major.ToString(CultureInfo.InvariantCulture));
        if(!Path.GetFullPath(runtime.Directory).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("只能清理方隅自动下载的 Java。");
        var roots=ExperienceFiles.Read<List<string>>(LibrariesFile(dataRoot))??[];
        foreach(var root in roots.Append(gameRoot).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!System.IO.Directory.Exists(root))return $"Java {runtime.Major} 已保留：有游戏库暂不可用，无法确认是否仍在使用。";
            foreach(var instance in InstanceRegistry.Load(root).Instances)
            {
                var other=ForInstance(dataRoot,root,instance.Id,instance.VersionId);
                if(other?.Directory.Equals(runtime.Directory,StringComparison.OrdinalIgnoreCase)==true)
                    return $"Java {runtime.Major} 已保留：其他实例仍需使用。";
            }
        }
        if(isRunning(runtime.Directory))return $"Java {runtime.Major} 已保留：正在使用中。";
        if(!System.IO.Directory.Exists(expected))return $"Java {runtime.Major} 已清理。";
        // Inspect before deleting so a linked runtime cannot redirect cleanup elsewhere.
        _=ExperienceFiles.Files(expected).Count();
        ExperienceFiles.DeleteTree(expected,Path.Combine(dataRoot,"java"));
        return $"已同时删除自动下载的 Java {runtime.Major}。";
    }
}
