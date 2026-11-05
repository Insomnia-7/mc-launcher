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
    public bool IsLanTab=>Tab==0;
    public bool IsRoomTab=>Tab==1;
    public bool HasWorlds=>Worlds.Count>0;
    public bool HasSession=>_network.Session is not null;
    public bool IsHost=>_network.Session?.IsHost==true;
    public bool EngineInstalled=>_network.IsInstalled;
    public string EngineState=>EngineInstalled?"联机组件已就绪":"首次使用需要下载联机组件";
    public string EngineDescription {get{try{return _network.DownloadDescription;}catch(PlatformNotSupportedException){return "当前系统架构暂不支持此组件。";}}}
    public RoomInvitation? CurrentInvitation=>_network.Session?.Invitation;
    public MultiplayerViewModel(ILanWorldDiscovery discovery,IGameServerStatusClient status,IRoomNetwork network)
    {
        _discovery=discovery;_status=status;_network=network;
        Worlds.CollectionChanged+=(_,_)=>OnPropertyChanged(nameof(HasWorlds));
        _network.Changed+=NetworkChanged;_timer.Tick+=async(_,_)=>await RefreshRoomAsync();
    }
    partial void OnTabChanged(int value){OnPropertyChanged(nameof(IsLanTab));OnPropertyChanged(nameof(IsRoomTab));}
    private async Task Operation(Func<CancellationToken,Task> action)
    {
        if(IsBusy)return;IsBusy=true;using var cts=new CancellationTokenSource();_operation=cts;
        try{await action(cts.Token);}
        catch(OperationCanceledException){Hint="操作已取消。";}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or PlatformNotSupportedException or System.Net.Sockets.SocketException or HttpRequestException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {Hint="操作未完成："+ex.Message;if(ConnectionHint=="正在检测游戏连接…")ConnectionHint=Hint;}
        finally{_operation=null;IsBusy=false;OnPropertyChanged(nameof(EngineInstalled));OnPropertyChanged(nameof(EngineState));}
    }
    [RelayCommand] private void ShowLan()=>Tab=0;
    [RelayCommand] private void ShowRooms()=>Tab=1;
    [RelayCommand] private void NodeGuide()
    {
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/EasyTier/EasyTier/discussions?discussions_q=%E5%85%AC%E5%85%B1%E8%8A%82%E7%82%B9"){UseShellExecute=true});}
        catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){Hint="无法打开浏览器，请访问 EasyTier 官方 GitHub Discussions 查找公共节点。";}
    }
    [RelayCommand] private void Cancel()=>_operation?.Cancel();
    [RelayCommand] private Task ScanAsync()=>Operation(async ct=>
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
    [RelayCommand] private Task InstallEngineAsync()=>Operation(async ct=>
    {Hint="正在从 EasyTier 官方发布下载并校验组件…";DownloadPercent=0;await _network.InstallAsync(new Progress<double>(p=>DownloadPercent=p),ct);Hint="联机组件已就绪。未安装虚拟网卡或系统服务。";});
    [RelayCommand] private Task CreateRoomAsync()=>Operation(async ct=>
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
    [RelayCommand] private Task JoinRoomAsync()=>Operation(async ct=>
    {
        if(HasSession)throw new InvalidOperationException("请先断开当前房间。");
        if(!EngineInstalled)throw new InvalidOperationException("请先下载联机组件。");
        var invitation=RoomInvitation.Decode(InviteToJoin);Hint="正在连接房间，等待网络穿透或中继…";
        var session=await _network.StartAsync(invitation,false,ct);ConnectAddress=session.Address.Endpoint;
        Hint="连接引擎已启动，正在检测房主世界。显示“游戏可连接”后再进入游戏。";await RefreshRoomAsync();
    });
    [RelayCommand] private void Disconnect()
    {_operation?.Cancel();_network.Stop();InvitationText="";InviteToJoin="";Hint="已断开房间并清理临时邀请配置。";}
    [RelayCommand] private async Task CopyInvitationAsync()
    {if(IsHost&&InvitationText.Length>0){try{await CopyText(InvitationText);Hint="邀请信息已复制，请仅分享给信任的朋友。";}catch(InvalidOperationException){Hint="剪贴板不可用，可以手动选择并复制邀请信息。";}}}
    [RelayCommand] private async Task CopyAddressAsync()
    {try{var target=ServerAddress.Parse(ConnectAddress);await CopyText(target.Endpoint);ConnectionHint="游戏地址已复制。";}catch(Exception ex)when(ex is ArgumentException or InvalidOperationException){ConnectionHint=ex.Message;}}
    [RelayCommand] private Task CheckConnectionAsync()=>Operation(async ct=>
    {var target=ServerAddress.Parse(ConnectAddress);ConnectionHint="正在检测游戏连接…";var status=await _status.QueryAsync(target,ct);ConnectionHint=Describe(status);});
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
        foreach(var property in new[]{nameof(HasSession),nameof(IsHost),nameof(EngineInstalled),nameof(EngineState),nameof(CurrentInvitation)})OnPropertyChanged(property);
        if(_network.Session is {} session){SessionTitle=session.Invitation.Name;SessionState=session.IsHost?"本机世界已共享，等待朋友连接。":"连接引擎运行中，正在检测游戏。";_timer.Start();}
        else{_timer.Stop();InvitationText="";SessionState="房间已断开。";}
    }
    private async Task RefreshRoomAsync()
    {
        if(_querying||_disposed||_network.Session is not {} session)return;
        if(session.Invitation.Expires<=DateTimeOffset.UtcNow){_network.Stop();Hint="房间邀请已过期，请重新创建房间。";return;}
        _querying=true;
        try{var status=await _status.QueryAsync(session.Address);if(_network.Session==session){SessionState=Describe(status);if(!session.IsHost)ConnectionHint=SessionState;}}
        catch(Exception ex)when(ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException)
        {if(_network.Session==session)SessionState=session.IsHost?"本机世界暂不可连接，请检查游戏是否仍在开放局域网。":"等待房主世界…可能正在穿透网络或中继节点暂不可达。";}
        finally{_querying=false;}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_operation?.Cancel();_timer.Stop();_network.Changed-=NetworkChanged;_network.Stop();}
}
