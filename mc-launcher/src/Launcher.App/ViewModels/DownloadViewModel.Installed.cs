using CommunityToolkit.Mvvm.Input;
using System.IO;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Application.Mods;
using Launcher.Domain.Content;

namespace Launcher.App.ViewModels;

public sealed partial class DownloadViewModel
{
    private string? _installedResourceInstanceId;
    public Func<string,Task>? LaunchInstalledResource {get;set;}
    public bool ResourceIsInstalled=>_installedResourceInstanceId is not null;
    public string ResourceInstallButtonIcon=>ResourceIsInstalled?"ActionPlay":"ActionCloudDownload";

    public void RefreshInstalledResource()
    {
        _installedResourceInstanceId=null;
        if(OpenedResource is {} resource&&SelectedResourceRelease is {} release)
        {
            var root=_gameRoot();
            if(resource.Category==DownloadCategory.Modpack)
            {
                foreach(var instance in InstanceRegistry.Load(root).Instances)
                {
                    var origin=ModpackBaseline.Read(Path.Combine(root,"instances",instance.Id))?.Origin;
                    if(origin is not null&&origin.Source.Equals(resource.Source,StringComparison.OrdinalIgnoreCase)
                        &&origin.ProjectId==resource.Id&&origin.ReleaseId==release.Id
                        &&File.Exists(Path.Combine(root,"versions",instance.VersionId,instance.VersionId+".json")))
                    {_installedResourceInstanceId=instance.Id;break;}
                }
            }
            else if(_currentInstance() is {} id&&resource.Category.InstanceSubdirectory() is {} subdirectory)
            {
                var directory=Path.Combine(root,"instances",id,".minecraft",subdirectory);
                if(ModSourceStore.Load(directory).Any(p=>p.Value.Source.Equals(resource.Source,StringComparison.OrdinalIgnoreCase)
                    &&p.Value.ProjectId==resource.Id&&p.Value.VersionId==release.Id&&File.Exists(Path.Combine(directory,p.Key))))
                    _installedResourceInstanceId=id;
            }
        }
        OnPropertyChanged(nameof(ResourceIsInstalled));OnPropertyChanged(nameof(ResourceInstallButtonText));OnPropertyChanged(nameof(ResourceInstallButtonIcon));
        ResourcePrimaryCommand.NotifyCanExecuteChanged();
    }
    private bool CanResourcePrimary()=>!IsBusy&&(ResourceIsInstalled?LaunchInstalledResource is not null:CanInstall());
    [RelayCommand(CanExecute=nameof(CanResourcePrimary))]
    private async Task ResourcePrimaryAsync()
    {
        RefreshInstalledResource();
        if(_installedResourceInstanceId is {} id&&LaunchInstalledResource is {} launch)await launch(id);
        else if(InstallCommand.CanExecute(null)){await InstallCommand.ExecuteAsync(null);RefreshInstalledResource();}
    }
}
