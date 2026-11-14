using Launcher.App;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Launcher.App.ViewModels;
using Launcher.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Launcher.Desktop;

public sealed partial class App : Avalonia.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private ServiceProvider? _services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var boot = Stopwatch.StartNew();
            _mutex = new Mutex(false, OperatingSystem.IsWindows() ? @"Local\MCLauncher.SingleInstance" : "MCLauncher.SingleInstance");
            try { _ownsMutex = _mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            // 已有实例在跑：安静退出。这里不能调 desktop.Shutdown() —— 框架之后仍会进入已经关掉的
            // 消息循环，表现为 0xE0434352 崩溃（事件日志：Dispatcher shut down）。
            // 无头检查与多开调试用 FANGYU_ALLOW_MULTI_INSTANCE=1 跳过这层。
            if (!_ownsMutex && Environment.GetEnvironmentVariable("FANGYU_ALLOW_MULTI_INSTANCE") != "1")
            {
                _mutex.Dispose();
                Environment.Exit(0);
            }

            AppPaths.EnsureCreated();
            // FlClash/M3 式按压反馈：类级注册一次，之后所有 Button 都带水波纹。
            Launcher.Desktop.Controls.Ripple.Install();
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(AppPaths.LogsDirectory, "desktop-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
                .CreateLogger();
            _services = LauncherServices.Create();
            var model = _services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = model };
            PlatformUi.Window = window;
            desktop.MainWindow = window;
            Log.Information("[desktop boot] 窗口构造 {Ms} ms", boot.ElapsedMilliseconds);
            window.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                Log.Information("[desktop boot] 界面就绪 {Ms} ms", boot.ElapsedMilliseconds);
                Launcher.Application.Update.SingleFileUpdate.AcknowledgeStartup(AppPaths.DataDirectory);
                // No network, Java scan, or image decoding is needed to construct the first screen.
                if(Environment.GetEnvironmentVariable("FANGYU_SMOKE_TEST")=="1")
                    DispatcherTimer.RunOnce(()=>desktop.Shutdown(),TimeSpan.FromSeconds(2));
                else model.StartBackgroundWork(checkUpdates: true);
                await model.LoadAppearanceWallpaperAsync();
            }, DispatcherPriority.Background);
            desktop.Exit += (_, _) =>
            {
                model.FlushAppearance();
                model.Multiplayer?.Dispose();
                model.WallpaperImage?.Dispose();
                _services?.Dispose();
                Log.CloseAndFlush();
                if (_ownsMutex) { _mutex?.ReleaseMutex(); _ownsMutex = false; }
                _mutex?.Dispose();
                PlatformUi.Window = null;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "未处理异常");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
