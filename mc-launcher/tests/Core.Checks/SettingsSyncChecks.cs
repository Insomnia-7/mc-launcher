using Launcher.Application.Experience;
using Launcher.Application.Install;

internal static class SettingsSyncChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-settings-sync-"+Guid.NewGuid().ToString("N"));var data=Path.Combine(root,"data");var game=Path.Combine(root,"game");
        Directory.CreateDirectory(Path.Combine(game,"versions","1.21.4"));File.WriteAllText(Path.Combine(game,"versions","1.21.4","1.21.4.json"),"{\"id\":\"1.21.4\"}");
        string Instance(string id)=>Path.Combine(game,"instances",id);
        string Options(string id)=>Path.Combine(Instance(id),".minecraft/options.txt");
        string Servers(string id)=>Path.Combine(Instance(id),".minecraft/servers.dat");
        void Seed(string id,string text){Directory.CreateDirectory(Path.GetDirectoryName(Options(id))!);new InstanceMetadata(id,null,null,null,"1.21.4",null,"1.21.4",false).Write(game,id);File.WriteAllText(Options(id),text);}
        void Check(string name,bool good)=>check(name,good,null);
        async Task<bool> Reject(Func<Task> action){try{await action();return false;}catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException or IOException or OperationCanceledException or UnauthorizedAccessException){return true;}}
        try
        {
            const string source="key_key.jump:key.keyboard.space\nsoundCategory_master:0.7\nlang:zh_cn\nrenderDistance:20\nkey_mod.magic:key.keyboard.g\naccessToken:private\njavaPath:private\n";
            const string target="# keep comment\r\nkey_key.jump:key.keyboard.space\r\nsoundCategory_master:0.5\r\nlang:en_us\r\nrenderDistance:8\r\nunknown:keep:value\r\n";
            Seed("source",source);Seed("target",target);Seed("legacy","key_key.jump:57\nmusic:0.3\n");Seed("broken",target);
            var settings=new InstanceSettings(4096,JavaPath:"machine-specific",ExtraJvmArgs:"-Dkeep=yes");settings.Save(game,"target");var privateSettings=File.ReadAllText(InstanceSettings.PathFor(game,"target"));
            var service=new SettingsSync(data,game);
            Check("未配置同步时默认关闭",service.Read() is null&&service.Member("target") is null);
            var profile=await service.SetSourceAsync("source",SyncSections.Keys|SyncSections.Sound|SyncSections.Language);
            Check("共享只捕获选择的游戏设置",profile.Options.ContainsKey("key_key.jump")&&profile.Options.ContainsKey("soundCategory_master")&&!profile.Options.ContainsKey("renderDistance")&&!profile.Options.ContainsKey("accessToken")&&!profile.Options.ContainsKey("javaPath"));
            service.SaveMember("target",true,SyncSections.Keys|SyncSections.Sound|SyncSections.Language,[]);
            var plan=await service.PreviewAsync("target");var before=File.ReadAllBytes(Options("target"));
            Check("首次不同值默认作为冲突保留本地",plan.Changes.Single(c=>c.Key=="soundCategory_master") is {IsConflict:true,ApplyByDefault:false});
            Check("不会写入目标未生成的模组按键",plan.Changes.Single(c=>c.Key=="key_mod.magic").IsSkipped);
            Check("预览不会改变游戏文件",before.SequenceEqual(File.ReadAllBytes(Options("target"))));
            Check("自动同步不会覆盖首次冲突",await service.ApplyAsync(plan,automatic:true)==0&&before.SequenceEqual(File.ReadAllBytes(Options("target"))));
            plan=await service.PreviewAsync("target");await service.ApplyAsync(plan,new Dictionary<string,bool>{{"soundCategory_master",true}});
            Check("手动确认后采用共享音量",SettingsPresets.ReadOptions(Options("target"))["soundCategory_master"]=="0.7");
            Check("未勾选的语言差异保存为实例例外",service.Member("target")!.IndependentKeys.Contains("lang")&&SettingsPresets.ReadOptions(Options("target"))["lang"]=="en_us");
            var patched=File.ReadAllText(Options("target"));
            Check("同步保留注释未知字段换行和画面设置",patched.Contains("# keep comment\r\n")&&patched.Contains("unknown:keep:value\r\n")&&patched.Contains("renderDistance:8\r\n"));
            Check("同步不改变 Java 内存及启动参数",File.ReadAllText(InstanceSettings.PathFor(game,"target"))==privateSettings);
            File.WriteAllText(Options("source"),source.Replace("0.7","0.8").Replace("key.keyboard.space","key.keyboard.j"));await service.RefreshSourceAsync();plan=await service.PreviewAsync("target");
            Check("后续来源变更可自动接续而非一次性预设",await service.ApplyAsync(plan,automatic:true)==2&&SettingsPresets.ReadOptions(Options("target"))["key_key.jump"]=="key.keyboard.j");
            File.WriteAllText(Options("target"),File.ReadAllText(Options("target")).Replace("0.8","0.9"));File.WriteAllText(Options("source"),File.ReadAllText(Options("source")).Replace("0.8","0.6"));await service.RefreshSourceAsync();plan=await service.PreviewAsync("target");
            Check("本地再次修改会重新形成冲突",plan.Changes.Single(c=>c.Key=="soundCategory_master").IsConflict&&await service.ApplyAsync(plan,automatic:true)==0&&SettingsPresets.ReadOptions(Options("target"))["soundCategory_master"]=="0.9");
            var corrupt=service.Backups("target").First();var corruptPath=Path.Combine(Instance("target"),".settings-sync-backups",corrupt.Id,"files/.minecraft/options.txt");File.AppendAllText(corruptPath,"tampered");
            Check("损坏恢复点在写入前被拒绝",await Reject(()=>service.RestoreAsync("target",corrupt.Id))&&SettingsPresets.ReadOptions(Options("target"))["soundCategory_master"]=="0.9");
            plan=await service.PreviewAsync("target");File.AppendAllText(Options("target"),"external:change\n");
            Check("预览后本地修改使旧计划失效",await Reject(()=>service.ApplyAsync(plan))&&File.ReadAllText(Options("target")).Contains("external:change"));
            plan=await service.PreviewAsync("target");File.WriteAllText(Options("source"),File.ReadAllText(Options("source")).Replace("0.6","0.4"));await service.RefreshSourceAsync();
            Check("预览后共享方案更新使旧计划失效",await Reject(()=>service.ApplyAsync(plan)));
            service.SaveMember("legacy",true,SyncSections.Keys,[]);var legacy=await service.PreviewAsync("legacy");
            Check("新旧按键编码不兼容时跳过",legacy.Changes.Single(c=>c.Key=="key_key.jump").IsSkipped&&await service.ApplyAsync(legacy,automatic:true)==0&&SettingsPresets.ReadOptions(Options("legacy"))["key_key.jump"]=="57");
            service.SaveMember("target",true,SyncSections.Sound,["soundCategory_master"]);plan=await service.PreviewAsync("target");
            Check("可按字段保留本实例独立设置",plan.Changes.Single(c=>c.Key=="soundCategory_master").Kind=="独立"&&await service.ApplyAsync(plan,automatic:true)==0);
            service.SaveMember("target",false,SyncSections.Sound,[]);
            Check("关闭实例同步后不能应用",await Reject(()=>service.PreviewAsync("target")));

            File.WriteAllBytes(Servers("source"),[10,0,0,9,0,7,115,101,114,118,101,114,115,10,0,0,0,0,0]);File.WriteAllBytes(Servers("target"),[10,0,0,0]);var oldServers=File.ReadAllBytes(Servers("target"));
            await service.SetSourceAsync("source",SyncSections.Servers);service.SaveMember("target",true,SyncSections.Servers,[]);plan=await service.PreviewAsync("target");
            Check("服务器列表不同需要明确选择",plan.Changes.Single().IsConflict&&!plan.Changes.Single().ApplyByDefault);
            await service.ApplyAsync(plan,new Dictionary<string,bool>{{SettingsSync.ServersKey,true}});
            Check("确认后同步完整服务器列表并备份",File.ReadAllBytes(Servers("source")).SequenceEqual(File.ReadAllBytes(Servers("target")))&&service.Backups("target").Count>0);
            var recovery=service.Backups("target").First();await service.RestoreAsync("target",recovery.Id);
            Check("恢复还原原服务器列表并暂停同步",oldServers.SequenceEqual(File.ReadAllBytes(Servers("target")))&&service.Member("target")?.Enabled==false);
            service.SaveMember("target",true,SyncSections.Servers,[]);plan=await service.PreviewAsync("target");await service.ApplyAsync(plan,new Dictionary<string,bool>{{SettingsSync.ServersKey,false}});
            Check("保留本地服务器列表会记住独立选择",!service.Member("target")!.Sections.HasFlag(SyncSections.Servers));

            await service.SetSourceAsync("source",SyncSections.Sound|SyncSections.Servers);service.SaveMember("broken",true,SyncSections.Sound|SyncSections.Servers,[]);Directory.CreateDirectory(Servers("broken"));
            var brokenOptions=File.ReadAllBytes(Options("broken"));var brokenMember=File.ReadAllBytes(Path.Combine(Instance("broken"),SettingsSyncMember.FileName));plan=await service.PreviewAsync("broken");
            Check("同步提交失败回滚此前设置和成员状态",await Reject(()=>service.ApplyAsync(plan,new Dictionary<string,bool>{{"soundCategory_master",true},{SettingsSync.ServersKey,true}}))&&brokenOptions.SequenceEqual(File.ReadAllBytes(Options("broken")))&&brokenMember.SequenceEqual(File.ReadAllBytes(Path.Combine(Instance("broken"),SettingsSyncMember.FileName))));
            Directory.Delete(Servers("broken"));
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();Check("取消同步保留当前设置",await Reject(()=>service.ApplyAsync(plan,ct:cancel.Token))&&brokenOptions.SequenceEqual(File.ReadAllBytes(Options("broken")))) ;}
            Check("不同游戏目录的共享方案彼此独立",new SettingsSync(data,Path.Combine(root,"other")).Read() is null);
            Check("拒绝越界实例编号",await Reject(()=>service.SetSourceAsync("../target",SyncSections.Sound)));
            var latest=await service.RefreshSourceAsync();var repeated=await service.RefreshSourceAsync();Check("来源未改变时不重复增加版本或写入",latest!.Revision==repeated!.Revision);
            File.WriteAllText(Options("source"),new string('x',1048577));
            var serverOnly=await service.SetSourceAsync("source",SyncSections.Servers);
            Check("仅共享服务器时不读取无关游戏选项",serverOnly.Options.Count==0&&serverOnly.Servers is not null);
        }
        finally{ExperienceFiles.DeleteTree(root,Path.GetTempPath());}
    }
}
