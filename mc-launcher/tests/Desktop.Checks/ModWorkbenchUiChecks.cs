using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Experience;
using Launcher.Application.Mods;
using Launcher.Application.Operations;

internal static class ModWorkbenchUiChecks
{
    public static void Run(MainViewModel model,Window window,WorkbenchFixture fixture,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,Action<Control> click,string root)
    {
        void Wait(Task task){var clock=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<15000)pump(10);if(!task.IsCompleted)throw new TimeoutException("工作台检查超时。");task.GetAwaiter().GetResult();pump(100);}
        void Until(Func<bool> done){var clock=System.Diagnostics.Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<15000)pump(10);if(!done())throw new TimeoutException("工作台状态未完成。");pump(80);}
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        model.ShowInstancesCommand.Execute(null);window.Width=1180;window.Height=800;model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
        const string source="fabric-loader-0.16.9-1.21.4";WorkbenchFixture.MakeInstance(model.GameRoot,source);
        ModUpdateCandidate? alpha=null,beta=null;Wait(Seed());
        async Task Seed(){alpha=await fixture.InstallOldAsync(model.GameRoot,source,"alpha","alpha-1.jar","alpha-2");beta=await fixture.InstallOldAsync(model.GameRoot,source,"beta","beta-1.jar","beta-2");}
        model.TryUseGameRoot(model.GameRoot,out _);model.SelectedInstance=model.Instances.Single(i=>i.InstanceId==source);model.OpenGameCommand.Execute(null);model.DetailTab=1;Wait(model.RefreshModsCommand.ExecuteAsync(null));
        var directory=ModChangePlans.ModsDirectory(model.GameRoot,source);
        check("模组工作台显示真实文件与批量操作",model.ManagedMods.Count==2&&Find<Button>("BatchDisableModsButton").Command==model.PrepareLocalModChangeCommand);
        model.SelectAllManagedModsCommand.Execute(null);check("多选支持全选和选择计数",model.ManagedMods.All(m=>m.Selected)&&model.ManagedSelectionText.Contains("2 / 2"));
        Wait(model.PrepareLocalModChangeCommand.ExecuteAsync("Disable"));check("界面批量停用后刷新状态",model.ManagedMods.All(m=>m.Mod.Enabled==false)&&File.Exists(Path.Combine(directory,"alpha-1.jar.disabled")));
        model.SelectAllManagedModsCommand.Execute(null);Wait(model.PreviewReinstallModsCommand.ExecuteAsync(null));
        check("重装先进入预览并锁定原发布版本",model.IsModPreviewOpen&&model.UpdateChoices.All(c=>c.Releases.Count==1)&&model.PlannedModChanges.All(c=>c.Kind=="重装"));
        Wait(model.ApplyPreparedModPlanCommand.ExecuteAsync(null));check("界面重装保留停用状态",model.ManagedMods.All(m=>!m.Mod.Enabled)&&ModSourceStore.Load(directory)["alpha-1.jar.disabled"].VersionId=="alpha-1");
        model.SelectAllManagedModsCommand.Execute(null);Wait(model.PrepareLocalModChangeCommand.ExecuteAsync("Enable"));check("界面批量启用恢复状态",model.ManagedMods.Count==2&&model.ManagedMods.All(m=>m.Mod.Enabled));
        model.SelectAllManagedModsCommand.Execute(null);Wait(model.PrepareLocalModChangeCommand.ExecuteAsync("Delete"));check("批量删除先确认数量和备份",model.IsConfirmOpen&&model.ConfirmMessage.Contains("2 个模组")&&model.ConfirmMessage.Contains("快照"));
        model.ConfirmCancelCommand.Execute(null);check("取消删除不改变任何所选模组",File.Exists(Path.Combine(directory,"alpha-1.jar"))&&File.Exists(Path.Combine(directory,"beta-1.jar")));
        Wait(model.PrepareLocalModChangeCommand.ExecuteAsync("Delete"));model.ConfirmOkCommand.Execute(null);Until(()=>!model.IsBusy&&!File.Exists(Path.Combine(directory,"alpha-1.jar")));
        check("确认删除后可从操作前快照恢复",!File.Exists(Path.Combine(directory,"beta-1.jar"))&&model.Snapshots.Count>0);
        model.OpenInstanceHistoryCommand.Execute(null);model.SelectedSnapshot=model.Snapshots.First();model.RestoreScope=0;model.RestoreSnapshotCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.ExperienceBusy&&File.Exists(Path.Combine(directory,"alpha-1.jar")));model.DetailTab=1;Wait(model.RefreshModsCommand.ExecuteAsync(null));
        check("删除恢复连同上游来源记录还原",ModSourceStore.Load(directory).Count==2&&model.ManagedMods.Count==2);
        void Candidates(){model.ModUpdates.Clear();foreach(var c in new[]{alpha!,beta!})model.ModUpdates.Add(new(c.ModId,c.FileName,c.ModId,c.CurrentVersion+" → "+c.LatestVersion,"Modrinth",c));pump(100);}
        Candidates();capture("workbench-managed-mods",true);Wait(model.PreviewSelectedModUpdatesCommand.ExecuteAsync(null));
        check("更新预览显示两项更新和去重前置",model.IsModPreviewOpen&&model.PlannedModChanges.Count==3&&model.ModPlanSummary.Contains("3 项变更")&&Find<Button>("ApplyModPlanButton").IsEffectivelyEnabled);
        check("更新日志与发布频道明确显示",model.PlannedModChanges.Where(c=>c.Kind=="更新").All(c=>c.ChannelText=="正式版"&&c.Changelog.Contains("修复")));
        capture("workbench-update-preview",true);
        var choice=model.UpdateChoices.Single(c=>c.Candidate.ProjectId=="alpha");choice.Release=choice.Releases.Single(r=>r.Channel=="beta");pump(30);
        check("切换目标版本使旧预览立即失效",!model.HasPreparedModPlan&&!Find<Button>("ApplyModPlanButton").IsEffectivelyEnabled);
        Wait(model.GenerateModPreviewCommand.ExecuteAsync(null));check("重新预览使用用户所选测试版",model.HasPreparedModPlan&&model.PlannedModChanges.Any(c=>c.After=="3.0-beta"&&c.ChannelText=="测试版"));
        window.Width=900;window.Height=640;pump(80);var apply=Find<Button>("ApplyModPlanToTrialButton");check("最小窗口保留试装确认入口",apply.TranslatePoint(new Point(),window) is {} p&&p.Y>=0&&p.Y+apply.Bounds.Height<=window.ClientSize.Height);capture("workbench-preview-compact",true);window.Width=1180;window.Height=800;
        model.CloseModPreviewCommand.Execute(null);check("关闭预览保留原文件并停止准备状态",!model.IsModPreviewOpen&&!model.IsModPreviewBusy&&File.Exists(Path.Combine(directory,"alpha-1.jar")));
        Candidates();Wait(model.PreviewSelectedModUpdatesCommand.ExecuteAsync(null));model.CloneName="更新试装 · 独立副本";model.CloneWorlds=false;var before=File.ReadAllBytes(Path.Combine(directory,"alpha-1.jar"));Wait(model.ApplyModPlanToTrialCommand.ExecuteAsync(null));
        var copy=model.SelectedInstance!;check("试装流程创建新实例并保持原版文件",copy.InstanceId!=source&&copy.VersionId==source&&File.ReadAllBytes(Path.Combine(directory,"alpha-1.jar")).SequenceEqual(before)&&!File.Exists(Path.Combine(directory,"alpha-2.jar")));
        check("更新只应用到副本且游戏库可管理",File.Exists(Path.Combine(ModChangePlans.ModsDirectory(model.GameRoot,copy.InstanceId),"alpha-2.jar"))&&File.Exists(Path.Combine(ModChangePlans.ModsDirectory(model.GameRoot,copy.InstanceId),"shared.jar"))&&!copy.IsModpack);
        check("整个试装流程由持久任务中心记录",model.DurableTasks.Any(t=>t.Record.Request.Kind=="trial-update"&&t.Record.State==DurableTaskState.Completed));
        model.SelectedInstance=model.Instances.Single(i=>i.InstanceId==source);model.OpenCloneEditorCommand.Execute(null);pump(100);check("游戏详情提供副本选项且默认不复制存档",model.IsCloneEditorOpen&&!model.CloneWorlds&&Find<Button>("CreateTrialCloneButton").IsEffectivelyVisible);capture("workbench-clone-options",true);
        model.CloneName="仅配置试装";model.CloneMods=false;model.CloneSettings=true;model.CloneResourcePacks=false;model.CloneShaderPacks=false;Wait(model.CreateTrialCloneCommand.ExecuteAsync(null));
        check("副本创建尊重用户取消模组复制的选择",model.SelectedInstance!.InstanceId!=source&&!Directory.Exists(ModChangePlans.ModsDirectory(model.GameRoot,model.SelectedInstance.InstanceId))&&model.SelectedGameName=="仅配置试装");
        model.SelectedInstance=model.Instances.Single(i=>i.InstanceId==source);model.OpenGameCommand.Execute(null);model.DetailTab=1;Wait(model.RefreshModsCommand.ExecuteAsync(null));Candidates();Wait(model.PreviewSelectedModUpdatesCommand.ExecuteAsync(null));
        File.WriteAllText(Path.Combine(directory,"external.jar"),"external change");Wait(model.ApplyPreparedModPlanCommand.ExecuteAsync(null));check("确认前文件改变会阻止旧预览并说明原因",model.ModPlanHint.Contains("改变")&&!model.HasPreparedModPlan&&File.Exists(Path.Combine(directory,"alpha-1.jar")));File.Delete(Path.Combine(directory,"external.jar"));
        model.CloseModPreviewCommand.Execute(null);Candidates();Wait(model.PreviewSelectedModUpdatesCommand.ExecuteAsync(null));fixture.PendingDownload=new(TaskCreationOptions.RunContinuationsAsynchronously);var task=model.ApplyPreparedModPlanCommand.ExecuteAsync(null);Until(()=>model.DurableTasks.Any(t=>t.Record.Request.Kind=="mod-batch"&&t.CanPause));
        var active=model.DurableTasks.Single(t=>t.CanPause);model.PauseDurableTaskCommand.Execute(active);Wait(task);check("界面可暂停批量更新且原文件仍可使用",active.CanResume&&File.Exists(Path.Combine(directory,"alpha-1.jar"))&&!model.IsBusy);
        fixture.PendingDownload=null;Wait(model.ResumeDurableTaskCommand.ExecuteAsync(active));Wait(model.RefreshModsCommand.ExecuteAsync(null));check("界面恢复批量任务并完成原定更新",active.Record.State==DurableTaskState.Completed&&File.Exists(Path.Combine(directory,"alpha-2.jar"))&&!File.Exists(Path.Combine(directory,"alpha-1.jar")));
        capture("workbench-updated-mods",true);model.ShowInstancesCommand.Execute(null);pump(100);
    }
}
