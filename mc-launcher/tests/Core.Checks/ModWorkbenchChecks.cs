using System.Text.Json;
using Launcher.Application.Experience;
using Launcher.Application.Instances;
using Launcher.Application.Mods;
using Launcher.Application.Operations;

internal static class ModWorkbenchChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-workbench-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        const string id="fabric-loader-0.16.9-1.21.4";
        void Check(string name,bool condition)=>check(name,condition,null);
        async Task<bool> Reject(Func<Task> action){try{await action();return false;}catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException){return true;}}
        try
        {
            var fixture=new WorkbenchFixture();WorkbenchFixture.MakeInstance(root);var service=fixture.Plans;var directory=ModChangePlans.ModsDirectory(root,id);
            var alpha=await fixture.InstallOldAsync(root,id,"alpha","alpha-1.jar","alpha-2");var beta=await fixture.InstallOldAsync(root,id,"beta","beta-1.jar","beta-2");
            var original=await ModChangePlans.FingerprintAsync(directory);
            var plan=await service.PrepareUpdatesAsync(root,id,[alpha,beta]);
            Check("批量预览列出两项更新与一项共享前置",plan.Changes.Count==3&&plan.Files.Count==3&&plan.Files.Count(f=>f.File.Origin.ProjectId=="shared")==1);
            Check("预览包含明确版本频道更新日志与下载大小",plan.Changes.Where(c=>c.Kind=="更新").All(c=>c.Channel=="release"&&c.Changelog.Contains("兼容性"))&&plan.DownloadBytes>0);
            Check("生成预览不下载或替换模组",fixture.DownloadCalls==0&&await ModChangePlans.FingerprintAsync(directory)==original);
            var serialized=JsonSerializer.Serialize(plan);var restored=JsonSerializer.Deserialize<ModChangePlan>(serialized)!;await service.ValidateAsync(restored);
            Check("变更计划可以持久化并恢复精确文件集合",restored.Files.Select(f=>f.File.Entry.RelativePath).SequenceEqual(plan.Files.Select(f=>f.File.Entry.RelativePath)));
            fixture.FailAfter=1;Check("下载中断不会改动任何原文件",await Reject(()=>service.ApplyAsync(plan))&&await ModChangePlans.FingerprintAsync(directory)==original);
            fixture.FailAfter=-1;fixture.CorruptDownload=true;Check("新文件校验失败时整批不提交",await Reject(()=>service.ApplyAsync(plan))&&await ModChangePlans.FingerprintAsync(directory)==original);fixture.CorruptDownload=false;
            fixture.AfterDownload=()=>Directory.CreateDirectory(Path.Combine(directory,"beta-2.jar"));
            Check("提交中途失败恢复已经替换的文件",await Reject(()=>service.ApplyAsync(plan))&&!File.Exists(Path.Combine(directory,"alpha-2.jar"))&&await ModChangePlans.FingerprintAsync(directory)==original);
            Directory.Delete(Path.Combine(directory,"beta-2.jar"));fixture.AfterDownload=null;
            await File.WriteAllBytesAsync(Path.Combine(directory,"added.jar"),WorkbenchFixture.Jar("added","1"));
            Check("预览之后模组集合改变会阻止旧计划",await Reject(()=>service.ApplyAsync(plan)));File.Delete(Path.Combine(directory,"added.jar"));
            await service.ApplyAsync(plan);
            Check("整批提交更新与前置并移除两个旧文件",File.Exists(Path.Combine(directory,"alpha-2.jar"))&&File.Exists(Path.Combine(directory,"beta-2.jar"))&&File.Exists(Path.Combine(directory,"shared.jar"))&&!File.Exists(Path.Combine(directory,"alpha-1.jar"))&&!File.Exists(Path.Combine(directory,"beta-1.jar")));
            Check("批量操作维护每个文件的上游来源",ModSourceStore.Load(directory).Count==3&&ModSourceStore.Load(directory)["alpha-2.jar"].VersionId=="alpha-2");
            var selection=new[]{new ModFileSelection("alpha-2.jar",WorkbenchFixture.Hash(fixture.Files["alpha-2.jar"])),new ModFileSelection("beta-2.jar",WorkbenchFixture.Hash(fixture.Files["beta-2.jar"]))};
            var disable=await service.PrepareLocalAsync(root,id,ModBatchAction.Disable,selection);await service.ApplyAsync(disable);
            Check("批量停用保留文件并更新来源键",selection.All(s=>File.Exists(Path.Combine(directory,s.FileName+".disabled")))&&ModSourceStore.Load(directory).ContainsKey("alpha-2.jar.disabled"));
            var disabled=selection.Select(s=>s with{FileName=s.FileName+".disabled"}).ToArray();
            var reinstall=await service.ReinstallCandidatesAsync(root,id,disabled);var reinstallPlan=await service.PrepareUpdatesAsync(root,id,reinstall,true);await service.ApplyAsync(reinstallPlan);
            Check("重装精确使用原发布版本并保留停用状态",reinstallPlan.Files.Count==2&&reinstallPlan.Files.All(f=>f.File.Entry.RelativePath.EndsWith(".disabled"))&&ModSourceStore.Load(directory)["alpha-2.jar.disabled"].VersionId=="alpha-2");
            var enable=await service.PrepareLocalAsync(root,id,ModBatchAction.Enable,disabled);await service.ApplyAsync(enable);
            Check("批量启用恢复原文件名",selection.All(s=>File.Exists(Path.Combine(directory,s.FileName)))&&!File.Exists(Path.Combine(directory,"alpha-2.jar.disabled")));
            var snapshotStore=new InstanceSnapshots(Path.Combine(root,"data"),root,id);var snapshot=await snapshotStore.CaptureAsync("删除前");
            var delete=await service.PrepareLocalAsync(root,id,ModBatchAction.Delete,selection);await service.ApplyAsync(delete);
            Check("批量删除只移除选定文件及来源记录",!File.Exists(Path.Combine(directory,"alpha-2.jar"))&&File.Exists(Path.Combine(directory,"shared.jar"))&&ModSourceStore.Load(directory).Count==1);
            await snapshotStore.RestoreAsync(snapshot,SnapshotRestoreScope.Configuration);
            Check("操作前快照可恢复模组和来源记录",File.Exists(Path.Combine(directory,"alpha-2.jar"))&&ModSourceStore.Load(directory).Count==3);
            var alphaCurrent=selection[0];await File.WriteAllTextAsync(Path.Combine(directory,"alpha-2.jar"),"locally modified");
            Check("来源不再匹配的文件不能盲目重装",await Reject(()=>service.ReinstallCandidatesAsync(root,id,[alphaCurrent])));await File.WriteAllBytesAsync(Path.Combine(directory,"alpha-2.jar"),fixture.Files["alpha-2.jar"]);
            Check("拒绝跨目录模组文件名",await Reject(()=>service.PrepareLocalAsync(root,id,ModBatchAction.Delete,[new("../outside.jar","x")])));
            await CloneChecks();await ConflictChecks();await DurableChecks();

            async Task CloneChecks()
            {
                var instance=Path.Combine(root,"instances",id);var config=Path.Combine(instance,".minecraft","config");Directory.CreateDirectory(config);await File.WriteAllTextAsync(Path.Combine(config,"test.cfg"),"original");
                var worlds=Path.Combine(instance,".minecraft","saves","World");Directory.CreateDirectory(worlds);await File.WriteAllTextAsync(Path.Combine(worlds,"level.dat"),"world");
                await File.WriteAllTextAsync(Path.Combine(instance,"accounts.json"),"must not be copied");
                var cloner=new InstanceCloner();var request=new InstanceCloneRequest(id,"trial-a","试装 A");var clone=await cloner.CloneAsync(root,request);
                var copy=Path.Combine(root,"instances",clone.InstanceId);Check("副本沿用原加载器版本且不是整合包误标",clone.VersionId==id&&InstanceRegistry.ResolveVersionId(root,clone.InstanceId)==id&&Launcher.Application.Install.InstanceMetadata.TryRead(root,clone.InstanceId)?.IsModpack==false);
                Check("默认副本复制模组配置而不复制存档账号与日志",File.Exists(Path.Combine(copy,".minecraft","mods","alpha-2.jar"))&&File.Exists(Path.Combine(copy,".minecraft","config","test.cfg"))&&!Directory.Exists(Path.Combine(copy,".minecraft","saves"))&&!File.Exists(Path.Combine(copy,"accounts.json")));
                await File.WriteAllTextAsync(Path.Combine(copy,".minecraft","config","test.cfg"),"trial change");Check("副本配置可以独立修改而不影响原实例",await File.ReadAllTextAsync(Path.Combine(config,"test.cfg"))=="original");
                var worldClone=await cloner.CloneAsync(root,new(id,"trial-world","带存档副本",Mods:false,ResourcePacks:false,ShaderPacks:false,Worlds:true));
                Check("复制选项只包含用户选择的内容",File.Exists(Path.Combine(root,"instances",worldClone.InstanceId,".minecraft","saves","World","level.dat"))&&!Directory.Exists(Path.Combine(root,"instances",worldClone.InstanceId,".minecraft","mods")));
                Check("已有目标目录不会被覆盖",await Reject(()=>cloner.CloneAsync(root,request))&&await File.ReadAllTextAsync(Path.Combine(copy,".minecraft","config","test.cfg"))=="trial change");
                using var canceled=new CancellationTokenSource();canceled.Cancel();Check("取消复制不会暴露半成品实例",await Reject(()=>cloner.CloneAsync(root,new(id,"trial-canceled","取消副本"),ct:canceled.Token))&&!Directory.Exists(Path.Combine(root,"instances","trial-canceled")));
                Check("副本路径不能逃出实例目录",await Reject(()=>cloner.CloneAsync(root,new(id,"../outside","错误副本"))));
            }
            async Task ConflictChecks()
            {
                var conflictRoot=Path.Combine(root,"conflicts");WorkbenchFixture.MakeInstance(conflictRoot);var data=new WorkbenchFixture();
                data.Add("shared","shared-2","2.0","shared-2.jar");data.Add("beta","beta-3","3.0","beta-3.jar",new("shared","shared-2","required"));
                var a=await data.InstallOldAsync(conflictRoot,id,"alpha","alpha-1.jar","alpha-2");var b=await data.InstallOldAsync(conflictRoot,id,"beta","beta-1.jar","beta-3");
                Check("互相矛盾的指定前置版本在预览时阻止",await Reject(()=>data.Plans.PrepareUpdatesAsync(conflictRoot,id,[a,b]))&&data.DownloadCalls==0);
                data.Add("child","child-1","1.0","child.jar",new("shared","shared-2","required"));
                var originalAlpha=data.Versions["alpha"];data.Versions["alpha"]=originalAlpha.Select(v=>v.Id=="alpha-2"?v with{Dependencies=[new("shared","shared-1","required"),new("child","child-1","required")]}:v).ToArray();
                Check("同一项目的深层前置冲突也会在预览阶段阻止",await Reject(()=>data.Plans.PrepareUpdatesAsync(conflictRoot,id,[a]))&&data.DownloadCalls==0);data.Versions["alpha"]=originalAlpha;
                var dir=ModChangePlans.ModsDirectory(conflictRoot,id);var old=await data.InstallOldAsync(conflictRoot,id,"shared","shared.jar","shared-1");File.Move(Path.Combine(dir,"shared.jar"),Path.Combine(dir,"shared.jar.disabled"));
                var origins=ModSourceStore.Load(dir);origins["shared.jar.disabled"]=origins["shared.jar"];origins.Remove("shared.jar");await ModSourceStore.SaveAsync(dir,origins);
                Check("启用模组依赖已停用前置时给出阻止提示",await Reject(()=>data.Plans.PrepareUpdatesAsync(conflictRoot,id,[a])));
                var cfOld=WorkbenchFixture.Jar("cfmod","1.0");await File.WriteAllBytesAsync(Path.Combine(dir,"cf-old.jar"),cfOld);
                var cf=new ModUpdateCandidate("cfmod","cf-old.jar","1.0","2.0",true,null,null,"curseforge"){ProjectId="10",VersionId="101",CurrentSha1=WorkbenchFixture.Hash(cfOld)};
                var cfPlan=await data.Plans.PrepareUpdatesAsync(conflictRoot,id,[cf]);Check("CurseForge 更新预览包含频道与更新日志",cfPlan.Changes[0].Channel=="release"&&cfPlan.Changes[0].Changelog.Contains("CurseForge"));
            }
            async Task DurableChecks()
            {
                var taskRoot=Path.Combine(root,"durable");WorkbenchFixture.MakeInstance(taskRoot);var data=new WorkbenchFixture();var a=await data.InstallOldAsync(taskRoot,id,"alpha","alpha-1.jar","alpha-2");var p=await data.Plans.PrepareUpdatesAsync(taskRoot,id,[a]);
                var before=await ModChangePlans.FingerprintAsync(ModChangePlans.ModsDirectory(taskRoot,id));var taskData=Path.Combine(taskRoot,"data");var tasks=new PersistentTasks(taskData);
                tasks.Execute=async(_,progress,ct)=>await data.Plans.ApplyAsync(p,ct:ct);
                var item=tasks.Add(new("mod-batch","测试批量更新",taskRoot,id,new()));data.PendingDownload=new(TaskCreationOptions.RunContinuationsAsynchronously);var running=tasks.RunAsync(item.Id);
                while(data.DownloadCalls==0)await Task.Delay(5);tasks.Pause(item.Id);await running;
                Check("批量下载暂停保持原模组并持久化恢复状态",tasks.List().Single().State==DurableTaskState.Paused&&await ModChangePlans.FingerprintAsync(ModChangePlans.ModsDirectory(taskRoot,id))==before);
                var restoredTasks=new PersistentTasks(taskData);data.PendingDownload=null;restoredTasks.Execute=tasks.Execute;await restoredTasks.RunAsync(item.Id);
                Check("程序重启后可按原计划恢复并完成更新",restoredTasks.List().Single().State==DurableTaskState.Completed&&File.Exists(Path.Combine(ModChangePlans.ModsDirectory(taskRoot,id),"alpha-2.jar")));
                var cancelRoot=Path.Combine(root,"cancel");WorkbenchFixture.MakeInstance(cancelRoot);var cancelData=new WorkbenchFixture();var old=await cancelData.InstallOldAsync(cancelRoot,id,"alpha","alpha-1.jar","alpha-2");var cancelPlan=await cancelData.Plans.PrepareUpdatesAsync(cancelRoot,id,[old]);
                var cancelTasks=new PersistentTasks(Path.Combine(cancelRoot,"data"));cancelTasks.Execute=async(_,_,ct)=>await cancelData.Plans.ApplyAsync(cancelPlan,ct:ct);var cancel=cancelTasks.Add(new("mod-batch","取消测试",cancelRoot,id,new()));cancelData.PendingDownload=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=cancelTasks.RunAsync(cancel.Id);
                while(cancelData.DownloadCalls==0)await Task.Delay(5);cancelTasks.Cancel(cancel.Id);await pending;
                Check("取消整批操作清理暂存下载且保留原文件",cancelTasks.List().Single().State==DurableTaskState.Canceled&&File.Exists(Path.Combine(ModChangePlans.ModsDirectory(cancelRoot,id),"alpha-1.jar"))&&!Directory.Exists(Path.Combine(cancelRoot,"data","task-center",cancel.Id)));
                var cloneData=Path.Combine(root,"clone-tasks");var cloning=new PersistentTasks(cloneData);var cloneRequest=new InstanceCloneRequest(id,"paused-clone","可恢复副本");
                var cloneTask=cloning.Add(new("clone","复制暂停测试",root,id,new()));var paused=false;
                cloning.Execute=async(_,progress,ct)=>await new InstanceCloner().CloneAsync(root,cloneRequest,new InlineProgress<TaskProgress>(p=>{progress.Report(p);if(!paused&&p.Detail.StartsWith("复制并校验")){paused=true;cloning.Pause(cloneTask.Id);}}),ct);
                await cloning.RunAsync(cloneTask.Id);Check("暂停复制保存清单且半成品不进入实例目录",cloning.List().Single().State==DurableTaskState.Paused&&!Directory.Exists(Path.Combine(root,"instances",cloneRequest.DestinationId)));
                var resumedClone=new PersistentTasks(cloneData);resumedClone.Execute=async(_,p,ct)=>await new InstanceCloner().CloneAsync(root,cloneRequest,p,ct);await resumedClone.RunAsync(cloneTask.Id);
                Check("重启后可恢复副本复制并独立登记",resumedClone.List().Single().State==DurableTaskState.Completed&&InstanceRegistry.ResolveVersionId(root,cloneRequest.DestinationId)==id);
                var commitData=Path.Combine(root,"clone-commit-tasks");var commitTasks=new PersistentTasks(commitData);var commitRequest=new InstanceCloneRequest(id,"commit-clone","已发布副本");var commitTask=commitTasks.Add(new("clone","发布后暂停",root,id,new()));var once=false;
                commitTasks.Execute=async(_,p,ct)=>await new InstanceCloner().CloneAsync(root,commitRequest,new InlineProgress<TaskProgress>(progress=>{p.Report(progress);if(progress.Percent==100&&!once){once=true;commitTasks.Pause(commitTask.Id);}}),ct);
                await commitTasks.RunAsync(commitTask.Id);commitTasks.Execute=async(_,p,ct)=>await new InstanceCloner().CloneAsync(root,commitRequest,p,ct);await commitTasks.RunAsync(commitTask.Id);
                Check("副本刚发布时中断可确认原任务归属并完成恢复",commitTasks.List().Single().State==DurableTaskState.Completed&&Directory.Exists(Path.Combine(root,"instances",commitRequest.DestinationId)));
            }
        }
        finally{ExperienceFiles.DeleteTree(root,Path.GetTempPath());}
    }
    private sealed class InlineProgress<T>(Action<T> report):IProgress<T>{public void Report(T value)=>report(value);}
}
