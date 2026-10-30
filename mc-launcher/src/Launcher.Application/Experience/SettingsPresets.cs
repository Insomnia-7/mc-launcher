using Launcher.Application.Install;

namespace Launcher.Application.Experience;

public sealed record SettingsPreset(string Id,string Name,string? GameVersion,string? Loader,InstanceSettings Settings,Dictionary<string,string> Options,Dictionary<string,string> Configs)
{
    public override string ToString()=>Name;
}
public sealed class SettingsPresets(string dataRoot)
{
    private readonly string _root=Path.Combine(dataRoot,"presets");
    private static bool Allowed(string key)=>key.StartsWith("key_")||key.StartsWith("soundCategory_")||new[]{"music","sound","fov","gamma","renderDistance","simulationDistance","maxFps","fullscreen","enableVsync","guiScale","lang","showSubtitles","bobView","autoJump","entityShadows","particles"}.Contains(key);
    public IReadOnlyList<SettingsPreset> List()=>Directory.Exists(_root)?Directory.EnumerateFiles(_root,"*.json").Select(ExperienceFiles.Read<SettingsPreset>).OfType<SettingsPreset>().OrderBy(x=>x.Name).ToArray():[];
    public SettingsPreset Capture(string gameRoot,string instanceId,string name,bool includeConfigs=false)
    {
        if(string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("请为预设命名。");
        var instance=ExperienceFiles.Child(Path.Combine(gameRoot,"instances"),instanceId);var game=Path.Combine(instance,".minecraft");
        var settings=InstanceSettings.Load(gameRoot,instanceId) with{JavaPath=null,ExtraJvmArgs=null,ExtraGameArgs=null};
        var options=ReadOptions(Path.Combine(game,"options.txt")).Where(k=>Allowed(k.Key)).ToDictionary(k=>k.Key,k=>k.Value);
        var configs=new Dictionary<string,string>();
        if(includeConfigs) foreach(var file in ExperienceFiles.Files(Path.Combine(game,"config")))
        {
            if(new FileInfo(file).Length>1048576) throw new InvalidDataException("模组配置超过 1 MiB，请单独备份："+Path.GetFileName(file));
            configs[Path.GetRelativePath(Path.Combine(game,"config"),file).Replace('\\','/')]=Convert.ToBase64String(File.ReadAllBytes(file));
        }
        var metadata=InstanceMetadata.TryRead(gameRoot,instanceId);
        var preset=new SettingsPreset(Guid.NewGuid().ToString("N"),name.Trim(),metadata?.MinecraftVersion,metadata?.Loader,settings,options,configs);
        ExperienceFiles.Write(Path.Combine(_root,preset.Id+".json"),preset);return preset;
    }
    public static Dictionary<string,string> ReadOptions(string path)
    {
        var result=new Dictionary<string,string>();if(!File.Exists(path)) return result;
        foreach(var line in File.ReadLines(path)) {var separator=line.IndexOf(':');if(separator>0)result[line[..separator]]=line[(separator+1)..];}return result;
    }
    public async Task<int> ApplyAsync(SettingsPreset preset,string gameRoot,string instanceId,bool applyConfigs=false,CancellationToken ct=default)
    {
        var instance=ExperienceFiles.Child(Path.Combine(gameRoot,"instances"),instanceId);var game=Path.Combine(instance,".minecraft");ExperienceFiles.RejectLinks(game);
        var metadata=InstanceMetadata.TryRead(gameRoot,instanceId);
        if(applyConfigs&&preset.Configs.Count>0&&(preset.GameVersion is null||metadata?.MinecraftVersion!=preset.GameVersion||metadata.Loader!=preset.Loader)) throw new InvalidOperationException("模组配置只能用于相同游戏版本和加载器的实例；可取消「包含模组配置」后只应用通用设置。");
        await new InstanceSnapshots(dataRoot,gameRoot,instanceId).CaptureAsync("应用预设前："+preset.Name,ct:ct);
        var current=InstanceSettings.Load(gameRoot,instanceId);var desired=preset.Settings.Normalized();
        (current with{MemoryMb=desired.MemoryMb,WindowWidth=desired.WindowWidth,WindowHeight=desired.WindowHeight,FullScreen=desired.FullScreen,JvmPreset=desired.JvmPreset}).Save(gameRoot,instanceId);
        var path=Path.Combine(game,"options.txt");var values=ReadOptions(path);var applied=0;
        foreach(var pair in preset.Options.Where(p=>Allowed(p.Key)))
        {
            if(pair.Key.StartsWith("key_")&&values.TryGetValue(pair.Key,out var original)&&int.TryParse(original,out _)!=int.TryParse(pair.Value,out _))continue;
            values[pair.Key]=pair.Value;applied++;
        }
        Directory.CreateDirectory(game);var temporary=path+".preset.tmp";
        await File.WriteAllLinesAsync(temporary,values.Select(p=>p.Key+":"+p.Value),ct);File.Move(temporary,path,true);
        if(applyConfigs) foreach(var config in preset.Configs) {ct.ThrowIfCancellationRequested();var target=ExperienceFiles.Child(Path.Combine(game,"config"),config.Key);ExperienceFiles.RejectLinks(target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);await File.WriteAllBytesAsync(target,Convert.FromBase64String(config.Value),ct);}
        return applied;
    }
    public void Delete(SettingsPreset preset) {if(!Guid.TryParseExact(preset.Id,"N",out _))throw new InvalidDataException("无效预设编号。");File.Delete(ExperienceFiles.Child(_root,preset.Id+".json"));}
}
