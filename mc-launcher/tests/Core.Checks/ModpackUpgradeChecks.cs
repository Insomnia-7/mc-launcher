using System.IO.Compression;
using System.Text.Json;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;
using Launcher.Infrastructure.Modpacks;

internal static class ModpackUpgradeChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fangyu-pack-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string id = "test-pack";
        var instance = Path.Combine(root, "instances", id); var game = Path.Combine(instance, ".minecraft");
        void Check(string name, bool result) => check(name, result, null);
        async Task<bool> Reject(Func<Task> action) { try { await action(); return false; } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException) { return true; } }
        string Write(string path, string text) { var full = Path.Combine(game, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); return full; }
        string Jar(string path, string modId, string version, string? depends = null)
        {
            var full = Path.Combine(game, "mods", path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var zip = ZipFile.Open(full, ZipArchiveMode.Create); using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            writer.Write(JsonSerializer.Serialize(new { id = modId, version, depends = depends is null ? new Dictionary<string,string>() : new() { [depends] = ">=2.0" } })); return full;
        }
        string Pack(string version, Dictionary<string, byte[]> content)
        {
            var path = Path.Combine(root, version + ".mrpack");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { formatVersion = 1, game = "minecraft", name = "测试整合包", versionId = version, dependencies = new Dictionary<string,string> { ["minecraft"] = "1.20.1" }, files = Array.Empty<object>() }));
            foreach (var (relative, bytes) in content) using (var output = zip.CreateEntry("overrides/" + relative).Open()) output.Write(bytes);
            return path;
        }
        try
        {
            Directory.CreateDirectory(game); Directory.CreateDirectory(Path.Combine(root, "versions", "1.20.1"));
            File.WriteAllText(Path.Combine(root,"versions","1.20.1","1.20.1.json"), "{}");
            new InstanceMetadata("测试整合包", "1", "Modrinth", null, "1.20.1", null, "1.20.1").Write(root, id);
            Write("config/settings.json", "{\"volume\":1,\"quality\":1}"); Write("config/conflict.toml", "quality=1");
            Write("obsolete.txt", "old"); var oldJar = Jar("old.jar", "demo", "1.0");
            var baseline = await ModpackBaseline.CaptureAsync(game, "测试整合包", "1", "Modrinth");
            ExperienceFiles.Write(Path.Combine(instance, ModpackBaseline.FileName), baseline);
            Write("config/settings.json", "{\"volume\":2,\"quality\":1}"); Write("config/conflict.toml", "quality=local");
            var world = Write("saves/world/level.dat", "world-data"); var screenshot = Write("screenshots/test.png", "image-data");
            var custom = Write("config/user-only.txt", "mine");
            var newJar = Jar("incoming.jar", "demo", "2.0"); var newBytes = File.ReadAllBytes(newJar); File.Delete(newJar);
            var pack = Pack("2", new() {
                ["mods/new.jar"] = newBytes, ["config/settings.json"] = "{\"volume\":1,\"quality\":2}"u8.ToArray(),
                ["config/conflict.toml"] = "quality=upstream"u8.ToArray(), ["new.txt"] = "new"u8.ToArray(),
                ["saves/world/level.dat"] = "must-not-replace-world"u8.ToArray() });
            var sink = new FakeDownloader((_,_)=>null); var meta = new StubVersionMeta(); var baseInstaller = new GameInstaller(sink, meta);
            var installer = new ModpackInstaller(baseInstaller, new(new StubLoaderMeta(), meta, baseInstaller), new ThrowingForgeInstaller(), new ThrowingNeoForgeInstaller(), sink);
            var service = installer.CreateUpdateService(); var factory = new ZipModpackSourceFactory(new StubCurseForgeClient());
            async Task<PackUpdatePlan> Prepare(string archive) { await using var source = factory.Open(archive); return await service.PrepareAsync(source, root, id); }
            var plan = await Prepare(pack);
            Check("整合包预览不会修改当前文件", File.Exists(oldJar) && !File.Exists(Path.Combine(game,"mods","new.jar")) && File.ReadAllText(custom)=="mine");
            Check("预览区分新增移除与冲突", plan.Changes.Any(c=>c.Path=="new.txt"&&c.Kind=="新增")&&plan.Changes.Any(c=>c.Path=="obsolete.txt"&&c.Kind=="移除")&&plan.ConflictCount==1);
            Check("互不冲突的 JSON 设置自动三方合并", plan.Changes.Single(c=>c.Path=="config/settings.json").Kind=="合并");
            Check("升级清单排除存档与截图", !plan.Changes.Any(c=>c.Path.StartsWith("saves/")));
            Write("config/settings.json", "{\"volume\":3,\"quality\":1}");
            Check("预览后修改实例会阻止旧计划执行", await Reject(()=>service.ApplyAsync(plan,new Dictionary<string,bool>(),factory)) && File.Exists(oldJar)); service.Discard(plan);
            Write("config/settings.json", "{\"volume\":2,\"quality\":1}"); plan=await Prepare(pack);
            var extra=Jar("extra.jar","extra","1.0");
            Check("预览后添加模组使计划失效",await Reject(()=>service.ApplyAsync(plan,new Dictionary<string,bool>(),factory)));File.Delete(extra);service.Discard(plan);
            plan=await Prepare(pack); using(var cts=new CancellationTokenSource())
            {
                cts.Cancel(); Check("应用前取消不改变实例",await Reject(()=>service.ApplyAsync(plan,new Dictionary<string,bool>(),factory,ct:cts.Token))&&File.Exists(oldJar));
            }
            await service.ApplyAsync(plan,new Dictionary<string,bool>(),factory);service.Discard(plan);
            using(var merged=JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"config/settings.json"))))
                Check("升级保留本地设置并合入上游设置",merged.RootElement.GetProperty("volume").GetInt32()==2&&merged.RootElement.GetProperty("quality").GetInt32()==2);
            Check("升级删除旧模组并安装新模组",!File.Exists(oldJar)&&File.Exists(Path.Combine(game,"mods/new.jar"))&&!File.Exists(Path.Combine(game,"obsolete.txt")));
            Check("默认保留配置冲突与用户新增文件",File.ReadAllText(Path.Combine(game,"config/conflict.toml"))=="quality=local"&&File.ReadAllText(custom)=="mine");
            Check("升级保留世界和截图原始内容",File.ReadAllText(world)=="world-data"&&File.ReadAllText(screenshot)=="image-data");
            Check("升级后更新实例和原始清单版本",InstanceMetadata.TryRead(root,id)?.PackVersion=="2"&&ModpackBaseline.Read(instance)?.Version=="2");
            var recovery=service.Recoveries(root,id).Single();
            Check("整合包升级生成可用恢复点",recovery.Files.ContainsKey("instance.json")&&recovery.Files.ContainsKey(".minecraft/mods/old.jar"));
            await service.RestoreAsync(root,id,recovery.Id);
            Check("恢复还原旧模组并移除新增文件",File.Exists(oldJar)&&!File.Exists(Path.Combine(game,"mods/new.jar"))&&!File.Exists(Path.Combine(game,"new.txt")));
            Check("恢复还原版本与本地修改",InstanceMetadata.TryRead(root,id)?.PackVersion=="1"&&File.ReadAllText(Path.Combine(game,"config/settings.json")).Contains("\"volume\":2"));
            Check("恢复不修改世界与用户新增文件",File.ReadAllText(world)=="world-data"&&File.Exists(custom));

            // Force failure after earlier files have been replaced, then verify the transaction restores them.
            var failurePack=Pack("3",new(){["config/settings.json"]="{\"volume\":1,\"quality\":3}"u8.ToArray(),["zblock.txt"]="blocked"u8.ToArray()});
            Directory.CreateDirectory(Path.Combine(game,"zblock.txt"));plan=await Prepare(failurePack);
            var original=File.ReadAllText(Path.Combine(game,"config/settings.json"));
            Check("提交中途失败自动恢复已替换文件",await Reject(()=>service.ApplyAsync(plan,new Dictionary<string,bool>(),factory))&&File.ReadAllText(Path.Combine(game,"config/settings.json"))==original&&File.Exists(oldJar));service.Discard(plan);
            Directory.Delete(Path.Combine(game,"zblock.txt"));
            File.Delete(Path.Combine(instance,ModpackBaseline.FileName));plan=await Prepare(pack);
            Check("旧实例无原始清单时重名文件默认保留",!plan.HasBaseline&&plan.Changes.Single(c=>c.Path=="config/settings.json").IsConflict&&!plan.Changes.Single(c=>c.Path=="config/settings.json").ApplyByDefault&&!plan.Changes.Any(c=>c.Kind=="移除"));service.Discard(plan);

            Check("JSON 同字段修改需要用户决定",ModpackUpdateService.MergeJson("{\"x\":1}","{\"x\":2}","{\"x\":3}")==null);
            Check("JSON 删除与空值不混淆",ModpackUpdateService.MergeJson("{\"x\":null}","{}","{\"x\":1}")==null);
            Check("版本范围支持 Fabric 与 Maven 常用写法",ModCompatibility.Satisfies("1.20.1",">=1.20 <1.21")==true&&ModCompatibility.Satisfies("2.0","[1,2)")==false&&ModCompatibility.Satisfies("0.2.4","^0.2.3")==true);
            Check("不认识的版本表达式不武断报错",ModCompatibility.Satisfies("1.0-beta","~>1")==null);
            var required=Jar("needs.jar","needs","1.0","missing");
            var issues=ModCompatibility.Inspect([required],"1.20.1","fabric","0.16");
            Check("兼容检查发现缺少前置",issues.Any(i=>i.IsError&&i.Title.Contains("missing")));
            var dependency=Jar("dependency.jar","missing","1.0");
            Check("兼容检查发现前置版本不符",ModCompatibility.Inspect([required,dependency],"1.20.1","fabric","0.16").Any(i=>i.IsError&&i.Title.Contains("版本不符")));
            File.Move(required,required+".disabled");
            Check("兼容检查忽略禁用模组",!ModCompatibility.Inspect([required+".disabled",dependency],"1.20.1","fabric","0.16").Any(i=>i.IsError));
            var copy=Path.Combine(game,"mods/duplicate.jar");File.Copy(dependency,copy);
            Check("兼容检查识别重复 Mod ID",ModCompatibility.Inspect([dependency,copy],"1.20.1","fabric","0.16").Any(i=>i.Title.StartsWith("重复模组")));
            string MetadataJar(string name,string entry,string content)
            {var path=Path.Combine(game,"mods",name);using var archive=ZipFile.Open(path,ZipArchiveMode.Create);using var writer=new StreamWriter(archive.CreateEntry(entry).Open());writer.Write(content);return path;}
            var neo=MetadataJar("neo.jar","META-INF/neoforge.mods.toml","[[mods]]\nmodId=\"neo_test\"\nversion=\"1.0\"\n[[dependencies.neo_test]]\nmodId=\"needed\"\ntype=\"required\"\nversionRange=\"[2,)\"\nside=\"BOTH\"\n[[dependencies.neo_test]]\nmodId=\"server_only\"\ntype=\"required\"\nside=\"SERVER\"\n");
            var neoIssues=ModCompatibility.Inspect([neo],"1.20.1","neoforge","20.1");
            Check("NeoForge 必需前置与服务端限定分别处理",neoIssues.Any(i=>i.IsError&&i.Title.Contains("needed"))&&!neoIssues.Any(i=>i.Title.Contains("server_only")));
            var forge=MetadataJar("forge.jar","META-INF/mods.toml","[[mods]]\nmodId=\"forge_test\"\nversion=\"1.0\"\n[[dependencies.forge_test]]\nmodId=\"needed\"\nmandatory=true\nversionRange=\"[2,)\"\nside=\"CLIENT\"\n");
            Check("Forge mandatory 前置可识别",ModCompatibility.Inspect([forge],"1.20.1","forge","47.1").Any(i=>i.IsError&&i.Title.Contains("needed")));
            var quilt=MetadataJar("quilt.jar","quilt.mod.json","""{"quilt_loader":{"id":"quilt_test","version":"1.0","depends":[{"id":"needed","versions":">=2"}]}}""");
            Check("Quilt 常用前置声明可识别",ModCompatibility.Inspect([quilt],"1.20.1","quilt","0.26").Any(i=>i.IsError&&i.Title.Contains("needed")));
            var outer=MetadataJar("outer.jar","fabric.mod.json","""{"id":"outer","version":"1","depends":{"included":">=1"},"jars":[{"file":"nested.jar"}]}""");
            using(var nestedBytes=new MemoryStream())
            {
                using(var inner=new ZipArchive(nestedBytes,ZipArchiveMode.Create,true))using(var writer=new StreamWriter(inner.CreateEntry("fabric.mod.json").Open()))writer.Write("""{"id":"included","version":"1"}""");
                using var outerZip=ZipFile.Open(outer,ZipArchiveMode.Update);using var output=outerZip.CreateEntry("nested.jar").Open();output.Write(nestedBytes.ToArray());
            }
            Check("Fabric 内嵌前置不会被误报缺失",!ModCompatibility.Inspect([outer],"1.20.1","fabric","0.16").Any(i=>i.IsError));
            var included=MetadataJar("included.jar","fabric.mod.json","""{"id":"included","version":"2"}""");
            Check("内嵌与独立前置不会被误报为重复顶层模组",!ModCompatibility.Inspect([outer,included],"1.20.1","fabric","0.16").Any(i=>i.IsError));

            var queue=new PersistentTasks(Path.Combine(root,"queue")){AutoContinue=true};var ran=new List<string>();
            queue.Execute=(r,_,_)=>{ran.Add(r.Title);return Task.CompletedTask;};
            var a=queue.Add(new("test","a",root,null,new()));var b=queue.Add(new("test","b",root,null,new()));var c=queue.Add(new("test","c",root,null,new()));
            queue.MoveReady(c.Id,-1);await queue.RunAsync(a.Id);
            Check("队列按调整后的顺序自动接续",ran.SequenceEqual(new[]{"a","c","b"})&&queue.List().All(t=>t.State==DurableTaskState.Completed));
            var failed=queue.Add(new("test","fail",root,null,new()));var pending=queue.Add(new("test","pending",root,null,new()));queue.Execute=(_,_,_)=>throw new IOException("expected");await queue.RunAsync(failed.Id);
            Check("任务失败不会继续自动执行后续任务",queue.List().Single(t=>t.Id==pending.Id).State==DurableTaskState.Ready);
            queue.Cancel(pending.Id);
            using(var cancel=new CancellationTokenSource())
            {
                var committed=queue.Add(new("test","committed",root,null,new()));
                queue.Execute=(_,_,_)=>{DurableExecution.Current!.MarkCommitted();cancel.Cancel();return Task.CompletedTask;};
                await queue.RunAsync(committed.Id,cancel.Token);
                Check("文件提交完成后的取消不会误报为未完成",queue.List().Single(t=>t.Id==committed.Id).State==DurableTaskState.Completed);
            }
        }
        finally { ExperienceFiles.DeleteTree(root, Path.GetTempPath()); }
    }
}
