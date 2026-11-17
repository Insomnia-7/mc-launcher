using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Application.Abstractions;
using Launcher.Application.Auth;
using Launcher.Infrastructure.Auth;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Launcher.Infrastructure.Horizon;

public sealed record HorizonBuild(string Directory, string Version, string Minecraft, int JavaMajor, string ClientHash, string PreparerHash)
{
    public string Title => $"{Version} · MC {Minecraft}";
}
public sealed record HorizonInstance(string Id, string Name, HorizonBuild Build);
public sealed record HorizonCommandResult(int ExitCode, string Output, string Error);

/// <summary>Horizon Launch API 0.1: verified builds, isolated resources, and sessions over stdin.</summary>
public sealed partial class HorizonClient(HttpClient http, string dataRoot)
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(dataRoot, "horizon"));
    public static string ClientName => OperatingSystem.IsWindows() ? "horizon-client.exe" : "horizon-client";
    public static string PreparerName => OperatingSystem.IsWindows() ? "horizon-prepare.exe" : "horizon-prepare";
    public string GameDirectory(HorizonInstance instance) => Path.Combine(Root,"instances",SafeId(instance.Id));
    public string Resources(HorizonBuild build) => Path.Combine(Root,"prepared",build.ClientHash[..16]+"-"+build.PreparerHash[..16]);
    private static string SafeId(string id) => Regex.IsMatch(id,"^[a-zA-Z0-9][a-zA-Z0-9_.+-]{0,99}$") && !id.Contains("..") ? id : throw new InvalidDataException("客户端标识无效。");
    public static string SignedNightlyVersion(string title)
    {
        var match=Regex.Match(title,@"^Nightly (\d{4}-\d{2}-\d{2}) · ([a-f0-9]{7,40})$");
        return match.Success?$"nightly-{match.Groups[1].Value}-{match.Groups[2].Value}":throw new InvalidDataException("上游构建缺少可验证的日期与提交标识。");
    }
    public static string VerifyArchive(string version, byte[] sums, string signatures, string archive)
    {
        var key=Convert.FromBase64String("MU4oiIQcCxkZvbwSjjXs7SySCuM6ywRQ3nGaJYZG0gw=");
        var message=Encoding.UTF8.GetBytes($"horizon release {version}\n").Concat(sums).ToArray();
        var valid=false;
        foreach(var line in signatures.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] signature;try{signature=Convert.FromBase64String(line.Trim());}catch(FormatException){continue;}
            if(signature.Length==64 && Ed25519.Verify(signature,0,key,0,message,0,message.Length)){valid=true;break;}
        }
        if(!valid)throw new InvalidDataException("Horizon 官方构建签名验证失败，未安装。");
        var entry=Encoding.UTF8.GetString(sums).Split('\n').Select(line=>line.TrimEnd('\r')).SingleOrDefault(line=>line.EndsWith("  "+archive,StringComparison.Ordinal));
        if(entry is null || !Regex.IsMatch(entry[..Math.Min(64,entry.Length)],"^[a-fA-F0-9]{64}$"))throw new InvalidDataException("签名清单缺少客户端安装包。");
        return entry[..64].ToLowerInvariant();
    }
    public async Task<HorizonBuild> InstallNightlyAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture!=System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("当前自动安装提供 Windows x64 构建；其他设备可导入对应平台的本地构建。");
        progress?.Report("正在获取 Horizon 官方 Nightly…");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/horizon-rust/horizon/releases/tags/nightly");
        request.Headers.UserAgent.ParseAdd("Fangyu-Horizon/1.0");
        using var response=await http.SendAsync(request,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        using var release=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var version=SignedNightlyVersion(release.RootElement.GetProperty("name").GetString()!);
        var target=Path.Combine(Root,"builds",version);
        if(File.Exists(Path.Combine(target,ClientName)))return await InspectAsync(target,ct).ConfigureAwait(false);
        string Url(string name)
        {
            var url=release.RootElement.GetProperty("assets").EnumerateArray().Single(a=>a.GetProperty("name").GetString()==name).GetProperty("browser_download_url").GetString()!;
            if(!url.StartsWith("https://github.com/horizon-rust/horizon/releases/download/",StringComparison.Ordinal))throw new InvalidDataException("上游安装包地址不属于 Horizon 仓库。");
            return url;
        }
        var sums=await http.GetByteArrayAsync(Url("SHA256SUMS"),ct).ConfigureAwait(false);
        var signature=await http.GetStringAsync(Url("SHA256SUMS.v2.sig"),ct).ConfigureAwait(false);
        var expected=VerifyArchive(version,sums,signature,"horizon-windows-x64.zip");
        Directory.CreateDirectory(Path.Combine(Root,"builds"));
        var stage=Path.Combine(Root,"builds","install-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        var zip=stage+".zip";
        try
        {
            progress?.Report("正在下载已签名的 Horizon 客户端…");
            await DownloadFileAsync(Url("horizon-windows-x64.zip"),zip,ct).ConfigureAwait(false);
            if(await HashAsync(zip,ct).ConfigureAwait(false)!=expected)throw new InvalidDataException("客户端安装包校验失败，未安装。");
            progress?.Report("正在解压并检查启动接口…");
            ZipFile.ExtractToDirectory(zip,stage);
            var client=Directory.EnumerateFiles(stage,ClientName,SearchOption.AllDirectories).Single();
            var extracted=Path.GetDirectoryName(client)!;
            await InspectAsync(extracted,ct).ConfigureAwait(false);
            Directory.Move(extracted,target);
            return await InspectAsync(target,ct).ConfigureAwait(false);
        }
        finally
        {
            if(File.Exists(zip))File.Delete(zip);
            // stage is a freshly-created child of our own builds directory, never a user-selected path.
            if(Directory.Exists(stage))Directory.Delete(stage,true);
        }
    }
    public async Task DownloadFileAsync(string url,string path,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var input=await http.GetStreamAsync(url,ct).ConfigureAwait(false);
        await using var output=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None,81920,true);
        await input.CopyToAsync(output,ct).ConfigureAwait(false);
    }
    public static async Task<string> HashAsync(string path,CancellationToken ct)
    {await using var file=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(file,ct).ConfigureAwait(false)).ToLowerInvariant();}
    public async Task<HorizonBuild> InspectAsync(string directory,CancellationToken ct)
    {
        directory=Path.GetFullPath(directory);
        foreach(var name in new[]{ClientName,PreparerName,"research-lock.json"})
            if(!File.Exists(Path.Combine(directory,name)))throw new InvalidDataException($"不是完整的 Horizon 构建：缺少 {name}。");
        if(!Directory.Exists(Path.Combine(directory,"exporters")))throw new InvalidDataException("Horizon 构建缺少 exporters 文件夹。");
        var client=await ApiAsync(directory,ClientName,ct).ConfigureAwait(false);
        var preparer=await ApiAsync(directory,PreparerName,ct).ConfigureAwait(false);
        using(client)using(preparer)
        {
            RequireOptions(client.RootElement,"--resources","--game-dir","--home","--session-stdin");
            RequireOptions(preparer.RootElement,"--prepare","--check","--output","--complete","--progress","--minecraft-dir","--server-jar","--java-home");
            var minecraft=SafeId(preparer.RootElement.GetProperty("minecraft").GetString()!);
            if(client.RootElement.GetProperty("minecraft").GetString()!=minecraft)throw new InvalidDataException("客户端与资源准备器的 Minecraft 版本不一致。");
            return new(directory,client.RootElement.GetProperty("horizon").GetString()!,minecraft,preparer.RootElement.GetProperty("java_major").GetInt32(),await HashAsync(Path.Combine(directory,ClientName),ct).ConfigureAwait(false),await HashAsync(Path.Combine(directory,PreparerName),ct).ConfigureAwait(false));
        }
    }
    private async Task<JsonDocument> ApiAsync(string directory,string program,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result=await RunAsync(directory,program,["--launch-api"],null,timeout.Token).ConfigureAwait(false);
        if(result.ExitCode!=0)throw new InvalidDataException($"{program} 不支持启动接口或无法运行：{result.Error}");
        var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.GetProperty("launch_api").GetString()!.StartsWith("0.1.",StringComparison.Ordinal)){json.Dispose();throw new InvalidDataException("此构建的启动接口版本暂不受支持。");}
        return json;
    }
    private static void RequireOptions(JsonElement api,params string[] required)
    {
        var available=api.GetProperty("options").EnumerateArray().Select(x=>x.GetProperty("name").GetString()).ToHashSet();
        foreach(var option in required)if(!available.Contains(option))throw new InvalidDataException($"此构建不支持 {option}，未启动。");
    }
    public async Task ValidateBuildAsync(HorizonBuild build,CancellationToken ct)
    {
        if(await HashAsync(Path.Combine(build.Directory,ClientName),ct).ConfigureAwait(false)!=build.ClientHash || await HashAsync(Path.Combine(build.Directory,PreparerName),ct).ConfigureAwait(false)!=build.PreparerHash)
            throw new InvalidDataException("Horizon 构建文件已变化，请重新导入或安装后再启动。");
    }
    public async Task VerifyOwnerAsync(MicrosoftAccount account,CancellationToken ct)
    {
        var profile=await new MinecraftProfileClient(http).GetProfileAsync(account.AccessToken,ct).ConfigureAwait(false);
        if(!string.Equals(profile.Uuid.Replace("-",""),account.Uuid.Replace("-",""),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("当前登录身份不一致，请重新登录。");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.minecraftservices.com/entitlements/mcstore");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",account.AccessToken);
        using var response=await http.SendAsync(request,ct).ConfigureAwait(false);response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if(!json.RootElement.GetProperty("items").EnumerateArray().Any(x=>x.GetProperty("name").GetString() is "game_minecraft" or "product_minecraft"))throw new InvalidOperationException("Horizon 需要当前账号拥有 Minecraft: Java Edition。");
    }
    public Task<HorizonCommandResult> CheckAsync(HorizonBuild build,CancellationToken ct) => RunAsync(build.Directory,PreparerName,["--check","--output",Resources(build)],null,ct);
    public async Task PrepareAsync(HorizonBuild build,string minecraftDirectory,string serverJar,string javaHome,IProgress<string>? progress,CancellationToken ct)
    {
        var result=await RunAsync(build.Directory,PreparerName,["--prepare","--minecraft-dir",minecraftDirectory,"--server-jar",serverJar,"--java-home",javaHome,"--output",Resources(build),"--complete","--progress","json"],progress,ct).ConfigureAwait(false);
        if(result.ExitCode!=0)throw new InvalidOperationException("Horizon 资源准备未完成："+result.Error+"\n"+result.Output);
        if((await CheckAsync(build,ct).ConfigureAwait(false)).ExitCode!=0)throw new InvalidDataException("Horizon 资源完整性检查未通过。");
    }
    private ProcessStartInfo StartInfo(string directory,string program,IReadOnlyList<string> arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(directory,program)){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        var temporary=Path.Combine(Root,"temp");Directory.CreateDirectory(temporary);
        foreach(var key in new[]{"TEMP","TMP","TMPDIR"})start.Environment[key]=temporary;
        return start;
    }
    private async Task<HorizonCommandResult> RunAsync(string directory,string program,IReadOnlyList<string> args,IProgress<string>? progress,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();using var process=Process.Start(StartInfo(directory,program,args))??throw new InvalidOperationException("无法启动 Horizon 工具。");
        async Task<string> Read(StreamReader reader)
        {
            var tail=new StringBuilder();
            while(await reader.ReadLineAsync().ConfigureAwait(false) is {} line)
            {
                tail.AppendLine(line);if(tail.Length>65536)tail.Remove(0,tail.Length-65536);
                if(progress is not null)
                {
                    try{using var json=JsonDocument.Parse(line);if(json.RootElement.TryGetProperty("message",out var text))progress.Report(text.GetString()!);else if(json.RootElement.TryGetProperty("fraction",out var fraction))progress.Report($"正在准备资源 · {fraction.GetDouble():P0}");}
                    catch(JsonException){progress.Report(line);}
                }
            }
            return tail.ToString();
        }
        var stdout=Read(process.StandardOutput);var stderr=Read(process.StandardError);
        try{await process.WaitForExitAsync(ct).ConfigureAwait(false);}
        catch{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync().ConfigureAwait(false);await Task.WhenAll(stdout,stderr).ConfigureAwait(false);throw;}
        return new(process.ExitCode,await stdout.ConfigureAwait(false),await stderr.ConfigureAwait(false));
    }
    public async Task<IGameProcess> StartAsync(HorizonInstance instance,MicrosoftAccount account,CancellationToken ct)
    {
        await ValidateBuildAsync(instance.Build,ct).ConfigureAwait(false);
        using(var api=await ApiAsync(instance.Build.Directory,ClientName,ct).ConfigureAwait(false))RequireOptions(api.RootElement,"--resources","--game-dir","--home","--session-stdin");
        if((await CheckAsync(instance.Build,ct).ConfigureAwait(false)).ExitCode!=0)throw new InvalidOperationException("请先准备此构建的游戏资源。");
        var directory=GameDirectory(instance);Directory.CreateDirectory(directory);
        var start=StartInfo(instance.Build.Directory,ClientName,["--resources",Resources(instance.Build),"--game-dir",directory,"--home",directory,"--session-stdin"]);
        start.RedirectStandardInput=true;
        ct.ThrowIfCancellationRequested();var process=Process.Start(start)??throw new InvalidOperationException("无法启动 Horizon。");
        try
        {
            var session=JsonSerializer.Serialize(new{name=account.Name,uuid=account.Uuid,access_token=account.AccessToken,user_type="msa"});
            await process.StandardInput.WriteLineAsync(session.AsMemory(),ct).ConfigureAwait(false);process.StandardInput.Close();
            return new NativeGameProcess(process,Path.Combine(directory,"fangyu-launch.log"),account.AccessToken);
        }
        catch{if(!process.HasExited)process.Kill(true);process.Dispose();throw;}
    }
    private sealed class NativeGameProcess : IGameProcess
    {
        private readonly Process _process;private readonly Task _logs;
        public NativeGameProcess(Process process,string log,string secret)
        {
            _process=process;var writer=TextWriter.Synchronized(new StreamWriter(log,false,new UTF8Encoding(false)));
            async Task Copy(StreamReader reader){while(await reader.ReadLineAsync().ConfigureAwait(false) is {} line)writer.WriteLine(line.Replace(secret,"[redacted]",StringComparison.Ordinal));}
            async Task Drain(){try{await Task.WhenAll(Copy(process.StandardOutput),Copy(process.StandardError)).ConfigureAwait(false);}finally{writer.Dispose();}}
            _logs=Drain();
        }
        public int Id=>_process.Id;public bool HasExited=>_process.HasExited;
        public async Task<int> WaitForExitAsync(CancellationToken ct=default){await _process.WaitForExitAsync(ct).ConfigureAwait(false);await _logs.ConfigureAwait(false);return _process.ExitCode;}
        public void Kill(){if(!_process.HasExited)_process.Kill(true);}
        public void Dispose()=>_process.Dispose();
    }
}
