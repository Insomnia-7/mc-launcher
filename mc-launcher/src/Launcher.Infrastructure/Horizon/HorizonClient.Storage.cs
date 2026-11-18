using Launcher.Application.Experience;

namespace Launcher.Infrastructure.Horizon;

public sealed partial class HorizonClient
{
    public IEnumerable<string> LocalBuildDirectories()
    {
        var builds=Path.Combine(Root,"builds");
        if(!Directory.Exists(builds))return [];
        return Directory.EnumerateDirectories(builds).Where(p=>File.Exists(Path.Combine(p,ClientName))&&File.Exists(Path.Combine(p,PreparerName))&&File.Exists(Path.Combine(p,"research-lock.json"))).ToArray();
    }

    public long ClearCache(IEnumerable<HorizonBuild> builds)
    {
        var targets=new List<string>{Path.Combine(Root,"prepared"),Path.Combine(Root,"minecraft"),Path.Combine(Root,"temp")};
        var managed=Path.Combine(Root,"builds")+Path.DirectorySeparatorChar;
        foreach(var build in builds)
            if(Path.GetFullPath(build.Directory).StartsWith(managed,StringComparison.OrdinalIgnoreCase))targets.Add(Path.Combine(build.Directory,".local"));
        long bytes=0;
        foreach(var path in targets.Distinct())
        {
            if(!Directory.Exists(path))continue;
            bytes+=ExperienceFiles.Files(path).Sum(file=>new FileInfo(file).Length);
            ExperienceFiles.DeleteTree(path,Root);
        }
        return bytes;
    }
}
