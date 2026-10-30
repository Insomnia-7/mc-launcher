using System.Collections.ObjectModel;
using System.IO.Compression;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App;
using Launcher.Application.Accounts;
using Launcher.Application.Abstractions;
using Launcher.Application.Diagnostics;
using Launcher.Application.Experience;
using Launcher.Application.Auth;
using Launcher.Application.FirstRun;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;
using Launcher.Domain.Content;
using Launcher.Domain.Loaders;

namespace Launcher.App.ViewModels;

public sealed partial class DurableTaskRow(DurableTaskRecord record):ObservableObject
{
    [ObservableProperty] private DurableTaskRecord _record=record;
    public string Id=>Record.Id;
    public string Title=>Record.Title;
    public string StateText=>Record.StateText;
    public string Detail=>Record.Detail;
    public double Percent=>Record.Percent;
    public bool CanResume=>Record.CanResume;
    public bool CanPause=>Record.CanPause;
    public bool CanCancel=>Record.CanCancel;
    partial void OnRecordChanged(DurableTaskRecord value)
    {foreach(var property in new[]{nameof(Title),nameof(StateText),nameof(Detail),nameof(Percent),nameof(CanResume),nameof(CanPause),nameof(CanCancel)})OnPropertyChanged(property);}
}
public sealed partial class MainViewModel
{
    private IPlayerAppearanceClient _appearanceClient=null!;
    private PersistentTasks _tasks=null!;
    private CancellationTokenSource? _wardrobeCts;
    private int _wardrobeRevision;
    private bool _experienceInitialized;
    [ObservableProperty] private bool _isExperienceOpen;
    [ObservableProperty] private int _experienceTab;
    [ObservableProperty] private bool _wardrobeBusy;
    [ObservableProperty] private bool _experienceBusy;
    [ObservableProperty] private string _experienceHint="";
    [ObservableProperty] private string _wardrobeHint="导入皮肤，预览后应用到当前身份。";
    [ObservableProperty] private string _lookName="新皮肤";
    [ObservableProperty] private bool _previewSlim;
    [ObservableProperty] private bool _previewOuterLayer=true;
    [ObservableProperty] private bool _previewAnimate=true;
    [ObservableProperty] private byte[]? _previewSkin;
    [ObservableProperty] private byte[]? _previewCape;
    [ObservableProperty] private WardrobeLook? _selectedLook;
    [ObservableProperty] private OwnedCape? _selectedOwnedCape;
    [ObservableProperty] private InstanceSnapshot? _selectedSnapshot;
    [ObservableProperty] private string _snapshotName="手动快照";
    [ObservableProperty] private bool _includeWorlds;
    [ObservableProperty] private int _restoreScope;
    [ObservableProperty] private SettingsPreset? _selectedPreset;
    [ObservableProperty] private string _presetName="我的设置";
    [ObservableProperty] private bool _includeModConfigs;
    [ObservableProperty] private string _updateChannel="正式版";
    public ObservableCollection<WardrobeLook> WardrobeLooks {get;}=[];
    public ObservableCollection<OwnedCape> OwnedCapes {get;}=[];
    public ObservableCollection<InstanceSnapshot> Snapshots {get;}=[];
    public ObservableCollection<SettingsPreset> Presets {get;}=[];
    public ObservableCollection<DurableTaskRow> DurableTasks {get;}=[];
    public bool HasDurableTasks=>DurableTasks.Count>0;
    public ObservableCollection<InspectionIssue> InspectionIssues {get;}=[];
    public ObservableCollection<string> LockedModFiles {get;}=[];
    public string[] UpdateChannels {get;}=["正式版","测试版","开发版"];
    public string[] RestoreScopes {get;}=["仅模组与配置（保留存档）","仅恢复存档","模组配置与存档"];
    public bool IsWardrobeTab=>ExperienceTab==0;
    public bool PreviewActive=>IsRolePage;
    public bool IsHistoryTab=>ExperienceTab==1;
    public bool IsToolsTab=>ExperienceTab==2;
    public bool CanEditExperience=>!ExperienceBusy&&!IsBusy&&!Download.IsBusy&&!IsModBusy;
    public bool CanEditWardrobe=>!WardrobeBusy;
    public string AppearanceIdentity=>AccountText;
    public string LocalSkinHint=>HasAccount?"官方外观同步到账号；本地衣柜方案不会自动上传。":"离线外观需安装兼容皮肤模组；本地导入不会自动让其他玩家看见。";
    private WardrobeStore CurrentWardrobe=>new(AppPaths.DataDirectory,Account?.Uuid??_library!.OfflineIdentity.Uuid);
    private InstanceSnapshots CurrentSnapshots=>new(AppPaths.DataDirectory,GameRoot,SelectedInstance?.InstanceId??throw new InvalidOperationException("请先选择游戏版本。"));
    public void InitializeExperience(IPlayerAppearanceClient appearance)
    {
        InitializeMaintenance();
        _appearanceClient=appearance;_tasks=new(AppPaths.DataDirectory);_tasks.Execute=ExecuteDurableTaskAsync;
        _tasks.Changed+=()=>{_ = PlatformUi.RunOnUiAsync(RefreshTaskRows);};
        Download.QueueTask=QueueExperienceTaskAsync;
        CancelPersistentTask=()=>{if(_tasks.ActiveId is not {} id)return false;_tasks.Cancel(id);return true;};
        Download.CancelPersistentTask=CancelPersistentTask;
        PersistentSetup=async(game,memory,launch)=>
        {
            if(string.IsNullOrWhiteSpace(game)||memory is < 512 or > 65536){SetupError="请选择版本并填写 512–65536 MiB 内存。";return;}
            if(!HasAccount){if(!OfflineIdentityFactory.IsValidName(SetupOfflineName)){SetupError="离线用户名需为 3–16 位字母、数字或下划线。";return;}OfflineName=SetupOfflineName;}
            SetupError="";IsSetupBusy=true;
            try
            {
                await QueueExperienceTaskAsync(new("setup",game+" 首次安装",GameRoot,null,new(){{"game",game},{"memory",memory.ToString()}}));
                if(_tasks.List().FirstOrDefault()?.State==DurableTaskState.Completed){RefreshInstances();SelectedInstance=Instances.FirstOrDefault(i=>i.VersionId==game);IsSetupOpen=false;IsInstanceSection=true;if(launch)await LaunchAsync();}
                else SetupError="任务尚未完成，可在任务中心恢复或查看原因。";
            }
            finally{IsSetupBusy=false;}
        };
        PersistentImport=path=>QueueExperienceTaskAsync(new("import","导入 "+Path.GetFileName(path),GameRoot,null,new(){{"path",Path.GetFullPath(path)}}));
        PersistentModUpdate=(id,candidate)=>QueueExperienceTaskAsync(new("update","更新 "+candidate.ModId,GameRoot,id,new()
        {
            {"source",candidate.Source},{"project",candidate.ProjectId??""},{"release",candidate.VersionId??""},
            {"file",candidate.FileName},{"current",candidate.CurrentVersion??""},{"latest",candidate.LatestVersion??""},{"hash",candidate.CurrentSha1??""}
        }));
        BeforeModUpdate=async(id,title,ct)=>
        {
            EnsureInstanceStopped(id);await new InstanceSnapshots(AppPaths.DataDirectory,GameRoot,id).CaptureAsync("更新模组前："+title,ct:ct);
        };
        IsModUpdateAllowed=c=>SelectedInstance is {} instance&&new ModUpdatePreferences(GameRoot,instance.InstanceId).Allows(c);
        ModUpdateReleaseChannel="release";
        PrepareAppearance=(id,online,ct)=>
        {
            if(online){WardrobeStore.RemoveLocalProfile(Path.Combine(GameRoot,"instances",id,".minecraft"));return Task.CompletedTask;}
            if(!online&&CurrentWardrobe.Selection is {UseInGame:true} selection&&CurrentWardrobe.List().FirstOrDefault(x=>x.Id==selection.LookId) is {} look)
            {
                var game=Path.Combine(GameRoot,"instances",id,".minecraft");
                var mods=InstalledModScanner.Scan(Path.Combine(game,"mods"),ct);
                if(!mods.Mods.Any(m=>m.Enabled&&(m.ModId?.Contains("customskinloader",StringComparison.OrdinalIgnoreCase)==true||m.FileName.Contains("customskinloader",StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException("该实例尚未安装 CustomSkinLoader，请在角色衣柜中安装适配组件，或停用游戏内本地外观。");
                CurrentWardrobe.ApplyLocal(game,_library!.OfflineIdentity.Name,look);
            }
            return Task.CompletedTask;
        };
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(Account) or nameof(OfflineName))
            {OnPropertyChanged(nameof(AppearanceIdentity));OnPropertyChanged(nameof(LocalSkinHint));if(IsExperienceOpen&&IsWardrobeTab)_=RefreshWardrobeAsync();}
            if(e.PropertyName==nameof(SelectedInstance)&&(IsExperienceOpen||IsDetailOpen))ReloadExperienceLists();
            if(e.PropertyName is nameof(IsBusy) or nameof(IsModBusy) or nameof(ExperienceBusy))OnPropertyChanged(nameof(CanEditExperience));
        };
        _experienceInitialized=true;RefreshTaskRows();
    }
    partial void OnExperienceTabChanged(int value)
    {
        OnPropertyChanged(nameof(PreviewActive));
        OnPropertyChanged(nameof(IsWardrobeTab));OnPropertyChanged(nameof(IsHistoryTab));OnPropertyChanged(nameof(IsToolsTab));
        if(IsExperienceOpen&&value==0)_=RefreshWardrobeAsync();
    }
    partial void OnWardrobeBusyChanged(bool value)=>OnPropertyChanged(nameof(CanEditWardrobe));
    partial void OnUpdateChannelChanged(string value)=>ModUpdateReleaseChannel=value=="测试版"?"beta":value=="开发版"?"alpha":"release";
    partial void OnIsExperienceOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewActive));
        OnPropertyChanged(nameof(HasOverlay));
        if(!value){WardrobeBusy=false;_wardrobeCts?.Cancel();_wardrobeRevision++;PreviewSkin=null;PreviewCape=null;}
    }
    [RelayCommand] private async Task OpenWardrobeAsync()
    {
        IsSetupOpen=false;IsAccountsOpen=false;IsAppearanceOpen=false;IsNetworkOpen=false;
        ExperienceTab=0;IsExperienceOpen=true;await RefreshWardrobeAsync();
    }
    [RelayCommand] private void OpenInstanceHistory(){OpenGame(null);DetailTab=2;ReloadExperienceLists();}
    [RelayCommand] private void OpenTaskCenter(){IsSetupOpen=false;IsAccountsOpen=false;IsAppearanceOpen=false;IsNetworkOpen=false;ExperienceTab=2;IsExperienceOpen=true;RefreshTaskRows();}
    [RelayCommand] private void CloseExperience(){ShowInstances();}
    [RelayCommand] private void SelectExperienceTab(string? value)
    {
        if(value=="1")OpenInstanceHistory();else if(value=="2")OpenTaskCenter();else if(value=="0"){ExperienceTab=0;IsExperienceOpen=true;}
    }
    public void PauseExperienceForShutdown()=>_tasks?.PauseActiveForShutdown();
    public bool HasActivePersistentTask=>_tasks?.ActiveId is not null;
    public Task PauseTasksForShutdownAsync()=>_tasks?.PauseAndWaitAsync()??Task.CompletedTask;
    private void RefreshTaskRows()
    {
        if(!_experienceInitialized)return;
        foreach(var task in _tasks.List())
        {
            var row=DurableTasks.FirstOrDefault(r=>r.Id==task.Id);
            if(row is null)DurableTasks.Insert(0,new(task));else row.Record=task;
        }
        OnPropertyChanged(nameof(CanEditExperience));
        OnPropertyChanged(nameof(HasDurableTasks));
    }
    private void ReloadExperienceLists()
    {
        if(IsDetailOpen && DetailTab == 2) _ = RefreshWorldsAsync();
        try
        {
            Snapshots.Clear();if(SelectedInstance is not null)foreach(var snapshot in CurrentSnapshots.List())Snapshots.Add(snapshot);
            SelectedSnapshot=Snapshots.FirstOrDefault();Presets.Clear();foreach(var preset in new SettingsPresets(AppPaths.DataDirectory).List())Presets.Add(preset);SelectedPreset=Presets.FirstOrDefault();
            LockedModFiles.Clear();if(SelectedInstance is {} instance)foreach(var file in new ModUpdatePreferences(GameRoot,instance.InstanceId).Read().LockedFiles)LockedModFiles.Add(file);
        }
        catch(Exception ex){ExperienceHint=ErrorText.Describe(ex);}
    }
    private void EnsureInstanceStopped(string id,string? root=null)
    {
        var version=Launcher.Application.Instances.InstanceRegistry.ResolveVersionId(root??GameRoot,id);
        lock(_running)if(_running.ContainsKey(version))throw new InvalidOperationException("请先关闭此实例的游戏，再修改外观组件、配置或存档。");
    }
    private async Task ExperienceActionAsync(Func<CancellationToken,Task> action)
    {
        if(!CanEditExperience){ExperienceHint="请等待当前任务结束，或先在任务中心暂停。";return;}
        try{if(SelectedInstance is {} instance)EnsureInstanceStopped(instance.InstanceId);}catch(Exception ex){ExperienceHint=ex.Message;return;}
        using var operation=GameOperationCoordinator.Shared.TryEnter(GameRoot,"实例管理");
        if(operation is null){ExperienceHint=GameOperationCoordinator.Shared.BusyMessage(GameRoot);return;}
        ExperienceBusy=true;IsBusy=true;using var ct=new CancellationTokenSource();
        try {await action(ct.Token);ReloadExperienceLists();}
        catch(Exception ex){ExperienceHint=ErrorText.Describe(ex);}
        finally{ExperienceBusy=false;IsBusy=false;}
    }
    private static void ValidatePreview(byte[] png)
    {using var stream=new MemoryStream(png);using var bitmap=new Bitmap(stream);if(bitmap.PixelSize.Width!=64||bitmap.PixelSize.Height is not (32 or 64))throw new InvalidDataException("PNG 图片无法作为 Minecraft 皮肤使用。");}
    private async Task WardrobeActionAsync(Func<CancellationToken,Task> action)
    {
        if(WardrobeBusy)return;WardrobeBusy=true;
        try{await action(CancellationToken.None);}catch(Exception ex){WardrobeHint=ErrorText.Describe(ex);}finally{WardrobeBusy=false;}
    }
    [RelayCommand] private async Task RefreshWardrobeAsync()
    {
        if(_appearanceClient is null)return;
        _wardrobeCts?.Cancel();_wardrobeCts?.Dispose();var cancellation=_wardrobeCts=new();var revision=++_wardrobeRevision;
        SelectedLook=null;WardrobeLooks.Clear();foreach(var look in CurrentWardrobe.List())WardrobeLooks.Add(look);
        SelectedLook=WardrobeLooks.FirstOrDefault(x=>x.Id==CurrentWardrobe.Selection?.LookId)??WardrobeLooks.FirstOrDefault();OwnedCapes.Clear();PreviewCape=null;
        if(Account is null){WardrobeHint="离线外观保存在本机。导入皮肤后，可为所选实例安装适配组件。";return;}
        WardrobeBusy=true;
        try
        {
            var account=await EnsureFreshAccountAsync(cancellation.Token)??throw new InvalidOperationException("请先登录正版账号。");
            var profile=await _appearanceClient.GetAsync(account.AccessToken,cancellation.Token);
            if(revision!=_wardrobeRevision||!IsExperienceOpen)return;
            if(!profile.Id.Replace("-","").Equals(account.Uuid.Replace("-",""),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("返回的外观资料与当前账号不一致。");
            foreach(var cape in profile.Capes)OwnedCapes.Add(cape);SelectedOwnedCape=OwnedCapes.FirstOrDefault(c=>c.Active);
            if(SelectedLook is null&&profile.SkinUrl is {} url){var bytes=await _appearanceClient.GetTextureAsync(url,cancellation.Token);if(revision==_wardrobeRevision){ValidatePreview(bytes);PreviewSkin=bytes;PreviewSlim=profile.Model=="slim";}}
            WardrobeHint="已读取官方资料。上传皮肤会同步到账号；披风只能选择账号已拥有的项目。";
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
        catch(Exception ex){if(revision==_wardrobeRevision)WardrobeHint=ErrorText.Describe(ex);}
        finally{if(revision==_wardrobeRevision)WardrobeBusy=false;}
    }
    partial void OnSelectedLookChanged(WardrobeLook? value)
    {
        if(value is null){PreviewSkin=null;return;}
        try{var skin=CurrentWardrobe.Skin(value);ValidatePreview(skin);PreviewSkin=skin;PreviewCape=CurrentWardrobe.Cape(value);PreviewSlim=value.Model=="slim";}catch(Exception ex){WardrobeHint=ErrorText.Describe(ex);}
    }
    partial void OnSelectedOwnedCapeChanged(OwnedCape? value){if(value is not null)_=PreviewOwnedCapeAsync(value);}
    private async Task PreviewOwnedCapeAsync(OwnedCape cape)
    {
        var revision=_wardrobeRevision;
        try{var bytes=await _appearanceClient.GetTextureAsync(cape.Url,_wardrobeCts?.Token??CancellationToken.None);if(revision==_wardrobeRevision&&SelectedOwnedCape?.Id==cape.Id)PreviewCape=bytes;}catch(OperationCanceledException){}catch(Exception ex){WardrobeHint=ErrorText.Describe(ex);}
    }
    [RelayCommand] private async Task ImportWardrobeSkinAsync()
    {
        var path=await PlatformUi.PickFileAsync("导入皮肤","PNG 皮肤|*.png");if(path is null)return;await ImportWardrobeSkinFileAsync(path);
    }
    public async Task ImportWardrobeSkinFileAsync(string path)
    {
        await WardrobeActionAsync(async ct=>
        {
            if(new FileInfo(path).Length>2097152)throw new InvalidDataException("皮肤不能超过 2 MiB。");
            var bytes=await File.ReadAllBytesAsync(path,ct);WardrobeStore.ValidatePng(bytes);ValidatePreview(bytes);
            var look=CurrentWardrobe.Import(LookName,bytes,PreviewSlim?"slim":"classic");WardrobeLooks.Add(look);SelectedLook=look;WardrobeHint="已加入本地衣柜，尚未上传或应用到游戏。";
        });
    }
    [RelayCommand] private async Task ImportWardrobeCapeAsync()
    {
        if(SelectedLook is not {} look){WardrobeHint="请先选择本地皮肤方案。";return;}
        var path=await PlatformUi.PickFileAsync("导入本地披风","PNG 披风|*.png");if(path is null)return;
        await WardrobeActionAsync(async ct=>
        {
            var bytes=await File.ReadAllBytesAsync(path,ct);WardrobeStore.ValidatePng(bytes,true);
            var updated=CurrentWardrobe.Import(look.Name,CurrentWardrobe.Skin(look),PreviewSlim?"slim":"classic",bytes);WardrobeLooks.Add(updated);SelectedLook=updated;WardrobeHint="已保存为含本地披风的新方案；这不是官方账号披风。";
        });
    }
    [RelayCommand] private async Task UploadWardrobeSkinAsync()
    {
        await WardrobeActionAsync(async ct=>
        {
            if(SelectedLook is not {} look)throw new InvalidOperationException("请先选择本地皮肤方案。");
            var account=await EnsureFreshAccountAsync(ct)??throw new InvalidOperationException("离线身份不能上传到官方账号。");
            look=CurrentWardrobe.SetModel(look,PreviewSlim?"slim":"classic");
            await _appearanceClient.UploadSkinAsync(account.AccessToken,CurrentWardrobe.Skin(look),look.Model,ct);
            var profile=await _appearanceClient.GetAsync(account.AccessToken,ct);
            WardrobeHint=profile.SkinUrl is null?"上传请求已成功，官方资料尚未返回皮肤，请稍后刷新。":"上传成功，官方资料已刷新；已运行的游戏可能需要重新进入。";
            if(profile.SkinUrl is {} url&&Account?.Uuid==account.Uuid)SkinHead=PlatformUi.DecodeSkinHead(await _appearanceClient.GetTextureAsync(url,ct));
        });
    }
    [RelayCommand] private async Task ResetOfficialSkinAsync()=>await WardrobeActionAsync(async ct=>{var account=await EnsureFreshAccountAsync(ct)??throw new InvalidOperationException("请先登录正版账号。");await _appearanceClient.ResetSkinAsync(account.AccessToken,ct);WardrobeHint="已请求恢复默认官方皮肤，请刷新资料。";});
    [RelayCommand] private async Task ApplyOwnedCapeAsync()=>await WardrobeActionAsync(async ct=>{var account=await EnsureFreshAccountAsync(ct)??throw new InvalidOperationException("请先登录正版账号。");var cape=SelectedOwnedCape??throw new InvalidOperationException("请选择已拥有的披风。");await _appearanceClient.SetCapeAsync(account.AccessToken,cape.Id,ct);WardrobeHint="官方披风已切换。";});
    [RelayCommand] private async Task HideOfficialCapeAsync()=>await WardrobeActionAsync(async ct=>{var account=await EnsureFreshAccountAsync(ct)??throw new InvalidOperationException("请先登录正版账号。");await _appearanceClient.SetCapeAsync(account.AccessToken,null,ct);PreviewCape=null;WardrobeHint="已隐藏官方披风。";});
    [RelayCommand] private void DeleteWardrobeLook(){if(SelectedLook is not {} look)return;CurrentWardrobe.Delete(look);WardrobeLooks.Remove(look);SelectedLook=WardrobeLooks.FirstOrDefault();}
    [RelayCommand] private async Task UseLocalLookAsync()
    {
        await ExperienceActionAsync(async ct=>
        {
            if(HasAccount)throw new InvalidOperationException("当前是正版身份，请使用「上传到账号」；本地游戏外观用于离线身份。");
            var look=SelectedLook??throw new InvalidOperationException("请先导入并选择皮肤。");var instance=SelectedInstance??throw new InvalidOperationException("请先选择游戏实例。");
            var game=Path.Combine(GameRoot,"instances",instance.InstanceId,".minecraft");
            if(!InstalledModScanner.Scan(Path.Combine(game,"mods"),ct).Mods.Any(m=>m.Enabled&&(m.ModId?.Contains("customskinloader",StringComparison.OrdinalIgnoreCase)==true||m.FileName.Contains("customskinloader",StringComparison.OrdinalIgnoreCase))))throw new InvalidOperationException("请先点击「安装离线皮肤组件」。纯原版实例需要先选择适配加载器。");
            await CurrentSnapshots.CaptureAsync("应用离线外观前",ct:ct);look=CurrentWardrobe.SetModel(look,PreviewSlim?"slim":"classic");
            CurrentWardrobe.ApplyLocal(game,_library!.OfflineIdentity.Name,look);CurrentWardrobe.Select(look,true);SkinHead=PlatformUi.DecodeSkinHead(CurrentWardrobe.Skin(look));WardrobeHint="本地外观已应用；其他玩家能否看到取决于其客户端与服务器配置。";
        });
    }
    [RelayCommand] private async Task DisableLocalLookAsync()=>await ExperienceActionAsync(async ct=>
    {
        var instance=SelectedInstance??throw new InvalidOperationException("请选择实例。");await CurrentSnapshots.CaptureAsync("停用离线外观前",ct:ct);
        WardrobeStore.RemoveLocalProfile(Path.Combine(GameRoot,"instances",instance.InstanceId,".minecraft"),true);CurrentWardrobe.DisableInGame();SkinHead=null;WardrobeHint="已停用此实例的方隅本地外观，其他皮肤源和配置保留。";
    });
    [RelayCommand] private async Task InstallOfflineSkinComponentAsync()
    {
        if(SelectedInstance is not {} instance){WardrobeHint="请先选择实例。";return;}
        try
        {
            EnsureInstanceStopped(instance.InstanceId);var environment=InstanceEnvironment.Resolve(GameRoot,instance.InstanceId);
            if(environment.LoaderSlug is null)throw new InvalidOperationException("这是纯原版实例，请先在发现资源中安装 Fabric / Forge 等加载器版本。");
            var release=await Download.ResolveOfflineSkinVersionAsync(instance.InstanceId);
            await QueueExperienceTaskAsync(new("resource","CustomSkinLoader 本地皮肤组件",GameRoot,instance.InstanceId,new(){{"source",DownloadViewModel.ModrinthSource},{"project","customskinloader"},{"category",DownloadCategory.Mod.ToString()},{"release",release}}));
            WardrobeHint="组件任务已处理，请查看任务中心结果；成功后点击应用本地外观。";
        }
        catch(Exception ex){WardrobeHint=ErrorText.Describe(ex);}
    }
    [RelayCommand] private async Task CreateSnapshotAsync()=>await ExperienceActionAsync(async ct=>{var snapshot=await CurrentSnapshots.CaptureAsync(SnapshotName,IncludeWorlds,ct);ExperienceHint="快照已保存："+snapshot.Name;});
    [RelayCommand] private void RestoreSnapshot()
    {
        if(SelectedSnapshot is not {} snapshot)return;
        AskConfirm("恢复实例快照",RestoreScope==0?"将恢复模组和配置，保留当前存档。":"将恢复选定范围；选中存档恢复时，会替换当前存档。",snapshot.Name+"\n"+snapshot.Summary,"确认恢复",()=>_ = RestoreSnapshotCoreAsync(snapshot,(SnapshotRestoreScope)RestoreScope));
    }
    private async Task RestoreSnapshotCoreAsync(InstanceSnapshot snapshot,SnapshotRestoreScope scope)=>await ExperienceActionAsync(async ct=>{await CurrentSnapshots.CaptureAsync("恢复前自动备份",scope!=SnapshotRestoreScope.Configuration,ct);await CurrentSnapshots.RestoreAsync(snapshot,scope,ct);RefreshInstances();await RefreshModsAsync();ExperienceHint="快照已恢复；恢复前状态已另存为快照。";});
    [RelayCommand] private void DeleteSnapshot(){if(SelectedSnapshot is not {} snapshot)return;AskConfirm("删除快照","仅删除这份备份，当前游戏数据不变。",snapshot.Name,"删除快照",()=>{CurrentSnapshots.Delete(snapshot);ReloadExperienceLists();});}
    [RelayCommand] private async Task SavePresetAsync()=>await ExperienceActionAsync(_=>{var instance=SelectedInstance??throw new InvalidOperationException("请选择实例。");new SettingsPresets(AppPaths.DataDirectory).Capture(GameRoot,instance.InstanceId,PresetName,IncludeModConfigs);ExperienceHint="设置预设已保存，不包含账号和本机 Java 路径。";return Task.CompletedTask;});
    [RelayCommand] private async Task ApplyPresetAsync()=>await ExperienceActionAsync(async ct=>{var preset=SelectedPreset??throw new InvalidOperationException("请选择预设。");var instance=SelectedInstance??throw new InvalidOperationException("请选择实例。");var count=await new SettingsPresets(AppPaths.DataDirectory).ApplyAsync(preset,GameRoot,instance.InstanceId,IncludeModConfigs,ct);LoadSettings(instance.InstanceId);ExperienceHint=$"预设已应用，共 {count} 项游戏设置；原配置已自动备份。";});
    [RelayCommand] private void DeletePreset(){if(SelectedPreset is not {} preset)return;new SettingsPresets(AppPaths.DataDirectory).Delete(preset);ReloadExperienceLists();}
    [RelayCommand] private void LockModUpdate(ModUpdateRow? row){if(row is null||SelectedInstance is not {} instance)return;new ModUpdatePreferences(GameRoot,instance.InstanceId).Lock(row.FileName,true);ModUpdates.Remove(row);ReloadExperienceLists();}
    [RelayCommand] private void SkipModUpdate(ModUpdateRow? row){if(row is null||SelectedInstance is not {} instance)return;new ModUpdatePreferences(GameRoot,instance.InstanceId).Skip(row.Candidate);ModUpdates.Remove(row);ExperienceHint="已跳过此发布版本，后续其他版本仍会提示。";}
    [RelayCommand] private void UnlockModUpdate(string? file){if(file is null||SelectedInstance is not {} instance)return;new ModUpdatePreferences(GameRoot,instance.InstanceId).Lock(file,false);ReloadExperienceLists();}
    [RelayCommand] private async Task InspectInstanceAsync()
    {
        await ExperienceActionAsync(async ct=>
        {
            var instance=SelectedInstance??throw new InvalidOperationException("请选择游戏实例。");var root=GameRoot;InspectionIssues.Clear();
            var version=await _versionLoader.LoadAsync(root,instance.VersionId,ct);var required=version.JavaVersion?.MajorVersion??8;
            var javas=await _javaLocator.DiscoverAsync(ct);
            if(root!=GameRoot||SelectedInstance?.InstanceId!=instance.InstanceId)return;
            if(!javas.Any(j=>j.MajorVersion>=required))InspectionIssues.Add(new("缺少合适的 Java",$"版本资料声明 Java {required}+；本机尚未发现。","启动时可自动准备，或在版本设置中指定 Java。",true));
            var settings=InstanceSettings.Load(GameRoot,instance.InstanceId);
            if(settings.MemoryMb>InstanceSettings.AdaptiveMemoryMb()*2)InspectionIssues.Add(new("分配内存偏高",$"当前分配 {settings.MemoryMb} MiB。","请结合整合包要求与系统剩余内存调整。"));
            var scan=InstalledModScanner.Scan(Path.Combine(GameRoot,"instances",instance.InstanceId,".minecraft","mods"),ct);
            foreach(var duplicate in scan.Mods.Where(m=>m.Enabled&&m.ModId is not null).GroupBy(m=>m.ModId).Where(g=>g.Count()>1))InspectionIssues.Add(new("重复模组："+duplicate.Key,string.Join("、",duplicate.Select(m=>m.FileName)),"保留一个适配版本，禁用或移除重复文件。",true));
            var environment=InstanceEnvironment.Resolve(GameRoot,instance.InstanceId);
            foreach(var mod in scan.Mods.Where(m=>m.Enabled&&m.LoaderKind is {} loader&&environment.LoaderSlug is {} current&&!loader.Contains(current,StringComparison.OrdinalIgnoreCase)&&!(current=="quilt"&&loader.Contains("fabric",StringComparison.OrdinalIgnoreCase))))InspectionIssues.Add(new("可能装错加载器："+mod.FileName,$"文件声明 {mod.LoaderKind}；实例为 {environment.Loader}。","核对模组下载版本；多加载器模组需结合项目说明确认。"));
            var log=Path.Combine(GameRoot,"instances",instance.InstanceId,".minecraft","logs","latest.log");
            if(StartupDiagnostics.Analyze(log) is {} diagnosis)InspectionIssues.Add(new(diagnosis.Summary,diagnosis.Evidence??"匹配最近游戏日志规则。",diagnosis.Advice));
            if(InspectionIssues.Count==0)InspectionIssues.Add(new("基础检查通过","未发现 Java 版本与重复 Mod ID 问题。","这不代表所有模组运行逻辑均兼容，可结合实际启动结果判断。"));
            ExperienceHint=$"体检完成，共 {InspectionIssues.Count} 条结果。";
        });
    }
    [RelayCommand] private async Task ExportDiagnosticsAsync()
    {
        if(SelectedInstance is not {} instance){ExperienceHint="请选择实例。";return;}
        var path=await PlatformUi.SaveFileAsync("导出已脱敏诊断包","Zip 诊断包|*.zip","方隅-诊断.zip");if(path is null)return;
        await ExperienceActionAsync(async ct=>{await DiagnosticBundle.ExportAsync(path,GameRoot,instance.InstanceId,DiagnosisText+"\n"+string.Join("\n\n",InspectionIssues.Select(i=>i.Title+"\n依据："+i.Evidence+"\n建议："+i.Advice)),ct);ExperienceHint="已导出日志尾部与模组列表，未包含账号文件，敏感令牌已脱敏。";});
    }
    private async Task<DurableTaskRecord?> QueueExperienceTaskAsync(DurableTaskRequest request)
    {
        if(ExperienceBusy){ExperienceHint="请等待实例管理完成。";return null;}
        var task=_tasks.Add(request);ExperienceHint="已保存任务："+request.Title;
        if(_tasks.ActiveId is null&&!IsBusy&&!Download.IsBusy)
        {
            await _tasks.RunAsync(task.Id);
            var result=_tasks.List().Single(t=>t.Id==task.Id);
            StatusText=Download.Status=result.State switch
            {
                DurableTaskState.Completed=>request.Kind=="import"||request.Arguments.GetValueOrDefault("category")==DownloadCategory.Modpack.ToString()?"「"+request.Title+"」导入完成":request.Kind=="resource"?"「"+request.Title+"」已装到实例":"安装完成 · "+request.Title,
                DurableTaskState.Failed=>"安装失败："+result.Detail,
                DurableTaskState.Canceled=>"已取消",
                _=>"已暂停，断点与任务清单已保存。"
            };
            ExperienceHint=result.Detail;
        }
        return _tasks.List().Single(t=>t.Id==task.Id);
    }
    [RelayCommand] private async Task ResumeDurableTaskAsync(DurableTaskRow? task)
    {
        if(task is null||!task.CanResume)return;
        try{if(IsBusy||Download.IsBusy||ExperienceBusy)throw new InvalidOperationException("请先等待或暂停当前操作。");await _tasks.RunAsync(task.Id);}catch(Exception ex){ExperienceHint=ErrorText.Describe(ex);}
    }
    [RelayCommand] private void PauseDurableTask(DurableTaskRow? task){if(task is not null)_tasks.Pause(task.Id);}
    [RelayCommand] private void CancelDurableTask(DurableTaskRow? task){if(task is not null)_tasks.Cancel(task.Id);}
    private async Task ExecuteDurableTaskAsync(DurableTaskRequest request,IProgress<TaskProgress> progress,CancellationToken ct)
    {
        if(request.InstanceId is {} targetInstance)EnsureInstanceStopped(targetInstance,request.GameRoot);
        using var operation=GameOperationCoordinator.Shared.TryEnter(request.GameRoot,request.Title);
        if(operation is null)throw new InvalidOperationException(GameOperationCoordinator.Shared.BusyMessage(request.GameRoot));
        await PlatformUi.RunOnUiAsync(()=>{IsBusy=true;Download.IsBusy=true;StatusText="任务："+request.Title;});
        try
        {
            var activeTask=_tasks.ActiveId;
            IProgress<TaskProgress> uiProgress=new Progress<TaskProgress>(p=>{if(ct.IsCancellationRequested||_tasks.ActiveId!=activeTask||!_tasks.List().Any(t=>t.Id==activeTask&&t.State==DurableTaskState.Running))return;ProgressPercent=p.Percent;Download.ProgressPercent=p.Percent;StatusText=Download.Status=p.Detail;progress.Report(p);});
            if(request.Kind is "world-backup" or "world-restore")
            {
                var worlds = new WorldCatalog(AppPaths.DataDirectory, request.GameRoot, request.InstanceId ?? throw new InvalidOperationException("缺少实例。"));
                uiProgress.Report(new(0, request.Kind == "world-backup" ? "正在压缩世界文件…" : "正在校验备份并恢复世界…"));
                if(request.Kind == "world-backup") await Task.Run(() => worlds.BackupAsync(request.Arguments["folder"], request.Arguments["name"], ct), ct);
                else await Task.Run(() => worlds.RestoreAsync(request.Arguments["backup"], ct), ct);
                uiProgress.Report(new(100, "世界操作已完成"));
            }
            else if(request.Kind == "repair")
            {
                var version = await _versionLoader.LoadAsync(request.GameRoot, request.Arguments["version"], ct);
                var repairProgress = new Progress<DownloadProgress>(p => uiProgress.Report(new(p.Percent, "修复游戏文件 · " + p.CurrentFile)));
                await Task.Run(() => _installer.RepairAsync(version, request.GameRoot, repairProgress, ct), ct);
            }
            else if(request.Kind is "mod-batch" or "clone" or "trial-update")
            {
                await ExecuteWorkbenchTaskAsync(request,uiProgress,ct);
            }
            else if(request.Kind=="update")
            {
                var id=request.InstanceId??throw new InvalidOperationException("更新任务缺少目标实例。");
                var args=request.Arguments;
                await new InstanceSnapshots(AppPaths.DataDirectory,request.GameRoot,id).CaptureAsync("更新模组前："+request.Title,ct:ct);
                var candidate=new ModUpdateCandidate(args["project"],args["file"],args["current"],args["latest"],true,null,0,args["source"]){ProjectId=args["project"],VersionId=args["release"],CurrentSha1=args["hash"]};
                await _modUpdateInstaller.UpdateAsync(request.GameRoot,id,candidate,new Progress<ModInstallProgress>(p=>uiProgress.Report(new(p.Percent,p.Stage+" · "+p.Detail))),ct);
            }
            else if(request.Kind=="setup")
            {
                await _setupInstaller.InstallAsync(new(request.Arguments["game"],int.Parse(request.Arguments["memory"])),request.GameRoot,AppPaths.DataDirectory,new Progress<FirstRunProgress>(p=>uiProgress.Report(new(p.Percent,p.Stage+" · "+p.Detail))),ct);
            }
            else if(request.Kind=="import")
            {
                await using var source=_modpackFactory.Open(request.Arguments["path"]);
                await _modpackInstaller.ImportAsync(source,request.GameRoot,new Progress<ModpackImportProgress>(p=>uiProgress.Report(new(p.Percent,p.Stage+" · "+p.Detail))),ct);
            }
            else await Download.ExecutePersistentInstallAsync(request,uiProgress,ct);
            await PlatformUi.RunOnUiAsync(()=>
            {
                StatusText=Download.Status="任务已完成："+request.Title;
                if(GameRoot!=request.GameRoot)return;
                RefreshInstances();
                if(request.Kind is "clone" or "trial-update")
                {
                    var clone=System.Text.Json.JsonSerializer.Deserialize<Launcher.Application.Instances.InstanceCloneRequest>(request.Arguments["clone"]);
                    SelectedInstance=Instances.FirstOrDefault(i=>i.InstanceId==clone?.DestinationId)??SelectedInstance;
                    IsDetailOpen=true;DetailTab=request.Kind=="trial-update"?1:0;
                }
            });
        }
        finally{await PlatformUi.RunOnUiAsync(()=>{IsBusy=false;Download.IsBusy=false;ProgressPercent=0;Download.ProgressPercent=0;if(request.Kind is "clone" or "trial-update"&&GameRoot==request.GameRoot)RefreshInstances();});}
    }
}
