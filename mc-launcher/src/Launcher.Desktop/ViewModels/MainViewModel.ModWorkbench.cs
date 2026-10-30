using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App;
using Launcher.Application.Experience;
using Launcher.Application.Instances;
using Launcher.Application.Mods;
using Launcher.Application.Operations;

namespace Launcher.App.ViewModels;

public sealed partial class ManagedModRow(MainViewModel.ModRow row):ObservableObject
{
    public MainViewModel.ModRow Mod {get;}=row;
    public string Name=>Mod.Title;
    public string Meta=>Mod.Meta;
    public string FileName=>Mod.FileName;
    public string State=>Mod.StateText;
    [ObservableProperty] private bool _selected;
}
public sealed partial class ModUpdateChoice(MainViewModel.ModUpdateRow row):ObservableObject
{
    public System.Windows.Input.ICommand? SkipCommand {get;init;}
    public System.Windows.Input.ICommand? LockCommand {get;init;}
    public MainViewModel.ModUpdateRow Update {get;}=row;
    public string Name=>Update.Title;
    public string Current=>Update.Candidate.CurrentVersion??"未知版本";
    [ObservableProperty] private bool _selected=true;
    [ObservableProperty] private ModReleaseOption? _release=new(row.Candidate.VersionId??"",row.Candidate.LatestVersion??"", "",null);
    public ObservableCollection<ModReleaseOption> Releases {get;}=[new(row.Candidate.VersionId??"",row.Candidate.LatestVersion??"","",null)];
    public ModUpdateCandidate Candidate=>Update.Candidate with{VersionId=Release?.Id,LatestVersion=Release?.Number};
}
public sealed partial class MainViewModel
{
    private ModChangePlans? _modPlans;
    private ModChangePlan? _preparedModPlan;
    private CancellationTokenSource? _planCts;
    private int _planRevision;
    private bool _syncingWorkbench;
    private bool _workbenchRowsQueued;
    private string? _previewRoot;
    private string? _previewInstance;
    private bool _previewReinstall;
    [ObservableProperty] private bool _isModPreviewOpen;
    [ObservableProperty] private bool _isModPreviewBusy;
    [ObservableProperty] private bool _isModVersionPickerOpen;
    [ObservableProperty] private string _modPlanHint="";
    [ObservableProperty] private string _modPlanSummary="";
    [ObservableProperty] private bool _isCloneEditorOpen;
    [ObservableProperty] private string _cloneName="";
    [ObservableProperty] private bool _cloneMods=true;
    [ObservableProperty] private bool _cloneSettings=true;
    [ObservableProperty] private bool _cloneResourcePacks=true;
    [ObservableProperty] private bool _cloneShaderPacks=true;
    [ObservableProperty] private bool _cloneWorlds;
    [ObservableProperty] private bool _cloneScreenshots;
    [ObservableProperty] private bool _cloneServers=true;
    public ObservableCollection<ManagedModRow> ManagedMods {get;}=[];
    public ObservableCollection<ModUpdateChoice> UpdateChoices {get;}=[];
    public ObservableCollection<ModPlannedChange> PlannedModChanges {get;}=[];
    public bool CanUseModWorkbench=>SelectedInstance is not null&&!IsBusy&&!Download.IsBusy&&!IsModBusy&&!ExperienceBusy&&!IsModPreviewBusy;
    public bool HasPreparedModPlan=>_preparedModPlan is not null&&CanUseModWorkbench;
    public bool HasManagedModSelection=>ManagedMods.Any(m=>m.Selected);
    public bool HasUpdateSelection=>UpdateChoices.Any(m=>m.Selected);
    public string ManagedSelectionText=>$"已选 {ManagedMods.Count(m=>m.Selected)} / {ManagedMods.Count}";
    public string UpdateSelectionText=>$"已选 {UpdateChoices.Count(m=>m.Selected)} 项更新";
    public bool CanGenerateModPreview=>CanUseModWorkbench&&UpdateChoices.Any(m=>m.Selected);
    public void InitializeModWorkbench(ModChangePlans plans)
    {
        _modPlans=plans;PreviewModUpdate=row=>OpenModUpdatePreviewAsync([row]);
        InstalledMods.CollectionChanged+=(_,_)=>QueueWorkbenchRows();
        ModUpdates.CollectionChanged+=(_,_)=>QueueWorkbenchRows();
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(SelectedInstance) or nameof(GameRoot)||e.PropertyName==nameof(ShellPage)&&ShellPage!="detail")
            {CloseModPreview();IsCloneEditorOpen=false;CloneName=SelectedGameName+" · 试装";}
            if(e.PropertyName is nameof(IsBusy) or nameof(IsModBusy) or nameof(ExperienceBusy) or nameof(IsModPreviewBusy) or nameof(SelectedInstance))NotifyWorkbench();
        };
        Download.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Download.IsBusy))NotifyWorkbench();};
        SyncWorkbenchRows();
    }
    private void NotifyWorkbench()
    {
        foreach(var name in new[]{nameof(CanUseModWorkbench),nameof(HasPreparedModPlan),nameof(HasManagedModSelection),nameof(HasUpdateSelection),nameof(ManagedSelectionText),nameof(UpdateSelectionText),nameof(CanGenerateModPreview)})OnPropertyChanged(name);
    }
    private void QueueWorkbenchRows()
    {
        if(_workbenchRowsQueued)return;_workbenchRowsQueued=true;
        Dispatcher.UIThread.Post(()=>{_workbenchRowsQueued=false;SyncWorkbenchRows();},DispatcherPriority.Background);
    }
    private void SyncWorkbenchRows()
    {
        _syncingWorkbench=true;
        foreach(var item in ManagedMods)item.PropertyChanged-=WorkbenchRowChanged;
        ManagedMods.Clear();foreach(var mod in InstalledMods){var row=new ManagedModRow(mod);row.PropertyChanged+=WorkbenchRowChanged;ManagedMods.Add(row);}
        if(!IsModPreviewOpen)
        {
            foreach(var item in UpdateChoices)item.PropertyChanged-=WorkbenchRowChanged;
            UpdateChoices.Clear();foreach(var update in ModUpdates){var row=new ModUpdateChoice(update){SkipCommand=SkipModUpdateCommand,LockCommand=LockModUpdateCommand};row.PropertyChanged+=WorkbenchRowChanged;UpdateChoices.Add(row);}
        }
        _syncingWorkbench=false;NotifyWorkbench();
    }
    private void WorkbenchRowChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_syncingWorkbench)return;
        if(sender is ModUpdateChoice&&e.PropertyName is nameof(ModUpdateChoice.Selected) or nameof(ModUpdateChoice.Release))
        {InvalidateModPlan();if(IsModPreviewOpen)IsModVersionPickerOpen=true;}
        NotifyWorkbench();
    }
    private void InvalidateModPlan()
    {
        _preparedModPlan=null;PlannedModChanges.Clear();ModPlanSummary="";
        if(IsModPreviewOpen)ModPlanHint="选择好目标版本后，重新生成变更预览。";
        NotifyWorkbench();
    }
    [RelayCommand] private void SelectAllManagedMods(string? value){foreach(var row in ManagedMods)row.Selected=value!="none";NotifyWorkbench();}
    [RelayCommand] private void SelectAllModUpdates(string? value){foreach(var row in UpdateChoices)row.Selected=value!="none";NotifyWorkbench();}
    private async Task<IReadOnlyList<ModFileSelection>> SelectedModFilesAsync()
    {
        var selected=ManagedMods.Where(m=>m.Selected).Select(m=>m.Mod).ToArray();
        if(selected.Length==0)throw new InvalidOperationException("请先勾选模组。");
        var result=new List<ModFileSelection>();
        foreach(var row in selected)result.Add(new(row.FileName,await ModSourceStore.HashAsync(row.FullPath)));
        return result;
    }
    [RelayCommand] private async Task PrepareLocalModChangeAsync(string? value)
    {
        if(!CanUseModWorkbench||_modPlans is null||SelectedInstance is not {} instance)return;
        if(!Enum.TryParse<ModBatchAction>(value,out var action))return;
        var root=GameRoot;IsModPreviewBusy=true;
        try
        {
            var files=await SelectedModFilesAsync();var plan=await _modPlans.PrepareLocalAsync(root,instance.InstanceId,action,files);
            if(root!=GameRoot||SelectedInstance?.InstanceId!=instance.InstanceId)return;
            if(action==ModBatchAction.Delete)
                AskConfirm("删除选中的模组",$"将删除 {plan.Changes.Count} 个模组。执行前保存实例快照，可在存档与备份中恢复。",string.Join("\n",plan.Removals.Take(12)),"备份并删除",()=>_ = QueueLocalPlanConfirmedAsync(plan));
            else await QueueModPlanAsync(plan,false);
        }
        catch(Exception ex){ExperienceHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
        finally{IsModPreviewBusy=false;NotifyWorkbench();}
    }
    private async Task QueueLocalPlanConfirmedAsync(ModChangePlan plan)
    {
        try{await QueueModPlanAsync(plan,false);}catch(Exception ex){ExperienceHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
    }
    [RelayCommand] private async Task PreviewSelectedModUpdatesAsync()
    {
        if(!CanUseModWorkbench)return;
        await OpenModUpdatePreviewAsync(UpdateChoices.Where(c=>c.Selected).Select(c=>c.Update).ToArray());
    }
    [RelayCommand] private async Task PreviewReinstallModsAsync()
    {
        if(!CanUseModWorkbench||_modPlans is null||SelectedInstance is not {} instance)return;
        var root=GameRoot;IsModPreviewBusy=true;
        try
        {
            var candidates=await _modPlans.ReinstallCandidatesAsync(root,instance.InstanceId,await SelectedModFilesAsync());
            if(root!=GameRoot||SelectedInstance?.InstanceId!=instance.InstanceId)return;
            await OpenModUpdatePreviewAsync(candidates.Select(c=>new ModUpdateRow(c.ModId,c.FileName,c.ModId,$"原版重装 {c.CurrentVersion}",c.Source,c)).ToArray(),true);
        }
        catch(Exception ex){ExperienceHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
        finally{IsModPreviewBusy=false;NotifyWorkbench();}
    }
    private async Task OpenModUpdatePreviewAsync(IReadOnlyList<ModUpdateRow> rows,bool reinstall=false)
    {
        if(_modPlans is null||SelectedInstance is not {} instance||rows.Count==0){ExperienceHint="请先检查更新并选择模组。";return;}
        if(IsBusy||Download.IsBusy||ExperienceBusy)return;
        CloseModPreview();_previewRoot=GameRoot;_previewInstance=instance.InstanceId;_previewReinstall=reinstall;
        IsExperienceOpen=false;IsDetailOpen=true;DetailTab=1;IsModPreviewOpen=true;ExperienceHint="";WorkspaceHint="";CloneName=SelectedGameName+" · 试装";
        _syncingWorkbench=true;
        foreach(var row in UpdateChoices)row.PropertyChanged-=WorkbenchRowChanged;
        UpdateChoices.Clear();foreach(var row in rows){var choice=new ModUpdateChoice(row){SkipCommand=SkipModUpdateCommand,LockCommand=LockModUpdateCommand};choice.PropertyChanged+=WorkbenchRowChanged;UpdateChoices.Add(choice);}
        _syncingWorkbench=false;
        IsModPreviewBusy=true;var revision=++_planRevision;var cancellation=_planCts=new();
        try
        {
            foreach(var row in UpdateChoices.ToArray())
            {
                var releases=await _modPlans.ReleasesAsync(_previewRoot,_previewInstance,row.Update.Candidate,cancellation.Token);
                if(revision!=_planRevision)return;
                var chosen=row.Release?.Id;_syncingWorkbench=true;row.Releases.Clear();
                foreach(var release in releases.Where(r=>!reinstall||r.Id==chosen))row.Releases.Add(release);
                row.Release=row.Releases.FirstOrDefault(r=>r.Id==chosen)??throw new InvalidOperationException("原目标版本已不可用，请重新检查更新。");
                _syncingWorkbench=false;
            }
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){return;}
        catch(Exception ex){ModPlanHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);return;}
        finally{_syncingWorkbench=false;if(revision==_planRevision){IsModPreviewBusy=false;NotifyWorkbench();}}
        if(revision==_planRevision)await GenerateModPreviewAsync();
    }
    [RelayCommand] private async Task GenerateModPreviewAsync()
    {
        if(!CanGenerateModPreview||_modPlans is null||_previewRoot is null||_previewInstance is null||_previewRoot!=GameRoot||_previewInstance!=SelectedInstance?.InstanceId)return;
        var selected=UpdateChoices.Where(c=>c.Selected).Select(c=>c.Candidate).ToArray();
        var revision=++_planRevision;_planCts?.Cancel();var cancellation=_planCts=new();
        InvalidateModPlan();IsModPreviewBusy=true;ModPlanHint="正在解析目标版本与必需依赖…";
        try
        {
            var plan=await _modPlans.PrepareUpdatesAsync(_previewRoot,_previewInstance,selected,_previewReinstall,cancellation.Token);
            if(revision!=_planRevision)return;
            _preparedModPlan=plan;PlannedModChanges.Clear();
            foreach(var change in plan.Changes)PlannedModChanges.Add(change with{Changelog=PlainChangelog(change.Changelog)});
            ModPlanSummary=plan.Summary;ModPlanHint="仅预览，尚未下载或替换文件。已有且满足要求的前置会继续沿用。";IsModVersionPickerOpen=false;
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){ }
        catch(Exception ex){if(revision==_planRevision)ModPlanHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
        finally{if(revision==_planRevision){IsModPreviewBusy=false;NotifyWorkbench();}}
    }
    private static string PlainChangelog(string value)
    {
        var plain=System.Net.WebUtility.HtmlDecode(Regex.Replace(value,"<[^>]+>"," ",RegexOptions.None,TimeSpan.FromSeconds(1)));
        return plain.Length>16000?plain[..16000]+"\n（内容较长，后续省略）":plain;
    }
    [RelayCommand] private void CloseModPreview()
    {
        ++_planRevision;_planCts?.Cancel();_planCts?.Dispose();_planCts=null;
        IsModPreviewOpen=false;IsModPreviewBusy=false;IsModVersionPickerOpen=false;_preparedModPlan=null;PlannedModChanges.Clear();ModPlanSummary="";ModPlanHint="";NotifyWorkbench();
    }
    [RelayCommand] private async Task ApplyPreparedModPlanAsync()=>await ApplyPreparedModPlanCoreAsync(false);
    [RelayCommand] private async Task ApplyModPlanToTrialAsync()=>await ApplyPreparedModPlanCoreAsync(true);
    private async Task ApplyPreparedModPlanCoreAsync(bool trial)
    {
        if(!HasPreparedModPlan||_preparedModPlan is not {} plan)return;
        if(plan.GameRoot!=GameRoot||plan.InstanceId!=SelectedInstance?.InstanceId){InvalidateModPlan();return;}
        try{await QueueModPlanAsync(plan,trial);}catch(Exception ex){InvalidateModPlan();ModPlanHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
    }
    private string StoreModPlan(ModChangePlan plan)
    {
        var path=ExperienceFiles.Child(Path.Combine(AppPaths.DataDirectory,"mod-plans"),plan.Id+".json");ExperienceFiles.Write(path,plan);return plan.Id;
    }
    private ModChangePlan LoadModPlan(DurableTaskRequest request)
    {
        var id=request.Arguments["plan"];if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("无效的变更计划编号。");
        var plan=ExperienceFiles.Read<ModChangePlan>(ExperienceFiles.Child(Path.Combine(AppPaths.DataDirectory,"mod-plans"),id+".json"))??throw new InvalidDataException("变更计划丢失，请重新生成预览。");
        if(plan.GameRoot!=request.GameRoot||plan.InstanceId!=request.InstanceId)throw new InvalidDataException("变更计划与目标实例不匹配。");return plan;
    }
    private async Task QueueModPlanAsync(ModChangePlan plan,bool trial)
    {
        if(_modPlans is null)return;
        EnsureInstanceStopped(plan.InstanceId);await _modPlans.ValidateAsync(plan);
        var args=new Dictionary<string,string>{{"plan",StoreModPlan(plan)}};
        if(trial)args["clone"]=JsonSerializer.Serialize(CreateCloneRequest(plan.InstanceId,true));
        var task=await QueueExperienceTaskAsync(new(trial?"trial-update":"mod-batch",trial?"创建试装副本并应用变更":$"模组批量操作 · {plan.Changes.Count} 项",plan.GameRoot,plan.InstanceId,args));
        if(task?.State==DurableTaskState.Completed){CloseModPreview();await RefreshModsAsync();ExperienceHint=trial?"变更已应用到试装副本，原实例保持不变。":"模组变更已完成；需要恢复时可使用操作前快照。";}
        else ModPlanHint=task?.Detail??ExperienceHint;
    }
    private InstanceCloneRequest CreateCloneRequest(string source,bool trial)
    {
        var name=CloneName.Trim();if(name.Length==0)name=SelectedGameName+" · 试装";
        if(name.Length>80)throw new InvalidOperationException("副本名称最多 80 字。");
        return new(source,source+"-trial-"+Guid.NewGuid().ToString("N")[..8],name,
            trial||CloneMods,trial||CloneSettings,CloneResourcePacks,CloneShaderPacks,CloneWorlds,CloneScreenshots,CloneServers);
    }
    [RelayCommand] private void OpenCloneEditor()
    {
        if(!CanUseModWorkbench||SelectedInstance is null)return;
        OpenGame(null);DetailTab=0;CloneName=SelectedGameName+" · 试装";IsCloneEditorOpen=true;WorkspaceHint="";
    }
    [RelayCommand] private void CloseCloneEditor()=>IsCloneEditorOpen=false;
    [RelayCommand] private async Task CreateTrialCloneAsync()
    {
        if(!CanUseModWorkbench||SelectedInstance is not {} instance)return;
        try
        {
            EnsureInstanceStopped(instance.InstanceId);var clone=CreateCloneRequest(instance.InstanceId,false);
            var task=await QueueExperienceTaskAsync(new("clone","创建试装副本 · "+clone.Name,GameRoot,instance.InstanceId,new(){{"clone",JsonSerializer.Serialize(clone)}}));
            if(task?.State==DurableTaskState.Completed){IsCloneEditorOpen=false;ExperienceHint="独立试装副本已创建，原实例保持不变。";}
        }
        catch(Exception ex){WorkspaceHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
    }
    private async Task ExecuteWorkbenchTaskAsync(DurableTaskRequest request,IProgress<TaskProgress> progress,CancellationToken ct)
    {
        if(request.Kind=="clone")
        {
            var clone=JsonSerializer.Deserialize<InstanceCloneRequest>(request.Arguments["clone"])??throw new InvalidDataException("缺少副本参数。");
            if(clone.SourceId!=request.InstanceId)throw new InvalidDataException("副本来源与任务不一致。");
            await new InstanceCloner().CloneAsync(request.GameRoot,clone,progress,ct);return;
        }
        var plan=LoadModPlan(request);
        if(request.Kind=="trial-update")
        {
            var clone=JsonSerializer.Deserialize<InstanceCloneRequest>(request.Arguments["clone"])??throw new InvalidDataException("缺少副本参数。");
            if(clone.SourceId!=request.InstanceId||!clone.Mods||!clone.Settings)throw new InvalidDataException("试装更新必须复制模组与设置。");
            await new InstanceCloner().CloneAsync(request.GameRoot,clone,progress,ct);
            plan=plan with{InstanceId=clone.DestinationId,Environment=InstanceEnvironment.Resolve(request.GameRoot,clone.DestinationId)};
        }
        await _modPlans!.ValidateAsync(plan,ct);
        progress.Report(new(0,"正在保存操作前快照…"));
        await new InstanceSnapshots(AppPaths.DataDirectory,request.GameRoot,plan.InstanceId).CaptureAsync("批量操作前 · "+plan.Changes.Count+" 项",ct:ct);
        progress.Report(new(1,"快照已保存，正在准备变更文件…"));
        await _modPlans.ApplyAsync(plan,new WorkbenchProgress(progress),ct);
    }
    private sealed class WorkbenchProgress(IProgress<TaskProgress> target):IProgress<ModInstallProgress>
    {public void Report(ModInstallProgress p)=>target.Report(new(p.Percent,p.Stage+" · "+p.Detail));}
}
