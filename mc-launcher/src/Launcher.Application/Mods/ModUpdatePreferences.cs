using Launcher.Application.Experience;

namespace Launcher.Application.Mods;

public sealed record ModUpdatePreferenceData(string[] LockedFiles,Dictionary<string,string> SkippedVersions);
public sealed class ModUpdatePreferences(string gameRoot,string instanceId)
{
    private readonly string _path=ExperienceFiles.Child(ExperienceFiles.Child(Path.Combine(gameRoot,"instances"),instanceId),"mod-update-policy.json");
    public ModUpdatePreferenceData Read()=>ExperienceFiles.Read<ModUpdatePreferenceData>(_path)??new([],new());
    public bool Allows(ModUpdateCandidate candidate)
    {
        var data=Read();return !data.LockedFiles.Contains(candidate.FileName,StringComparer.OrdinalIgnoreCase)&&(!data.SkippedVersions.TryGetValue(candidate.FileName,out var version)||version!=candidate.VersionId);
    }
    public void Lock(string file,bool locked)
    {
        var data=Read();var files=data.LockedFiles.Where(f=>!f.Equals(file,StringComparison.OrdinalIgnoreCase));
        ExperienceFiles.Write(_path,data with{LockedFiles=(locked?files.Append(file):files).Distinct().ToArray()});
    }
    public void Skip(ModUpdateCandidate candidate)
    {
        var data=Read();data.SkippedVersions[candidate.FileName]=candidate.VersionId??candidate.LatestVersion??"";ExperienceFiles.Write(_path,data);
    }
}
