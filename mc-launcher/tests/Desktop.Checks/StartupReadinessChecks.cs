using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Accounts;
using Launcher.Application.Auth;
using Launcher.Desktop;
using Microsoft.Extensions.DependencyInjection;

internal static class StartupReadinessChecks
{
    private sealed class SlowSkin : ISkinService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public bool Cancelled;
        public string? Url;
        public Task<SkinFetchResult> FetchAsync(string uuid, string name, CancellationToken ct = default)
        {
            Started.TrySetResult();
            try { Release.Wait(ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return Task.FromResult(Url is null ? SkinFetchResult.Failure("fixture unavailable") :
                SkinFetchResult.Success(new PlayerSkin(uuid, name, Url, null, "classic", DateTimeOffset.UtcNow)));
        }
    }

    internal static async Task Run(string root, Action<string, bool> check)
    {
        var isolatedData = Path.GetFullPath(Path.Combine(root, "data"));
        if (!string.Equals(Path.GetFullPath(AppPaths.DataDirectory), isolatedData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Startup fixtures require the isolated test data directory.");
        var accounts = AccountLibrary.Load(AppPaths.DataDirectory);
        accounts.AddOrUpdate(new MicrosoftAccount("00112233445566778899aabbccddeeff", "StartupFixture", "fixture", "fixture", DateTimeOffset.UtcNow.AddDays(1)));
        accounts.Save(AppPaths.DataDirectory);
        // A local texture response is held independently of the profile response.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var textureRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTexture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            textureRequested.SetResult();
            await releaseTexture.Task;
            using var icon = AssetLoader.Open(new Uri("avares://MCLauncher.Desktop/Assets/Brand/fangyu.png"));
            using var bytes = new MemoryStream(); icon.CopyTo(bytes);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes.ToArray());
        });
        var skin = new SlowSkin { Url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/skin.png" };
        using var services = LauncherServices.Create(s => s.AddSingleton<ISkinService>(skin));
        var model = services.GetRequiredService<MainViewModel>();
        model.SelectedMotion = model.MotionChoices.Single(x => x.Id == "smooth");
        var main = new MainWindow { DataContext = model };
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var openedWithAvatar = false;
        main.Opened += (_, _) => openedWithAvatar = model.HasSkinHead;
        check("正版账号采用启动准备流程", StartupLogoWindow.BeforeMainWindow(lifetime, main));
        var splash = lifetime.MainWindow!; splash.Show();
        try
        {
            await skin.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(750);
            check("超过原固定等待时间仍保留启动图标且不暴露半成品主界面", splash.IsVisible && !main.IsVisible);
            check("皮肤服务同步等待不阻塞界面线程", !skin.Release.IsSet);
            skin.Release.Set();
            await textureRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check("头像纹理未完成时继续保留启动图标", splash.IsVisible && !main.IsVisible && !model.HasSkinHead);
            releaseTexture.SetResult(); await response;
            using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!main.IsVisible) await Task.Delay(20, readyDeadline.Token);
            check("主窗口首帧已经具备正版头像", openedWithAvatar && model.SkinHead is Avalonia.Media.Imaging.Bitmap);
            await Task.Delay(350);
            var selection = main.FindControl<Border>("NavSelection")!;
            var home = main.FindControl<Button>("HomeNav")!;
            check("导航入场期间高亮条不跟随负位移而被裁短", ((TranslateTransform)selection.RenderTransform!).X >= 0 && Math.Abs(selection.Width - home.Bounds.Width) < 0.1);
            await main.StartupEntranceCompletion;
            check("启动完成后导航高亮保持完整宽度", selection.Width > 180 && !splash.IsVisible);
        }
        finally { skin.Release.Set(); releaseTexture.TrySetResult(); main.Close(); splash.Close(); }

        foreach (var reason in new[] { "取消", "超时", "无皮肤" })
        {
            var fixture = new SlowSkin();
            using var caseServices = LauncherServices.Create(s => s.AddSingleton<ISkinService>(fixture));
            var caseModel = caseServices.GetRequiredService<MainViewModel>();
            caseModel.SelectedMotion = caseModel.MotionChoices.Single(x => x.Id == "off");
            var window = new MainWindow { DataContext = caseModel };
            var desktop = new ClassicDesktopStyleApplicationLifetime();
            StartupLogoWindow.BeforeMainWindow(desktop, window);
            var logo = desktop.MainWindow!; logo.Show();
            try
            {
                await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                check(reason + "：关闭动画仍在图标阶段等待头像", !window.IsVisible && logo.IsVisible);
                if (reason == "取消")
                {
                    logo.Close(); await Task.Delay(150);
                    check("关闭启动图标取消头像请求且不再显示主窗口", fixture.Cancelled && !window.IsVisible);
                }
                else
                {
                    if (reason == "无皮肤") fixture.Release.Set();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(11));
                    while (!window.IsVisible) await Task.Delay(20, deadline.Token);
                    check(reason + "：网络不可用时进入可操作主界面", !caseModel.HasSkinHead && window.IsVisible && window.IsHitTestVisible);
                    if (reason == "超时") check("头像请求达到八秒预算后被真正取消", fixture.Cancelled);
                }
            }
            finally { fixture.Release.Set(); window.Close(); logo.Close(); }
        }
    }
}
