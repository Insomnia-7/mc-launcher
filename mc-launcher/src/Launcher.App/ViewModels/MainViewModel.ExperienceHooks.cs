using Launcher.Application.Mods;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    public Func<string,Task>? PersistentImport {get;set;}
    public Func<string,ModUpdateCandidate,Task>? PersistentModUpdate {get;set;}
    public Func<string,int,bool,Task>? PersistentSetup {get;set;}
    public Func<bool>? CancelPersistentTask {get;set;}
    public Func<string,string,CancellationToken,Task>? BeforeModUpdate {get;set;}
    public Func<string,bool,CancellationToken,Task>? PrepareAppearance {get;set;}
    public Func<ModUpdateCandidate,bool>? IsModUpdateAllowed {get;set;}
    public string? ModUpdateReleaseChannel {get;set;}
}
