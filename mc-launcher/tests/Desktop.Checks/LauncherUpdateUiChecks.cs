using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Experience;
using Launcher.Application.Update;
using Launcher.Desktop;
using Microsoft.Extensions.DependencyInjection;

internal sealed class LauncherUpdateFixture : IUpdateClient
{
    public UpdateCheckResult Result = new(null, Reason: "已是最新（0.7.0-preview.1）");
    public string? Location;
    public bool Hold;
    public int Staged;
    public Task<UpdateCheckResult> CheckAsync(string currentVersion, string? location = null, CancellationToken ct = default)
    { Location = location; return Task.FromResult(Result); }
    public async Task<UpdateStaging> StageAsync(UpdateManifest manifest, string? location, string targetDirectory, string dataDirectory, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        Staged++; progress?.Report(new("下载", 35, "Fangyu.exe"));
        if(Hold) await Task.Delay(Timeout.Infinite, ct);
        return new(Path.Combine(dataDirectory, "update", manifest.Version), manifest, 1, 0);
    }
}

internal static class LauncherUpdateUiChecks
{
    public static void Run(Window window, Action<int> pump, Action<string, bool> check, Action<string, bool> capture)
    {
        void Until(Func<bool> done) { var timer = System.Diagnostics.Stopwatch.StartNew(); while(!done() && timer.ElapsedMilliseconds < 12000) pump(10); if(!done()) throw new TimeoutException("自动更新界面检查超时。"); pump(80); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        var old = window.DataContext; var fixture = new LauncherUpdateFixture();
        using var services = LauncherServices.Create(s => { s.AddSingleton<IUpdateClient>(fixture); var appearance = new AppearanceFixture(); s.AddSingleton<Launcher.Application.Accounts.IPlayerAppearanceClient>(appearance); s.AddSingleton<Launcher.Application.Accounts.ISkinService>(appearance); });
        var model = services.GetRequiredService<MainViewModel>();
        var handoffs = 0; model.LaunchSelfUpdater = (_, _, _) => { handoffs++; return Task.CompletedTask; };
        try
        {
            window.DataContext = model; pump(150); model.OpenLauncherUpdatesCommand.Execute(null); pump(100);
            check("设置提供关于与更新且不混入网络内容", model.ShowsLauncherUpdates && !model.IsNetworkSettings && !model.ShowsStorage);
            check("更新显示包含预览标识的当前版本", model.LauncherCurrentVersion.Contains("preview") && Find<Button>("CheckLauncherUpdateButton").Command == model.CheckUpdateCommand);
            Wait(model.CheckUpdateCommand.ExecuteAsync(null));
            check("默认更新地址指向签名清单", fixture.Location == MainViewModel.LauncherUpdateFeed && !model.HasUpdate && model.UpdateText.Contains("已是最新"));
            var manifest = new UpdateManifest("0.7.0-preview.2", [new("Fangyu.exe", "https://fixture/Fangyu.exe", new string('a',64), 20000000)], "改进动画\n修复下载问题", Entry: "Fangyu.exe");
            fixture.Result = new(manifest, "https://fixture/update.json"); Wait(model.CheckUpdateCommand.ExecuteAsync(null));
            check("检测新版后显示版本大小及更新说明", model.HasUpdate && model.LauncherUpdateNotes.Contains("动画") && model.LauncherUpdateVersion == manifest.Version && model.CanInstallLauncherUpdate);
            check("发现更新会在侧栏提示", Find<Button>("LauncherUpdateNotice").IsVisible);
            model.SelectedTheme = model.ThemeChoices.Single(t => t.Id == "glacier"); capture("launcher-update-light", true);
            model.ConfirmLauncherUpdateCommand.Execute(null); check("下载更新前确认重启与数据保留", model.IsConfirmOpen && model.ConfirmMessage.Contains("账号") && model.ConfirmDetail.Contains(manifest.Version));
            model.ConfirmCancelCommand.Execute(null); check("取消确认不开始下载", fixture.Staged == 0);
            model.IsBusy = true; check("游戏安装忙碌时禁止更新确认", !model.CanInstallLauncherUpdate); model.IsBusy = false;
            fixture.Hold = true; model.ConfirmLauncherUpdateCommand.Execute(null); model.ConfirmOkCommand.Execute(null); Until(() => model.IsUpdating);
            check("下载时禁止并发检查且允许取消", !model.CanCheckLauncherUpdate && Find<Button>("CancelLauncherUpdateButton").IsVisible);
            model.CancelLauncherUpdateCommand.Execute(null); Until(() => !model.IsUpdating);
            check("取消更新恢复界面且未退出交接", !model.IsBusy && model.UpdateText.Contains("取消") && handoffs == 0);
            fixture.Hold = false; model.ConfirmLauncherUpdateCommand.Execute(null); model.ConfirmOkCommand.Execute(null); Until(() => handoffs == 1 && !model.IsUpdating);
            check("校验暂存完成后才交接独立更新器", handoffs == 1 && fixture.Staged == 2 && model.UpdateText.Contains("重启"));
            fixture.Result = new(null, Reason: "更新验证失败：签名不匹配", Failed: true); Wait(model.CheckUpdateCommand.ExecuteAsync(null));
            check("签名失败明确展示且清除旧更新入口", model.LauncherUpdateFailed && !model.HasUpdate && model.UpdateText.Contains("签名") && !model.CanInstallLauncherUpdate);
            model.AutomaticLauncherUpdates = false; check("自动检查开关写入本机设置", ExperienceFiles.Read<LauncherUpdatePreferences>(Path.Combine(AppPaths.DataDirectory,"launcher-update.json")) is { Automatic: false });
            model.AutomaticLauncherUpdates = true;
            model.OpenNetworkCommand.Execute(null); check("更新页面可切回网络", model.IsNetworkSettings && !model.ShowsLauncherUpdates);
            model.OpenLauncherUpdatesCommand.Execute(null); model.OpenAppearanceCommand.Execute(null); check("更新页面可切回外观", model.IsAppearanceSettings && !model.ShowsLauncherUpdates);
            model.OpenLauncherUpdatesCommand.Execute(null); fixture.Result = new(manifest, "https://fixture/update.json"); Wait(model.CheckUpdateCommand.ExecuteAsync(null));
            model.SelectedTheme = model.ThemeChoices.Single(t => t.Id == "aurora"); capture("launcher-update-dark", true);
            window.Width = 900; window.Height = 640; pump(120); capture("launcher-update-compact", true);
            check("最小窗口更新入口仍可见", Find<Button>("CheckLauncherUpdateButton").TranslatePoint(default, window) is { X: >= 0, Y: >= 0 });
            model.AutomaticLauncherUpdates = false;
            using(var resumed = LauncherServices.Create()) check("重建启动器保留关闭自动检查设置", !resumed.GetRequiredService<MainViewModel>().AutomaticLauncherUpdates);
            model.AutomaticLauncherUpdates = true;
        }
        finally { window.DataContext = old; window.Width = 1180; window.Height = 800; }
    }
}
