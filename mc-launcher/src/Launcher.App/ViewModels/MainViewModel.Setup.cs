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
    /// <summary>上一次安装失败了：界面上给一个「重试安装」，不用重新挑版本。</summary>
    [ObservableProperty] private bool _canRetrySetup;
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
        NotifyAccountActions();
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

    /// <summary>安装向导装完后通知窗口：把新版本滚进视野并给一次高亮脉冲。</summary>
    public event Action<InstanceCard>? InstanceRevealed;

    private bool CanInstallSetup() => !IsSetupBusy && !IsLoadingSetupVersions && !IsLoggingIn && !IsBusy && !Download.IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstallSetup))]
    private async Task InstallSetupAsync()
    {
        using var operation = Launcher.Application.Operations.GameOperationCoordinator.Shared.TryEnter(GameRoot, "安装游戏");
        if (operation is null) { SetupError = StatusText = Launcher.Application.Operations.GameOperationCoordinator.Shared.BusyMessage(GameRoot); return; }
        SetupError = string.Empty;
        if (HasGameRootProblem) { SetupError = GameRootProblem; return; }
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
        _setupClock.Restart();
        try
        {
            if (Account is null)
            {
                OfflineName = SetupOfflineName;
                if (_library?.OfflineIdentity.Name != SetupOfflineName)
                    throw new InvalidOperationException("无法保存离线身份，请检查数据目录后重试。");
            }
            CanRetrySetup = false;
        SetupStatus = StatusText = $"正在安装 {version.Id}…";
            await _setupInstaller.InstallAsync(new(version.Id, memory), GameRoot, AppPaths.DataDirectory,
                new Progress<FirstRunProgress>(p =>
                {
                    if (!IsSetupBusy) return;
                    ProgressPercent = p.Percent;
                    SetupStatus = StatusText = DescribeSetupProgress(p);
                }), cts.Token);
            RefreshInstances();
            SelectedInstance = Instances.FirstOrDefault(i => i.InstanceId == version.Id);
            IsInstanceSection = true;
            // 列表可能滚到了别处：让窗口把它滚进视野并闪一下，证明「新装的是这一行」
            if (SelectedInstance is { } revealed) InstanceRevealed?.Invoke(revealed);
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
            CanRetrySetup = true;
        SetupStatus = StatusText = "可点「重试安装」再来一次，或检查网络和下载源后返回主界面。";
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
    /// <summary>安装向导的秒表：算「还要多久」需要一个起点，进度回调里没有时间信息。</summary>
    private readonly System.Diagnostics.Stopwatch _setupClock = new();

    /// <summary>
    /// 把进度拼成一句话：「下载游戏 · 35% · 162.4 / 463 MB · 还剩 1 分 12 秒 · d8bfc754」。
    /// <para>
    /// 只有百分比时用户没法判断还要等 10 秒还是 10 分钟；字节数与剩余时间是这条链路上最容易补的信息。
    /// 剩余时间用「已下载 / 已耗时」的全程平均速率估算——不追求精确，够让人决定要不要去泡杯茶。
    /// </para>
    /// </summary>
    private string DescribeSetupProgress(FirstRunProgress progress)
    {
        var text = $"{progress.Stage} · {progress.Percent:F0}%";

        if (progress.HasBytes)
        {
            text += $" · {progress.CompletedMegabytes:N1} / {progress.TotalMegabytes:N0} MB";

            var elapsed = _setupClock.Elapsed.TotalSeconds;
            if (elapsed > 3 && progress.CompletedBytes > 0)
            {
                var rate = progress.CompletedBytes / elapsed;
                if (rate > 1024)
                {
                    var remaining = (progress.TotalBytes - progress.CompletedBytes) / rate;
                    if (remaining > 1) text += $" · 还剩 {FormatRemaining(remaining)}";
                }
            }
        }

        if (progress.Detail is { Length: > 0 } detail) text += $" · {ShortenDetail(detail)}";
        return text;
    }

    private static string FormatRemaining(double seconds)
        => seconds < 60
            ? $"{seconds:F0} 秒"
            : seconds < 3600
                ? $"{(int)(seconds / 60)} 分 {seconds % 60:F0} 秒"
                : $"{(int)(seconds / 3600)} 小时 {(int)(seconds % 3600 / 60)} 分";

    /// <summary>进度里的 Detail 可能是很长的相对路径，取最后一段并截断，避免把状态栏撑爆。</summary>
    private static string ShortenDetail(string detail)
    {
        var cut = detail.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 && cut + 1 < detail.Length ? detail[(cut + 1)..] : detail;
        return name.Length <= 28 ? name : name[..27] + "…";
    }
}
