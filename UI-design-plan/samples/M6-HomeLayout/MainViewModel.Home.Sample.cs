using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Settings;

namespace Launcher.App.ViewModels;

public sealed partial class LibraryEntry
{
    public string LoaderBadgeText =>
        Instance.LoaderName is { Length: > 0 } loader ? loader
        : Instance.IsModpack ? "整合包"
        : "原版";
}

/// <summary>
/// 首页扩展分部类（放入 src/Launcher.Desktop/ViewModels/MainViewModel.Home.cs）。
/// 遵循 UI.md §10, §11, §25, §31：
/// - 100% 复用现有 SelectedInstance、LibraryEntries、LaunchCommand、Multiplayer、ActiveNetworkText
/// - 不改动任何底层启动流程，不伪造未实现的 NAT 类型或假延迟数字
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isHomeSection = true;

    public bool IsHomePage => ShellPage == "home";
    public string AllInstancesLinkText => $"全部实例 ({Instances.Count}) ›";
    public string QuickLaunchSubcaption => HasInstances
        ? "选择实例并一键启动游戏"
        : "尚未发现实例，请先新建或导入整合包";

    public LibraryEntry? SelectedHomeEntry
    {
        get => _allLibraryEntries.FirstOrDefault(e => e.Instance.InstanceId == SelectedInstance?.InstanceId);
        set
        {
            if (value is not null && SelectedInstance?.InstanceId != value.Instance.InstanceId)
                SelectedInstance = value.Instance;
        }
    }

    // 真实联机服务状态：绿色仅在房间已建立连接时使用（遵循 UI.md §10「绿色仅表示成功或正常」）
    public string HomeMultiplayerStateText =>
        Multiplayer.HasSession ? (Multiplayer.IsHost ? "房间共享中" : "已连接房间")
        : Multiplayer.EngineInstalled ? "组件已就绪"
        : "待安装组件";

    public string HomeMultiplayerStatusIcon =>
        Multiplayer.HasSession ? "StatusOnline"
        : Multiplayer.EngineInstalled ? "StatusCheck"
        : "StatusInfo";

    public IBrush HomeMultiplayerStatusBrush => ResolveThemeBrush(
        Multiplayer.HasSession ? "SuccessBrush"
        : Multiplayer.EngineInstalled ? "AccentBrush"
        : "MutedBrush");

    // 真实延迟：仅当 MultiplayerViewModel.LastPingMs 有实测值时显示毫秒数，绝不编造假数值
    public string HomeLatencyText => Multiplayer.LastPingMs is { } ms
        ? (ms <= 60 ? $"优秀 ({ms} ms)" : ms <= 150 ? $"良好 ({ms} ms)" : $"偏高 ({ms} ms)")
        : "未检测";

    public string HomeLatencyStatusIcon => Multiplayer.LastPingMs is { } ms
        ? (ms <= 150 ? "StatusOnline" : "StatusWarning")
        : "StatusInfo";

    public IBrush HomeLatencyStatusBrush => ResolveThemeBrush(
        Multiplayer.LastPingMs is { } ms
            ? (ms <= 100 ? "SuccessBrush" : ms <= 180 ? "WarnBrush" : "DangerBrush")
            : "FaintBrush");

    public string HomeProxyAndSourceSummary
    {
        get
        {
            var source = _downloadSource.Current == DownloadSource.Bmclapi ? "BMCLAPI" : "官方源";
            var proxy = ProxyModeIndex switch
            {
                (int)ProxyMode.Direct => "直连",
                (int)ProxyMode.Custom => "自定义代理",
                _ => "系统网络"
            };
            return $"{proxy} · {source}";
        }
    }

    [RelayCommand]
    private void ShowHome()
    {
        BeforeMainNavigation?.Invoke();
        IsInstanceSection = true;
        IsHomeSection = true;
        NotifyWorkspaceNavigation();
    }

    [RelayCommand]
    private async Task QuickLaunchEntryAsync(LibraryEntry? entry)
    {
        if (entry is null || IsBusy) return;
        SelectedInstance = entry.Instance;
        if (LaunchCommand.CanExecute(null))
            await LaunchCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenEntryDirectory(LibraryEntry? entry)
    {
        if (entry is null) return;
        SelectedInstance = entry.Instance;
        if (OpenGameDirectoryCommand.CanExecute(null))
            OpenGameDirectoryCommand.Execute(null);
    }

    [RelayCommand]
    private async Task DeleteEntryAsync(LibraryEntry? entry)
    {
        if (entry is null) return;
        SelectedInstance = entry.Instance;
        if (DeleteInstanceCommand.CanExecute(null))
            await DeleteInstanceCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenSelectedMods()
    {
        if (SelectedHomeEntry is { } entry)
        {
            OpenGameCommand.Execute(entry);
            SelectDetailTabCommand.Execute("1");
        }
        else
        {
            ShowDownloadsCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void OpenSelectedDiagnosticsOrLogs()
    {
        if (SelectedHomeEntry is { } entry)
        {
            OpenGameCommand.Execute(entry);
            SelectDetailTabCommand.Execute("4");
        }
        else if (OpenLogsCommand.CanExecute(null))
        {
            OpenLogsCommand.Execute(null);
        }
    }

    private static IBrush ResolveThemeBrush(string key) =>
        Avalonia.Application.Current?.Resources.TryGetResource(key, null, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
