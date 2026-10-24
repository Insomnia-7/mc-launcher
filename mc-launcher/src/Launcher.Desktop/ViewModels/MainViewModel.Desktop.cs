using CommunityToolkit.Mvvm.ComponentModel;
using Launcher.Application.Settings;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    public bool IsLightTheme
    {
        get => SelectedTheme?.Id == "glacier";
        set
        {
            if (value == IsLightTheme) return;
            SelectedTheme = ThemeChoices.FirstOrDefault(t => t.Id == (value ? "glacier" : "aurora"));
        }
    }
    public bool HasInstances => Instances.Count > 0;
    public string InstanceCountText => $"{Instances.Count:00} 个世界";
    public string WorldVersion => SelectedInstance?.BaseVersion ?? "添加你的第一个世界";
    public string WorldDisplayName => SelectedInstance?.DisplayName ?? "";
    public bool IsModpackSelection => SelectedInstance?.IsModpack == true;
    public string WorldKind => SelectedInstance is { IsModpack: true } ? "整合包" : SelectedInstance is { IsLoader: true } ? SelectedInstance.Subtitle : "原版 · Java Edition";
    public string MemoryText => $"{MemoryMb / 1024d:0.#} GB";
    public string JavaText => SelectedJava is { ExecutablePath: not null } java ? java.Title : "自动选择";
    public string LaunchTitle => IsBusy ? "正在准备…" : "启动游戏";
    public string WorldCaption => HasInstances ? "你的世界，随时出发。" : "从一个新世界开始。";
    public bool HasOverlay => IsSetupOpen || IsAccountsOpen || IsAppearanceOpen || IsNetworkOpen || IsConfirmOpen;
    partial void OnSkinHeadChanging(object? value) { if (SkinHead is IDisposable old) old.Dispose(); }
    partial void OnIsAccountsOpenChanged(bool value)
    {
        if(value && !PlatformUi.CanStoreSecrets(_secretProtector)) AccountHint="此系统的安全账号存储尚未接入，当前可使用离线身份；微软登录会在安全存储接入后开放。";
    }

    private void InitializeDesktopShell()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsSetupOpen) or nameof(IsAccountsOpen) or nameof(IsAppearanceOpen) or nameof(IsNetworkOpen) or nameof(IsConfirmOpen)) OnPropertyChanged(nameof(HasOverlay));
            if (e.PropertyName == nameof(SelectedInstance))
            {
                OnPropertyChanged(nameof(WorldVersion)); OnPropertyChanged(nameof(WorldKind)); OnPropertyChanged(nameof(WorldDisplayName)); OnPropertyChanged(nameof(IsModpackSelection));
            }
            if (e.PropertyName == nameof(MemoryMb)) OnPropertyChanged(nameof(MemoryText));
            if (e.PropertyName == nameof(SelectedJava)) OnPropertyChanged(nameof(JavaText));
            if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(LaunchTitle));
        };
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstances)); OnPropertyChanged(nameof(InstanceCountText)); OnPropertyChanged(nameof(WorldCaption));
        };
    }
}
