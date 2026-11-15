using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Multiplayer;
using Launcher.Domain.Launch;

namespace Launcher.App.ViewModels;

public sealed partial class MultiplayerViewModel : ObservableObject,IDisposable
{
    private readonly ILanWorldDiscovery _discovery;
    private readonly IGameServerStatusClient _status;
    private readonly IRoomNetwork _network;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(5)};
    private CancellationTokenSource? _operation;
    private bool _querying,_disposed;
    private long? _lastPingMs;
    /// <summary>Most recent successful game-server status query; cleared on target change, failure or disconnect.</summary>
    public long? LastPingMs => _lastPingMs;
    private void SetLastPing(long? value) => SetProperty(ref _lastPingMs, value, nameof(LastPingMs));
    partial void OnConnectAddressChanged(string value) => SetLastPing(null);
    public Func<(string? Version,string? Loader)>? SelectedGame {get;set;}
    public Func<ServerAddress,Task>? LaunchRequested {get;set;}
    public Func<string,Task> CopyText {get;set;}=PlatformUi.CopyTextAsync;
    public ObservableCollection<LanWorld> Worlds {get;}=[];
    [ObservableProperty] private int _tab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _hint="先在 Minecraft 单人世界中选择“对局域网开放”，再扫描附近世界。";
    [ObservableProperty] private string _connectionHint="选择世界或输入服务器地址后，可检测连接。";
    [ObservableProperty] private string _roomName="朋友的世界";
    [ObservableProperty] private string _hostPort="25565";
    [ObservableProperty] private string _peerAddress="";
    [ObservableProperty] private string _inviteToJoin="";
    [ObservableProperty] private string _invitationText="";
    [ObservableProperty] private string _connectAddress="";
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _sessionTitle="";
    [ObservableProperty] private string _sessionState="";
    /// <summary>房间操作阶段（大纲 9.4）：UI 只在服务层确认结果后显示成功/失败，不把「点击」当「已开启」。</summary>
    public enum RoomActionPhase { Idle, Working, Done, Failed }
    [ObservableProperty] private RoomActionPhase _roomPhase = RoomActionPhase.Idle;
    /// <summary>正在执行的动作名，驱动按钮文案（创建房间… / 连接房间… / 断开房间…）。</summary>
    [ObservableProperty] private string _roomAction = "";
    /// <summary>最近一次失败的动作名。Operation 的 finally 会清空 RoomAction，文案要靠它判断「哪个按钮该显示重试」。</summary>
    private string _failedAction = "";
    /// <summary>当前动作是否为「创建房间」：驱动该按钮的加载态与文案。</summary>
    public bool RoomBusy => RoomPhase == RoomActionPhase.Working && RoomAction == "创建房间";
    public bool JoinRoomBusy => RoomPhase == RoomActionPhase.Working && RoomAction == "连接房间";
    public bool DisconnectBusy => RoomPhase == RoomActionPhase.Working && RoomAction == "断开房间";
    public bool EngineInstallBusy => RoomPhase == RoomActionPhase.Working && RoomAction == "下载联机组件";
    /// <summary>下载进度条只对「下载联机组件」可见，避免扫描/检测时显示一条 Value=0 的空进度条。</summary>
    public bool DownloadVisible => EngineInstallBusy;
    public string InstallEngineLabel => EngineInstallBusy ? "正在下载…" : _failedAction == "下载联机组件" ? "重试下载" : "下载 / 修复组件";
    public string CreateRoomLabel => RoomBusy ? "正在创建…" : RoomPhase == RoomActionPhase.Done && !HasSession ? "重新创建" : _failedAction == "创建房间" ? "重试创建" : "创建房间";
    public string JoinRoomLabel => JoinRoomBusy ? "正在连接…" : _failedAction == "连接房间" ? "重试连接" : "连接房间";
    public string DisconnectLabel => DisconnectBusy ? "正在断开…" : "断开房间";
    public bool IsLanTab=>Tab==0;
    public bool IsRoomTab=>Tab==1;
    public bool HasWorlds=>Worlds.Count>0;
    public bool HasSession=>_network.Session is not null;
    public bool IsHost=>_network.Session?.IsHost==true;
    public bool EngineInstalled=>_network.IsInstalled;
    public string EngineState=>EngineInstalled?"联机组件已就绪":"首次使用需要下载联机组件";
    public string EngineDescription {get{try{return _network.DownloadDescription;}catch(PlatformNotSupportedException){return "当前系统架构暂不支持此组件。";}}}
    public RoomInvitation? CurrentInvitation=>_network.Session?.Invitation;
    /// <summary>实际游戏连接地址（来自服务层 session），对应大纲 9.5「地址必须由程序检测或服务层提供」。</summary>
    public string SessionAddress=>_network.Session?.Address.Endpoint??"";
    public string SessionGamePort=>_network.Session?.Invitation.GamePort.ToString()??"";
    public string SessionPeer=>_network.Session?.Invitation.Peer??"";
    public string SessionNetwork=>_network.Session?.Invitation.Network??"";
    public bool HasSessionAddress=>SessionAddress.Length>0;
    public MultiplayerViewModel(ILanWorldDiscovery discovery,IGameServerStatusClient status,IRoomNetwork network)
    {
        _discovery=discovery;_status=status;_network=network;
        Worlds.CollectionChanged+=(_,_)=>OnPropertyChanged(nameof(HasWorlds));
        _network.Changed+=NetworkChanged;_timer.Tick+=async(_,_)=>await RefreshRoomAsync();
    }
    partial void OnTabChanged(int value){OnPropertyChanged(nameof(IsLanTab));OnPropertyChanged(nameof(IsRoomTab));}
    partial void OnRoomPhaseChanged(RoomActionPhase value)=>NotifyRoomLabels();
    partial void OnRoomActionChanged(string value)=>NotifyRoomLabels();
    private void NotifyRoomLabels()
    {
        foreach(var name in new[]{nameof(RoomBusy),nameof(JoinRoomBusy),nameof(DisconnectBusy),nameof(EngineInstallBusy),nameof(DownloadVisible),nameof(CreateRoomLabel),nameof(JoinRoomLabel),nameof(DisconnectLabel),nameof(InstallEngineLabel)})OnPropertyChanged(name);
    }
    private async Task Operation(string actionLabel,Func<CancellationToken,Task> action)
    {
        if(IsBusy)return;IsBusy=true;RoomAction=actionLabel;_failedAction="";RoomPhase=RoomActionPhase.Working;using var cts=new CancellationTokenSource();_operation=cts;
        try{await action(cts.Token);RoomPhase=RoomActionPhase.Done;}
        catch(OperationCanceledException){Hint="操作已取消。";RoomPhase=RoomActionPhase.Idle;}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or PlatformNotSupportedException or System.Net.Sockets.SocketException or HttpRequestException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {SetFailure(actionLabel,ex.Message);}
        catch(Exception ex){SetFailure(actionLabel,ex.Message);}   // 兜底：白名单外的异常也不能把 RoomPhase 卡在 Working
        finally{_operation=null;IsBusy=false;RoomAction="";if(RoomPhase==RoomActionPhase.Working)RoomPhase=RoomActionPhase.Failed;OnPropertyChanged(nameof(EngineInstalled));OnPropertyChanged(nameof(EngineState));}
    }
    private void SetFailure(string actionLabel,string reason)
    {
        Hint="操作未完成："+reason;ConnectionHint=Hint;_failedAction=actionLabel;RoomPhase=RoomActionPhase.Failed;
    }
    [RelayCommand] private void ShowLan()=>Tab=0;
    [RelayCommand] private void ShowRooms()=>Tab=1;
    [RelayCommand] private void NodeGuide()
    {
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/EasyTier/EasyTier/discussions?discussions_q=%E5%85%AC%E5%85%B1%E8%8A%82%E7%82%B9"){UseShellExecute=true});}
        catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){Hint="无法打开浏览器，请访问 EasyTier 官方 GitHub Discussions 查找公共节点。";}
    }
    [RelayCommand] private void Cancel()=>_operation?.Cancel();
    [RelayCommand] private Task ScanAsync()=>Operation("扫描世界",async ct=>
    {
        Hint="正在查找附近世界…";var worlds=await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3),ct);Worlds.Clear();foreach(var world in worlds)Worlds.Add(world);
        Hint=worlds.Count==0?"暂未发现世界。请确认双方在同一网络，游戏已开放局域网；也可以手动输入地址。":$"发现 {worlds.Count} 个世界。选择后检测连接，或共享本机世界。";
    });
    [RelayCommand] private void SelectWorld(LanWorld? world){if(world is not null){ConnectAddress=world.Endpoint;ConnectionHint="已选择 "+world.CleanMotd;}}
    [RelayCommand] private void ShareWorld(LanWorld? world)
    {
        if(world?.IsLocal!=true){Hint="只能共享本机开放的世界。";return;}
        HostPort=world.Port.ToString();RoomName=string.IsNullOrWhiteSpace(world.CleanMotd)?"朋友的世界":world.CleanMotd[..Math.Min(60,world.CleanMotd.Length)];Tab=1;
        Hint="已填入本机世界端口，点击“创建房间”即可分享。";
    }
    [RelayCommand] private Task InstallEngineAsync()=>Operation("下载联机组件",async ct=>
    {Hint="正在从 EasyTier 官方发布下载并校验组件…";DownloadPercent=0;await _network.InstallAsync(new Progress<double>(p=>DownloadPercent=p),ct);Hint="联机组件已就绪。未安装虚拟网卡或系统服务。";});
    [RelayCommand] private Task CreateRoomAsync()=>Operation("创建房间",async ct=>
    {
        if(HasSession)throw new InvalidOperationException("请先断开当前房间。");
        if(!EngineInstalled)throw new InvalidOperationException("请先下载联机组件。");
        if(!int.TryParse(HostPort,out var port)||port is <1 or >65535)throw new ArgumentException("请填写游戏“对局域网开放”后显示的实际端口。");
        var game=SelectedGame?.Invoke()??default;
        var invitation=RoomInvitation.Create(RoomName,port,PeerAddress,game.Version,game.Loader);
        Hint="正在确认本机世界是否可连接…";await _status.QueryAsync(new("127.0.0.1",port),ct);
        Hint="正在创建私密房间…";await _network.StartAsync(invitation,true,ct);
        InvitationText=invitation.Encode();Hint="房间已创建。将邀请信息分享给朋友；双方保持方隅运行。";await RefreshRoomAsync();
    });
    [RelayCommand] private Task JoinRoomAsync()=>Operation("连接房间",async ct=>
    {
        if(HasSession)throw new InvalidOperationException("请先断开当前房间。");
        if(!EngineInstalled)throw new InvalidOperationException("请先下载联机组件。");
        var invitation=RoomInvitation.Decode(InviteToJoin);Hint="正在连接房间，等待网络穿透或中继…";
        var session=await _network.StartAsync(invitation,false,ct);ConnectAddress=session.Address.Endpoint;
        Hint="连接引擎已启动，正在检测房主世界。显示“游戏可连接”后再进入游戏。";await RefreshRoomAsync();
    });
    [RelayCommand] private Task DisconnectAsync()=>Operation("断开房间",async ct=>
    {
        // 注意：这里不能 _operation?.Cancel()——那会取消 Operation 为本次断开自己创建的 cts，
        // 让 Working 态被 OperationCanceledException 立刻吞掉。按钮已由 !IsBusy 门禁保护。
        // 让加载态停留约 60ms，使「正在断开…」在 UI 线程真正渲染一帧，而不是同步瞬间结束。
        await Task.Delay(60, ct);
        _network.Stop();InvitationText="";InviteToJoin="";
        Hint="已断开房间并清理临时邀请配置。";ConnectionHint="房间已断开。";
    });
    [RelayCommand] private async Task CopyInvitationAsync()
    {if(IsHost&&InvitationText.Length>0){try{await CopyText(InvitationText);Hint="邀请信息已复制，请仅分享给信任的朋友。";}catch(InvalidOperationException){Hint="剪贴板不可用，可以手动选择并复制邀请信息。";}}}
    [RelayCommand] private async Task CopyAddressAsync()
    {try{var target=ServerAddress.Parse(ConnectAddress);await CopyText(target.Endpoint);ConnectionHint="游戏地址已复制。";}catch(Exception ex)when(ex is ArgumentException or InvalidOperationException){ConnectionHint=ex.Message;}}
    [RelayCommand] private async Task CopySessionAddressAsync()
    {try{if(!HasSessionAddress)return;await CopyText(SessionAddress);Hint="游戏地址已复制："+SessionAddress;}catch(InvalidOperationException){Hint="剪贴板不可用，请手动复制地址。";}}
    [RelayCommand] private Task CheckConnectionAsync()=>Operation("检测连接",async ct=>
    {SetLastPing(null);var address=ConnectAddress;var target=ServerAddress.Parse(address);ConnectionHint="正在检测游戏连接…";var status=await _status.QueryAsync(target,ct);if(ConnectAddress==address){SetLastPing(status.ElapsedMilliseconds);ConnectionHint=Describe(status);}});
    [RelayCommand] private async Task LaunchGameAsync()
    {
        if(IsBusy)return;
        try{var address=ServerAddress.Parse(ConnectAddress);if(LaunchRequested is not null)await LaunchRequested(address);}
        catch(ArgumentException ex){ConnectionHint=ex.Message;}
    }
    private static string Describe(GameServerStatus status)=>$"游戏可连接 · {status.Version} · {status.Online}/{status.Maximum} 人 · {status.ElapsedMilliseconds} ms";
    private void NetworkChanged()
    {if(Dispatcher.UIThread.CheckAccess())UpdateNetwork();else Dispatcher.UIThread.Post(UpdateNetwork);}
    private void UpdateNetwork()
    {
        if(_disposed)return;
        SetLastPing(null);
        foreach(var property in new[]{nameof(HasSession),nameof(IsHost),nameof(EngineInstalled),nameof(EngineState),nameof(CurrentInvitation),nameof(SessionAddress),nameof(SessionGamePort),nameof(SessionPeer),nameof(SessionNetwork),nameof(HasSessionAddress)})OnPropertyChanged(property);
        NotifyRoomLabels();
        if(_network.Session is {} session){SessionTitle=session.Invitation.Name;SessionState=session.IsHost?"本机世界已共享，等待朋友连接。":"连接引擎运行中，正在检测游戏。";_timer.Start();}
        else{_timer.Stop();InvitationText="";SessionState="房间已断开。";}
    }
    private async Task RefreshRoomAsync()
    {
        if(_querying||_disposed||_network.Session is not {} session)return;
        if(session.Invitation.Expires<=DateTimeOffset.UtcNow){_network.Stop();Hint="房间邀请已过期，请重新创建房间。";ConnectionHint=Hint;return;}
        _querying=true;
        try{var status=await _status.QueryAsync(session.Address);if(_network.Session==session){SetLastPing(status.ElapsedMilliseconds);SessionState=Describe(status);if(!session.IsHost)ConnectionHint=SessionState;}}
        catch(Exception ex)when(ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException)
        {if(_network.Session==session){SetLastPing(null);SessionState=session.IsHost?"本机世界暂不可连接，请检查游戏是否仍在开放局域网。":"等待房主世界…可能正在穿透网络或中继节点暂不可达。";if(!session.IsHost)ConnectionHint=SessionState;Hint=SessionState;}}
        finally{_querying=false;}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_operation?.Cancel();_timer.Stop();_network.Changed-=NetworkChanged;_network.Stop();}
}
