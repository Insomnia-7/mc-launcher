using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Abstractions;
using Launcher.Domain.Content;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IVersionMetaClient _versionMeta;

    [RelayCommand]
    private void CreateInstance()
    {
        Download.SelectedCategory=Download.Categories.First(c=>c.Category==DownloadCategory.MinecraftVersion);
        Download.BackToVersionsCommand.Execute(null);
        ShowDownloads();
    }
}
