using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Accounts;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;
using Launcher.Domain.Install;
using Launcher.Infrastructure.Accounts;
using Launcher.Infrastructure.Http;

internal static class ExperienceChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        Console.WriteLine("== 玩家中心、持久任务与实例恢复 ==");
        var root=Path.Combine(Path.GetTempPath(),"fangyu-experience-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Check(string name,bool value)=>check(name,value,null);
        try
        {
            var skin=Png(64,64);var cape=Png(64,32);var store=new WardrobeStore(Path.Combine(root,"data"),"offline-id");
            var look=store.Import("测试方案",skin,"slim",cape);store.Select(look,true);
            Check("衣柜保存原始皮肤、披风及模型",store.Skin(look).SequenceEqual(skin)&&store.Cape(look)!.SequenceEqual(cape)&&store.List().Single().Model=="slim");
            Check("衣柜选择在重启后保留",new WardrobeStore(Path.Combine(root,"data"),"offline-id").Selection is {UseInGame:true} selected&&selected.LookId==look.Id);
            Check("不同身份衣柜互相隔离",new WardrobeStore(Path.Combine(root,"data"),"other").List().Count==0);
            bool invalid=false;try{store.Import("错误",Png(128,128),"classic");}catch(InvalidDataException){invalid=true;}Check("官方尺寸限制在上传之前验证",invalid);
            var local=Path.Combine(root,"local-game");store.ApplyLocal(local,"Player_1",look);
            Check("离线皮肤与披风写入组件约定目录",File.ReadAllBytes(Path.Combine(local,"CustomSkinLoader","LocalSkin","Fangyu","skins","Player_1.png")).SequenceEqual(skin)&&File.ReadAllBytes(Path.Combine(local,"CustomSkinLoader","LocalSkin","Fangyu","capes","Player_1.png")).SequenceEqual(cape));
            var csl=Path.Combine(local,"CustomSkinLoader");var configuration=Path.Combine(csl,"CustomSkinLoader.json");
            File.WriteAllText(configuration,"""{"enable":true,"loadlist":[{"name":"MojangAPI","type":"MojangAPI"},{"name":"Other","type":"Legacy","skin":"other/{USERNAME}.png"}]}""");
            look=store.SetModel(look,"classic");store.ApplyLocal(local,"Player_1",look);
            using(var config=System.Text.Json.JsonDocument.Parse(File.ReadAllText(configuration)))
            {var list=config.RootElement.GetProperty("loadlist");Check("方隅本地皮肤优先且保留其他皮肤源",list[0].GetProperty("name").GetString()=="Fangyu"&&list.GetArrayLength()==3&&config.RootElement.GetProperty("enable").GetBoolean());Check("皮肤模型更改同时持久化并传给组件",list[0].GetProperty("model").GetString()=="default"&&store.List().Single().Model=="classic");}
            WardrobeStore.RemoveLocalProfile(local,true);
            using(var config=System.Text.Json.JsonDocument.Parse(File.ReadAllText(configuration)))Check("停用只清理方隅纹理及来源",config.RootElement.GetProperty("loadlist").GetArrayLength()==2&&!Directory.Exists(Path.Combine(csl,"LocalSkin","Fangyu")));
            invalid=false;try{store.ApplyLocal(local,"../escape",look);}catch(InvalidDataException){invalid=true;}Check("离线用户名不能形成越界文件路径",invalid);
            store.DisableInGame();Check("可停用启动时应用本地外观",store.Selection?.UseInGame==false);
            var uploads=0;var modifications=0;var authorized=true;
            using(var http=new HttpClient(new ExperienceHandler(async request=>
            {
                if(request.RequestUri!.Host=="textures.minecraft.net") {Check("纹理请求不携带账号凭据",request.Headers.Authorization is null);return new(HttpStatusCode.OK){Content=new ByteArrayContent(skin)};}
                if(!authorized)return new(HttpStatusCode.Unauthorized);
                Check("正版请求使用 Minecraft Bearer 令牌",request.Headers.Authorization?.Parameter=="fixture-token");
                if(request.Method==HttpMethod.Post)
                {
                    var body=await request.Content!.ReadAsByteArrayAsync();var text=Encoding.Latin1.GetString(body);
                    Check("上传包含 PNG 文件与模型字段",text.Contains("variant")&&text.Contains("slim")&&body.AsSpan().IndexOf(skin)>=0);uploads++;
                }
                if(request.Method==HttpMethod.Put){Check("切换披风仅发送选定编号",(await request.Content!.ReadAsStringAsync()).Contains("owned-cape"));modifications++;}
                return new(HttpStatusCode.OK){Content=new StringContent("""{"id":"00112233445566778899aabbccddeeff","name":"Player","skins":[{"state":"ACTIVE","url":"https://textures.minecraft.net/texture/test","variant":"SLIM"}],"capes":[{"id":"owned-cape","alias":"已有披风","url":"https://textures.minecraft.net/texture/cape","state":"ACTIVE"}]}""")};
            })))
            {
                var client=new MinecraftAppearanceClient(http);var profile=await client.GetAsync("fixture-token");
                Check("官方资料解析皮肤模型与自有披风",profile.Model=="slim"&&profile.Capes.Single().Active);
                await client.UploadSkinAsync("fixture-token",skin,"slim");await client.SetCapeAsync("fixture-token","owned-cape");await client.SetCapeAsync("fixture-token",null);await client.ResetSkinAsync("fixture-token");
                Check("上传、切换、隐藏和重置均执行真实请求路径",uploads==1&&modifications==1);
                await client.GetTextureAsync("http://textures.minecraft.net/texture/test");
                invalid=false;try{await client.GetTextureAsync("https://other.invalid/skin.png");}catch(InvalidDataException){invalid=true;}Check("官方纹理仅从受信任纹理服务读取",invalid);
                authorized=false;invalid=false;try{await client.GetAsync("expired");}catch(HttpRequestException e){invalid=e.StatusCode==HttpStatusCode.Unauthorized;}Check("登录失效不会假报外观修改成功",invalid);
            }
            var gameRoot=Path.Combine(root,"game");var instance=Path.Combine(gameRoot,"instances","test");var game=Path.Combine(instance,".minecraft");
            Directory.CreateDirectory(Path.Combine(game,"mods"));Directory.CreateDirectory(Path.Combine(game,"config"));Directory.CreateDirectory(Path.Combine(game,"saves","world"));
            File.WriteAllText(Path.Combine(game,"mods","old.jar"),"old-mod");File.WriteAllText(Path.Combine(game,"config","config.txt"),"old-config");File.WriteAllText(Path.Combine(game,"saves","world","level.dat"),"old-world");
            new InstanceMetadata("测试",null,null,"Fabric","1.21.4",null,"test").Write(gameRoot,"test");
            var history=new InstanceSnapshots(Path.Combine(root,"data"),gameRoot,"test");var snapshot=await history.CaptureAsync("初始状态",true);
            File.Delete(Path.Combine(game,"mods","old.jar"));File.WriteAllText(Path.Combine(game,"mods","new.jar"),"new-mod");File.WriteAllText(Path.Combine(game,"config","config.txt"),"new-config");File.WriteAllText(Path.Combine(game,"saves","world","level.dat"),"new-world");
            await history.RestoreAsync(snapshot,SnapshotRestoreScope.Configuration);
            Check("配置恢复还原旧模组并移除后来新增文件",File.Exists(Path.Combine(game,"mods","old.jar"))&&!File.Exists(Path.Combine(game,"mods","new.jar"))&&File.ReadAllText(Path.Combine(game,"config","config.txt"))=="old-config");
            Check("默认恢复配置保留当前存档",File.ReadAllText(Path.Combine(game,"saves","world","level.dat"))=="new-world");
            await history.RestoreAsync(snapshot,SnapshotRestoreScope.Worlds);Check("显式恢复存档可还原旧进度",File.ReadAllText(Path.Combine(game,"saves","world","level.dat"))=="old-world");
            invalid=false;try{await history.RestoreAsync(snapshot with{Files=snapshot.Files.Append(new("../../escape",0,"")).ToArray()},SnapshotRestoreScope.All);}catch(InvalidDataException){invalid=true;}Check("快照拒绝未经许可的恢复路径",invalid);
            var configOnly=await history.CaptureAsync("不含存档");invalid=false;try{await history.RestoreAsync(configOnly,SnapshotRestoreScope.Worlds);}catch(InvalidOperationException){invalid=true;}Check("无存档快照不能用于覆盖存档",invalid);
            var configPath=Path.Combine(game,"config","config.txt");
            using(var occupied=File.Open(configPath,FileMode.Open,FileAccess.Read,FileShare.None))
            {invalid=false;try{await history.RestoreAsync(snapshot,SnapshotRestoreScope.Configuration);}catch(IOException){invalid=true;}Check("恢复遇到文件占用时会失败回滚",invalid);}
            Check("恢复失败不改变原有文件",File.ReadAllText(configPath)=="old-config");
            new InstanceSettings(MemoryMb:6144,JavaPath:"D:\\private-java.exe",ExtraJvmArgs:"-Dprivate=value").Save(gameRoot,"test");
            File.WriteAllLines(Path.Combine(game,"options.txt"),["music:0.4","renderDistance:12","key_key.jump:key.keyboard.space","serverSensitive:keep"]);
            var presets=new SettingsPresets(Path.Combine(root,"data"));var preset=presets.Capture(gameRoot,"test","建筑",true);
            Check("设置预设不保存本机 Java 与私人额外参数",preset.Settings.JavaPath is null&&preset.Settings.ExtraJvmArgs is null&&!preset.Options.ContainsKey("serverSensitive"));
            new InstanceSettings(MemoryMb:2048,JavaPath:"D:\\keep.exe",ExtraJvmArgs:"-Dkeep=yes").Save(gameRoot,"test");
            File.WriteAllLines(Path.Combine(game,"options.txt"),["music:1","key_key.jump:57","unknownOption:preserve"]);
            await presets.ApplyAsync(preset,gameRoot,"test");var applied=InstanceSettings.Load(gameRoot,"test");var options=SettingsPresets.ReadOptions(Path.Combine(game,"options.txt"));
            Check("预设应用保留目标机器专属配置",applied.MemoryMb==6144&&applied.JavaPath=="D:\\keep.exe"&&applied.ExtraJvmArgs=="-Dkeep=yes");
            Check("预设合并保留未知选项并跳过旧格式按键",options["music"]=="0.4"&&options["unknownOption"]=="preserve"&&options["key_key.jump"]=="57");
            Check("预设应用前生成可恢复的快照",history.List().Any(x=>x.Name.StartsWith("应用预设前")));
            new InstanceMetadata("测试",null,null,"Forge","1.20.1",null,"test").Write(gameRoot,"test");invalid=false;try{await presets.ApplyAsync(preset,gameRoot,"test",true);}catch(InvalidOperationException){invalid=true;}Check("模组配置预设拒绝跨版本跨加载器覆盖",invalid);
            var policy=new ModUpdatePreferences(gameRoot,"test");var candidate=new ModUpdateCandidate("id","old.jar","1","2",true,"",0,"modrinth"){VersionId="v2"};
            policy.Lock("old.jar",true);Check("版本锁定阻止更新提示",!policy.Allows(candidate));policy.Lock("old.jar",false);policy.Skip(candidate);Check("跳过更新仅针对该发布版本",!policy.Allows(candidate)&&policy.Allows(candidate with{VersionId="v3"}));
            var secret="Bearer TOP_SECRET --accessToken SECRET_ARG {\"refreshToken\":\"SECRET_REFRESH\"} D:\\Users\\SensitiveName\\game";var redacted=DiagnosticBundle.Redact(secret);
            Check("诊断脱敏覆盖请求头、启动参数、JSON令牌和用户名",!redacted.Contains("TOP_SECRET")&&!redacted.Contains("SECRET_ARG")&&!redacted.Contains("SECRET_REFRESH")&&!redacted.Contains("SensitiveName"));
            Directory.CreateDirectory(Path.Combine(game,"logs"));File.WriteAllText(Path.Combine(game,"logs","latest.log"),secret);
            var diagnostic=Path.Combine(root,"diagnostic.zip");await DiagnosticBundle.ExportAsync(diagnostic,gameRoot,"test",secret);
            using(var zip=ZipFile.OpenRead(diagnostic))
            {
                var texts=new List<string>();foreach(var entry in zip.Entries){using var reader=new StreamReader(entry.Open());texts.Add(await reader.ReadToEndAsync());}
                Check("诊断包不含账号文件或原始令牌",zip.Entries.All(e=>!e.FullName.Contains("accounts"))&&!string.Join("",texts).Contains("TOP_SECRET"));
            }
            await TasksAsync(Path.Combine(root,"task-data"),gameRoot,Check);
        }
        finally{ExperienceFiles.DeleteTree(root,Path.GetTempPath());}
    }
    private static async Task TasksAsync(string data,string gameRoot,Action<string,bool> check)
    {
        var tasks=new PersistentTasks(data);var record=tasks.Add(new("fixture","固定版本下载",gameRoot,null,new(){{"version","pinned-v1"}}));
        var body=Encoding.UTF8.GetBytes("abcdefghijkl");var target=Path.Combine(gameRoot,"downloads");Directory.CreateDirectory(target);
        using(var client=new HttpClient(new ExperienceHandler(request=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new ExperienceStream(body[..6],()=>tasks.Pause(record.Id)))}))))
        {
            var downloader=new HttpDownloader(client,new DownloadOptions(MaxConcurrency:1,MaxRetries:1));
            tasks.Execute=(_,p,ct)=>downloader.DownloadAsync([new("https://fixture.invalid/file","file.jar",body.Length,Convert.ToHexStringLower(SHA1.HashData(body)),DownloadKind.Library)],target,ct:ct);
            await tasks.RunAsync(record.Id);
        }
        check("暂停真正保留传输中的 part 断点",tasks.List().Single().State==DurableTaskState.Paused&&File.ReadAllBytes(Path.Combine(target,"file.jar.part")).SequenceEqual(body[..6]));
        var resumed=new PersistentTasks(data);var range=-1L;
        using(var client=new HttpClient(new ExperienceHandler(request=>
        {
            range=request.Headers.Range?.Ranges.First().From??-1;var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(body[6..])};response.Content.Headers.ContentRange=new ContentRangeHeaderValue(6,body.Length-1,body.Length);return Task.FromResult(response);
        })))
        {
            var downloader=new HttpDownloader(client,new DownloadOptions(MaxConcurrency:1,MaxRetries:1));
            resumed.Execute=(request,p,ct)=>{check("重启恢复保留固定发布版本",request.Arguments["version"]=="pinned-v1");return downloader.DownloadAsync([new("https://fixture.invalid/file","file.jar",body.Length,Convert.ToHexStringLower(SHA1.HashData(body)),DownloadKind.Library)],target,ct:ct);};
            await resumed.RunAsync(record.Id);
        }
        check("恢复使用 HTTP Range 且内容通过哈希校验",range==6&&resumed.List().Single().State==DurableTaskState.Completed&&File.ReadAllBytes(Path.Combine(target,"file.jar")).SequenceEqual(body));
        var pending=resumed.Add(new("fixture","关闭时暂停",gameRoot,null,new()));
        resumed.Execute=async(_,p,ct)=>{await Task.Delay(Timeout.Infinite,ct);};var running=resumed.RunAsync(pending.Id);await resumed.PauseAndWaitAsync();await running;
        check("关闭流程等待任务暂停并落盘",resumed.ActiveId is null&&new PersistentTasks(data).List().First(x=>x.Id==pending.Id).State==DurableTaskState.Paused);
        resumed.Cancel(pending.Id);check("暂停的任务可以明确取消",resumed.List().First(x=>x.Id==pending.Id).State==DurableTaskState.Canceled);
        var failed=resumed.Add(new("fixture","故障重试",gameRoot,null,new()));resumed.Execute=(_,_,_)=>throw new IOException("fixture failure");await resumed.RunAsync(failed.Id);
        check("任务失败保留重试入口与错误原因",resumed.List().First(x=>x.Id==failed.Id) is {State:DurableTaskState.Failed,CanResume:true});
        IProgress<TaskProgress>? late=null;resumed.Execute=(_,p,_)=>{late=p;return Task.CompletedTask;};await resumed.RunAsync(failed.Id);late!.Report(new(15,"迟到进度"));
        check("完成任务不会被迟到进度改写",resumed.List().First(x=>x.Id==failed.Id) is {State:DurableTaskState.Completed,Percent:100,Detail:"安装完成"});
        File.WriteAllText(Path.Combine(data,"task-center","broken.json"),"{broken");
        check("损坏的可选任务记录不阻止其他任务恢复",new PersistentTasks(data).List().Count==resumed.List().Count);
        var staged=resumed.Add(new("fixture","暂停后清理多处断点",gameRoot,null,new()));
        var javaPart=Path.Combine(data,"java",".cache","fixture.zip.part");var outside=Path.Combine(data,"unrelated.part");string? stagePart=null;
        resumed.Execute=async(_,_,ct)=>{stagePart=Path.Combine(DurableExecution.Current!.Directory,"fixture.part");Directory.CreateDirectory(Path.GetDirectoryName(stagePart)!);Directory.CreateDirectory(Path.GetDirectoryName(javaPart)!);foreach(var file in new[]{stagePart,javaPart,outside}){File.WriteAllText(file,"partial");DurableExecution.TrackPartial(file);}await Task.Delay(Timeout.Infinite,ct);};
        running=resumed.RunAsync(staged.Id);await resumed.PauseAndWaitAsync();await running;resumed.Cancel(staged.Id);
        check("暂停后取消清理任务暂存及受管 Java 断点",!File.Exists(stagePart)&&!File.Exists(javaPart)&&resumed.List().Single(x=>x.Id==staged.Id).Detail.StartsWith("已取消"));
        check("任务取消不删除白名单外的文件",File.Exists(outside));
        check("持久任务不泄漏执行上下文",DurableExecution.Current is null);
    }
    public static byte[] Png(int width,int height)
    {
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        void Chunk(string type,byte[] data)
        {
            var length=new byte[4];BinaryPrimitives.WriteInt32BigEndian(length,data.Length);output.Write(length);var name=Encoding.ASCII.GetBytes(type);output.Write(name);output.Write(data);
            uint crc=0xffffffff;foreach(var b in name.Concat(data)){crc^=b;for(var i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}crc^=0xffffffff;BinaryPrimitives.WriteUInt32BigEndian(length,crc);output.Write(length);
        }
        var header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,width);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);header[8]=8;header[9]=6;Chunk("IHDR",header);
        using var compressed=new MemoryStream();using(var zip=new ZLibStream(compressed,CompressionLevel.Fastest,true))
        for(var y=0;y<height;y++){zip.WriteByte(0);for(var x=0;x<width;x++)zip.Write(new byte[]{(byte)(60+x*2),(byte)(80+y*2),110,255});}
        Chunk("IDAT",compressed.ToArray());Chunk("IEND",[]);return output.ToArray();
    }
}
internal sealed class ExperienceHandler(Func<HttpRequestMessage,Task<HttpResponseMessage>> response):HttpMessageHandler
{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>response(request);}
internal sealed class ExperienceStream(byte[] first,Action pause):Stream
{
    private bool _sent;
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){if(!_sent){_sent=true;first.CopyTo(buffer);return ValueTask.FromResult(first.Length);}pause();ct.ThrowIfCancellationRequested();return ValueTask.FromResult(0);}
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).GetAwaiter().GetResult();public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
