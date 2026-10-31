using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Application.Update;

namespace Launcher.Application.Experience;

[Flags]
public enum SyncSections { None = 0, Keys = 1, Sound = 2, Display = 4, Language = 8, Servers = 16 }
public sealed record SharedGameSettings(string Id, string SourceId, SyncSections Sections, Dictionary<string,string> Options,
    string? Servers, long Revision, DateTimeOffset UpdatedAt);
public sealed record SettingsSyncMember(string ProfileId, bool Enabled, SyncSections Sections, HashSet<string> IndependentKeys,
    Dictionary<string,string> Baseline, string? ServersBaseline = null)
{
    public const string FileName = ".fangyu-settings-sync.json";
}
public sealed record SettingsSyncChange(string Key, string Name, string? Local, string Shared, string Kind, string Reason, bool ApplyByDefault)
{
    public bool IsConflict => Kind == "冲突";
    public bool IsSkipped => Kind is "独立" or "跳过";
}
public sealed record SettingsSyncPlan(string InstanceId, string Fingerprint, SharedGameSettings Profile, SettingsSyncMember Member,
    IReadOnlyList<SettingsSyncChange> Changes)
{
    public int Conflicts => Changes.Count(c => c.IsConflict);
    public int Available => Changes.Count(c => !c.IsSkipped);
}
public sealed record SettingsSyncBackup(string Id, DateTimeOffset CreatedAt, Dictionary<string,SnapshotFile?> Files, bool Complete = false)
{
    public string Label => $"{CreatedAt.LocalDateTime:MM-dd HH:mm} · 同步前设置";
    public override string ToString() => Label;
}

/// <summary>Opt-in local, source-to-followers sync. No polling, accounts, Java options or mod configuration copying.</summary>
public sealed class SettingsSync(string dataRoot, string gameRoot)
{
    public const string ServersKey = "$servers";
    private static readonly SyncSections Supported = SyncSections.Keys | SyncSections.Sound | SyncSections.Display | SyncSections.Language | SyncSections.Servers;
    private readonly string _root = Path.GetFullPath(gameRoot);
    public string ProfilePath => Path.Combine(dataRoot, "settings-sync", RootKey(_root) + ".json");
    private static string RootKey(string root) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? root.TrimEnd('\\','/').ToUpperInvariant() : root.TrimEnd('/'))));
    public SharedGameSettings? Read() => ExperienceFiles.Read<SharedGameSettings>(ProfilePath);
    private string Instance(string id)
    {
        if (id.IndexOfAny(['/', '\\', ':']) >= 0 || id is "." or ".." || string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("无效实例编号。");
        var path = ExperienceFiles.Child(Path.Combine(_root,"instances"),id); ExperienceFiles.RejectLinks(path); return path;
    }
    private string GameFile(string id,string name) => Path.Combine(Instance(id), ".minecraft", name);
    public SettingsSyncMember? Member(string id) => ExperienceFiles.Read<SettingsSyncMember>(Path.Combine(Instance(id), SettingsSyncMember.FileName));
    public SettingsSyncMember SaveMember(string id, bool enabled, SyncSections sections, IEnumerable<string> independent)
    {
        var profile = Read() ?? throw new InvalidOperationException("请先保存一个共享来源。");
        ValidateInstance(id);
        var previous = Member(id);
        var member = previous?.ProfileId == profile.Id ? previous : new(profile.Id, false, SyncSections.Keys | SyncSections.Sound | SyncSections.Language, [], new());
        member = member with { Enabled = enabled, Sections = sections & Supported,
            IndependentKeys = independent.Where(k => Section(k) != SyncSections.None).ToHashSet(StringComparer.Ordinal) };
        ExperienceFiles.Write(Path.Combine(Instance(id),SettingsSyncMember.FileName),member); return member;
    }
    private void ValidateInstance(string id)
    {
        Instance(id);
        if (!InstanceRegistry.Load(_root).Instances.Any(i => i.Id == id)) throw new InvalidOperationException("游戏实例已不存在，请刷新游戏库。");
    }
    public async Task<SharedGameSettings> SetSourceAsync(string sourceId,SyncSections sections,CancellationToken ct = default)
    {
        ValidateInstance(sourceId); sections &= Supported;
        if (sections == SyncSections.None) throw new InvalidOperationException("请至少选择一类要共享的设置。");
        var previous = Read();
        var values = (sections&~SyncSections.Servers)!=0 ? ReadOptions(GameFile(sourceId,"options.txt")).Where(p => (Section(p.Key) & sections) != 0).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal) : new Dictionary<string,string>(StringComparer.Ordinal);
        var servers = (sections & SyncSections.Servers) != 0 ? await ReadServersAsync(sourceId,ct).ConfigureAwait(false) : null;
        if (values.Count == 0 && servers is null) throw new InvalidOperationException("来源实例还没有可共享的设置，请先运行游戏生成设置文件。");
        var profile = new SharedGameSettings(previous?.Id ?? Guid.NewGuid().ToString("N"),sourceId,sections,values,servers,(previous?.Revision ?? 0)+1,DateTimeOffset.UtcNow);
        ExperienceFiles.RejectLinks(ProfilePath); ExperienceFiles.Write(ProfilePath,profile); return profile;
    }
    public async Task<SharedGameSettings?> RefreshSourceAsync(CancellationToken ct = default)
    {
        var profile = Read(); if (profile is null) return null;
        ValidateInstance(profile.SourceId);
        var options = (profile.Sections&~SyncSections.Servers)!=0 ? ReadOptions(GameFile(profile.SourceId,"options.txt")).Where(p=>(Section(p.Key)&profile.Sections)!=0).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal) : new Dictionary<string,string>(StringComparer.Ordinal);
        var servers = (profile.Sections & SyncSections.Servers)!=0 ? await ReadServersAsync(profile.SourceId,ct).ConfigureAwait(false) : null;
        if (options.Count == profile.Options.Count && options.All(p=>profile.Options.GetValueOrDefault(p.Key)==p.Value) && servers == profile.Servers) return profile;
        var changed = profile with { Options=options,Servers=servers,Revision=profile.Revision+1,UpdatedAt=DateTimeOffset.UtcNow };
        ExperienceFiles.RejectLinks(ProfilePath); ExperienceFiles.Write(ProfilePath,changed); return changed;
    }
    public async Task<SettingsSyncPlan> PreviewAsync(string id,CancellationToken ct = default)
    {
        ValidateInstance(id);
        var profile = Read() ?? throw new InvalidOperationException("尚未建立共享设置。");
        var member = Member(id) ?? throw new InvalidOperationException("请先为此实例保存同步选项。");
        if (profile.Id != member.ProfileId) throw new InvalidOperationException("共享方案已变化，请重新保存实例同步选项。");
        if (profile.SourceId == id) throw new InvalidOperationException("此实例是共享来源，无需接收自己的设置。");
        if (!member.Enabled) throw new InvalidOperationException("此实例尚未启用共享设置。");
        var local = (profile.Sections&member.Sections&~SyncSections.Servers)!=0 ? ReadOptions(GameFile(id,"options.txt")) : new Dictionary<string,string>(StringComparer.Ordinal);
        var changes = new List<SettingsSyncChange>();
        foreach (var (key, value) in profile.Options.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var section = Section(key);
            if (section == SyncSections.None || (section & profile.Sections & member.Sections) == 0) continue;
            var current = local.GetValueOrDefault(key);
            if (member.IndependentKeys.Contains(key)) { changes.Add(new(key,Name(key),current,value,"独立","本实例保留独立设置",false)); continue; }
            if (current is null) { changes.Add(new(key,Name(key),null,value,"跳过","当前游戏未生成此设置，保留版本和模组差异",false)); continue; }
            if (section == SyncSections.Keys && int.TryParse(current,out _) != int.TryParse(value,out _))
            { changes.Add(new(key,Name(key),current,value,"跳过","新旧游戏按键编码不同，已保留本地",false)); continue; }
            if (current == value) continue;
            var unchanged = member.Baseline.TryGetValue(key,out var original) && current == original;
            changes.Add(new(key,Name(key),current,value,unchanged?"更新":"冲突",unchanged?"共享来源已更新":"本地值不同；默认保留，勾选后才采用共享值",unchanged));
        }
        if ((profile.Sections & member.Sections & SyncSections.Servers) != 0 && profile.Servers is {} serverData)
        {
            var current = await ReadServersAsync(id,ct).ConfigureAwait(false);
            if (current != serverData)
            {
                var same = current is null || ServerHash(current) == member.ServersBaseline;
                changes.Add(new(ServersKey,"服务器列表",current,serverData,same?"更新":"冲突",same?"同步共享服务器列表":"两个列表不同；勾选将替换整个列表，并备份原列表",same));
            }
        }
        return new(id,await FingerprintAsync(id,ct).ConfigureAwait(false),profile,member,changes);
    }

    public async Task<int> ApplyAsync(SettingsSyncPlan plan,IReadOnlyDictionary<string,bool>? choices = null,bool automatic = false,CancellationToken ct = default)
    {
        if (await FingerprintAsync(plan.InstanceId,ct).ConfigureAwait(false)!=plan.Fingerprint) throw new InvalidOperationException("共享设置或实例文件已改变，请重新预览。");
        if (Read()?.Id != plan.Profile.Id || !plan.Member.Enabled) throw new InvalidOperationException("实例同步选项已变化。");
        var member = plan.Member with { Baseline=new(plan.Member.Baseline,StringComparer.Ordinal),IndependentKeys=new(plan.Member.IndependentKeys,StringComparer.Ordinal) };
        var accepted = new Dictionary<string,string>(StringComparer.Ordinal);
        var current = (plan.Profile.Sections&member.Sections&~SyncSections.Servers)!=0 ? ReadOptions(GameFile(plan.InstanceId,"options.txt")) : new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var (key,value) in plan.Profile.Options)
            if (Section(key)!=SyncSections.None && (Section(key)&plan.Profile.Sections&member.Sections)!=0 && !member.IndependentKeys.Contains(key) && current.GetValueOrDefault(key)==value) member.Baseline[key]=value;
        string? servers = null;
        foreach (var change in plan.Changes.Where(c=>!c.IsSkipped))
        {
            var apply = choices?.GetValueOrDefault(change.Key,change.ApplyByDefault) ?? change.ApplyByDefault;
            if (!apply)
            {
                if (!automatic && change.Key!=ServersKey) member.IndependentKeys.Add(change.Key);
                else if (!automatic && change.Key==ServersKey) member=member with {Sections=member.Sections&~SyncSections.Servers};
                continue;
            }
            if (change.Key == ServersKey) { servers=change.Shared; member=member with {ServersBaseline=ServerHash(servers)}; }
            else
            {
                if (Section(change.Key)==SyncSections.None) throw new InvalidDataException("同步包含不允许的设置。");
                accepted[change.Key]=change.Shared;member.Baseline[change.Key]=change.Shared;member.IndependentKeys.Remove(change.Key);
            }
        }
        if (servers is null && (plan.Profile.Sections&member.Sections&SyncSections.Servers)!=0)
        {
            var local = await ReadServersAsync(plan.InstanceId,ct).ConfigureAwait(false);
            if (local is not null && local==plan.Profile.Servers) member=member with {ServersBaseline=ServerHash(local)};
        }
        if (accepted.Count==0 && servers is null)
        {
            ct.ThrowIfCancellationRequested();ExperienceFiles.Write(Path.Combine(Instance(plan.InstanceId),SettingsSyncMember.FileName),member); return 0;
        }
        var stage = Path.Combine(_root,".cache","settings-sync",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            var files = new List<string>{SettingsSyncMember.FileName};
            ExperienceFiles.Write(Path.Combine(stage,SettingsSyncMember.FileName),member);
            if (accepted.Count>0)
            {
                var target=ExperienceFiles.Child(stage,".minecraft/options.txt");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target,PatchOptions(GameFile(plan.InstanceId,"options.txt"),accepted),ct).ConfigureAwait(false);files.Add(".minecraft/options.txt");
            }
            if (servers is not null)
            {
                var target=ExperienceFiles.Child(stage,".minecraft/servers.dat");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target,Convert.FromBase64String(servers),ct).ConfigureAwait(false);files.Add(".minecraft/servers.dat");
            }
            if(await FingerprintAsync(plan.InstanceId,ct).ConfigureAwait(false)!=plan.Fingerprint)throw new InvalidOperationException("准备同步期间文件发生变化，请重新预览。");
            await CommitAsync(plan.InstanceId,stage,files,"同步设置",ct).ConfigureAwait(false);
            return accepted.Count+(servers is null?0:1);
        }
        finally {ExperienceFiles.DeleteTree(stage,Path.Combine(_root,".cache","settings-sync"));}
    }

    private async Task CommitAsync(string id,string stage,IReadOnlyList<string> paths,string title,CancellationToken ct)
    {
        var instance=Instance(id);var backupId=Guid.NewGuid().ToString("N");var directory=Path.Combine(instance,".settings-sync-backups",backupId);
        var originals=new Dictionary<string,SnapshotFile?>();
        foreach(var path in paths)
        {
            var file=ExperienceFiles.Child(instance,path);var hash=await ModpackBaseline.HashAsync(file,ct).ConfigureAwait(false);
            originals[path]=hash is null?null:new(path,new FileInfo(file).Length,hash);
        }
        var backup=new SettingsSyncBackup(backupId,DateTimeOffset.UtcNow,originals);
        ExperienceFiles.Write(Path.Combine(directory,"recovery.json"),backup);
        var marker=".settings-sync-backups/"+backupId+"/recovery.json";
        ExperienceFiles.Write(ExperienceFiles.Child(stage,marker),backup with {Complete=true});
        await UpdateFileTransaction.ApplyAsync(paths.Append(marker).Select(p=>new UpdateFile(p,"","",0)).ToArray(),stage,instance,Path.Combine(directory,"files"),ct:ct).ConfigureAwait(false);
    }
    public IReadOnlyList<SettingsSyncBackup> Backups(string id)
    {
        var directory=Path.Combine(Instance(id),".settings-sync-backups");ExperienceFiles.RejectLinks(directory);
        return Directory.Exists(directory)?Directory.EnumerateDirectories(directory).Select(d=>ExperienceFiles.Read<SettingsSyncBackup>(Path.Combine(d,"recovery.json")))
            .Where(b=>b is {Complete:true}&&Guid.TryParseExact(b.Id,"N",out _)).Cast<SettingsSyncBackup>().OrderByDescending(b=>b.CreatedAt).ToArray():[];
    }
    public async Task RestoreAsync(string id,string backupId,CancellationToken ct=default)
    {
        var backup=Backups(id).FirstOrDefault(b=>b.Id==backupId)??throw new InvalidDataException("同步恢复点不存在。");
        var instance=Instance(id);var directory=Path.Combine(instance,".settings-sync-backups",backup.Id);var stage=Path.Combine(_root,".cache","settings-sync",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach(var (path,info) in backup.Files)
            {
                if(path is not (".minecraft/options.txt" or ".minecraft/servers.dat" or SettingsSyncMember.FileName))throw new InvalidDataException("恢复点包含不允许的文件。");
                ExperienceFiles.RejectLinks(ExperienceFiles.Child(instance,path));if(info is null)continue;
                var file=ExperienceFiles.Child(Path.Combine(directory,"files"),path);
                if(await ModpackBaseline.HashAsync(file,ct).ConfigureAwait(false)!=info.Sha256)throw new InvalidDataException("恢复点文件校验失败："+path);
                var target=ExperienceFiles.Child(stage,path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
            }
            var member=ExperienceFiles.Read<SettingsSyncMember>(Path.Combine(stage,SettingsSyncMember.FileName))??Member(id);
            if(member is not null)ExperienceFiles.Write(Path.Combine(stage,SettingsSyncMember.FileName),member with {Enabled=false});
            var replacements=backup.Files.Where(p=>p.Value is not null).Select(p=>p.Key).ToHashSet(StringComparer.Ordinal);
            if(member is not null)replacements.Add(SettingsSyncMember.FileName);
            await UpdateFileTransaction.ApplyAsync(replacements.Select(p=>new UpdateFile(p,"","",0)).ToArray(),stage,instance,Path.Combine(directory,"before-restore-"+Guid.NewGuid().ToString("N")),ct:ct,
                removeFiles:backup.Files.Where(p=>p.Value is null&&!replacements.Contains(p.Key)).Select(p=>p.Key).ToArray()).ConfigureAwait(false);
            ExperienceFiles.Write(Path.Combine(directory,"recovery.json"),backup with {Complete=false});
        }
        finally {ExperienceFiles.DeleteTree(stage,Path.Combine(_root,".cache","settings-sync"));}
    }
    private async Task<string> FingerprintAsync(string id,CancellationToken ct)
    {
        var sections=(Read()?.Sections??SyncSections.None)&(Member(id)?.Sections??SyncSections.None);
        var paths=new List<string>{ProfilePath,Path.Combine(Instance(id),SettingsSyncMember.FileName)};
        if((sections&~SyncSections.Servers)!=0)paths.Add(GameFile(id,"options.txt"));
        if((sections&SyncSections.Servers)!=0)paths.Add(GameFile(id,"servers.dat"));
        var text=new StringBuilder();foreach(var path in paths)text.Append(await ModpackBaseline.HashAsync(path,ct).ConfigureAwait(false)??"missing").Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static Dictionary<string,string> ReadOptions(string path)
    {
        ExperienceFiles.RejectLinks(path);if(File.Exists(path)&&new FileInfo(path).Length>1048576)throw new InvalidDataException("游戏设置文件超过 1 MiB，请先检查文件。");
        return SettingsPresets.ReadOptions(path);
    }
    private async Task<string?> ReadServersAsync(string id,CancellationToken ct)
    {
        var path=GameFile(id,"servers.dat");ExperienceFiles.RejectLinks(path);if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>1048576)throw new InvalidDataException("服务器列表超过 1 MiB，请单独管理。");
        return Convert.ToBase64String(await File.ReadAllBytesAsync(path,ct).ConfigureAwait(false));
    }
    private static string ServerHash(string encoded)=>Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(encoded)));
    private static string PatchOptions(string path,IReadOnlyDictionary<string,string> values)
    {
        var text=File.ReadAllText(path);var newline=text.Contains("\r\n",StringComparison.Ordinal)?"\r\n":"\n";
        var lines=text.Replace("\r\n","\n").Split('\n');
        return string.Join(newline,lines.Select(line=>{var colon=line.IndexOf(':');return colon>0&&values.TryGetValue(line[..colon],out var value)?line[..(colon+1)]+value:line;}));
    }
    public static SyncSections Section(string key)=>key.StartsWith("key_",StringComparison.Ordinal)?SyncSections.Keys:
        key.StartsWith("soundCategory_",StringComparison.Ordinal)||key is "music" or "sound"?SyncSections.Sound:
        key is "fov" or "gamma" or "renderDistance" or "simulationDistance" or "maxFps" or "fullscreen" or "enableVsync" or "guiScale" or "bobView" or "entityShadows" or "particles"?SyncSections.Display:
        key is "lang" or "showSubtitles"?SyncSections.Language:SyncSections.None;
    public static string Name(string key)=>key switch
    {
        ServersKey=>"服务器列表","fov"=>"视野","gamma"=>"亮度","renderDistance"=>"渲染距离","simulationDistance"=>"模拟距离","maxFps"=>"帧率上限","fullscreen"=>"全屏","enableVsync"=>"垂直同步","guiScale"=>"界面大小","lang"=>"游戏语言","showSubtitles"=>"声音字幕","bobView"=>"视角摇晃","entityShadows"=>"实体阴影","particles"=>"粒子效果",
        "key_key.forward"=>"前进","key_key.back"=>"后退","key_key.left"=>"向左","key_key.right"=>"向右","key_key.jump"=>"跳跃","key_key.sneak"=>"潜行","key_key.sprint"=>"疾跑","key_key.inventory"=>"背包","key_key.attack"=>"攻击 / 破坏","key_key.use"=>"使用 / 放置","key_key.chat"=>"聊天",
        "soundCategory_master"=>"主音量","soundCategory_music"=>"音乐","soundCategory_record"=>"唱片","soundCategory_weather"=>"天气音量","soundCategory_block"=>"方块音量","soundCategory_hostile"=>"敌对生物音量","soundCategory_neutral"=>"友善生物音量","soundCategory_player"=>"玩家音量","soundCategory_ambient"=>"环境音量","soundCategory_voice"=>"语音音量",
        _=>key.StartsWith("key_",StringComparison.Ordinal)?"按键 · "+key[4..]:key.StartsWith("soundCategory_",StringComparison.Ordinal)?"音量 · "+key[14..]:key
    };
}
