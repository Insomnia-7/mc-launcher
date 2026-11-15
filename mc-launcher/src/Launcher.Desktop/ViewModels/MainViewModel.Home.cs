using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Settings;

namespace Launcher.App.ViewModels;

public sealed partial class LibraryEntry
{
    public string LoaderBadgeText => Instance.LoaderName is { Length: > 0 } loader ? loader : Instance.IsModpack ? "整合包" : "原版";
    public string VersionBadgeText => Instance.BaseVersion;
}

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isHomeSection = true;
    // The home page must not inherit the library page's search/group/favorite filters.
    public ObservableCollection<LibraryEntry> HomeEntries { get; } = [];
    public bool IsHomePage => ShellPage == "home";
    public string AllInstancesLinkText => $"全部实例 ({Instances.Count}) ›";
    public LibraryEntry? SelectedHomeEntry
    {
        get => HomeEntries.FirstOrDefault(e => e.Instance.InstanceId == SelectedInstance?.InstanceId);
        set { if (value is not null) SelectHomeEntry(value); }
    }
    public string HomeMultiplayerStateText => Multiplayer?.HasSession == true
        ? Multiplayer.IsHost ? "房间共享中" : "已连接房间"
        : Multiplayer?.EngineInstalled == true ? "组件已就绪" : "待安装组件";
    public string HomeMultiplayerStatusIcon => Multiplayer?.HasSession == true ? "StatusOnline"
        : Multiplayer?.EngineInstalled == true ? "StatusCheck" : "StatusInfo";
    public string HomeLatencyText => Multiplayer?.LastPingMs is { } ms
        ? $"{(ms <= 60 ? "优秀" : ms <= 150 ? "良好" : "偏高")} ({ms} ms)" : "未测速";
    public bool HomeHasLatency => Multiplayer?.LastPingMs is not null;
    public bool HomeLatencyHigh => Multiplayer?.LastPingMs > 150;
    public string HomeLatencyStatusIcon => HomeHasLatency ? HomeLatencyHigh ? "StatusWarning" : "StatusCheck" : "StatusInfo";
    public string HomeNetworkSummary
    {
        get
        {
            var mode = ActiveNetworkText.StartsWith("直接连接", StringComparison.Ordinal) ? "直连"
                : ActiveNetworkText.StartsWith("自定义代理", StringComparison.Ordinal) ? "自定义代理"
                : ActiveNetworkText.StartsWith("自动选择", StringComparison.Ordinal) ? "系统 / 环境代理" : "网络配置异常";
            return mode + " · " + (_downloadSource.Current == DownloadSource.Bmclapi ? "BMCLAPI" : "官方源");
        }
    }
    public string HomeGameRootSummary => "游戏库 · " + Path.GetFileName(Path.TrimEndingDirectorySeparator(GameRoot));
    public string HomeJavaSummary => SelectedJava?.ExecutablePath is not null ? "Java · " + SelectedJava.Title
        : JavaOptions.Any(j => j.ExecutablePath is not null) ? "Java · 已发现，自动选择" : "Java · 自动检测 / 按需准备";
    public string HomeLastPlayedText => HomeEntries.Where(e => e.LastPlayed is not null).MaxBy(e => e.LastPlayed)?.LastPlayed is { } last
        ? "最近游玩 " + last.LocalDateTime.ToString("MM-dd HH:mm") : "尚无启动记录";
    public string HomeSelectionText => HasInstances ? "当前实例 · " + SelectedGameName : "先新建或导入一个游戏实例";

    private void InitializeHome()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsInstanceSection) or nameof(IsDetailOpen) or nameof(IsExperienceOpen)
                or nameof(IsAppearanceOpen) or nameof(IsNetworkOpen) or nameof(IsMultiplayerOpen))
            {
                if ((e.PropertyName == nameof(IsInstanceSection) || IsDetailOpen || IsExperienceOpen || IsPreferencesOpen || IsMultiplayerOpen) && IsHomeSection)
                    IsHomeSection = false;
            }
            if (e.PropertyName is nameof(SelectedInstance) or nameof(SelectedGameName))
            {
                OnPropertyChanged(nameof(SelectedHomeEntry)); OnPropertyChanged(nameof(HomeSelectionText));
            }
            if (e.PropertyName is nameof(ActiveNetworkText) or nameof(DownloadSourceIndex)) OnPropertyChanged(nameof(HomeNetworkSummary));
            if (e.PropertyName == nameof(SelectedJava)) OnPropertyChanged(nameof(HomeJavaSummary));
            if (e.PropertyName == nameof(GameRoot)) OnPropertyChanged(nameof(HomeGameRootSummary));
        };
        Instances.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(AllInstancesLinkText)); OnPropertyChanged(nameof(HomeSelectionText)); };
        JavaOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HomeJavaSummary));
    }
    private void RefreshHomeEntries()
    {
        HomeEntries.Clear();
        foreach (var entry in _allLibraryEntries.OrderByDescending(e => e.Favorite).ThenByDescending(e => e.LastPlayed).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            HomeEntries.Add(entry);
        OnPropertyChanged(nameof(SelectedHomeEntry)); OnPropertyChanged(nameof(HomeLastPlayedText)); OnPropertyChanged(nameof(HomeSelectionText));
    }
    private void NotifyHomeMultiplayer()
    {
        foreach (var property in new[] { nameof(HomeMultiplayerStateText), nameof(HomeMultiplayerStatusIcon), nameof(HomeLatencyText), nameof(HomeHasLatency), nameof(HomeLatencyHigh), nameof(HomeLatencyStatusIcon) })
            OnPropertyChanged(property);
    }
    partial void OnIsHomeSectionChanged(bool value) => NotifyWorkspaceNavigation();
    [RelayCommand] private void ShowHome()
    {
        BeforeMainNavigation?.Invoke(); IsInstanceSection = true; IsHomeSection = true; NotifyWorkspaceNavigation();
    }
    private bool SelectHomeEntry(LibraryEntry? entry)
    {
        var instance = Instances.FirstOrDefault(i => i.InstanceId == entry?.Instance.InstanceId);
        if (instance is null) return false;
        if (SelectedInstance?.InstanceId != instance.InstanceId) SelectedInstance = instance;
        return true;
    }
    [RelayCommand] private async Task QuickLaunchEntryAsync(LibraryEntry? entry)
    {
        if (IsBusy || !SelectHomeEntry(entry) || !LaunchCommand.CanExecute(null)) return;
        await LaunchCommand.ExecuteAsync(null);
    }
    [RelayCommand] private void OpenHomeEntryMods(LibraryEntry? entry) { if (SelectHomeEntry(entry)) OpenSelectedMods(); }
    [RelayCommand] private void OpenHomeEntryDirectory(LibraryEntry? entry) { if (SelectHomeEntry(entry)) OpenGameDirectoryCommand.Execute(null); }
    [RelayCommand] private async Task ExportHomeEntryAsync(LibraryEntry? entry)
    {
        if (SelectHomeEntry(entry) && ExportInstanceCommand.CanExecute(null)) await ExportInstanceCommand.ExecuteAsync(null);
    }
    [RelayCommand] private void CloneHomeEntry(LibraryEntry? entry) { if (SelectHomeEntry(entry)) OpenCloneEditorCommand.Execute(null); }
    [RelayCommand] private void DeleteHomeEntry(LibraryEntry? entry)
    {
        if (SelectHomeEntry(entry) && DeleteInstanceCommand.CanExecute(null)) DeleteInstanceCommand.Execute(null);
    }
    [RelayCommand] private void OpenSelectedMods()
    {
        if (SelectedInstance is not null) { OpenGame(SelectedHomeEntry); SelectDetailTabCommand.Execute("1"); }
        else { ShowDownloadsCommand.Execute(null); Download.SelectedCategory = Download.Categories.First(c => !c.IsSeparator && c.Category == Launcher.Domain.Content.DownloadCategory.Mod); }
    }
    [RelayCommand] private void OpenSelectedDiagnosticsOrLogs()
    {
        if (SelectedInstance is not null) { OpenGame(SelectedHomeEntry); SelectDetailTabCommand.Execute("4"); }
        else OpenLogsCommand.Execute(null);
    }
}
