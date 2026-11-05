using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Experience;
using Launcher.Application.Multiplayer;
using Launcher.Domain.Launch;
using Launcher.Domain.Versions;
using Launcher.Infrastructure.Multiplayer;

internal static class MultiplayerChecks
{
    public static async Task RunAsync(Action<string,bool,string?> check,bool native=false,bool publicPeer=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"fangyu-multiplayer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Check(string name,bool result)=>check(name,result,null);
        async Task<bool> Reject(Func<Task> action){try{await action();return false;}catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or OperationCanceledException){return true;}}
        try
        {
            Check("服务器地址支持默认端口、域名和 IPv6",ServerAddress.Parse("example.org").Port==25565&&ServerAddress.Parse("[::1]:25566").Endpoint=="[::1]:25566");
            foreach(var invalid in new[]{"host:0","host:65536","host:abc","--server anything","https://example.org","host\narg"})
                Check("拒绝无效服务器地址 "+invalid.Replace('\n',' '),await Reject(()=>{ServerAddress.Parse(invalid);return Task.CompletedTask;}));
            var room=RoomInvitation.Create("朋友的世界",25566,"tcp://public.easytier.top:11010","1.21.4","Fabric");
            Check("房间邀请完整往返并使用随机网络密钥",RoomInvitation.Decode(room.Encode())==room&&RoomInvitation.Create("另一房间",25566,room.Peer,null,null).Secret!=room.Secret);
            foreach(var invalid in new[]{"123456",room.Encode()[..^5],(room with{Expires=DateTimeOffset.UtcNow.AddMinutes(-1)}).Encode(),(room with{Peer="tcp://user:pass@example.org:22"}).Encode(),(room with{GamePort=0}).Encode()})
                Check("无效或过期邀请不会启动连接",await Reject(()=>{RoomInvitation.Decode(invalid);return Task.CompletedTask;}));
            var config=EasyTierRoomNetwork.BuildConfiguration(room,false,25567,25568);
            Check("房间默认免网卡、不改 DNS、不是出口节点且仅监听本机游戏入口",config.Contains("no_tun = true")&&config.Contains("accept_dns = false")&&config.Contains("enable_exit_node = false")&&config.Contains("bind_addr = \"127.0.0.1:25567\""));
            var udpPort=Port();var discovery=new LanWorldDiscovery(udpPort);var scan=discovery.DiscoverAsync(TimeSpan.FromMilliseconds(350));
            await Task.Delay(60);using(var sender=new UdpClient())
            {
                foreach(var text in new[]{"garbage","[MOTD]§a旧世界[/MOTD][AD]25566[/AD]","[MOTD]新世界[/MOTD][AD]25566[/AD]"})await sender.SendAsync(Encoding.UTF8.GetBytes(text),new IPEndPoint(IPAddress.Loopback,udpPort));
            }
            var found=await scan;Check("真实 UDP 接收、去重并识别本机世界",found.Count==1&&found[0].CleanMotd=="新世界"&&found[0].IsLocal);
            using(var canceled=new CancellationTokenSource()){canceled.Cancel();Check("取消 LAN 扫描返回取消而不是旧结果",await Reject(()=>discovery.DiscoverAsync(TimeSpan.FromSeconds(2),canceled.Token)));}
            await using var game=new TestGameServer();var status=new GameServerStatusClient();var result=await status.QueryAsync(new("127.0.0.1",game.Port));
            Check("真实 Minecraft 状态协议读取版本、人数及文本组件",result.Version=="1.21.4"&&result.Online==2&&result.Maximum==8&&result.Description=="测试世界");
            await using(var malformed=new TestGameServer("[]"))Check("非对象状态响应安全拒绝",await Reject(()=>status.QueryAsync(new("127.0.0.1",malformed.Port))));
            await using(var stalled=new TestGameServer(null))using(var cancel=new CancellationTokenSource(100))Check("游戏连接检测可取消",await Reject(()=>status.QueryAsync(new("127.0.0.1",stalled.Port),cancel.Token)));

            var platform=new PlatformContext("windows","x86_64","10");var launch=new LaunchContext("Steve",LaunchContext.OfflineUuid("Steve"),null,"legacy","Fangyu","1",root,root,root,"client.jar",1024,null,"--server old --port 1",1000,600,false){MultiplayerServer=new("127.0.0.1",25566)};
            var old=new VersionJson{Id="1.19.4",MainClass="Main",MinecraftArguments="--username ${auth_player_name}"};
            var args=LaunchPlanBuilder.Build(old,platform,launch,"java").GameArguments;
            Check("旧游戏加入参数只覆盖一次连接且不写实例配置",args.Count(a=>a=="--server")==1&&args.Contains("25566")&&!args.Contains("old"));
            var modern=new VersionJson{Id="1.21.4",MainClass="Main",Arguments=new(){Game=["--quickPlayMultiplayer",new ArgumentValue{Value="unused",Rules=[new Rule{Action="allow",Features=new(){IsQuickPlayMultiplayer=true}}]}]}};
            var modernArgs=LaunchPlanBuilder.Build(modern,platform,launch,"java").GameArguments;
            Check("现代版本使用 Quick Play 连接参数",modernArgs.Contains("--quickPlayMultiplayer")&&modernArgs.Contains("127.0.0.1:25566")&&!modernArgs.Contains("--server"));

            var bytes=Zip();var package=new EasyTierPackage(EasyTierRoomNetwork.Runtime,"https://example.invalid/engine.zip",bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var http=new HttpClient(new BytesHandler(bytes));using var engine=new EasyTierRoomNetwork(http,Path.Combine(root,"install"),package);
            await engine.InstallAsync();Check("组件安装只提取 core，不安装驱动或其他程序",engine.IsInstalled&&Directory.GetFiles(Path.GetDirectoryName(engine.Executable)!).Length==2&&!File.Exists(Path.Combine(root,"outside.txt")));
            await engine.VerifyInstalledAsync();File.AppendAllText(engine.Executable,"tamper");Check("启动前发现组件文件篡改",await Reject(()=>engine.VerifyInstalledAsync()));
            await engine.InstallAsync();var original=File.ReadAllBytes(engine.Executable);
            using(var blockingHttp=new HttpClient(new BytesHandler(bytes,true)))using(var canceledEngine=new EasyTierRoomNetwork(blockingHttp,Path.Combine(root,"install"),package))using(var cancel=new CancellationTokenSource(100))
                Check("取消组件下载保留已装组件并删除临时文件",await Reject(()=>canceledEngine.InstallAsync(ct:cancel.Token))&&File.ReadAllBytes(engine.Executable).SequenceEqual(original)&&!Directory.GetFiles(Path.GetDirectoryName(engine.Executable)!,"*.part").Any());
            var packages=EasyTierRoomNetwork.Packages();Check("六种平台组件固定官方 URL、版本和 SHA-256",packages.Count==6&&packages.All(p=>p.Url.StartsWith("https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/")&&p.Sha256.Length==64&&p.Size>0));
            if(native)await NativeAsync(root,game,status,Check,publicPeer);
        }
        finally{ExperienceFiles.DeleteTree(root,Path.GetTempPath());}
    }
    private static int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private static byte[] Zip()
    {
        using var output=new MemoryStream();using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            foreach(var name in new[]{OperatingSystem.IsWindows()?"folder/easytier-core.exe":"folder/easytier-core","folder/wintun.dll","../../outside.txt"})
                using(var writer=new StreamWriter(zip.CreateEntry(name).Open()))writer.Write("fixture");
        }
        return output.ToArray();
    }
    private sealed class BytesHandler(byte[] bytes,bool block=false):HttpMessageHandler
    {protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){if(block)await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}}

    private static async Task NativeAsync(string root,TestGameServer game,GameServerStatusClient status,Action<string,bool> check,bool publicPeer)
    {
        var archive=Environment.GetEnvironmentVariable("FANGYU_EASYTIER_ARCHIVE")??throw new InvalidOperationException("Set FANGYU_EASYTIER_ARCHIVE to the verified official ZIP.");
        using var http=new HttpClient(new BytesHandler([]));
        EasyTierRoomNetwork Seed(string name)
        {
            var network=new EasyTierRoomNetwork(http,Path.Combine(root,name));Directory.CreateDirectory(Path.GetDirectoryName(network.Executable)!);
            File.Copy(archive,Path.Combine(Path.GetDirectoryName(network.Executable)!,"upstream.zip"));using var zip=ZipFile.OpenRead(archive);
            using var input=zip.Entries.Single(e=>e.Name==Path.GetFileName(network.Executable)).Open();using var output=File.Create(network.Executable);input.CopyTo(output);return network;
        }
        using var host=Seed("host");using var guest=Seed("guest");using var rejected=Seed("rejected");
        await host.VerifyInstalledAsync();Process? relay=null;
        try
        {
            var peer=Environment.GetEnvironmentVariable("FANGYU_EASYTIER_PEER")??"tcp://public.easytier.top:11010";
            if(!publicPeer)
            {
                var physical=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.GetIPProperties().GatewayAddresses.Count>0).SelectMany(n=>n.GetIPProperties().UnicastAddresses).First(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address)).Address;
                var port=Port();var dir=Path.Combine(root,"relay");Directory.CreateDirectory(dir);var path=Path.Combine(dir,"relay.toml");
                File.WriteAllText(path,$"listeners = [\"tcp://0.0.0.0:{port}\"]\nrpc_portal = \"127.0.0.1:{Port()}\"\nhostname = \"FangyuTestRelay\"\n[network_identity]\nnetwork_name = \"relay-{Guid.NewGuid():N}\"\nnetwork_secret = \"test-only\"\n[flags]\nno_tun = true\nrelay_network_whitelist = \"*\"\n");
                var start=new ProcessStartInfo(host.Executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=dir,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{"-c",path,"--console-log-level","error","--file-log-level","off"})start.ArgumentList.Add(arg);
                relay=Process.Start(start)!;relay.OutputDataReceived+=(_,_)=>{};relay.ErrorDataReceived+=(_,_)=>{};relay.BeginOutputReadLine();relay.BeginErrorReadLine();
                await Task.Delay(600);peer=$"tcp://{physical}:{port}";
            }
            var invitation=RoomInvitation.Create("Native test",game.Port,peer,"1.21.4",null);
            await host.StartAsync(invitation,true);var joined=await guest.StartAsync(invitation,false);
            GameServerStatus? connected=null;var clock=Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromSeconds(publicPeer?60:25))
            {try{connected=await status.QueryAsync(joined.Address);break;}catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){await Task.Delay(300);}}
            check(publicPeer?"官方公共节点下真实双节点游戏状态互通":"官方组件免 TUN 三节点真实游戏协议互通",connected?.Version=="1.21.4"&&connected.Online==2);
            if(connected is null)throw new InvalidOperationException("Native EasyTier game forwarding did not become ready.");
            check("加入者使用仅本机可访问的连接端口",joined.Address.Host=="127.0.0.1"&&joined.Address.Port!=game.Port);
            var wrong=invitation with{Secret=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))};var unauthorized=await rejected.StartAsync(wrong,false);
            var refused=false;try{await status.QueryAsync(unauthorized.Address);}catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException){refused=true;}
            check("不同房间密钥不能访问房主游戏",refused);rejected.Stop();
            guest.Stop();host.Stop();check("停止房间后进程会话和临时密钥配置已清理",host.Session is null&&guest.Session is null&&!Directory.EnumerateFiles(root,"room.toml",SearchOption.AllDirectories).Any());
            using var canceled=new CancellationTokenSource(10);var wasCanceled=false;try{await guest.StartAsync(invitation,false,canceled.Token);}catch(OperationCanceledException){wasCanceled=true;}
            check("取消原生组件启动不会残留会话",wasCanceled&&guest.Session is null&&!Directory.EnumerateFiles(root,"room.toml",SearchOption.AllDirectories).Any());
        }
        finally{guest.Stop();host.Stop();rejected.Stop();if(relay is not null){if(!relay.HasExited){relay.Kill(true);await relay.WaitForExitAsync();}relay.Dispose();}}
    }
    private sealed class TestGameServer : IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Any,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly Task _loop;
        private readonly string? _json;
        public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
        public TestGameServer(string? json="{\"version\":{\"name\":\"1.21.4\",\"protocol\":769},\"players\":{\"online\":2,\"max\":8},\"description\":{\"text\":\"§a测试\",\"extra\":[{\"text\":\"世界\"}]}}")
        {_json=json;_listener.Start();_loop=Run();}
        private async Task Run()
        {
            try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_=Respond(client);}}
            catch(Exception ex)when(ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        }
        private async Task Respond(TcpClient client)
        {
            using(client)try
            {
                var stream=client.GetStream();var length=await ReadInt(stream);if(length is <1 or >1000)return;var handshake=new byte[length];await stream.ReadExactlyAsync(handshake,_stop.Token);
                var request=new byte[2];await stream.ReadExactlyAsync(request,_stop.Token);
                if(_json is null){await Task.Delay(Timeout.Infinite,_stop.Token);return;}
                var json=Encoding.UTF8.GetBytes(_json);using var payload=new MemoryStream();WriteInt(payload,0);WriteInt(payload,json.Length);payload.Write(json);using var packet=new MemoryStream();WriteInt(packet,(int)payload.Length);payload.Position=0;payload.CopyTo(packet);await stream.WriteAsync(packet.ToArray(),_stop.Token);
            }
            catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
        }
        private async Task<int> ReadInt(Stream stream){var value=0;var b=new byte[1];for(var i=0;i<5;i++){await stream.ReadExactlyAsync(b,_stop.Token);value|=(b[0]&127)<<(i*7);if(b[0]<128)return value;}throw new IOException();}
        private static void WriteInt(Stream stream,int value){var v=(uint)value;do{var b=(byte)(v&127);v>>=7;if(v>0)b|=128;stream.WriteByte(b);}while(v>0);}
        public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _loop;_stop.Dispose();}
    }
}
