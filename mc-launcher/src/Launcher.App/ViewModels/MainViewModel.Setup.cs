using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Abstractions;
using Launcher.Application.Auth;
using Launcher.Application.Diagnostics;
using Launcher.Application.Install;
using Launcher.Domain.Versions;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    private IVersionMetaClient _setupMeta = null!;
    private FirstRunInstaller _setupInstaller = null!;
    public ObservableCollection<ManifestEntry> SetupVersions { get; } = [];

    [ObservableProperty] private bool _isSetupOpen;
    [ObservableProperty] private bool _isSetupBusy;
    [ObservableProperty] private bool _isLoadingSetupVersions;
    [ObservableProperty] private ManifestEntry? _setupVersion;
    [ObservableProperty] private string _setupOfflineName = OfflineIdentityFactory.DefaultName;
    [ObservableProperty] private string _setupMemoryText = InstanceSettings.AdaptiveMemoryMb().ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _setupError = string.Empty;
    [ObservableProperty] private string _setupStatus = "选择版本后即可安装，Java 会自动准备。";
    [ObservableProperty] private bool _setupLaunchAfterInstall = true;

    public bool CanEditSetup => !IsSetupBusy;
    public string SetupIdentityText => Account is null ? "使用离线身份：3–16 位字母、数字或下划线。" : $"将使用已登录的正版账号：{Account.Name}";

    private void InitializeSetup(IVersionMetaClient meta)
    {
        _setupMeta = meta;
        _setupInstaller = new FirstRunInstaller(_installer, meta, _javaProvisioner);
        SetupOfflineName = _library?.OfflineIdentity.Name ?? OfflineIdentityFactory.DefaultName;
    }

    partial void OnIsSetupBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditSetup));
        InstallSetupCommand.NotifyCanExecuteChanged();
        CloseSetupCommand.NotifyCanExecuteChanged();
        ReloadSetupVersionsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingSetupVersionsChanged(bool value)
    {
        InstallSetupCommand.NotifyCanExecuteChanged();
        ReloadSetupVersionsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenSetupAsync()
    {
        if (IsBusy || Download.IsBusy || IsLoggingIn || IsUpdating) { StatusText = "请等待当前任务结束后再打开安装向导"; return; }
        IsSetupOpen = true;
        SetupError = string.Empty;
        await LoadSetupVersionsAsync();
    }

    private bool CanCloseSetup() => !IsSetupBusy && !IsLoggingIn;

    [RelayCommand(CanExecute = nameof(CanCloseSetup))]
    private void CloseSetup() => IsSetupOpen = false;

    private bool CanReloadSetupVersions() => !IsSetupBusy && !IsLoadingSetupVersions;

    [RelayCommand(CanExecute = nameof(CanReloadSetupVersions))]
    private Task ReloadSetupVersionsAsync() => LoadSetupVersionsAsync(force: true);

    private async Task LoadSetupVersionsAsync(bool force = false)
    {
        if (IsLoadingSetupVersions || (!force && SetupVersions.Count > 0)) return;
        IsLoadingSetupVersions = true;
        SetupError = string.Empty;
        SetupStatus = "正在获取正式版列表…";
        try
        {
            var manifest = await _setupMeta.GetManifestAsync();
            var previous = SetupVersion?.Id;
            SetupVersions.Clear();
            foreach (var entry in manifest.Versions.Where(v => v.Type == "release").OrderByDescending(v => v.ReleaseTime))
                SetupVersions.Add(entry);
            SetupVersion = SetupVersions.FirstOrDefault(v => v.Id == previous)
                ?? SetupVersions.FirstOrDefault(v => v.Id == manifest.Latest?.Release) ?? SetupVersions.FirstOrDefault();
            SetupStatus = "游戏和 Java 将自动安装，可随时取消；再次安装会复用已下载的文件。";
            if (SetupVersion is null) SetupError = "没有获取到正式版，请刷新列表重试。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "安装向导读取版本失败");
            SetupError = "获取版本失败：" + ErrorText.Describe(ex);
            SetupStatus = "检查网络或下载源后，点击「刷新版本」。";
        }
        finally { IsLoadingSetupVersions = false; }
    }

    private bool CanInstallSetup() => !IsSetupBusy && !IsLoadingSetupVersions && !IsLoggingIn && !IsBusy && !Download.IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstallSetup))]
    private async Task InstallSetupAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "安装游戏");
        if (operation is null) { SetupError = StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        SetupError = string.Empty;
        if (SetupVersion is not { } version) { SetupError = "请先选择一个正式版。"; return; }
        if (!int.TryParse(SetupMemoryText, out var memory) || memory is < InstanceSettings.MinMemoryMb or > InstanceSettings.MaxMemoryMb)
        { SetupError = "内存请输入 512–65536 之间的整数，单位 MB。"; return; }
        if (Account is null && !OfflineIdentityFactory.IsValidName(SetupOfflineName))
        { SetupError = "离线用户名只能包含 3–16 位字母、数字或下划线。"; return; }

        var launch = SetupLaunchAfterInstall;
        var succeeded = false;
        IsSetupBusy = true;
        IsBusy = true;
        ProgressPercent = 0;
        using var cts = new CancellationTokenSource();
        _runningCts = cts;
        try
        {
            if (Account is null) OfflineName = SetupOfflineName;
            SetupStatus = StatusText = $"正在安装 {version.Id}…";
            await _setupInstaller.InstallAsync(new(version.Id, memory), GameRoot, AppPaths.DataDirectory,
                new Progress<FirstRunProgress>(p =>
                {
                    if (!IsSetupBusy) return;
                    ProgressPercent = p.Percent;
                    SetupStatus = StatusText = $"{p.Stage} · {p.Percent:F0}%" + (p.Detail is null ? "" : $" · {p.Detail}");
                }), cts.Token);
            RefreshInstances();
            SelectedInstance = Instances.FirstOrDefault(i => i.InstanceId == version.Id);
            IsInstanceSection = true;
            SetupStatus = StatusText = $"{version.Id} 已准备好，可以启动游戏。";
            succeeded = true;
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            SetupStatus = StatusText = "安装已取消，已下载的文件会在重试时复用。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装向导失败：{Version}", version.Id);
            SetupError = "安装失败：" + ErrorText.Describe(ex);
            SetupStatus = StatusText = "可检查网络和下载源后重试，或返回主界面。";
        }
        finally
        {
            if (ReferenceEquals(_runningCts, cts)) _runningCts = null;
            IsBusy = false;
            IsSetupBusy = false;
            ProgressPercent = 0;
        }
        operation.Dispose();
        if (succeeded)
        {
            IsSetupOpen = false;
            if (launch) await LaunchAsync();
        }
    }
}
