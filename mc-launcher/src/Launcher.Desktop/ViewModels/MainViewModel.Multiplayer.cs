using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Multiplayer;
using Launcher.Domain.Launch;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isMultiplayerOpen;
    public MultiplayerViewModel Multiplayer {get;private set;}=null!;
    public bool IsMultiplayerPage=>ShellPage=="multiplayer";
    public void InitializeMultiplayer(ILanWorldDiscovery discovery,IGameServerStatusClient status,IRoomNetwork network)
    {
        if (Multiplayer is not null) Multiplayer.PropertyChanged -= HomeMultiplayerChanged;
        Multiplayer=new(discovery,status,network){SelectedGame=()=> (SelectedInstance?.BaseVersion,SelectedInstance?.LoaderName),LaunchRequested=LaunchMultiplayerGameAsync};
        Multiplayer.PropertyChanged += HomeMultiplayerChanged;
        OnPropertyChanged(nameof(Multiplayer));NotifyHomeMultiplayer();
    }
    private void HomeMultiplayerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyHomeMultiplayer();
    private async Task LaunchMultiplayerGameAsync(ServerAddress address)
    {
        if(SelectedInstance is null){Multiplayer.ConnectionHint="请先添加并选择一个游戏实例。";return;}
        if(IsBusy||IsLoggingIn){Multiplayer.ConnectionHint="请等待当前游戏或账号操作完成。";return;}
        if(Multiplayer.CurrentInvitation is {GameVersion:{Length:>0} version} invitation&&
            (version!=SelectedInstance.BaseVersion||!string.Equals(invitation.Loader,SelectedInstance.LoaderName,StringComparison.OrdinalIgnoreCase)))
        {
            AskConfirm("游戏版本可能不匹配","房间与所选实例的版本或加载器不同。",$"房间：{version} · {invitation.Loader??"原版"}\n所选：{SelectedGameMeta}","仍然尝试连接",async()=>await LaunchIntoServerAsync(address));return;
        }
        await LaunchIntoServerAsync(address);
    }
    [RelayCommand] private void OpenMultiplayer(){BeforeMainNavigation?.Invoke();IsMultiplayerOpen=true;}
    partial void OnIsMultiplayerOpenChanged(bool value)=>NotifyWorkspaceNavigation();
    public void ConfirmMultiplayerClose(Action close)=>AskConfirm("断开联机并退出？","退出方隅会结束当前房间连接。","朋友将无法继续通过此房间连接你的世界。","断开并退出",()=>{Multiplayer.DisconnectCommand.Execute(null);close();});
}
