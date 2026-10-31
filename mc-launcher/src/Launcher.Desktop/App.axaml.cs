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
            if (!_ownsMutex) { desktop.Shutdown(); return; }

            AppPaths.EnsureCreated();
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
