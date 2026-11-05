using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Application.Experience;
using Launcher.Application.Multiplayer;
using Launcher.Domain.Launch;

namespace Launcher.Infrastructure.Multiplayer;

public sealed record EasyTierPackage(string Runtime,string Url,long Size,string Sha256);

/// <summary>Optional, pinned upstream executable. No TUN, driver, service, DNS or system route changes.</summary>
public sealed class EasyTierRoomNetwork(HttpClient http,string dataDirectory,EasyTierPackage? packageOverride=null):IRoomNetwork
{
    public const string EngineVersion="2.6.4";
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _sync=new();
    private Process? _process;
    private string? _sessionDirectory;
    private RoomSession? _session;
    private bool _disposed;
    public event Action? Changed;
    public RoomSession? Session {get{lock(_sync)return _session;}}
    private EasyTierPackage Package=>packageOverride??Packages().SingleOrDefault(p=>p.Runtime==Runtime)??throw new PlatformNotSupportedException("此系统架构暂无联机组件。");
    public static string Runtime=>(OperatingSystem.IsWindows()?"win":OperatingSystem.IsMacOS()?"osx":"linux")+"-"+System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    private string EngineName=>OperatingSystem.IsWindows()?"easytier-core.exe":"easytier-core";
    private string Root=>Path.Combine(dataDirectory,"multiplayer","engines",EngineVersion,Runtime);
    public string Executable=>Path.Combine(Root,EngineName);
    private string Archive=>Path.Combine(Root,"upstream.zip");
    public bool IsInstalled=>File.Exists(Executable)&&File.Exists(Archive);
    public string DownloadDescription=>$"EasyTier {EngineVersion} · 首次下载约 {Package.Size/1048576d:F1} MiB · 免虚拟网卡";
    public static IReadOnlyList<EasyTierPackage> Packages()
    {
        using var stream=typeof(EasyTierRoomNetwork).Assembly.GetManifestResourceStream("Launcher.EasyTierPackages.json")!;
        return JsonSerializer.Deserialize<EasyTierPackage[]>(stream)??[];
    }
    public async Task InstallAsync(IProgress<double>? progress=null,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(Session is not null)throw new InvalidOperationException("请先断开房间，再安装联机组件。");
            var package=Package;ExperienceFiles.RejectLinks(Root);Directory.CreateDirectory(Root);
            var temporary=Archive+".part";
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(10));
                using var response=await http.GetAsync(package.Url,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);response.EnsureSuccessStatusCode();
                await using(var input=await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
                await using(var output=File.Create(temporary))
                {
                    var buffer=new byte[65536];long total=0;int count;var report=Stopwatch.StartNew();
                    while((count=await input.ReadAsync(buffer,deadline.Token).ConfigureAwait(false))>0)
                    {
                        total+=count;if(total>package.Size)throw new InvalidDataException("联机组件下载大小不符。");
                        await output.WriteAsync(buffer.AsMemory(0,count),deadline.Token).ConfigureAwait(false);
                        if(report.ElapsedMilliseconds>200){progress?.Report(total*100d/package.Size);report.Restart();}
                    }
                }
                if(!await MatchesAsync(temporary,package.Size,package.Sha256,deadline.Token).ConfigureAwait(false))throw new InvalidDataException("联机组件校验失败，请重试。");
                using(var zip=ZipFile.OpenRead(temporary))
                {
                    var entry=CoreEntry(zip);await using var input=entry.Open();await using var output=File.Create(Executable+".part");
                    await input.CopyToAsync(output,deadline.Token).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temporary,Archive,true);File.Move(Executable+".part",Executable,true);
                if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Executable,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
                progress?.Report(100);Changed?.Invoke();
            }
            finally{File.Delete(temporary);File.Delete(Executable+".part");}
        }
        finally{_gate.Release();}
    }
    private ZipArchiveEntry CoreEntry(ZipArchive archive)=>archive.Entries.Single(e=>e.Name==EngineName);
    public async Task VerifyInstalledAsync(CancellationToken ct=default)
    {
        ExperienceFiles.RejectLinks(Executable);ExperienceFiles.RejectLinks(Archive);
        if(!IsInstalled||!await MatchesAsync(Archive,Package.Size,Package.Sha256,ct).ConfigureAwait(false))throw new InvalidDataException("联机组件缺失或损坏，请重新下载。");
        using var zip=ZipFile.OpenRead(Archive);await using var expected=CoreEntry(zip).Open();await using var current=File.OpenRead(Executable);
        var expectedHash=await SHA256.HashDataAsync(expected,ct).ConfigureAwait(false);var currentHash=await SHA256.HashDataAsync(current,ct).ConfigureAwait(false);
        if(!CryptographicOperations.FixedTimeEquals(expectedHash,currentHash))throw new InvalidDataException("联机组件已变化，请重新下载。");
    }
    public async Task<RoomSession> StartAsync(RoomInvitation invitation,bool host,CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);invitation.Validate();if(Session is not null)throw new InvalidOperationException("请先断开当前房间。");
            await VerifyInstalledAsync(ct).ConfigureAwait(false);
            var peer=RoomInvitation.ValidatePeer(invitation.Peer);
            try{using var lookup=CancellationTokenSource.CreateLinkedTokenSource(ct);lookup.CancelAfter(TimeSpan.FromSeconds(5));await Dns.GetHostAddressesAsync(peer.DnsSafeHost,lookup.Token).ConfigureAwait(false);}
            catch(System.Net.Sockets.SocketException ex){throw new IOException("节点地址无法解析，请房主检查或更换节点后重新创建邀请。",ex);}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IOException("节点地址解析超时，请检查网络或更换节点。");}
            var port=host?invitation.GamePort:FreePort();var rpc=FreePort();
            var directory=Path.Combine(dataDirectory,"multiplayer","sessions",Guid.NewGuid().ToString("N"));ExperienceFiles.RejectLinks(directory);Directory.CreateDirectory(directory);
            _sessionDirectory=directory;
            var config=Path.Combine(directory,"room.toml");await File.WriteAllTextAsync(config,BuildConfiguration(invitation,host,port,rpc),new UTF8Encoding(false),ct).ConfigureAwait(false);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(config,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("ET_",StringComparison.OrdinalIgnoreCase)||k=="RUST_LOG").ToArray())start.Environment.Remove(key);
            foreach(var argument in new[]{"-c",config,"--console-log-level","error","--file-log-level","off"})start.ArgumentList.Add(argument);
            start.Environment["TEMP"]=Path.Combine(directory,"temp");start.Environment["TMP"]=start.Environment["TEMP"];start.Environment["TMPDIR"]=start.Environment["TEMP"];Directory.CreateDirectory(start.Environment["TEMP"]!);
            var process=new Process{StartInfo=start};
            // Drain without persisting upstream output: warnings may contain local machine information.
            process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};
            lock(_sync){_process=process;}
            process.Exited+=(_,_)=>Ended(process);
            if(!process.Start())throw new IOException("联机组件启动失败。");
            process.EnableRaisingEvents=true;process.BeginOutputReadLine();process.BeginErrorReadLine();
            await Task.Delay(700,ct).ConfigureAwait(false);
            if(process.HasExited)throw new IOException("联机组件未能启动，请重新下载或检查端口占用。");
            var session=new RoomSession(invitation,host,new("127.0.0.1",port));
            lock(_sync){if(_process!=process||process.HasExited)throw new IOException("联机组件已退出。");_session=session;}
            Changed?.Invoke();return session;
        }
        catch{Stop();throw;}
        finally{_gate.Release();}
    }
    public static string BuildConfiguration(RoomInvitation invitation,bool host,int localPort,int rpcPort)
    {
        invitation.Validate();if(localPort is <1 or >65535||rpcPort is <1 or >65535)throw new ArgumentException("联机端口无效。");
        string Q(string value)=>JsonSerializer.Serialize(value);
        return $"instance_name = {Q("fangyu-"+Guid.NewGuid().ToString("N"))}\nhostname = {Q(host?"FangyuHost":"FangyuGuest")}\n"+
            (host?$"ipv4 = {Q(RoomInvitation.HostAddress)}\n":"dhcp = true\n")+
            $"rpc_portal = \"127.0.0.1:{rpcPort}\"\nlisteners = [\"tcp://0.0.0.0:0\",\"udp://0.0.0.0:0\"]\n"+
            $"[[peer]]\nuri = {Q(invitation.Peer)}\n"+
            (host?"":$"[[port_forward]]\nbind_addr = \"127.0.0.1:{localPort}\"\ndst_addr = \"{RoomInvitation.HostAddress}:{invitation.GamePort}\"\nproto = \"tcp\"\n")+
            $"[network_identity]\nnetwork_name = {Q(invitation.Network)}\nnetwork_secret = {Q(invitation.Secret)}\n"+
            "[flags]\nno_tun = true\nuse_smoltcp = true\nenable_exit_node = false\naccept_dns = false\n"+
            $"tcp_whitelist = [{(host?invitation.GamePort:0)}]\nudp_whitelist = [0]\nrelay_network_whitelist = \"\"\n";
    }
    private void Ended(Process process)
    {
        lock(_sync){if(_process!=process)return;_process=null;_session=null;}
        CleanupSession();Changed?.Invoke();
    }
    public void Stop()
    {
        Process? process;lock(_sync){process=_process;_process=null;_session=null;}
        if(process is not null){try{if(!process.HasExited){process.Kill(true);process.WaitForExit(3000);}}catch(InvalidOperationException){}finally{process.Dispose();}}
        CleanupSession();Changed?.Invoke();
    }
    private void CleanupSession()
    {
        var directory=Interlocked.Exchange(ref _sessionDirectory,null);if(directory is null)return;
        try{ExperienceFiles.DeleteTree(directory,Path.Combine(dataDirectory,"multiplayer","sessions"));}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
    }
    private static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{return((IPEndPoint)listener.LocalEndpoint).Port;}finally{listener.Stop();}}
    private static async Task<bool> MatchesAsync(string path,long size,string sha,CancellationToken ct)
    {if(!File.Exists(path)||new FileInfo(path).Length!=size)return false;await using var input=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct).ConfigureAwait(false))==sha;}
    public void Dispose(){_disposed=true;Stop();}
}
