using Launcher.App;
using System.IO;
using System.Net.Http;
using System.Threading;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Accounts;
using Launcher.Application.Auth;
using Launcher.Application.Export;
using Launcher.Application.Java;
using Launcher.Infrastructure.Accounts;
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

namespace Launcher.Desktop;
internal static class LauncherServices
{
    public static ServiceProvider Create(Action<IServiceCollection>? configure = null)
    {
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
        services.AddSingleton<IResourcePresentationClient>(sp => new ResourcePresentationClient(
            sp.GetRequiredService<HttpClient>(), Path.Combine(AppPaths.CacheDirectory, "resource-presentation")));
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
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<ILogger<MainViewModel>>(), sp.GetRequiredService<ILogger<DownloadViewModel>>(),
            sp.GetRequiredService<GameInstaller>(), sp.GetRequiredService<ModpackInstaller>(), sp.GetRequiredService<IModpackSourceFactory>(),
            sp.GetRequiredService<IJavaLocator>(), sp.GetRequiredService<IProcessLauncher>(), sp.GetRequiredService<IModrinthClient>(),
            sp.GetRequiredService<ICurseForgeClient>(), sp.GetRequiredService<ModInstallService>(), sp.GetRequiredService<CurseForgeInstallService>(),
            sp.GetRequiredService<ModpackDownloadService>(), sp.GetRequiredService<IVersionMetaClient>(), sp.GetRequiredService<VersionLoader>(),
            sp.GetRequiredService<IMicrosoftAuthService>(), sp.GetRequiredService<IJavaProvisioner>(), sp.GetRequiredService<DownloadSourceProvider>(),
            sp.GetRequiredService<IUpdateClient>(), sp.GetService<ISecretProtector>(), sp.GetRequiredService<ModUpdateService>(),
            sp.GetRequiredService<InstanceExportService>(), sp.GetRequiredService<ModUpdateInstaller>(), sp.GetRequiredService<ISkinService>(), sp.GetRequiredService<ILoaderMetaClient>(),
            sp.GetRequiredService<LoaderInstaller>(), sp.GetRequiredService<IForgeInstaller>(), sp.GetRequiredService<INeoForgeInstaller>(), sp.GetRequiredService<IResourcePresentationClient>()));

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

        // 玩家头像：只用来显示，失败不影响任何功能
        services.AddSingleton<ISkinService>(_ => new SkinService(AppPaths.DataDirectory));
        if(!OperatingSystem.IsWindows()) services.AddSingleton<IJavaProvisioner, ManualJavaProvisioner>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
