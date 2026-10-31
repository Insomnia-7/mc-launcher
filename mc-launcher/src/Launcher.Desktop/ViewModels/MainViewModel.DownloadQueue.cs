using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Application.Experience;
namespace Launcher.App.ViewModels;
public sealed record QueuePreferences(bool AutoContinue);
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _autoContinueDownloads = true;
    partial void OnAutoContinueDownloadsChanged(bool value)
    {
        _tasks.AutoContinue = value;
        ExperienceFiles.Write(Path.Combine(AppPaths.DataDirectory,"download-queue.json"),new QueuePreferences(value));
    }
}
