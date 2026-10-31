using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Experience;
using Launcher.Application.Operations;

namespace Launcher.App.ViewModels;

public sealed record SyncInstanceChoice(string Id,string Name) { public override string ToString()=>Name; }
public sealed partial class SyncFieldChoice(string key,bool independent) : ObservableObject
{
    public string Key {get;}=key;
    public string Name=>SettingsSync.Name(Key);
    [ObservableProperty] private bool _independent=independent;
}
public sealed partial class SyncChangeRow(SettingsSyncChange change) : ObservableObject
{
    public SettingsSyncChange Change {get;}=change;
    public string Name=>Change.Name;
    public string Kind=>Change.Kind;
    public string Reason=>Change.Reason;
    public bool CanChoose=>!Change.IsSkipped;
    public string Local=>Value(Change.Key,Change.Local);
    public string Shared=>Value(Change.Key,Change.Shared);
    [ObservableProperty] private bool _apply=change.ApplyByDefault;
    private static string Value(string key,string? value)
    {
        if(value is null)return "尚未设置";
        if(key==SettingsSync.ServersKey)return $"服务器列表文件 · {Convert.FromBase64String(value).Length/1024d:0.#} KiB";
        if(SettingsSync.Section(key)==SyncSections.Sound&&double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var volume))return $"{volume*100:0}%";
        return value.StartsWith("key.keyboard.",StringComparison.Ordinal)?value[13..].ToUpperInvariant():value is "true"?"开启":value is "false"?"关闭":value;
    }
}

public sealed partial class MainViewModel
{
    private SettingsSyncPlan? _syncPlan;
    private bool _loadingSync;
    private string? _loadedSyncSourceId;
    [ObservableProperty] private bool _showsSettingsSync;
    [ObservableProperty] private bool _settingsSyncBusy;
    [ObservableProperty] private bool _syncSourceEditorOpen=true;
    [ObservableProperty] private string _settingsSyncHint="选择共享来源，再为想同步的实例启用跟随；默认不会修改游戏设置。";
    [ObservableProperty] private SyncInstanceChoice? _syncSource;
    [ObservableProperty] private SyncInstanceChoice? _syncTarget;
    [ObservableProperty] private bool _shareKeys=true;
    [ObservableProperty] private bool _shareSound=true;
    [ObservableProperty] private bool _shareDisplay;
    [ObservableProperty] private bool _shareLanguage=true;
    [ObservableProperty] private bool _shareServers;
    [ObservableProperty] private bool _followSettings;
    [ObservableProperty] private bool _followKeys=true;
    [ObservableProperty] private bool _followSound=true;
    [ObservableProperty] private bool _followDisplay;
    [ObservableProperty] private bool _followLanguage=true;
    [ObservableProperty] private bool _followServers;
    [ObservableProperty] private string _settingsSyncSourceName="尚未设置共享来源";
    public ObservableCollection<SyncInstanceChoice> SyncInstances {get;}=[];
    public ObservableCollection<SyncFieldChoice> SyncIndependentFields {get;}=[];
    public ObservableCollection<SyncChangeRow> SyncChanges {get;}=[];
    public bool CanUseSettingsSync=>CanEditExperience&&!SettingsSyncBusy;
    public bool CanEditSyncTarget=>CanUseSettingsSync&&SyncTarget is {} target&&_loadedSyncSourceId is not null&&_loadedSyncSourceId!=target.Id;
    public bool HasSettingsSyncPlan=>_syncPlan is not null;
    public string SyncTargetName=>SyncTarget?.Name??"选择游戏实例";
    public string SettingsSyncSummary=>_syncPlan is {} plan?$"{plan.Changes.Count(c=>!c.IsSkipped&&!c.IsConflict)} 项可更新 · {plan.Conflicts} 项需选择 · {plan.Changes.Count(c=>c.IsSkipped)} 项保留独立":"先预览差异，再决定采用哪些共享设置。";
    public bool HasSettingsSyncBackup {get;private set;}
    private SettingsSync CurrentSettingsSync=>new(AppPaths.DataDirectory,GameRoot);
    private SyncSections SharedSections=>Sections(ShareKeys,ShareSound,ShareDisplay,ShareLanguage,ShareServers);
    private SyncSections FollowedSections=>Sections(FollowKeys,FollowSound,FollowDisplay,FollowLanguage,FollowServers);
    private static SyncSections Sections(bool keys,bool sound,bool display,bool language,bool servers)=>(keys?SyncSections.Keys:0)|(sound?SyncSections.Sound:0)|(display?SyncSections.Display:0)|(language?SyncSections.Language:0)|(servers?SyncSections.Servers:0);

    private void InitializeSettingsSync()
    {
        BeforeNetworkSettings+=()=>ShowsSettingsSync=false;
        PrepareSharedSettings=ApplySharedBeforeLaunchAsync;
        GameExited=(root,id)=>{_=PlatformUi.RunOnUiAsync(()=>_=RefreshSharedAfterExitAsync(root,id));};
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(IsAppearanceOpen) or nameof(IsNetworkOpen))ShowsSettingsSync=false;
            if(e.PropertyName is nameof(CanEditExperience) or nameof(SettingsSyncBusy)){OnPropertyChanged(nameof(CanUseSettingsSync));OnPropertyChanged(nameof(CanEditSyncTarget));}
            if(e.PropertyName==nameof(GameRoot)){_syncPlan=null;SyncChanges.Clear();NotifySyncPlan();if(ShowsSettingsSync)LoadSettingsSyncEditor();}
        };
    }
    partial void OnShowsSettingsSyncChanged(bool value)
    {OnPropertyChanged(nameof(IsNetworkSettings));OnPropertyChanged(nameof(IsAppearanceSettings));if(value)LoadSettingsSyncEditor();}
    partial void OnSyncTargetChanged(SyncInstanceChoice? value) {OnPropertyChanged(nameof(SyncTargetName));if(!_loadingSync)LoadSyncMember();}
    [RelayCommand] private void OpenSettingsSync(){OpenNetwork();ShowsStorage=false;ShowsLauncherUpdates=false;ShowsSettingsSync=true;}
    private void NotifySyncPlan(){OnPropertyChanged(nameof(HasSettingsSyncPlan));OnPropertyChanged(nameof(SettingsSyncSummary));}
    private void LoadSettingsSyncEditor()
    {
        _loadingSync=true;
        try
        {
            var selected=SyncTarget?.Id??SelectedInstance?.InstanceId;
            SyncInstances.Clear();foreach(var instance in Instances)SyncInstances.Add(new(instance.InstanceId,instance.DisplayName));
            var profile=CurrentSettingsSync.Read();
            _loadedSyncSourceId=profile?.SourceId;
            SyncSourceEditorOpen=profile is null;
            SyncSource=SyncInstances.FirstOrDefault(i=>i.Id==profile?.SourceId)??SyncInstances.FirstOrDefault(i=>i.Id==SelectedInstance?.InstanceId)??SyncInstances.FirstOrDefault();
            SettingsSyncSourceName=profile is null?"尚未设置共享来源":"共享来源 · "+(SyncInstances.FirstOrDefault(i=>i.Id==profile.SourceId)?.Name??"来源实例已不存在");
            if(profile is not null){ShareKeys=profile.Sections.HasFlag(SyncSections.Keys);ShareSound=profile.Sections.HasFlag(SyncSections.Sound);ShareDisplay=profile.Sections.HasFlag(SyncSections.Display);ShareLanguage=profile.Sections.HasFlag(SyncSections.Language);ShareServers=profile.Sections.HasFlag(SyncSections.Servers);}
            SyncTarget=SyncInstances.FirstOrDefault(i=>i.Id==selected)??SyncInstances.FirstOrDefault(i=>i.Id!=profile?.SourceId)??SyncInstances.FirstOrDefault();
        }
        catch(Exception ex){SettingsSyncHint=ex.Message;}
        finally{_loadingSync=false;LoadSyncMember();}
    }
    private void LoadSyncMember()
    {
        _syncPlan=null;SyncChanges.Clear();SyncIndependentFields.Clear();NotifySyncPlan();HasSettingsSyncBackup=false;
        try
        {
            if(SyncTarget is not {} target)return;
            var sync=CurrentSettingsSync;var profile=sync.Read();var member=sync.Member(target.Id);
            FollowSettings=member?.Enabled==true&&member.ProfileId==profile?.Id;
            var sections=member?.Sections??(SyncSections.Keys|SyncSections.Sound|SyncSections.Language);
            FollowKeys=sections.HasFlag(SyncSections.Keys);FollowSound=sections.HasFlag(SyncSections.Sound);FollowDisplay=sections.HasFlag(SyncSections.Display);FollowLanguage=sections.HasFlag(SyncSections.Language);FollowServers=sections.HasFlag(SyncSections.Servers);
            if(profile is not null)foreach(var key in profile.Options.Keys.OrderBy(SettingsSync.Name))SyncIndependentFields.Add(new(key,member?.IndependentKeys.Contains(key)==true));
            HasSettingsSyncBackup=sync.Backups(target.Id).Count>0;
        }
        catch(Exception ex){SettingsSyncHint=ex.Message;}
        finally{OnPropertyChanged(nameof(CanEditSyncTarget));OnPropertyChanged(nameof(HasSettingsSyncBackup));}
    }
    private async Task SyncActionAsync(string id,Func<SettingsSync,CancellationToken,Task> action)
    {
        if(!CanUseSettingsSync)return;
        var root=GameRoot;
        try{EnsureInstanceStopped(id,root);}catch(Exception ex){SettingsSyncHint=ex.Message;return;}
        using var operation=GameOperationCoordinator.Shared.TryEnter(root,"同步游戏设置");
        if(operation is null){SettingsSyncHint=GameOperationCoordinator.Shared.BusyMessage(root);return;}
        SettingsSyncBusy=true;IsBusy=true;
        try{await action(new(AppPaths.DataDirectory,root),CancellationToken.None);}
        catch(Exception ex){SettingsSyncHint="设置同步未完成："+ex.Message;}
        finally{SettingsSyncBusy=false;IsBusy=false;}
    }
    [RelayCommand] private void SaveSettingsSyncSource()
    {
        if(!CanUseSettingsSync||SyncSource is not {} source)return;
        var sections=SharedSections;var root=GameRoot;
        AskConfirm("保存共享来源","共享方案仅保存在本机。其他实例启用跟随后，会在启动前读取共享设置；冲突默认保留本地。",source.Name,"保存共享来源",()=>_=SyncActionAsync(source.Id,async(sync,ct)=>
        {
            if(root!=GameRoot)return;await sync.SetSourceAsync(source.Id,sections,ct);LoadSettingsSyncEditor();SettingsSyncHint="共享来源已保存；可以为其他实例选择跟随内容。";ShowNotice("共享设置已保存");
        }));
    }
    [RelayCommand] private async Task SaveSettingsSyncTargetAsync()
    {
        if(!CanEditSyncTarget||SyncTarget is not {} target)return;
        var enabled=FollowSettings;var sections=FollowedSections;var independent=SyncIndependentFields.Where(f=>f.Independent).Select(f=>f.Key).ToArray();
        await SyncActionAsync(target.Id,(sync,ct)=>{sync.SaveMember(target.Id,enabled,sections,independent);LoadSyncMember();SettingsSyncHint=enabled?"已启用跟随；首次不同的设置将保留本地，可在预览中选择采用共享值。":"已暂停此实例同步，现有设置保留。";return Task.CompletedTask;});
    }
    [RelayCommand] private async Task PreviewSettingsSyncAsync()
    {
        if(!CanEditSyncTarget||SyncTarget is not {} target)return;
        await SyncActionAsync(target.Id,async(sync,ct)=>
        {
            if(sync.Read() is {} profile&&InstanceStopped(profile.SourceId,GameRoot))await Task.Run(()=>sync.RefreshSourceAsync(ct),ct);
            var plan=await Task.Run(()=>sync.PreviewAsync(target.Id,ct),ct);
            if(SyncTarget?.Id!=target.Id)return;
            _syncPlan=plan;SyncChanges.Clear();foreach(var change in plan.Changes)SyncChanges.Add(new(change));NotifySyncPlan();
            SettingsSyncHint="勾选采用共享值；未勾选的差异将保留为本实例例外。服务器列表按整个文件替换，应用前自动备份。";
        });
    }
    [RelayCommand] private void ApplySettingsSync()
    {
        if(!CanEditSyncTarget||_syncPlan is not {} plan)return;
        var choices=SyncChanges.Where(c=>c.CanChoose).ToDictionary(c=>c.Change.Key,c=>c.Apply,StringComparer.Ordinal);
        AskConfirm("应用共享设置","将备份并应用已勾选的差异；未勾选的设置保留为本实例例外。",SettingsSyncSummary,"备份并应用",()=>_=SyncActionAsync(plan.InstanceId,async(sync,ct)=>
        {
            var count=await Task.Run(()=>sync.ApplyAsync(plan,choices,ct:ct),ct);LoadSyncMember();SettingsSyncHint=count>0?$"已同步 {count} 项，原设置已备份。":"已保留本地设置，并保存实例例外。";ShowNotice(SettingsSyncHint);
        }));
    }
    [RelayCommand] private void CloseSettingsSyncPreview(){_syncPlan=null;SyncChanges.Clear();NotifySyncPlan();}
    [RelayCommand] private void RestoreSettingsSync()
    {
        if(!CanEditSyncTarget||SyncTarget is not {} target)return;
        var backup=CurrentSettingsSync.Backups(target.Id).FirstOrDefault();if(backup is null)return;
        AskConfirm("恢复同步前设置","恢复上次同步涉及的设置，并暂停此实例的同步。",backup.Label,"恢复并暂停同步",()=>_=SyncActionAsync(target.Id,async(sync,ct)=>
        {await Task.Run(()=>sync.RestoreAsync(target.Id,backup.Id,ct),ct);LoadSyncMember();SettingsSyncHint="已恢复同步前的设置，并暂停此实例同步。";ShowNotice("已恢复原设置并暂停同步");}));
    }
    private bool InstanceStopped(string id,string root){try{EnsureInstanceStopped(id,root);return true;}catch(InvalidOperationException){return false;}}
    private async Task ApplySharedBeforeLaunchAsync(string id,CancellationToken ct)
    {
        var root=GameRoot;var sync=new SettingsSync(AppPaths.DataDirectory,root);
        try
        {
            if(sync.Member(id) is not {Enabled:true})return;
            var profile=sync.Read();if(profile is null||profile.SourceId==id)return;
            if(InstanceStopped(profile.SourceId,root))await Task.Run(()=>sync.RefreshSourceAsync(ct),ct);
            var plan=await Task.Run(()=>sync.PreviewAsync(id,ct),ct);var count=await Task.Run(()=>sync.ApplyAsync(plan,automatic:true,ct:ct),ct);
            SettingsSyncHint=plan.Conflicts>0?$"已同步 {count} 项；{plan.Conflicts} 项不同设置保留本地，可在游戏同步中选择。":count>0?$"启动前已同步 {count} 项游戏设置。":"当前设置已匹配；独立项和不兼容的按键保持本地设置。";
            if(count>0||plan.Conflicts>0)ShowNotice(SettingsSyncHint,()=>{OpenSettingsSync();SyncTarget=SyncInstances.FirstOrDefault(i=>i.Id==id);});
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){SettingsSyncHint="已保留当前游戏设置，本次跳过同步："+ex.Message;ShowNotice(SettingsSyncHint,()=>OpenSettingsSync());}
    }
    private async Task RefreshSharedAfterExitAsync(string root,string id)
    {
        var sync=new SettingsSync(AppPaths.DataDirectory,root);if(sync.Read()?.SourceId!=id)return;
        using var operation=GameOperationCoordinator.Shared.TryEnter(root,"更新共享设置");if(operation is null)return;
        try{await Task.Run(()=>sync.RefreshSourceAsync());if(root==GameRoot)SettingsSyncHint="来源游戏已退出，共享设置已更新，下次启动跟随实例时应用。";}
        catch(Exception ex){if(root==GameRoot)SettingsSyncHint="共享来源暂未刷新："+ex.Message;}
    }
}
