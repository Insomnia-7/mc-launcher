using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;
using Launcher.Application.Java;
using Launcher.Domain.Loaders;
using Launcher.Domain.Modpacks;

namespace Launcher.App.ViewModels;

public sealed partial class InstallLoaderChoice(LoaderKind kind, string title, string description, string icon) : ObservableObject
{
    public LoaderKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Icon { get; } = icon;
    public System.Windows.Input.ICommand? SelectCommand { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class DownloadViewModel
{
    private readonly ILoaderMetaClient _loaderMeta;
    private readonly LoaderInstaller _loaderInstaller;
    private readonly IForgeInstaller _forgeInstaller;
    private readonly INeoForgeInstaller _neoForgeInstaller;
    private readonly IJavaProvisioner _javaProvisioner;
    private CancellationTokenSource? _loaderQueryCts;

    public ObservableCollection<InstallLoaderChoice> InstallLoaders { get; } = [];
    public ObservableCollection<string> InstallLoaderVersions { get; } = [];
    [ObservableProperty] private InstallLoaderChoice? _selectedInstallLoader;
    [ObservableProperty] private string? _selectedInstallLoaderVersion;
    [ObservableProperty] private bool _isLoadingLoaderVersions;
    [ObservableProperty] private string _loaderVersionStatus = string.Empty;

    public string InstallGameVersion => SelectedVersion?.Id ?? string.Empty;
    public bool ShowsBrowseControls => !IsVersionConfiguration && !IsResourceDetail;
    public string DiscoverHeading => IsVersionConfiguration ? "安装游戏" : "发现资源";
    public bool IsVersionList => IsVersionMode && SelectedVersion is null;
    public bool IsVersionConfiguration => IsVersionMode && SelectedVersion is not null;
    public bool ShowsInstallLoaderVersions => SelectedInstallLoader?.Kind is not null and not LoaderKind.Vanilla;
    public bool CanConfigureVersion => !IsBusy;
    public string InstallSummary => SelectedVersion is not { } game ? "先选择 Minecraft 版本"
        : !ShowsInstallLoaderVersions ? $"Minecraft {game.Id} · 仅安装原版"
        : $"Minecraft {game.Id} · {SelectedInstallLoader?.Title} {SelectedInstallLoaderVersion ?? "（请选择加载器版本）"}";

    private void InitializeInstallLoaders()
    {
        InstallLoaders.Add(new(LoaderKind.Vanilla, "仅安装原版", "不安装 Mod 加载器", "Tile.LoaderVanilla") { SelectCommand = ChooseInstallLoaderCommand });
        InstallLoaders.Add(new(LoaderKind.Fabric, "Fabric", "轻量模组生态", "Tile.LoaderFabric") { SelectCommand = ChooseInstallLoaderCommand });
        InstallLoaders.Add(new(LoaderKind.Quilt, "Quilt", "Quilt 模组生态", "Tile.LoaderQuilt") { SelectCommand = ChooseInstallLoaderCommand });
        InstallLoaders.Add(new(LoaderKind.Forge, "Forge", "Forge 模组生态", "Tile.LoaderForge") { SelectCommand = ChooseInstallLoaderCommand });
        InstallLoaders.Add(new(LoaderKind.NeoForge, "NeoForge", "NeoForge 模组生态", "Tile.LoaderNeoforge") { SelectCommand = ChooseInstallLoaderCommand });
        SelectedInstallLoader = InstallLoaders[0];
    }

    [RelayCommand(CanExecute = nameof(CanConfigureVersion))]
    private void BackToVersions() => SelectedVersion = null;

    [RelayCommand]
    private void ChooseInstallLoader(InstallLoaderChoice? choice)
    {
        if (!IsBusy && choice is not null && InstallLoaders.Contains(choice)) SelectedInstallLoader = choice;
    }

    partial void OnSelectedInstallLoaderChanged(InstallLoaderChoice? value)
    {
        foreach (var choice in InstallLoaders) choice.IsSelected = ReferenceEquals(choice, value);
        OnPropertyChanged(nameof(ShowsInstallLoaderVersions));
        _ = ReloadLoaderVersionsAsync();
    }

    partial void OnSelectedInstallLoaderVersionChanged(string? value) => NotifyInstallSelection();
    partial void OnIsLoadingLoaderVersionsChanged(bool value) => InstallVersionCommand.NotifyCanExecuteChanged();

    private void NotifyInstallSelection()
    {
        OnPropertyChanged(nameof(InstallSummary));
        InstallVersionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ReloadLoaderVersionsAsync()
    {
        if (IsBusy) return;
        _loaderQueryCts?.Cancel();
        InstallLoaderVersions.Clear();
        SelectedInstallLoaderVersion = null;
        LoaderVersionStatus = string.Empty;
        IsLoadingLoaderVersions = false;
        NotifyInstallSelection();
        if (SelectedVersion is not { } game || SelectedInstallLoader is not { Kind: not LoaderKind.Vanilla } loader) return;

        using var cts = new CancellationTokenSource();
        _loaderQueryCts = cts;
        IsLoadingLoaderVersions = true;
        LoaderVersionStatus = $"正在查询适用于 {game.Id} 的 {loader.Title} 版本…";
        try
        {
            var versions = await _loaderMeta.GetLoaderVersionsAsync(loader.Kind, game.Id, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedVersion, game) || !ReferenceEquals(SelectedInstallLoader, loader)) return;
            foreach (var version in versions) InstallLoaderVersions.Add(version);
            SelectedInstallLoaderVersion = InstallLoaderVersions.FirstOrDefault();
            LoaderVersionStatus = versions.Count == 0 ? $"暂无适用于 {game.Id} 的 {loader.Title} 版本，可切换加载器或仅安装原版。"
                : $"{versions.Count} 个可用版本 · 新版本在前，带 beta / alpha 的版本为测试版。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && ReferenceEquals(_loaderQueryCts, cts))
                LoaderVersionStatus = "加载版本列表失败，可点击重试：" + Launcher.Application.Diagnostics.ErrorText.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_loaderQueryCts, cts))
            {
                _loaderQueryCts = null;
                IsLoadingLoaderVersions = false;
                NotifyInstallSelection();
            }
        }
    }

    private async Task<string> InstallSelectedLoaderAsync(string game, string root, LoaderKind kind, string version,
        IProgress<DownloadProgress> downloadProgress, CancellationToken ct)
    {
        if (kind is LoaderKind.Fabric or LoaderKind.Quilt)
            return (await _loaderInstaller.InstallAsync(game, kind, root, version, downloadProgress, ct)).VersionId;

        var metadata = await _meta.GetVersionAsync(game, ct);
        var requiredJava = Math.Max(17, metadata.JavaVersion?.MajorVersion ?? 8);
        Status = $"准备 Java {requiredJava}，用于安装 {kind}…";
        await _javaProvisioner.EnsureAsync(requiredJava, AppPaths.DataDirectory,
            new Progress<JavaProvisionProgress>(p => { ProgressPercent = p.Percent; Status = $"{p.Stage} {p.Detail}"; }), ct);
        var progress = new Progress<ModpackImportProgress>(p => { ProgressPercent = p.Percent; Status = $"{p.Stage} {p.Detail}"; });
        return kind switch
        {
            LoaderKind.Forge => await _forgeInstaller.InstallAsync(game, version, root, progress, ct),
            LoaderKind.NeoForge => await _neoForgeInstaller.InstallAsync(game, version, root, progress, ct),
            _ => throw new NotSupportedException($"不支持的加载器：{kind}"),
        };
    }
}
