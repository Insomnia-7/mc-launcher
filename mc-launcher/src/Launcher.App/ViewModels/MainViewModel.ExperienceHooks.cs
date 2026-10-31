using Launcher.Application.Mods;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    public Action? BeforeMainNavigation {get;set;}
    public Action? BeforeNetworkSettings {get;set;}
    public string? DefaultUpdateLocation {get;set;}
    public Func<bool>? ShouldCheckLauncherUpdates {get;set;}
    public Func<Launcher.Application.Update.UpdateStaging,string,CancellationToken,Task>? LaunchSelfUpdater {get;set;}
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
    public Func<ModUpdateCandidate,bool>? IsModUpdateAllowed {get;set;}
    public string? ModUpdateReleaseChannel {get;set;}
}
