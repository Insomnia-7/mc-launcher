using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Auth;
using Launcher.Application.Export;
using Launcher.Application.Java;
using Launcher.Infrastructure.Auth;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Update;
using Launcher.Application.Versions;
using Launcher.Infrastructure;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Http;
using Launcher.Infrastructure.Java;
using Launcher.Infrastructure.Loaders;
using Launcher.Infrastructure.Meta;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Modrinth;
using Launcher.Infrastructure.Processes;
using Launcher.Infrastructure.Security;
using Launcher.Infrastructure.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Launcher.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\MCLauncher.SingleInstance";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private ServiceProvider? _services;

    /// <summary>给额外窗口（Mod 市场等）拿服务的入口。</summary>
    public static IServiceProvider? CurrentServices { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        
        var boot = System.Diagnostics.Stopwatch.StartNew();
base.OnStartup(e);

        // 单实例：零等待抢所有权。不能用 createdNew 判断 —— 那个参数只在「本次创建」时生效，
        // 第二个进程拿到的是自己并不拥有的 Mutex，退出时 ReleaseMutex 会抛
        // ApplicationException（Object synchronization method was called from an unsynchronized block of code），
        // 表现就是「窗口闪一下、进程崩在 OnExit」。
        _instanceMutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            _ownsMutex = _instanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出留下的，所有权已经转到我们手上
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            MessageBox.Show("启动器已经在运行了。", "MC Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "launcher-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("启动器启动 v{Version}，数据目录 {DataDir}", BuildInfo.Version, AppPaths.DataDirectory);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "未处理异常");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: true));

        services.AddSingleton(_ => MojangMetaClient.CreateHttpClient(BuildInfo.Name, BuildInfo.Version));

        // 下载源。DownloadSourceProvider 每次改写前 stat 一次 settings.json 的写入时间，
        // 变了才重读，所以界面上切源后下一批下载立刻走新源，不用重建 DI 图。
        services.AddSingleton(_ => new DownloadSourceProvider(AppPaths.DataDirectory));
        services.AddSingleton<IUrlRewriter>(sp => sp.GetRequiredService<DownloadSourceProvider>());

        services.AddSingleton<IVersionMetaClient>(sp => new MojangMetaClient(
            sp.GetRequiredService<HttpClient>(),
            cacheDirectory: Path.Combine(AppPaths.CacheDirectory, "meta"),
            urlRewriter: sp.GetRequiredService<IUrlRewriter>()));
        // 装饰器放在最外层：Forge installer、整合包、Mod 文件走的都是这个 IDownloader，
        // 换源对它们一起生效。传委托而不是实例，是为了拿得到"当前"的源。
        services.AddSingleton<IDownloader>(sp => new RewritingDownloader(
            new HttpDownloader(
                sp.GetRequiredService<HttpClient>(),
                new DownloadOptions(MaxConcurrency: 16)),
            () => sp.GetRequiredService<IUrlRewriter>()));

        // 额外扫描数据目录下的 java/<major>/bin/java.exe（JavaProvisioner 装在这）
        services.AddSingleton<IJavaLocator>(_ =>
            new JavaLocator([Path.Combine(AppPaths.DataDirectory, "java")]));
        services.AddSingleton<IProcessLauncher, WinProcessLauncher>();
        services.AddSingleton<ILoaderMetaClient>(sp => new LoaderMetaClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IUrlRewriter>()));
        services.AddSingleton<VersionLoader>();
        services.AddSingleton<IMicrosoftAuthService>(_ => new MicrosoftAuthService(
            MicrosoftAuthService.CreateHttpClient($"{BuildInfo.Name}/{BuildInfo.Version}")));
        // Java 运行时是单个 49 MB 的大文件，慢网下用 16 并发那套默认超时（单文件 120 秒）
        // 必然超时失败。给它单独一个下载器：并发低、单文件超时长、停滞判定更宽松。
        services.AddSingleton<IJavaProvisioner>(sp => new JavaProvisioner(
            JavaProvisioner.CreateHttpClient(),
            // 专用下载 client + 30 分钟单文件窗口：Java 安装包在慢网上会跑十几分钟，
            // 复用 Mojang 元数据那个 100s 的 HttpClient 每 100 秒就被掐一次。
            new HttpDownloader(
                JavaProvisioner.CreateDownloadHttpClient(),
                new DownloadOptions(
                    MaxConcurrency: 4,
                    PerFileTimeout: TimeSpan.FromMinutes(30),
                    StallTimeout: TimeSpan.FromSeconds(90))),
            sp.GetRequiredService<IJavaLocator>()));
        services.AddSingleton<GameInstaller>(sp => new GameInstaller(
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<IVersionMetaClient>(),
            sp.GetRequiredService<IUrlRewriter>()));
        services.AddSingleton<LoaderInstaller>(sp => new LoaderInstaller(
            sp.GetRequiredService<ILoaderMetaClient>(),
            sp.GetRequiredService<IVersionMetaClient>(),
            sp.GetRequiredService<GameInstaller>()));
        services.AddSingleton<IForgeInstaller>(sp => new ForgeInstallerRunner(
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<IJavaLocator>(),
            urlRewriter: sp.GetRequiredService<IUrlRewriter>()));
        services.AddSingleton<INeoForgeInstaller>(sp => new NeoForgeInstallerRunner(
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<IJavaLocator>(),
            urlRewriter: sp.GetRequiredService<IUrlRewriter>()));
        services.AddSingleton<IModpackSourceFactory>(sp =>
            new ZipModpackSourceFactory(sp.GetRequiredService<ICurseForgeClient>()));
        services.AddSingleton<ModpackInstaller>(sp => new ModpackInstaller(
            sp.GetRequiredService<GameInstaller>(),
            sp.GetRequiredService<LoaderInstaller>(),
            sp.GetRequiredService<IForgeInstaller>(),
            sp.GetRequiredService<INeoForgeInstaller>(),
            sp.GetRequiredService<IDownloader>()));
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        // Modrinth / CurseForge 各用独立的 HttpClient：它们的 BaseAddress 不同，
        // 不能共用一个注册项，否则后注册的会顶掉前面的。
        services.AddSingleton<IModrinthClient>(_ =>
            new ModrinthClient(ModrinthClient.CreateHttpClient($"{BuildInfo.Name}/{BuildInfo.Version}")));
        services.AddSingleton<ICurseForgeClient>(_ =>
            new CurseForgeClient(
                CurseForgeClient.CreateHttpClient($"{BuildInfo.Name}/{BuildInfo.Version}"),
                () => CurseForgeKeyStore.Resolve(AppPaths.DataDirectory)));
        services.AddSingleton<ModInstallService>(sp => new ModInstallService(
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<IModrinthClient>()));
        services.AddSingleton<CurseForgeInstallService>(sp => new CurseForgeInstallService(
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<ICurseForgeClient>()));
        services.AddSingleton<ModpackDownloadService>(sp => new ModpackDownloadService(
            sp.GetRequiredService<IDownloader>(),
            sp.GetRequiredService<IModrinthClient>(),
            sp.GetRequiredService<ICurseForgeClient>(),
            sp.GetRequiredService<IModpackSourceFactory>(),
            sp.GetRequiredService<ModpackInstaller>()));
        services.AddSingleton<ModUpdateService>(sp => new ModUpdateService(
            sp.GetRequiredService<IModrinthClient>(),
            sp.GetRequiredService<ICurseForgeClient>()));
        services.AddSingleton<InstanceExportService>();
        services.AddSingleton<ModUpdateInstaller>();

        // 自更新：清单地址优先看环境变量，其次 settings.json 的 updateUrl
        services.AddSingleton<IUpdateClient>(_ =>
            new UpdateClient(UpdateClient.CreateHttpClient()));

        if (DpapiSecretProtector.TryCreate() is { } protector)
            services.AddSingleton<ISecretProtector>(protector);
        _services = services.BuildServiceProvider();
        CurrentServices = _services;

        Serilog.Log.Information("[boot] 容器就绪 {Ms} ms", boot.ElapsedMilliseconds);
        var window = _services.GetRequiredService<MainWindow>();
        Serilog.Log.Information("[boot] 窗口构造完成 {Ms} ms", boot.ElapsedMilliseconds);
        window.ContentRendered += (_, _) =>
            Serilog.Log.Information("[boot] 首帧渲染完成 {Ms} ms", boot.ElapsedMilliseconds);
        window.Show();
        Serilog.Log.Information("[boot] Show 返回 {Ms} ms", boot.ElapsedMilliseconds);

        // 首帧之后再做 Java 扫描与更新检查（构造函数里做会拖慢冷启动）。
        (window.DataContext as MainViewModel)?.StartBackgroundWork();
        TryUseDarkTitleBar(window);
    }

    /// <summary>Win10 20H1+ / Win11 的深色标题栏。失败就算了，不影响功能。</summary>
    internal static void TryUseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 或 dwmapi 不可用
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");
        MessageBox.Show(
            $"出错了：{e.Exception.Message}\n\n详细日志：{AppPaths.LogsDirectory}",
            "MC Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("启动器退出，代码 {ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();

        _services?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 所有权已不在我们手上（宿主/调试器接管），退出路径不该因此崩溃
            }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Windows 侧路径约定（ADR-001：平台差异只允许出现在 App/Infrastructure.Windows）。</summary>
public static class AppPaths
{
    /// <summary>
    /// 数据根目录。优先级：MCLAUNCHER_HOME &gt; %APPDATA%\MCLauncher。
    /// 可通过环境变量覆盖默认数据目录。
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("MCLAUNCHER_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCLauncher");

    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");
    public static string InstancesDirectory { get; } = Path.Combine(DataDirectory, "instances");
    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache");

    /// <summary>
    /// 游戏目录。可用 MCLAUNCHER_GAME_ROOT 覆盖（便携版 / 多份安装 / 测试）。
    /// </summary>
    public static string GameRoot { get; } =
        Environment.GetEnvironmentVariable("MCLAUNCHER_GAME_ROOT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(DataDirectory, "game");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(GameRoot);
    }
}

public static class BuildInfo
{
    public const string Name = "MCLauncher";
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
