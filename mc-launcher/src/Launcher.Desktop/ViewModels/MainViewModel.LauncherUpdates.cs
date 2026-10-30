using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App;
using Launcher.Application.Experience;
using Launcher.Application.Update;

namespace Launcher.App.ViewModels;

public sealed record LauncherUpdatePreferences(bool Automatic = true);

public sealed partial class MainViewModel
{
    public const string LauncherReleasePage = "https://github.com/Insomnia-7/mc-launcher/releases/latest";
    public const string LauncherUpdateFeed = "https://github.com/Insomnia-7/mc-launcher/releases/latest/download/update.json";
    [ObservableProperty] private bool _showsLauncherUpdates;
    [ObservableProperty] private bool _automaticLauncherUpdates = true;
    [ObservableProperty] private string _launcherUpdatePreviousResult = "";
    public string LauncherCurrentVersion => BuildInfo.Version;
    public bool CanCheckLauncherUpdate => !IsCheckingLauncherUpdate && !IsUpdating;
    public bool CanInstallLauncherUpdate => OperatingSystem.IsWindows() && HasUpdate && CanCheckLauncherUpdate && !IsBusy && !Download.IsBusy && !IsModBusy;
    public bool CanUseWindowsUpdater => OperatingSystem.IsWindows();
    private string LauncherUpdatePreferencesPath => Path.Combine(AppPaths.DataDirectory, "launcher-update.json");
    private void InitializeLauncherUpdates()
    {
        AutomaticLauncherUpdates = ExperienceFiles.Read<LauncherUpdatePreferences>(LauncherUpdatePreferencesPath)?.Automatic ?? true;
        DefaultUpdateLocation = LauncherUpdateFeed;
        ShouldCheckLauncherUpdates = () => AutomaticLauncherUpdates;
        LaunchSelfUpdater = StartSingleFileUpdaterAsync;
        var result = ExperienceFiles.Read<LauncherUpdateResult>(Path.Combine(AppPaths.DataDirectory, "update", "launcher-update-result.json"));
        if(result is { State: "completed" or "failed" }) LauncherUpdatePreviousResult = result.Message;
        if(UpdateText.Length == 0) UpdateText = "可检查启动器新版本，下载前会显示更新说明。";
        PropertyChanged += (_, e) =>
        {
            if(e.PropertyName is nameof(IsCheckingLauncherUpdate) or nameof(IsUpdating) or nameof(HasUpdate) or nameof(IsBusy) or nameof(IsModBusy))
            { OnPropertyChanged(nameof(CanCheckLauncherUpdate)); OnPropertyChanged(nameof(CanInstallLauncherUpdate)); }
        };
        Download.PropertyChanged += (_, e) => { if(e.PropertyName == nameof(Download.IsBusy)) OnPropertyChanged(nameof(CanInstallLauncherUpdate)); };
    }
    partial void OnAutomaticLauncherUpdatesChanged(bool value)
    {
        try { ExperienceFiles.Write(LauncherUpdatePreferencesPath, new LauncherUpdatePreferences(value)); }
        catch(Exception ex) { LauncherUpdatePreviousResult = "保存自动检查设置失败：" + ex.Message; }
    }
    partial void OnShowsLauncherUpdatesChanged(bool value)
    { OnPropertyChanged(nameof(IsNetworkSettings)); OnPropertyChanged(nameof(IsAppearanceSettings)); }
    [RelayCommand] private void OpenLauncherUpdates()
    { OpenNetwork(); ShowsLauncherUpdates = true; }
    [RelayCommand] private void ConfirmLauncherUpdate()
    {
        if(!CanInstallLauncherUpdate) return;
        var version = LauncherUpdateVersion;
        AskConfirm("更新方隅", "下载并校验新版本后，启动器会退出、替换并重新打开。账号、设置与游戏数据保留。", $"{BuildInfo.Version} → {version} · {LauncherUpdateSize}", "下载并更新", () =>
        {
            if(CanInstallLauncherUpdate && version == LauncherUpdateVersion) _ = ApplyUpdateCommand.ExecuteAsync(null);
        });
    }
    [RelayCommand] private void OpenLauncherReleasePage()
    {
        try { Process.Start(new ProcessStartInfo(LauncherReleasePage) { UseShellExecute = true }); }
        catch(Exception ex) { UpdateText = "无法打开发布页：" + ex.Message; }
    }
    private static async Task StartSingleFileUpdaterAsync(UpdateStaging staging, string target, CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows()) throw new InvalidOperationException("此平台暂请从发布页下载更新。");
        var executable = Environment.ProcessPath ?? throw new IOException("无法定位当前启动器。");
        if(!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("开发运行不支持自替换，请使用发布的单文件 EXE。");
        var helperDirectory = Path.Combine(AppPaths.DataDirectory, "update", "runners", Guid.NewGuid().ToString("N"));
        ExperienceFiles.RejectLinks(helperDirectory); Directory.CreateDirectory(helperDirectory);
        var helperPath = Path.Combine(helperDirectory, "Fangyu.Update.exe");
        await Task.Run(() => File.Copy(executable, helperPath), ct);
        var resultPath = SingleFileUpdate.ResultPath(staging.Directory);
        if(File.Exists(resultPath)) File.Delete(resultPath);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, WorkingDirectory = helperDirectory };
        foreach(var item in new[] { "--launcher-update", "--staging", staging.Directory, "--target", target, "--entry", Path.GetFileName(executable), "--wait-pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(item);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新器。");
        try
        {
            var clock = Stopwatch.StartNew();
            while(clock.Elapsed < TimeSpan.FromSeconds(40))
            {
                ct.ThrowIfCancellationRequested();
                var result = ExperienceFiles.Read<LauncherUpdateResult>(resultPath);
                if(result is { State: "ready" } && result.Version == staging.Manifest.Version) return;
                if(result is { State: "failed" }) throw new IOException(result.Message);
                if(process.HasExited) throw new IOException("更新器提前退出，旧版保持不变。");
                await Task.Delay(100, ct);
            }
            throw new IOException("更新器未能就绪，旧版保持不变，请重新尝试。");
        }
        catch
        {
            if(!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            throw;
        }
    }
}
