using Launcher.Application.Mods;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    public Action? BeforeMainNavigation {get;set;}
    public Action? BeforeNetworkSettings {get;set;}
    public string? DefaultUpdateLocation {get;set;}
    public Func<bool>? ShouldCheckLauncherUpdates {get;set;}
    public Func<Launcher.Application.Update.UpdateStaging,string,CancellationToken,Task>? LaunchSelfUpdater {get;set;}
    public Func<string>? UpdateTargetDirectory {get;set;}
    public Action<string>? GameStarted {get;set;}
    public Action? RunningGamesChanged {get;set;}
    public Func<ModUpdateRow,Task>? PreviewModUpdate {get;set;}
    public Func<string,Task>? PersistentImport {get;set;}
    public Func<string,ModUpdateCandidate,Task>? PersistentModUpdate {get;set;}
    public Func<string,int,bool,Task>? PersistentSetup {get;set;}
    public Func<bool>? CancelPersistentTask {get;set;}
    public Func<string,string,CancellationToken,Task>? BeforeModUpdate {get;set;}
    public Func<string,bool,CancellationToken,Task>? PrepareAppearance {get;set;}
    public Func<string,CancellationToken,Task<bool>>? BeforeLaunchChecks {get;set;}
    public Func<string,CancellationToken,Task>? PrepareSharedSettings {get;set;}
    public Action<string,string>? GameExited {get;set;}
    /// <summary>删除版本成功后的界面收尾（关闭详情页、刷新列表、显示提示、清理该实例的展示偏好与封面）：由桌面端注入，参数为 <c>(实例 id, 显示名, 版本是否被共用保留)</c>。</summary>
    public Action<string,string,bool>? InstanceDeleted {get;set;}
    /// <summary>目标实例是否还有未终止的持久化任务（Ready/Running/Paused/Failed）。桌面端注入，WPF 端无任务中心时返回 false。</summary>
    public Func<string,string,bool>? HasPendingInstanceTasks {get;set;}
    public Func<ModUpdateCandidate,bool>? IsModUpdateAllowed {get;set;}
    public string? ModUpdateReleaseChannel {get;set;}
}
