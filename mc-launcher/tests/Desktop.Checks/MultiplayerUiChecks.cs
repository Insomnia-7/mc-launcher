using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Multiplayer;
using Launcher.Domain.Launch;

internal sealed class MultiplayerFixture : ILanWorldDiscovery,IGameServerStatusClient,IRoomNetwork
{
    public bool IsInstalled {get;private set;}
    public string DownloadDescription=>"EasyTier 2.6.4 · 首次下载约 31.1 MiB · 免虚拟网卡";
    public RoomSession? Session {get;private set;}
    public event Action? Changed;
    public int Scans,Starts,Queries;
    public bool BlockScan,FailQuery;
    public async Task<IReadOnlyList<LanWorld>> DiscoverAsync(TimeSpan window,CancellationToken cancellationToken=default)
    {
        Scans++;if(BlockScan)await Task.Delay(Timeout.Infinite,cancellationToken);
        return [new("朋友的生存世界","192.168.1.8",25565,DateTimeOffset.UtcNow),new("本机的溪流草甸","127.0.0.1",51234,DateTimeOffset.UtcNow){IsLocal=true}];
    }
    public Task<GameServerStatus> QueryAsync(ServerAddress address,CancellationToken ct=default)
    {Queries++;if(FailQuery)throw new IOException("测试世界已关闭");return Task.FromResult(new GameServerStatus("1.21.4",2,8,"溪流草甸",16));}
    public Task InstallAsync(IProgress<double>? progress=null,CancellationToken ct=default){IsInstalled=true;progress?.Report(100);Changed?.Invoke();return Task.CompletedTask;}
    public Task<RoomSession> StartAsync(RoomInvitation invitation,bool host,CancellationToken ct=default)
    {Starts++;Session=new(invitation,host,new("127.0.0.1",host?invitation.GamePort:30000));Changed?.Invoke();return Task.FromResult(Session);}
    public void Stop(){Session=null;Changed?.Invoke();}
    public void Dispose()=>Stop();
}

internal static class MultiplayerUiChecks
{
    public static void Run(MainViewModel model,Window window,MultiplayerFixture fixture,TestLauncher launcher,Action<int> pump,Action<string,bool> check,Action<string,bool> capture)
    {
        T Find<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        void Wait(Task task){var clock=Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<7000)pump(10);if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();pump(60);}
        var vm=model.Multiplayer;string copied="";vm.CopyText=text=>{copied=text;return Task.CompletedTask;};
        check("首页不扫描局域网、不下载或启动联机组件",fixture.Scans==0&&fixture.Starts==0&&fixture.Queries==0&&!fixture.IsInstalled);
        model.OpenMultiplayerCommand.Execute(null);pump(100);
        check("联机导航独立成页且无弹窗",model.IsMultiplayerPage&&!model.HasOverlay&&Find<Button>("ScanLanButton").IsEffectivelyVisible);
        Wait(vm.ScanCommand.ExecuteAsync(null));
        check("扫描展示本机和其他世界",vm.Worlds.Count==2&&vm.HasWorlds);
        capture("multiplayer-lan-light",true);
        vm.SelectWorldCommand.Execute(vm.Worlds[0]);Wait(vm.CheckConnectionCommand.ExecuteAsync(null));
        check("选择世界填入真实地址并显示游戏状态",vm.ConnectAddress=="192.168.1.8:25565"&&vm.ConnectionHint.Contains("2/8"));
        vm.ShareWorldCommand.Execute(vm.Worlds[0]);check("不能把远程世界当成本机房间共享",vm.IsLanTab);
        vm.ShareWorldCommand.Execute(vm.Worlds[1]);vm.PeerAddress="tcp://example.org:11010";check("共享本机世界填写实际随机游戏端口",vm.IsRoomTab&&vm.HostPort=="51234");
        Wait(vm.CreateRoomCommand.ExecuteAsync(null));check("未装组件时给出引导且不启动引擎",fixture.Starts==0&&vm.Hint.Contains("下载联机组件"));
        capture("multiplayer-room-setup",true);
        Wait(vm.InstallEngineCommand.ExecuteAsync(null));check("组件只由用户操作安装",vm.EngineInstalled);
        Wait(vm.CreateRoomCommand.ExecuteAsync(null));check("房主生成有期限的私密邀请",vm.IsHost&&RoomInvitation.Decode(vm.InvitationText).GamePort==51234);
        var invitation=vm.InvitationText;
        Wait(vm.CopyInvitationCommand.ExecuteAsync(null));check("复制邀请使用完整信息",copied==invitation);
        capture("multiplayer-room-host",true);
        var closed=false;model.ConfirmMultiplayerClose(()=>closed=true);check("退出活动房间会确认",model.IsConfirmOpen&&!closed);
        model.ConfirmCancelCommand.Execute(null);check("取消退出保留房间",vm.HasSession&&!closed);
        vm.DisconnectCommand.Execute(null);pump(50);check("断开房间清除邀请和会话",!vm.HasSession&&vm.InvitationText.Length==0);
        vm.InviteToJoin="invalid";Wait(vm.JoinRoomCommand.ExecuteAsync(null));check("无效邀请不会启动组件",fixture.Starts==1&&vm.Hint.Contains("无效"));
        vm.InviteToJoin=invitation;Wait(vm.JoinRoomCommand.ExecuteAsync(null));
        check("加入者获得本地游戏入口且状态来自游戏探测",vm.HasSession&&!vm.IsHost&&vm.ConnectAddress=="127.0.0.1:30000"&&vm.ConnectionHint.Contains("游戏可连接"));
        capture("multiplayer-room-guest-light",true);
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="aurora");capture("multiplayer-room-guest-dark",true);
        window.Width=900;window.Height=640;pump(80);capture("multiplayer-compact",true);
        check("最小窗口房间卡片没有横向溢出",Find<Button>("DisconnectRoomButton").TranslatePoint(new Avalonia.Point(),window) is {} point&&point.X>=0&&point.X+Find<Button>("DisconnectRoomButton").Bounds.Width<=window.ClientSize.Width);
        window.Width=1180;window.Height=800;model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
        vm.DisconnectCommand.Execute(null);pump(50);vm.ConnectAddress="192.168.1.8:25565";
        Wait(vm.LaunchGameCommand.ExecuteAsync(null));check("启动并连接传递一次性游戏地址",launcher.Starts==1&&launcher.Plan!.GameArguments.Contains("192.168.1.8"));
        pump(2200);Wait(model.LaunchCommand.ExecuteAsync(null));check("后续普通启动不会保留联机地址",launcher.Starts==2&&!launcher.Plan!.GameArguments.Contains("--server"));
        fixture.FailQuery=true;Wait(vm.CheckConnectionCommand.ExecuteAsync(null));check("连接失败替换检测中提示",vm.ConnectionHint.Contains("已关闭"));fixture.FailQuery=false;
        vm.ShowLanCommand.Execute(null);fixture.BlockScan=true;var scanning=vm.ScanCommand.ExecuteAsync(null);pump(40);vm.CancelCommand.Execute(null);Wait(scanning);
        check("取消扫描恢复页面交互",!vm.IsBusy&&vm.Hint.Contains("取消"));
        model.ShowInstancesCommand.Execute(null);pump(60);check("主导航可从联机返回游戏库",model.IsLibraryPage&&!model.IsMultiplayerOpen);
        vm.Dispose();
    }
}
