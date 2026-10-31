using System.Formats.Tar;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Update;
using Launcher.Domain.Install;
using Launcher.Domain.Java;
using Launcher.Infrastructure.Http;
using Launcher.Infrastructure.Java;
using Launcher.Infrastructure.Security;
using Launcher.Infrastructure.Update;

if(args.FirstOrDefault()=="--fixture-updater")
{
    var spki=File.ReadAllBytes(args[1]);var id=Convert.ToHexStringLower(SHA256.HashData(spki));
    return await LauncherUpdateRunner.RunAsync(args[2..],new UpdateSignatureVerifier(new Dictionary<string,string>{{id,Convert.ToBase64String(spki)}}));
}
if(args.FirstOrDefault()=="--fixture-ready")
{
    for(var i=0;i<100;i++){SingleFileUpdate.AcknowledgeStartup(args[1]);if(File.ReadAllText(Path.Combine(args[1],"update/launcher-update-result.json")).Contains("completed"))return 0;await Task.Delay(50);}return 2;
}
var root=Path.GetFullPath(args.FirstOrDefault(a=>!a.StartsWith("--"))??Path.Combine(Path.GetTempPath(),"fangyu-platform-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);var passed=0;
void Check(string name,bool condition){if(!condition)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
async Task<bool> Reject(Func<Task> action){try{await action();return false;}catch(Exception ex) when(ex is IOException or UpdateManifestException or InvalidOperationException or CryptographicException or OperationCanceledException){return true;}}
var key=RandomNumberGenerator.GetBytes(32);var protector=new SystemKeySecretProtector(_=>key.ToArray(),"test");
var cipher=protector.Protect("test-refresh-token");
Check("系统密钥加密可恢复",protector.TryUnprotect(cipher)=="test-refresh-token");
Check("账号密文不包含明文",!Encoding.UTF8.GetString(cipher).Contains("test-refresh-token"));
Check("同一明文采用不同随机 nonce",!cipher.SequenceEqual(protector.Protect("test-refresh-token")));
cipher[^1]^=1;Check("篡改密文被拒绝",protector.TryUnprotect(cipher) is null);
Check("缺少密钥不会退回明文",new SystemKeySecretProtector(_=>null,"missing").TryUnprotect(protector.Protect("test")) is null);
Check("密钥环锁定时禁止保存",await Reject(()=>Task.Run(()=>new SystemKeySecretProtector(_=>null,"missing").Protect("test"))));
foreach(var os in new[]{"windows","linux","mac"})foreach(var architecture in new[]{"x64","arm64"})
{
    var target=JavaRuntimeTarget.Resolve(os,architecture);var data=Path.Combine(root,os+"-"+architecture);
    byte[] archive;
    var entryName=os=="mac"?"jdk-test/Contents/Home/bin/java":"jdk-test/bin/"+target.Executable;
    using(var memory=new MemoryStream())
    {
        if(os=="windows"){using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){using var writer=new StreamWriter(zip.CreateEntry(entryName).Open());writer.Write("fake java");}}
        else{using(var gzip=new GZipStream(memory,CompressionLevel.Fastest,true)){using var tar=new TarWriter(gzip,leaveOpen:true);var entry=new PaxTarEntry(TarEntryType.RegularFile,entryName){DataStream=new MemoryStream("fake java"u8.ToArray()),Mode=UnixFileMode.UserRead|UnixFileMode.UserExecute};tar.WriteEntry(entry);}}
        archive=memory.ToArray();
    }
    var name="java-test"+target.PackageExtension;var sha=Convert.ToHexStringLower(SHA256.HashData(archive));
    using var http=new HttpClient(new Metadata(JsonSerializer.Serialize(new[]{new{binary=new{package=new{link="https://example.invalid/java",name,checksum=sha,size=archive.Length}},version=new{major=21,openjdk_version="21-test"}}})));
    var downloader=new BytesDownloader(archive);var provisioner=new JavaProvisioner(http,downloader,new ProbeLocator(target.Architecture),target);
    var install=await provisioner.EnsureAsync(21,data);
    Check($"{os}/{architecture} 选择正确 API",target.AssetUrl(21).Contains("os="+os)&&target.AssetUrl(21).Contains("architecture="+(architecture=="arm64"?"aarch64":"x64")));
    Check($"{os}/{architecture} 解压到可发现 Java",File.Exists(install.ExecutablePath)&&Path.GetFullPath(install.ExecutablePath)==Path.GetFullPath(Path.Combine(data,"java","21","bin",target.Executable)));
    Check($"{os}/{architecture} 暂存与压缩包已清理",!Directory.EnumerateDirectories(Path.Combine(data,"java"),"*.staging-*").Any()&&!Directory.EnumerateFiles(Path.Combine(data,"java/.cache")).Any());
    await provisioner.EnsureAsync(21,data);Check($"{os}/{architecture} 复用已安装 Java 不下载",downloader.Calls==1);
    if(os!="windows"&&!OperatingSystem.IsWindows())Check($"{os}/{architecture} Java 保留执行权限",File.GetUnixFileMode(install.ExecutablePath).HasFlag(UnixFileMode.UserExecute));
}
foreach(var runtime in new[]{"win-x64","linux-x64","linux-arm64","osx-x64","osx-arm64"})
{
    var entry=runtime.StartsWith("win")?"Fangyu.exe":runtime.StartsWith("osx")?"MacOS/Fangyu":"Fangyu";
    var paths=runtime.StartsWith("osx")?new[]{entry,"Info.plist","Resources/fangyu.icns","_CodeSignature/CodeResources"}:new[]{entry};
    var manifest=new UpdateManifest("1.0.1",paths.Select(p=>new UpdateFile(p,"https://example.invalid/"+p,new string('a',64),10)).ToArray(),Entry:entry,Runtime:runtime);
    SingleFileUpdate.ValidateManifest(manifest,runtime);Check(runtime+" 使用对应更新布局",true);
    Check(runtime+" 拒绝其他架构的签名包",await Reject(()=>{SingleFileUpdate.ValidateManifest(manifest with{Runtime="wrong"},runtime);return Task.CompletedTask;}));
}
Check("macOS 更新定位整个 Contents",DesktopUpdateLayout.Resolve(Path.Combine(root,"方隅.app/Contents/MacOS/Fangyu"),"osx-arm64").Entry=="MacOS/Fangyu");
Check("开发运行不支持自替换",await Reject(()=>{DesktopUpdateLayout.Resolve(Path.Combine(root,"dotnet"));return Task.CompletedTask;}));
// Verify the same signed staging/transaction code on the actual host, including all macOS resources.
using(var rsa=RSA.Create(3072))
{
    var publicKey=rsa.ExportSubjectPublicKeyInfo();var keyId=Convert.ToHexStringLower(SHA256.HashData(publicKey));var verifier=new UpdateSignatureVerifier(new Dictionary<string,string>{{keyId,Convert.ToBase64String(publicKey)}});
    var rid=DesktopUpdateLayout.CurrentRuntime;var entry=OperatingSystem.IsWindows()?"Fangyu.exe":OperatingSystem.IsMacOS()?"MacOS/Fangyu":"Fangyu";
    var paths=OperatingSystem.IsMacOS()?new[]{entry,"Info.plist","Resources/fangyu.icns","_CodeSignature/CodeResources"}:new[]{entry};var feed=Path.Combine(root,"feed");var target=Path.Combine(root,"target");Directory.CreateDirectory(feed);Directory.CreateDirectory(target);
    var files=new List<UpdateFile>();
    foreach(var path in paths){var source=Path.Combine(feed,path);Directory.CreateDirectory(Path.GetDirectoryName(source)!);File.WriteAllText(source,"new "+path);var destination=Path.Combine(target,path);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllText(destination,"old "+path);files.Add(new(path,path,Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),new FileInfo(source).Length));}
    var document=JsonSerializer.SerializeToUtf8Bytes(new{version="1.0.1",entry,runtime=rid,files=files.Select(f=>new{path=f.Path,url=f.Url,sha256=f.Sha256,size=f.Size})});
    File.WriteAllBytes(Path.Combine(feed,"update.json"),document);File.WriteAllBytes(Path.Combine(feed,"update.json.sig"),JsonSerializer.SerializeToUtf8Bytes(new{schema=1,algorithm=UpdateSignatureVerifier.Algorithm,keyId,signature=Convert.ToBase64String(rsa.SignData(document,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))}));
    using var http=new HttpClient();var client=new UpdateClient(http,verifier);var result=await client.CheckAsync("1.0.0",feed);Check("本机签名更新清单可验证",result.HasUpdate&&result.Manifest!.Runtime==rid);
    var staging=await client.StageAsync(result.Manifest!,feed,target,Path.Combine(root,"data"));var prepared=await SingleFileUpdate.PrepareAsync(staging.Directory,target,entry,verifier);var backup=await SingleFileUpdate.ApplyAsync(prepared);
    Check("更新提交包含全部应用文件",paths.All(p=>File.ReadAllText(Path.Combine(target,p))=="new "+p));Check("更新备份包含全部旧文件",paths.All(p=>File.ReadAllText(Path.Combine(backup,p))=="old "+p));
    if(!OperatingSystem.IsWindows())Check("更新后可执行文件保留执行位",File.GetUnixFileMode(Path.Combine(target,entry)).HasFlag(UnixFileMode.UserExecute));
    File.AppendAllText(Path.Combine(staging.Directory,paths[^1]),"tampered");Check("任一资源篡改时拒绝整包更新",await Reject(()=>SingleFileUpdate.PrepareAsync(staging.Directory,target,entry,verifier)));
    await UpdateFileTransaction.ApplyAsync(prepared.Files,backup,target,Path.Combine(root,"recovery"));Check("全部应用文件可恢复",paths.All(p=>File.ReadAllText(Path.Combine(target,p))=="old "+p));
    if(!OperatingSystem.IsWindows())
    {
        var keyFile=Path.Combine(root,"test-public-key.der");File.WriteAllBytes(keyFile,publicKey);
        foreach(var scenario in new[]{"success","bad-image","tampered"})
        {
            var caseRoot=Path.Combine(root,"native-update-"+scenario);var caseData=Path.Combine(caseRoot,"data");var stage=Path.Combine(caseData,"update/1.0.1");var installed=Path.Combine(caseRoot,"program");Directory.CreateDirectory(stage);Directory.CreateDirectory(installed);
            var entries=new List<UpdateFile>();var dotnet=Environment.ProcessPath!;var assembly=typeof(ProbeLocator).Assembly.Location;
            string Quote(string value)=>"'"+value.Replace("'","'\\''")+"'";
            foreach(var path in paths)
            {
                var source=Path.Combine(stage,path);var destination=Path.Combine(installed,path);Directory.CreateDirectory(Path.GetDirectoryName(source)!);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllText(destination,"old "+path);
                var content=path==entry?(scenario=="bad-image"?"invalid native image":"#!/bin/sh\nexec "+Quote(dotnet)+" "+Quote(assembly)+" --fixture-ready "+Quote(caseData)+"\n"):"new "+path;
                File.WriteAllText(source,content);entries.Add(new(path,path,Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),new FileInfo(source).Length));
            }
            var plan=JsonSerializer.SerializeToUtf8Bytes(new{version="1.0.1",entry,runtime=rid,files=entries.Select(f=>new{path=f.Path,url=f.Url,sha256=f.Sha256,size=f.Size})});
            File.WriteAllBytes(Path.Combine(stage,"update.json"),plan);File.WriteAllBytes(Path.Combine(stage,"update.json.sig"),JsonSerializer.SerializeToUtf8Bytes(new{schema=1,algorithm=UpdateSignatureVerifier.Algorithm,keyId,signature=Convert.ToBase64String(rsa.SignData(plan,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))}));
            if(scenario=="tampered")File.AppendAllText(Path.Combine(stage,entry),"tampered");
            using var parent=Process.Start(new ProcessStartInfo("sleep"){ArgumentList={"60"},UseShellExecute=false})!;
            var start=new ProcessStartInfo(dotnet){UseShellExecute=false};
            foreach(var value in new[]{assembly,"--fixture-updater",keyFile,"--staging",stage,"--target",installed,"--entry",entry,"--wait-pid",parent.Id.ToString()})start.ArgumentList.Add(value);
            using var helper=Process.Start(start)!;var statePath=SingleFileUpdate.ResultPath(stage);var timer=Stopwatch.StartNew();
            try
            {
                while(timer.Elapsed<TimeSpan.FromSeconds(15)&&!helper.HasExited&&(!File.Exists(statePath)||!File.ReadAllText(statePath).Contains("ready")))await Task.Delay(50);
                if(scenario=="tampered")
                {
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Check("原生更新篡改在旧程序退出前拒绝",helper.ExitCode==2&&!parent.HasExited&&File.ReadAllText(Path.Combine(installed,entry))=="old "+entry);
                }
                else
                {
                    Check("原生更新器先校验并就绪："+scenario,File.Exists(statePath)&&File.ReadAllText(statePath).Contains("ready"));parent.Kill();await parent.WaitForExitAsync();await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55));
                    if(scenario=="success")Check("原生更新替换执行并收到新版确认",helper.ExitCode==0&&File.ReadAllText(statePath).Contains("completed")&&paths.All(p=>File.ReadAllText(Path.Combine(installed,p)).StartsWith(p==entry?"#!/bin/sh":"new ")));
                    else Check("原生新版无法执行时恢复全部旧资源",helper.ExitCode==2&&paths.All(p=>File.ReadAllText(Path.Combine(installed,p))=="old "+p));
                }
            }
            finally{if(!parent.HasExited){parent.Kill();await parent.WaitForExitAsync();}if(!helper.HasExited){helper.Kill();await helper.WaitForExitAsync();}}
        }
    }
}
if(args.Contains("--native-credentials"))
{
    var native=SystemKeySecretProtector.TryCreate(Path.Combine(root,"native-key-test"))??throw new Exception("Native keychain unavailable");var payload=native.Protect("isolated-test-account");
    var second=SystemKeySecretProtector.TryCreate(Path.Combine(root,"native-key-test"))!;Check("系统真实凭据存储跨对象恢复",second.TryUnprotect(payload)=="isolated-test-account");
    payload[^1]^=1;Check("系统真实密钥拒绝篡改",second.TryUnprotect(payload) is null);
}
if(args.Contains("--live-java"))
{
    using var http=JavaProvisioner.CreateHttpClient();using var downloads=JavaProvisioner.CreateDownloadHttpClient();var locator=new FreshLocator();
    var java=await new JavaProvisioner(http,new HttpDownloader(downloads),locator).EnsureAsync(21,Path.Combine(root,"live-java"));
    Check("官方 Java 包实际下载解压并执行",(await new JavaLocator().ProbeAsync(java.ExecutablePath))?.MajorVersion==21);
    Check("官方 Java 架构与本机一致",JavaRuntimeTarget.Current.Architecture=="aarch64"?java.Arch is "arm64" or "aarch64":java.Arch=="x64");
}
File.WriteAllText(Path.Combine(root,"platform-checks.json"),JsonSerializer.Serialize(new{runtime=DesktopUpdateLayout.CurrentRuntime,passed,nativeCredentials=args.Contains("--native-credentials"),liveJava=args.Contains("--live-java")}));
Console.WriteLine($"{passed} platform checks passed");return 0;

sealed class Metadata(string json):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});}
sealed class BytesDownloader(byte[] bytes):IDownloader
{
    public int Calls;public async Task<DownloadSummary> DownloadAsync(IReadOnlyList<DownloadEntry> entries,string root,IProgress<DownloadProgress>? progress=null,CancellationToken ct=default){Calls++;Directory.CreateDirectory(root);await File.WriteAllBytesAsync(Path.Combine(root,entries.Single().RelativePath),bytes,ct);return new(1,1,0,bytes.Length,TimeSpan.Zero);}
}
sealed class ProbeLocator(string arch):IJavaLocator
{
    public Task<IReadOnlyList<JavaInstall>> DiscoverAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<JavaInstall>>([]);
    public Task<JavaInstall?> ProbeAsync(string path,CancellationToken ct=default)=>Task.FromResult(File.Exists(path)?new JavaInstall(path,21,"21-test","fixture",arch):null);
}
sealed class FreshLocator:IJavaLocator
{
    public Task<IReadOnlyList<JavaInstall>> DiscoverAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<JavaInstall>>([]);
    public Task<JavaInstall?> ProbeAsync(string path,CancellationToken ct=default)=>new JavaLocator().ProbeAsync(path,ct);
}
