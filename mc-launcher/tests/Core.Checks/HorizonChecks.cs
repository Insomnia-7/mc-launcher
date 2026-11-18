using System.Diagnostics;
using System.Net;
using System.Text;
using Launcher.Application.Auth;
using Launcher.Infrastructure.Horizon;

internal static class HorizonChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check)
    {
        void Check(string name,bool result)=>check(name,result,null);
        async Task Reject(string name,Func<Task> action)
        {try{await action();Check(name,false);}catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException or ArgumentException){Check(name,true);}}
        var root=Path.Combine(Path.GetTempPath(),"fangyu-horizon-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var fixtures=Path.Combine(AppContext.BaseDirectory,"HorizonSigned");
            var sums=await File.ReadAllBytesAsync(Path.Combine(fixtures,"SHA256SUMS"));var sig=await File.ReadAllTextAsync(Path.Combine(fixtures,"SHA256SUMS.v2.sig"));
            const string version="nightly-2026-11-13-9f42064";
            Check("官方实际 Nightly v2 签名有效",HorizonClient.VerifyArchive(version,sums,sig,"horizon-windows-x64.zip")=="f47f9f988b7c30b727dfb35aed2b74f01428925b2eccc9bafff2352f4847f7d6");
            await Reject("其他版本不能重放签名",()=>Task.FromResult(HorizonClient.VerifyArchive("nightly-2026-11-14-9f42064",sums,sig,"horizon-windows-x64.zip")));
            var changed=sums.ToArray();changed[0]^=1;
            await Reject("篡改校验表被拒绝",()=>Task.FromResult(HorizonClient.VerifyArchive(version,changed,sig,"horizon-windows-x64.zip")));
            await Reject("未签名安装包被拒绝",()=>Task.FromResult(HorizonClient.VerifyArchive(version,sums,sig,"unknown.zip")));
            Check("构建名包含日期及提交",HorizonClient.SignedNightlyVersion("Nightly 2026-11-13 · 9f42064")==version);
            await Reject("不明确的 Nightly 名称被拒绝",()=>Task.FromResult(HorizonClient.SignedNightlyVersion("Nightly")));
            using var handler=new OwnerHandler();using var http=new HttpClient(handler);var client=new HorizonClient(http,root);
            var account=new MicrosoftAccount("0123456789abcdef0123456789abcdef","FixturePlayer","fixture-token-not-real","unused",DateTimeOffset.UtcNow.AddHours(1));
            await client.VerifyOwnerAsync(account,default);Check("启动前检查档案和购买权益",handler.Calls==2&&handler.BearerCorrect);
            handler.Owned=false;await Reject("无购买权益不得继续",()=>client.VerifyOwnerAsync(account,default));handler.Owned=true;
            await Reject("身份不匹配不得继续",()=>client.VerifyOwnerAsync(account with{Uuid="different"},default));
            handler.Unauthorized=true;await Reject("失效令牌不得继续",()=>client.VerifyOwnerAsync(account,default));handler.Unauthorized=false;
            var directory=HorizonFixture.CreateBuild(Path.Combine(root,"build"));
            var build=await client.InspectAsync(directory,default);
            Check("读取实际子进程的版本与 Java 要求",build.Minecraft=="26.3"&&build.JavaMajor==25&&build.ClientHash.Length==64);
            var mode=Path.Combine(directory,"fixture-mode");
            File.WriteAllText(mode,"api");await Reject("不兼容的启动 API 被拒绝",()=>client.InspectAsync(directory,default));
            File.WriteAllText(mode,"option");await Reject("缺少标准输入会话能力被拒绝",()=>client.InspectAsync(directory,default));File.Delete(mode);
            var first=new HorizonInstance(Guid.NewGuid().ToString("N"),"相同名称",build);var second=first with{Id=Guid.NewGuid().ToString("N")};
            Check("同名实例的存档目录独立",client.GameDirectory(first)!=client.GameDirectory(second));
            Check("资源按客户端与准备器构建隔离",client.Resources(build)!=client.Resources(build with{PreparerHash=new string('a',64)}));
            await Reject("实例路径不能越界",()=>Task.FromResult(client.GameDirectory(first with{Id="../escape"})));
            Check("资源缺失可识别",(await client.CheckAsync(build,default)).ExitCode==1);
            await Reject("资源未完整时禁止启动",()=>client.StartAsync(first,account,default));
            var progress=new List<string>();await client.PrepareAsync(build,root,Path.Combine(root,"server.jar"),root,new Immediate(progress.Add),default);
            Check("完整准备后重新检查通过",(await client.CheckAsync(build,default)).ExitCode==0);
            Check("解析准备进度及文字",progress.Count==2&&progress.Contains("fixture prepared"));
            File.WriteAllText(mode,"languages");await client.PrepareAsync(build,root,Path.Combine(root,"server.jar"),root,null,default);
            Check("旧 Nightly 缺语言资源时补齐并重新验收",File.Exists(Path.Combine(client.Resources(build),"languages"))&&(await client.CheckAsync(build,default)).ExitCode==0);File.Delete(mode);
            using(var game=await client.StartAsync(first,account,default))Check("会话通过标准输入并正常退出",await game.WaitForExitAsync()==0);
            var gameRoot=client.GameDirectory(first);var argv=File.ReadAllText(Path.Combine(gameRoot,"argv.json"));
            Check("访问令牌不进入进程参数",!argv.Contains(account.AccessToken)&&argv.Contains("--session-stdin")&&!argv.Contains("--session-file"));
            var log=File.ReadAllText(Path.Combine(gameRoot,"fangyu-launch.log"));
            Check("stdout 和 stderr 被记录且令牌脱敏",log.Contains("[redacted]")&&log.Contains("fixture stderr")&&!log.Contains(account.AccessToken));
            Check("子进程临时目录在实例数据所在 D 盘",Path.GetFullPath(File.ReadAllText(Path.Combine(gameRoot,"temp.txt"))).StartsWith(client.Root,StringComparison.OrdinalIgnoreCase));
            Check("会话未持久化到实例",Directory.EnumerateFiles(gameRoot).All(p=>!File.ReadAllText(p).Contains(account.AccessToken)));
            File.WriteAllText(mode,"crash");using(var game=await client.StartAsync(second,account,default))Check("客户端崩溃退出码向上传递",await game.WaitForExitAsync()==101);
            File.WriteAllText(mode,"hang");using(var cts=new CancellationTokenSource())
            {
                var pending=client.PrepareAsync(build,root,"server.jar",root,null,cts.Token);
                var pidFile=Path.Combine(directory,"child.pid");var watch=Stopwatch.StartNew();
                while(!File.Exists(pidFile)&&watch.ElapsedMilliseconds<5000)await Task.Delay(20);
                Check("取消测试确实启动了准备器",File.Exists(pidFile));
                var pid=int.Parse(File.ReadAllText(pidFile));cts.Cancel();
                var cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}
                var alive=false;try{using var process=Process.GetProcessById(pid);alive=!process.HasExited;}catch(ArgumentException){}
                Check("取消准备会终止子进程",cancelled&&!alive);
                Check("取消保留已有完整资源",File.Exists(Path.Combine(client.Resources(build),"ready")));
            }
            File.Delete(mode);
            var managed=Path.Combine(client.Root,"builds","nightly-fixture");Directory.CreateDirectory(managed);
            foreach(var file in new[]{HorizonClient.ClientName,HorizonClient.PreparerName,"research-lock.json"})File.WriteAllText(Path.Combine(managed,file),"fixture");
            Directory.CreateDirectory(Path.Combine(client.Root,"builds","install-incomplete"));
            Check("本地扫描只返回完整构建目录",client.LocalBuildDirectories().SequenceEqual(new[]{managed}));
            var world=Path.Combine(client.GameDirectory(first),"saves","world","level.dat");Directory.CreateDirectory(Path.GetDirectoryName(world)!);File.WriteAllText(world,"keep");
            var importedCache=Path.Combine(directory,".local");Directory.CreateDirectory(importedCache);File.WriteAllText(Path.Combine(importedCache,"keep"),"keep");
            var managedCache=Path.Combine(managed,".local");Directory.CreateDirectory(managedCache);File.WriteAllText(Path.Combine(managedCache,"scratch"),"remove");
            var bytes=client.ClearCache([build,build with{Directory=managed}]);
            Check("缓存清理移除转换资源和受管转换缓存",bytes>0&&!Directory.Exists(client.Resources(build))&&!Directory.Exists(managedCache));
            Check("缓存清理保留客户端和实例存档",File.Exists(Path.Combine(managed,HorizonClient.ClientName))&&File.ReadAllText(world)=="keep");
            Check("缓存清理不删除外部导入构建的文件",File.Exists(Path.Combine(importedCache,"keep")));
            Check("重复清理空缓存不会误删实例",client.ClearCache([build])==0&&File.Exists(world));
            File.AppendAllText(Path.Combine(directory,HorizonClient.ClientName),"changed");
            await Reject("构建内容变化须重新导入",()=>client.ValidateBuildAsync(build,default));
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class Immediate(Action<string> report):IProgress<string>{public void Report(string value)=>report(value);}
    private sealed class OwnerHandler:HttpMessageHandler
    {
        public int Calls;public bool Owned=true,Unauthorized,BearerCorrect=true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;BearerCorrect&=request.Headers.Authorization?.Parameter=="fixture-token-not-real";
            var body=request.RequestUri!.AbsolutePath.EndsWith("profile")?"{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"FixturePlayer\"}":Owned?"{\"items\":[{\"name\":\"game_minecraft\"}]}":"{\"items\":[]}";
            return Task.FromResult(new HttpResponseMessage(Unauthorized?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
}
