using Launcher.Application.Experience;
using Launcher.Application.Instances;
using Launcher.Application.Operations;

internal static class LibraryStorageChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fangyu-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, bool value) => check(name, value, null);
        string Seed(string relative, string text = "fixture") { var p = ExperienceFiles.Child(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
        async Task<bool> Reject(Func<Task> operation) { try { await operation(); return false; } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { return true; } }
        try
        {
            var source = Path.Combine(root,"MC","中文整合包",".minecraft");
            Seed("MC/中文整合包/.minecraft/versions/1.21/1.21.json", """{"id":"1.21","type":"release"}""");
            Seed("MC/中文整合包/.minecraft/versions/broken/broken.json", "bad");
            var original = Seed("MC/中文整合包/.minecraft/saves/World/level.dat", "precious-world");
            Seed("MC/中文整合包/.minecraft/mods/example.jar");
            Seed("MC/中文整合包/.minecraft/config/mod.json");
            var found = GameLibraryDiscovery.Scan([Path.Combine(root,"MC")],4,default);
            Check("指定父目录发现深层中文路径并排除损坏版本", found.Count==1 && found[0].Versions==1 && !found[0].IsFangyu);
            Check("发现外部库不写入索引", !File.Exists(InstanceRegistry.PathFor(source)));
            var target = Path.Combine(root,"imported");
            await ExternalGameImport.CopyAsync(source,"1.21",source,target);
            Check("复制导入将原存档和模组放入真实运行目录", File.ReadAllText(Path.Combine(target,"instances","1.21",".minecraft","saves","World","level.dat"))=="precious-world" && File.Exists(Path.Combine(target,"instances","1.21",".minecraft","mods","example.jar")));
            Check("复制导入不修改来源", File.ReadAllText(original)=="precious-world" && !File.Exists(InstanceRegistry.PathFor(source)));
            Check("目标成为独立方隅库", GameLibraryDiscovery.IsFangyuLibrary(target));
            Check("拒绝覆盖非空目录", await Reject(()=>ExternalGameImport.CopyAsync(source,"1.21",source,target)));
            Check("拒绝把副本放在来源里面", await Reject(()=>ExternalGameImport.CopyAsync(source,"1.21",source,Path.Combine(source,"child"))));
            var canceled = Path.Combine(root,"canceled"); using var cts=new CancellationTokenSource(); cts.Cancel();
            Check("取消复制返回取消状态", await Reject(()=>ExternalGameImport.CopyAsync(source,"1.21",source,canceled,ct:cts.Token)));
            Check("取消后没有半成品和临时目录", !Directory.Exists(canceled) || !Directory.EnumerateFileSystemEntries(canceled).Any());
            var data=Path.Combine(root,"data"); var cache=Seed("data/cache/meta/old.json","12345"); File.SetLastWriteTimeUtc(cache,DateTime.UtcNow.AddDays(-8));
            Seed("data/cache/meta/fresh.json","12345"); Seed("data/cache/meta/keep.part","12345");
            Seed("data/java/runtime/bin/java","runtime"); var package=Seed("data/java/.cache/runtime.zip","cache"); File.SetLastWriteTimeUtc(package,DateTime.UtcNow.AddDays(-8));
            var catalog=new StorageCatalog(data,target); var areas=catalog.Scan();
            Check("存储分类同时提供总占用和可回收", areas.Single(a=>a.Key=="cache/meta") is {Bytes:15,ReclaimableBytes:5,Files:3});
            Check("Java与安装包缓存不重复计算", areas.Single(a=>a.Key=="java").Bytes==7 && areas.Single(a=>a.Key=="java/.cache").Bytes==5);
            Check("存储分类展示真实路径", areas.All(a=>Path.IsPathFullyQualified(a.Path)));
            using var scanCanceled = new CancellationTokenSource(); scanCanceled.Cancel();
            Check("扫描支持取消", await Reject(()=>Task.Run(()=>catalog.Scan(scanCanceled.Token))));
            var tasks=new PersistentTasks(data){Execute=(_,_,_)=>Task.CompletedTask};
            var task=tasks.Add(new("repair","repair",target,null,new())); await tasks.RunAsync(task.Id);
            Check("不同任务有对应完成文案", tasks.List().Single().Detail=="文件修复已完成");
            Check("清除完成记录不会删除已安装游戏", tasks.RemoveHistory(task.Id) && File.Exists(Path.Combine(target,"versions","1.21","1.21.json")));
            Check("清除后重启记录不再出现", new PersistentTasks(data).List().Count==0);
            var pending=tasks.Add(new("install","pending",target,null,new()));
            Check("进行中记录不能误清", !tasks.RemoveHistory(pending.Id));
            var other=Path.Combine(root,"other");
            Check("其他游戏库任务不阻止当前库缓存清理", !tasks.ConflictsWithCleanup(Path.Combine(other,".cache","imports")));
            Check("同库未完成任务保护导入临时文件", tasks.ConflictsWithCleanup(Path.Combine(target,".cache","imports")));
            tasks.Cancel(pending.Id); Check("取消完成后可清空历史", tasks.ClearHistory()==1);
            var snapshots=new InstanceSnapshots(data,target,"1.21");
            await snapshots.CaptureAsync("迁移前",true);
            var migratedGame=Path.Combine(root,"migrated-game");
            await GameLibraryMigration.CopyAsync(target,migratedGame,data);
            Check("游戏库迁移保留私有存档并关联快照",File.Exists(Path.Combine(migratedGame,"instances","1.21",".minecraft","saves","World","level.dat")) && new InstanceSnapshots(data,migratedGame,"1.21").List().Count==1);
            Check("游戏库迁移保留原副本",Directory.Exists(target));
            if(Environment.GetEnvironmentVariable("FANGYU_LINK_FIXTURE") is {Length:>0} linked)
            {
                var linkedAreas=new StorageCatalog(data,linked).Scan();
                Check("链接不再中断整次存储统计",linkedAreas.Count==9 && linkedAreas.Single(a=>a.Key=="game").Skipped>0);
            }
            var newData=Path.Combine(root,"new-data");
            await DataLocationMigration.CopyAsync(data,newData,target);
            Check("数据迁移复制并校验Java和配置", File.Exists(Path.Combine(newData,"java","runtime","bin","java")) && File.ReadAllText(Path.Combine(newData,"game-root.txt"))==target);
            Check("迁移保留旧副本与原游戏库", File.Exists(cache) && File.Exists(original));
            Check("数据迁移拒绝非空目标", await Reject(()=>DataLocationMigration.CopyAsync(data,newData,target)));
        }
        finally { ExperienceFiles.DeleteTree(root,Path.GetTempPath()); }
    }
}
