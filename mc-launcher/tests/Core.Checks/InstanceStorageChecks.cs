using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Application.Operations;
using Launcher.Domain.Modpacks;

internal static class InstanceStorageChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check,ModpackInstaller installer)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-instance-storage-"+Guid.NewGuid().ToString("N"));
        void Check(string name,bool value)=>check(name,value,null);
        string Write(string path,string text="fixture"){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);return path;}
        try
        {
            var data=Path.Combine(root,"launcher-data");var game=Path.Combine(root,"game-disk");
            Write(Path.Combine(game,"versions","base","base.json"),"""{"id":"base"}""");
            Write(Path.Combine(game,"versions","fabric","fabric.json"),"""{"id":"fabric","inheritsFrom":"base"}""");
            Check("加载器的原版依赖不单独出现在实例列表",InstanceRegistry.Load(game).Instances.Select(x=>x.Id).SequenceEqual(["fabric"]));
            Check("隐藏依赖不会删除启动需要的原版文件",File.Exists(Path.Combine(game,"versions","base","base.json")));
            InstanceRegistry.RegisterStandaloneVersion(game,"base");
            Check("明确安装纯原版后与加载器实例同时显示",InstanceRegistry.Load(game).Instances.Count==2&&InstanceMetadata.TryRead(game,"base") is {IsModpack:false});
            var settings=new InstanceSettings(MemoryMb:6144,AutomaticMemory:false);settings.Save(game,"base");
            Check("旧数值设置保留为自定义模式",InstanceSettings.Load(game,"base") is {MemoryMb:6144,AutomaticMemory:false});
            (settings with{MemoryMb=512,AutomaticMemory=true}).Save(game,"base");
            var automatic=InstanceSettings.Load(game,"base");
            Check("自动内存每次加载重新计算且保存模式",automatic.AutomaticMemory&&automatic.MemoryMb==InstanceSettings.AdaptiveMemoryMb());
            Check("新实例默认自动内存",InstanceSettings.Load(game,"new").AutomaticMemory);
            Check("自动内存启动归一化重新计算",(automatic with{MemoryMb=512}).Normalized().MemoryMb==InstanceSettings.AdaptiveMemoryMb());
            Directory.CreateDirectory(Path.Combine(game,"instances","base",".minecraft"));
            var presets=new SettingsPresets(data);var autoPreset=presets.Capture(game,"base","自动");
            settings.Save(game,"base");var manualPreset=presets.Capture(game,"base","自定义");
            await presets.ApplyAsync(autoPreset,game,"base");
            Check("设置预设恢复自动内存模式",InstanceSettings.Load(game,"base").AutomaticMemory);
            await presets.ApplyAsync(manualPreset,game,"base");
            Check("设置预设恢复自定义内存及分配值",InstanceSettings.Load(game,"base") is {AutomaticMemory:false,MemoryMb:6144});
            var tasks=new PersistentTasks(data);
            var success=tasks.Add(new("fixture","长路径导入",game,null,new()));
            var id="1.20.1-"+new string('v',90);
            Write(Path.Combine(game,"versions",id,id+".json"),"{\"id\":\""+id+"\"}");
            var pack=new ModrinthPack{Name="长路径安装",VersionId="1",Dependencies=new Dictionary<string,string>{{"minecraft",id}}};
            string? prepared=null;
            tasks.Execute=async(_,_,ct)=>
            {
                await installer.ImportAsync(new FakeModpackSource(pack,(directory,_,_)=>
                {
                    prepared=directory;Write(Path.Combine(directory,"config","example.txt"));return Task.FromResult(1);
                }),game,ct:ct);
            };
            await tasks.RunAsync(success.Id);
            Check("长短路径前缀混用不再使目录提交失败",tasks.List().Single().State==DurableTaskState.Completed);
            Check("持久任务的导入暂存与目标位于游戏盘",prepared is not null&&prepared.StartsWith(Path.Combine(game,".cache","tasks",success.Id))&&Path.GetPathRoot(LongPath.Canonical(prepared))==Path.GetPathRoot(game));
            Check("导入后文件进入实例且清理暂存",File.Exists(Path.Combine(game,"instances",id,".minecraft","config","example.txt"))&&!Directory.Exists(PersistentTasks.WorkspacePath(success)));
            tasks.Execute=async(_,_,ct)=>{await installer.ImportAsync(new FakeModpackSource(pack,(dir,_,_)=>{Write(Path.Combine(dir,"config","example.txt"),"updated");return Task.FromResult(1);}),game,ct:ct);};
            var update=tasks.Add(new("fixture","重新导入",game,null,new()));await tasks.RunAsync(update.Id);
            Check("已有长路径实例也能更新而非只支持首次安装",tasks.List().Single(t=>t.Id==update.Id).State==DurableTaskState.Completed&&File.ReadAllText(Path.Combine(game,"instances",id,".minecraft","config","example.txt"))=="updated");

            var cancel=tasks.Add(new("fixture","旧断点记录",game,null,new()));
            tasks.Execute=(_,_,ct)=>
            {
                var directory=DurableExecution.Current!.Directory;
                var path=Write(Path.Combine(directory,"deep",new string('p',100),"file.jar.part"));
                DurableExecution.TrackPartial(LongPath.Normalize(path));
                LongPath.DeleteDirectory(directory);tasks.Cancel(cancel.Id);ct.ThrowIfCancellationRequested();return Task.CompletedTask;
            };
            await tasks.RunAsync(cancel.Id);
            Check("已经清理的长路径断点不再被误报无法清理",tasks.List().Single(t=>t.Id==cancel.Id) is {State:DurableTaskState.Canceled,Partials.Count:0,IsHistory:true});
            var legacy=tasks.Add(new("fixture","旧版临时文件",game,null,new()));
            var legacyPath=Write(Path.Combine(data,"task-center",legacy.Id,"nested","old.part"));
            tasks.Cancel(legacy.Id);
            Check("取消旧任务清理旧目录且保留任务记录",!File.Exists(legacyPath)&&File.Exists(Path.Combine(data,"task-center",legacy.Id+".json")));
            var paused=tasks.Add(new("fixture","暂停任务",game,null,new()));
            string? pausedFile=null;
            tasks.Execute=async(_,_,ct)=>{pausedFile=Write(Path.Combine(DurableExecution.Current!.Directory,"paused.part"));DurableExecution.TrackPartial(pausedFile);tasks.Pause(paused.Id);await Task.Delay(1,ct);};
            await tasks.RunAsync(paused.Id);
            var abandoned=Write(Path.Combine(game,".cache","tasks",Guid.NewGuid().ToString("N"),"orphan.part"));File.SetLastWriteTimeUtc(abandoned,DateTime.UtcNow.AddDays(-8));
            var fresh=Write(Path.Combine(game,".cache","tasks",Guid.NewGuid().ToString("N"),"fresh.part"));
            var shared=Write(Path.Combine(game,"libraries","keep.jar"));
            var catalog=new StorageCatalog(data,game);var areas=catalog.Scan();
            Check("存储管理单独展示共享资源和任务暂存",areas.Any(x=>x.Key=="game-tasks")&&areas.Any(x=>x.Key=="shared-libraries"&&!x.CanClean));
            catalog.Clean("game-tasks");
            Check("缓存清理删除过期无记录断点",!File.Exists(abandoned));
            Check("缓存清理保留暂停断点和新暂存",File.Exists(pausedFile)&&File.Exists(fresh));
            Check("缓存清理不删除完整共享运行库",File.Exists(shared));
            var recovery=tasks.Add(new("fixture","恢复保护",game,null,new()));
            string? recoveryFile=null;
            tasks.Execute=(_,_,_)=>{recoveryFile=Write(Path.Combine(DurableExecution.Current!.Directory,"backup","original.jar"));DurableExecution.Current.PreserveRecovery();throw new AggregateException(new IOException("fixture rollback locked"));};
            await tasks.RunAsync(recovery.Id);tasks.Cancel(recovery.Id);catalog.Clean("game-tasks");
            var recovered=new PersistentTasks(data).List().Single(t=>t.Id==recovery.Id);
            Check("回滚未完成的恢复资料不会被取消或缓存清理删除",File.Exists(recoveryFile)&&recovered.NeedsRecovery&&!recovered.CanCancel&&!recovered.CanResume&&!recovered.IsHistory);
            if(OperatingSystem.IsWindows())
            {
                var locked=tasks.Add(new("fixture","完成后清理被占用",game,null,new()));FileStream? handle=null;
                tasks.Execute=(_,_,_)=>{var path=Write(Path.Combine(DurableExecution.Current!.Directory,"locked.zip"));handle=new(path,FileMode.Open,FileAccess.Read,FileShare.None);return Task.CompletedTask;};
                await tasks.RunAsync(locked.Id);handle!.Dispose();
                Check("游戏已完成时缓存清理失败不会误报安装失败",tasks.List().Single(t=>t.Id==locked.Id) is {State:DurableTaskState.Completed,Partials.Count:>0});
                catalog.Clean("game-tasks");
                Check("占用解除后可从存储管理回收完成任务缓存",!File.Exists(Path.Combine(PersistentTasks.WorkspacePath(locked),"locked.zip")));
            }
        }
        finally{LongPath.DeleteDirectory(root);}
    }
}
