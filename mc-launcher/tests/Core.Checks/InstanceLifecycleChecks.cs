using Launcher.Application.Experience;
using Launcher.Application.Instances;
using Launcher.Application.Install;
using Launcher.Application.Java;

internal static class InstanceLifecycleChecks
{
    public static void Run(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-lifecycle-"+Guid.NewGuid().ToString("N"));
        void Check(string name,bool value)=>check(name,value,null);
        void Write(string path,string text){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
        void Version(string game,string id,string? parent=null,string key="x-launcher-parent",int? java=null)=>Write(Path.Combine(game,"versions",id,id+".json"),"{\"id\":\""+id+"\""+(parent is null?"":",\""+key+"\":\""+parent+"\"")+(java is null?"":",\"javaVersion\":{\"majorVersion\":"+java+"}")+"}");
        try
        {
            foreach(var key in new[]{"inheritsFrom","x-launcher-parent"})
            {
                var game=Path.Combine(root,key);Version(game,"base");Version(game,"fabric","base",key);
                new InstanceSettings().Save(game,"base");
                Check(key+"：基础版本的 settings 目录不会冒充原版实例",InstanceRegistry.LoadOrMigrate(game).Instances.Select(x=>x.Id).SequenceEqual(["fabric"]));
                InstanceDeletion.Delete(game,"fabric","fabric");
                Check(key+"：删除加载器同时回收未共享的原版依赖",!Directory.Exists(Path.Combine(game,"versions","base")));
                Check(key+"：重启索引后不会复活原版或孤儿目录",InstanceRegistry.LoadOrMigrate(game).Instances.Count==0);
                Version(game,"base");
                Check(key+"：旧依赖文件被恢复也不会冒充独立原版",InstanceRegistry.Load(game).Instances.Count==0);
                InstanceRegistry.RegisterStandaloneVersion(game,"base");
                Check(key+"：明确重装原版后重新显示",InstanceRegistry.Load(game).Instances.Single().Id=="base");
            }
            var shared=Path.Combine(root,"shared");Version(shared,"base");Version(shared,"fabric","base");Version(shared,"forge","base","inheritsFrom");
            InstanceDeletion.Delete(shared,"fabric","fabric");
            Check("删除一个加载器保留另一加载器需要的原版 JAR",File.Exists(Path.Combine(shared,"versions","base","base.json"))&&InstanceRegistry.Load(shared).Instances.Single().Id=="forge");
            InstanceRegistry.RegisterStandaloneVersion(shared,"base");InstanceDeletion.Delete(shared,"forge","forge");
            Check("独立安装的原版不会随加载器删除",InstanceRegistry.Load(shared).Instances.Single().Id=="base"&&File.Exists(Path.Combine(shared,"versions","base","base.json")));
            var packs=Path.Combine(root,"packs");Version(packs,"base");Version(packs,"loader","base");
            new InstanceMetadata("整合包 A","1","modrinth","Fabric","base",null,"loader").Write(packs,"pack-a");
            new InstanceMetadata("整合包 B","1","modrinth","Fabric","base",null,"loader").Write(packs,"pack-b");
            Check("同加载器的整合包只显示两个可玩实例",InstanceRegistry.LoadOrMigrate(packs).Instances.Select(x=>x.Id).SequenceEqual(["pack-a","pack-b"]));
            InstanceDeletion.Delete(packs,"pack-a","loader");
            Check("删除一个整合包保留共享加载器和基础版本",File.Exists(Path.Combine(packs,"versions","loader","loader.json"))&&File.Exists(Path.Combine(packs,"versions","base","base.json"))&&InstanceRegistry.Load(packs).Instances.Single().Id=="pack-b");
            InstanceDeletion.Delete(packs,"pack-b","loader");
            Check("删除最后一个整合包清理基础依赖且重启后为空",InstanceRegistry.LoadOrMigrate(packs).Instances.Count==0&&!Directory.Exists(Path.Combine(packs,"versions","base")));
            var chain=Path.Combine(root,"chain");Version(chain,"base");Version(chain,"middle","base");Version(chain,"loader","middle");
            InstanceDeletion.Delete(chain,"loader","loader");
            Check("多层继承依赖按顺序回收",!Directory.EnumerateDirectories(Path.Combine(chain,"versions")).Any()&&InstanceRegistry.Load(chain).Instances.Count==0);
            if(OperatingSystem.IsWindows())
            {
                var locked=Path.Combine(root,"locked");Version(locked,"base");Version(locked,"loader","base");
                var jar=Path.Combine(locked,"versions","base","base.jar");Write(jar,"fixture");
                using var handle=new FileStream(jar,FileMode.Open,FileAccess.Read,FileShare.None);
                var result=InstanceDeletion.Delete(locked,"loader","loader");
                Check("依赖缓存被占用时仍报告实例已删除且不复活原版",result.CleanupWarning is not null&&!Directory.Exists(Path.Combine(locked,"versions","loader"))&&InstanceRegistry.Load(locked).Instances.Count==0);
            }

            var data=Path.Combine(root,"data");var gameA=Path.Combine(root,"java-a");var gameB=Path.Combine(root,"java-b");
            var exe=Path.Combine(data,"java","21","bin",OperatingSystem.IsWindows()?"java.exe":"java");Write(exe,"fixture");
            Version(gameA,"one",java:21);Version(gameB,"two",java:21);
            ManagedJavaCleanup.RememberLibrary(data,gameA);ManagedJavaCleanup.RememberLibrary(data,gameB);
            var runtime=ManagedJavaCleanup.ForInstance(data,gameA,"one","one")!;
            Check("自动 Java 与当前实例的版本要求相匹配",runtime.Major==21&&runtime.Executable==exe);
            new InstanceSettings(JavaPath:Path.Combine(root,"external-java","bin","java.exe")).Save(gameA,"one");
            Check("手动指定的外部 Java 不提供自动清理",ManagedJavaCleanup.ForInstance(data,gameA,"one","one") is null);
            new InstanceSettings().Save(gameA,"one");InstanceDeletion.Delete(gameA,"one","one");
            var kept=ManagedJavaCleanup.RemoveIfUnused(data,gameA,runtime,_=>false);
            Check("其他游戏库仍需使用的 Java 会保留",File.Exists(exe)&&kept.Contains("其他实例"));
            InstanceDeletion.Delete(gameB,"two","two");
            kept=ManagedJavaCleanup.RemoveIfUnused(data,gameA,runtime,_=>true);
            Check("正在使用的自动 Java 不会删除",File.Exists(exe)&&kept.Contains("正在使用"));
            var removed=ManagedJavaCleanup.RemoveIfUnused(data,gameA,runtime,_=>false);
            Check("用户选择后只删除无人使用的自动 Java",!Directory.Exists(runtime.Directory)&&removed.Contains("已同时删除"));
            var manual=Path.Combine(root,"manual");Write(Path.Combine(manual,"bin","java.exe"),"manual");
            var rejected=false;try{ManagedJavaCleanup.RemoveIfUnused(data,gameA,new(21,manual,Path.Combine(manual,"bin","java.exe")),_=>false);}catch(IOException){rejected=true;}
            Check("清理拒绝启动器管理目录以外的 Java",rejected&&Directory.Exists(manual));
        }
        finally{LongPath.DeleteDirectory(root);}
    }
}
