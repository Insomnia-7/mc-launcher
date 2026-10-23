// mcl —— MC 启动器 M1/M3 链路的命令行验证工具（不是交付物，是拿来跑通链路的）
using System.Diagnostics;
using System.Net;
using Launcher.Application.Abstractions;
using Launcher.Application.Export;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Application.Launch;
using Launcher.Application.Mods;
using Launcher.Application.Settings;
using Launcher.Domain.Content;
using Launcher.Domain.Launch;
using Launcher.Domain.Loaders;
using Launcher.Domain.Versions;
using Launcher.Infrastructure;
using Launcher.Infrastructure.CurseForge;
using Launcher.Infrastructure.Http;
using Launcher.Infrastructure.Loaders;
using Launcher.Infrastructure.Meta;
using Launcher.Infrastructure.Modpacks;
using Launcher.Infrastructure.Modrinth;
using Launcher.Infrastructure.Processes;

try
{
    return await RunAsync(args);
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("[取消] 操作被中断");
    return 130;
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.Error.WriteLine("[失败] " + Describe(ex));
    return 1;
}

// 下载器会把一批文件的失败包成 AggregateException，整段堆栈对用户没有价值
static string Describe(Exception ex) => ex switch
{
    AggregateException aggregate => string.Join(
        Environment.NewLine + "  · ",
        aggregate.Flatten().InnerExceptions.Select(Describe)),
    IOException { InnerException: { } inner } io => $"{io.Message} ← {inner.Message}",
    _ => ex.Message,
};

static async Task<int> RunAsync(string[] args)
{
    // source 只管读写 settings.json，参数形状和其余命令不同，先单独处理
    if (args.Length >= 1 && args[0].Equals("source", StringComparison.OrdinalIgnoreCase))
    {
        return RunSourceCommand(args);
    }

    // update 只跟更新清单打交道，和游戏目录无关，也先单独处理
    if (args.Length >= 2 && args[0].Equals("update", StringComparison.OrdinalIgnoreCase))
    {
        return await RunUpdateCommandAsync(args);
    }

    // netdiag 只回答「直连能不能通、代理能不能通」，不碰游戏目录
    if (args.Length >= 1 && args[0].Equals("netdiag", StringComparison.OrdinalIgnoreCase))
    {
        return await RunNetDiagCommandAsync(args);
    }

    // --source X / --source=X / --mirror 可以写在任何位置（`mcl --source bmclapi install ...` 也认），先摘出来
    (args, var sourceOverride) = ExtractSource(args);

    if (args.Length < 2)
    {
        Console.WriteLine("""
            用法:
              mcl install <versionId> <gameRoot> [--assets] [--source official|bmclapi]
              mcl loader  <gameVersion> <fabric|quilt> <gameRoot> [--loader-version X] [--source ...]
              mcl plan    <versionId> <gameRoot> <javaPath> [--preset fast|compatible|default] [--extra-jvm "..."]
              mcl launch  <versionId> <gameRoot> <javaPath> [--seconds N] [--preset ...] [--extra-jvm "..."] [--skip-preflight]
              mcl import  <modpack.zip> <gameRoot> [--source official|bmclapi]
              mcl source  [official|bmclapi]    查看 / 切换持久化下载源（写 settings.json）
              mcl search  <关键词> [gameVersion] [loader]
              mcl mod     <instanceId> <projectSlug> <gameRoot>
              mcl cf      <instanceId> <modId|项目链接> <gameRoot>
              mcl pack    <整合包.zip>            只解析并打印清单，不安装
              mcl export  <instanceId> <输出.zip> <gameRoot>   把版本打包成 zip（mods/配置/存档）
              mcl mods    <instanceId> <gameRoot> 列出本地模组（含重复 modId 警告）
              mcl modupd  <instanceId> <gameRoot> 检查本地模组是否有上游新版本
              mcl cf-files <modId>                查 CurseForge 项目 / 文件列表
              mcl update  check <清单|URL> [当前版本]        检查更新
              mcl update  stage <清单|URL> <启动器目录>      暂存到 <数据目录>/update/<版本>
              mcl update  apply <暂存目录> <启动器目录> [exe]  交给 mclupd 应用并重启
              mcl netdiag [URL...]              逐通道探测 URL（直连 / 系统代理），默认探镜像与官方清单
            通用开关: --verbose 打印 [http] 路由诊断；--concurrency N 覆盖下载并发（默认 16）
            """);
        return 2;
    }

    var command = args[0];

    // 下载源优先级：命令行 --source > settings.json 的 downloadSource > 默认 Official
    var dataDirectory = LauncherDataPaths.ResolveHome();
    var settingsPath = LauncherDataPaths.ResolveSettingsFile(dataDirectory);
    var downloadSource = sourceOverride ?? DownloadSourceStore.ReadFile(settingsPath);
    var urlRewriter = UrlRewriterFactory.From(downloadSource);
    Console.WriteLine(sourceOverride is null
        ? $"[source] {downloadSource.ToToken()} ← {settingsPath}"
        : $"[source] {downloadSource.ToToken()} ← 命令行 --source");

    // clean 只需要 gameRoot，参数形状和其余命令不同，先单独处理
    // search / mod 走 Modrinth，参数形状和其余命令不同，先单独处理
    if (command == "search")
    {
        using var searchHttp = ModrinthClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        var client = new ModrinthClient(searchHttp);
        var result = await client.SearchAsync(
            args[1],
            DownloadCategory.Mod,
            args.Length > 2 ? args[2] : null,
            args.Length > 3 ? args[3] : null);

        Console.WriteLine($"[search] \"{args[1]}\" 命中 {result.TotalHits} 个项目，取前 {result.Hits.Count} 条");
        foreach (var hit in result.Hits)
            Console.WriteLine($"  {hit.Title}  [{hit.Slug}]  {hit.DownloadsText} 次下载  {Truncate(hit.Description, 64)}");
        return 0;
    }

    if (command == "mod")
    {
        if (args.Length < 4)
        {
            Console.WriteLine("用法: mcl mod <instanceId> <projectSlug> <gameRoot>");
            return 2;
        }

        using var modHttp = ModrinthClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        var client = new ModrinthClient(modHttp);
        var modDownloader = new RewritingDownloader(
            new HttpDownloader(
                modHttp, new DownloadOptions(MaxConcurrency: 8, PerFileTimeout: TimeSpan.FromSeconds(180))),
            urlRewriter);
        var service = new ModInstallService(modDownloader, client);

        var env = InstanceEnvironment.Resolve(Path.GetFullPath(args[3]), args[1]);
        Console.WriteLine($"[mod] 版本 {args[1]} → {env.Describe()}");

        var modProgress = new Progress<ModInstallProgress>(p =>
            Console.Write($"\r  {p.Stage,-10} {p.Percent,6:F1}%  {Truncate(p.Detail ?? "", 56),-56}"));

        var report = await service.InstallAsync(
            Path.GetFullPath(args[3]), args[1], DownloadCategory.Mod, args[2], modProgress);
        Console.WriteLine();
        Console.WriteLine($"[mod] {report.ProjectTitle} {report.VersionNumber} → {report.FileName}（{report.Megabytes:F1} MB）");
        Console.WriteLine($"[mod] 目录 {report.TargetDirectory}，附带依赖 {report.DependencyFiles} 个");
        return 0;
    }

    if (command == "cf")
    {
        if (args.Length < 4)
        {
            Console.WriteLine("用法: mcl cf <instanceId> <modId|项目链接> <gameRoot>");
            return 2;
        }

        var dataDirectoryForCf = dataDirectory;
        using var cfHttp = CurseForgeClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        var client = new CurseForgeClient(cfHttp, () => CurseForgeKeyStore.Resolve(dataDirectoryForCf));

        if (!client.IsConfigured)
        {
            Console.WriteLine("""
                [cf] 没有可用的 CurseForge API Key。
                 1) 到 https://console.curseforge.com/ 免费申请（Core 计划）
                 2) 设置环境变量 CURSEFORGE_API_KEY=<key>
                 3) 或写入 <数据目录>\settings.json：{ "curseForgeApiKey": "<key>" }
                """);
            return 3;
        }

        int modId;
        if (int.TryParse(args[2], out var parsedId))
        {
            modId = parsedId;
        }
        else
        {
            var urlRef = client.ParseUrl(args[2]);
            if (urlRef?.ModId is not { } fromUrl)
            {
                Console.WriteLine($"[cf] 无法从「{args[2]}」解析出项目数字 ID；直接填 modId 更稳。");
                return 2;
            }

            modId = fromUrl;
        }

        var cfDownloader = new RewritingDownloader(
            new HttpDownloader(
                cfHttp, new DownloadOptions(MaxConcurrency: 8, PerFileTimeout: TimeSpan.FromSeconds(180))),
            urlRewriter);
        var service = new CurseForgeInstallService(cfDownloader, client);

        var cfProgress = new Progress<ModInstallProgress>(p =>
            Console.Write($"\r  {p.Stage,-8} {p.Percent,6:F1}%  {Truncate(p.Detail ?? "", 56),-56}"));

        var report = await service.InstallAsync(
            Path.GetFullPath(args[3]), args[1], DownloadCategory.Mod, modId, cfProgress);
        Console.WriteLine();
        Console.WriteLine($"[cf] {report.ProjectName} → {report.FileName}（{report.Megabytes:F1} MB，依赖 {report.DependencyFiles} 个）");
        Console.WriteLine($"[cf] 目录 {report.TargetDirectory}");
        return 0;
    }

    // export / mods / modupd：参数形状各不相同，单独处理
    if (command == "export")
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("用法: mcl export <instanceId> <输出.zip> <gameRoot>");
            return 2;
        }

        var instanceId = args[1];
        var archivePath = Path.GetFullPath(args[2]);
        var exportRoot = Path.GetFullPath(args[3]);
        var exporter = new InstanceExportService();
        var exportReport = await exporter.ExportAsync(
            exportRoot, instanceId, archivePath,
            progress: new Progress<string>(text => Console.WriteLine($"[export] {text}")));
        Console.WriteLine($"[export] {exportReport.ToSummary()}");
        Console.WriteLine($"[export] 包路径 {exportReport.ArchivePath}");
        return 0;
    }

    if (command == "mods")
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: mcl mods <instanceId> <gameRoot>");
            return 2;
        }

        var modsInstance = args[1];
        var modsRoot = Path.GetFullPath(args[2]);
        var modsDirectory = Path.Combine(modsRoot, "instances", modsInstance, ".minecraft", "mods");
        var scan = InstalledModScanner.Scan(modsDirectory);
        Console.WriteLine($"[mods] {modsInstance} · 目录 {modsDirectory}");
        Console.WriteLine($"[mods] 共 {scan.Mods.Count} 个：启用 {scan.EnabledCount} / 停用 {scan.DisabledCount}，{scan.TotalMegabytes:N1} MB");
        foreach (var mod in scan.Mods)
            Console.WriteLine($"  {(mod.Enabled ? "启用" : "停用")}  {mod.FileName,-52} {mod.Describe()}");
        if (scan.DuplicateModIdCount > 0)
            Console.WriteLine($"[warn] 有 {scan.DuplicateModIdCount} 个 modId 装了两份：{string.Join("、", scan.DuplicateModIds)}");
        return 0;
    }

    if (command == "modupd")
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: mcl modupd <instanceId> <gameRoot>");
            return 2;
        }

        var updateInstance = args[1];
        var updateRoot = Path.GetFullPath(args[2]);
        using var modHttp = ModrinthClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        using var cfUpdHttp = CurseForgeClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        var modrinthClient = new ModrinthClient(modHttp);
        var cfUpdClient = new CurseForgeClient(cfUpdHttp, () => CurseForgeKeyStore.Resolve(dataDirectory));
        var updateService = new ModUpdateService(modrinthClient, cfUpdClient);

        var updateReport = await updateService.CheckAsync(
            updateRoot, updateInstance,
            progress: new Progress<ModInstallProgress>(p =>
                Console.Write($"\r  [modupd] {p.Stage} {p.Percent,3:F0}% {Truncate(p.Detail, 40)}{new string(' ', 8)}")));
        Console.WriteLine();
        Console.WriteLine($"[modupd] 可更新 {updateReport.UpdateCount} 个 / 查到上游 {updateReport.CheckedCount} 个 / 查不到 {updateReport.FailedCount} 个");
        foreach (var candidate in updateReport.Updates)
            Console.WriteLine($"  {candidate.FileName,-52} {candidate.CurrentVersion ?? "?"} → {candidate.LatestVersion ?? "?"}  [{candidate.Source}]");
        return 0;
    }

    // pack 只需要一个路径，形状和其余命令不同，先单独处理
    if (command == "pack")
    {
        var zipPath = Path.GetFullPath(args[1]);

        using var cfHttp = CurseForgeClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        var cfClient = new CurseForgeClient(
            cfHttp, () => CurseForgeKeyStore.Resolve(dataDirectory));

        await using var source = new ZipModpackSourceFactory(cfClient).Open(zipPath);
        var pack = source.Pack;

        Console.WriteLine($"[pack] {pack.Name} {pack.VersionId} · 来源 {pack.SourceLabel}");
        Console.WriteLine($"  MC {pack.MinecraftVersion ?? "-"} · Forge {pack.ForgeVersion ?? "-"} · NeoForge {pack.NeoForgeVersion ?? "-"} · Fabric {pack.FabricVersion ?? "-"} · Quilt {pack.QuiltVersion ?? "-"}");
        Console.WriteLine($"  依赖: {string.Join(", ", pack.Dependencies.Select(kv => $"{kv.Key}={kv.Value}"))}");

        if (pack.SourceLabel == "CurseForge" && !cfClient.IsConfigured)
        {
            Console.WriteLine("  (CurseForge 包需要 API Key 才能解析下载地址，先跳过文件解析)");
            return 0;
        }

        var files = await source.ResolveFilesAsync(new Progress<ModpackImportProgress>(p =>
            Console.Write($"\r  [{p.Stage}] {p.Percent,3:F0}% {Truncate(p.Detail, 40)}{new string(' ', 8)}")));
        Console.WriteLine();

        foreach (var file in files.Take(10))
            Console.WriteLine($"  · {file.Path}  {Truncate(file.PrimaryUrl, 72)}");
        if (files.Count > 10) Console.WriteLine($"  …还有 {files.Count - 10} 个");

        Console.WriteLine($"[pack] 共 {files.Count} 个文件 / {files.Sum(f => f.FileSize) / 1024.0 / 1024.0:F1} MB");
        return 0;
    }

    // cf-files 只查项目与文件列表，不落盘，用来验证 API key 与 classId / 直链
    if (command == "cf-files")
    {
        using var cfFilesHttp = CurseForgeClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
        var cfFilesClient = new CurseForgeClient(
            cfFilesHttp, () => CurseForgeKeyStore.Resolve(dataDirectory));

        if (!cfFilesClient.IsConfigured)
        {
            Console.WriteLine("[cf] 没有可用的 API Key");
            return 2;
        }

        var modIdForFiles = int.Parse(args[1]);
        var mod = await cfFilesClient.GetModAsync(modIdForFiles);
        Console.WriteLine($"[cf] {mod?.Name ?? "(查不到)"} · classId={mod?.ClassId} · slug={mod?.Slug}");

        var modFiles = await cfFilesClient.GetFilesAsync(modIdForFiles);
        Console.WriteLine($"[cf] 共 {modFiles.Count} 个文件，前 8 个：");
        foreach (var f in modFiles.Take(8))
            Console.WriteLine($"  {f.Id}  {f.FileName}  {Truncate(f.DownloadUrl, 60)}");

        return 0;
    }

    if (args.Length < 3)
    {
        Console.WriteLine("这个命令至少需要 3 个参数，跑 mcl 看用法。");
        return 2;
    }

    var versionId = args[1];
    var gameRoot = command == "loader" ? Path.GetFullPath(args[3]) : Path.GetFullPath(args[2]);
    var rest = command == "loader" ? args.Skip(4).ToArray() : args.Skip(3).ToArray();
    var flags = rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // 带值开关（--preset fast / --seconds 20 / --loader-version X）：值本身不能被当成 positional，
    // 否则 `mcl plan 1.21.4 <game-root> java --preset fast` 里的 fast 会顶掉 javaPath 之后的参数。
    var restList = rest.ToList();
    string? TakeOption(string name)
    {
        var index = restList.FindIndex(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= restList.Count)
            throw new InvalidOperationException($"{name} 后面缺少值");

        var value = restList[index + 1];
        restList.RemoveRange(index, 2);
        return value;
    }

    var presetText = TakeOption("--preset");
    var secondsText = TakeOption("--seconds");
    var pinnedLoaderVersion = TakeOption("--loader-version");
    var extraJvmText = TakeOption("--extra-jvm");

    // 限流场景下并发就是命门：16 路同时撞 BMCLAPI 的令牌桶，会一直被 429 拍回去，降到 4-6 反而更快。
    var concurrency = DownloadOptions.Default.MaxConcurrency;
    var concurrencyText = TakeOption("--concurrency");
    if (concurrencyText is not null
        && (!int.TryParse(concurrencyText, out concurrency) || concurrency is < 1 or > 64))
    {
        Console.WriteLine($"[用法] --concurrency 需要 1-64 的整数，收到 {concurrencyText}");
        return 2;
    }

    if (flags.Contains("--verbose"))
    {
        HttpDiagnostics.Configure(line => Console.Error.WriteLine(line));
        Console.WriteLine("[verbose] [http] 路由诊断已开启（写 stderr）");
    }

    var positional = restList.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

    if (!JvmPresets.TryParse(presetText, out var preset))
    {
        Console.WriteLine($"[用法] 不认识 --preset {presetText}；可选 fast / compatible / default");
        return 2;
    }

    Directory.CreateDirectory(gameRoot);

    using var http = MojangMetaClient.CreateHttpClient("MCLauncher-CLI", "0.1.0");
    var meta = new MojangMetaClient(
        http,
        cacheDirectory: Path.Combine(gameRoot, ".cache", "meta"),
        urlRewriter: urlRewriter);
    var downloader = new RewritingDownloader(
        new HttpDownloader(http, new DownloadOptions(MaxConcurrency: concurrency, PerFileTimeout: TimeSpan.FromSeconds(180))),
        () => urlRewriter);

    switch (command)
    {
        case "install":
        {
            var installer = new GameInstaller(downloader, meta, urlRewriter);
            var report = await installer.InstallAsync(versionId, gameRoot, includeAssets: flags.Contains("--assets"), progress: Progress());
            Console.WriteLine();
            Console.WriteLine("[done] " + report);
            return 0;
        }

        case "loader":
        {
            var kind = LoaderKindExtensions.Parse(args[2]);
            var loaderClient = new LoaderMetaClient(http, urlRewriter);
            var installer = new GameInstaller(downloader, meta, urlRewriter);
            var loaderInstaller = new LoaderInstaller(loaderClient, meta, installer);

            var explicitVersion = pinnedLoaderVersion;

            var report = await loaderInstaller.InstallAsync(versionId, kind, gameRoot, explicitVersion, Progress());
            Console.WriteLine();
            Console.WriteLine($"[loader] {report.Kind} {report.LoaderVersion} → {report.VersionId}（{report.LibraryCount} 个库）");
            Console.WriteLine("[done] " + report.Install);
            return 0;
        }

        case "plan":
        {
            var javaPath = positional.FirstOrDefault() ?? "java";
            // 位置参数既可以是版本 id，也可以是实例 id（同 loader 多整合包会出现 -2 后缀）。
            var instanceId = versionId;
            var resolvedVersionId = InstanceRegistry.ResolveVersionId(gameRoot, versionId);
            var version = await LoadMergedAsync(gameRoot, resolvedVersionId);
            var context = CreateContext(gameRoot, version, preset, extraJvmText, instanceId);
            var classPath = ClassPathBuilder.Build(version, PlatformContext.Current, context.LibrariesRoot, context.ClientJarPath);
            var plan = LaunchPlanBuilder.Build(version, PlatformContext.Current, context, javaPath, classPath);

            Console.WriteLine($"[plan] {versionId} → mainClass={plan.MainClass}  preset={JvmPresets.Key(preset)}");
            Console.WriteLine($"  jvm args ({plan.JvmArguments.Count}):");
            foreach (var arg in plan.JvmArguments) Console.WriteLine("    " + Truncate(arg, 160));
            Console.WriteLine($"  game args ({plan.GameArguments.Count}):");
            foreach (var arg in plan.GameArguments.Take(16)) Console.WriteLine("    " + Truncate(arg, 160));
            Console.WriteLine($"  argfile 行数: {plan.ToArgFileContent().Split('\n').Length}");
            Console.WriteLine($"  classpath 长度: {plan.JvmArguments.LastOrDefault()?.Length ?? 0} 字符");

            var preflight = await LaunchPreflight.CheckAsync(version, PlatformContext.Current, context, preparedClassPath: classPath);
            Console.WriteLine("[preflight] " + preflight.Describe());
            return 0;
        }

        case "launch":
        {
            var javaPath = positional.FirstOrDefault() ?? "java";
            var seconds = 20;
            if (secondsText is not null && !int.TryParse(secondsText, out seconds)) seconds = 20;

            var instanceId = versionId;
            var resolvedVersionId = InstanceRegistry.ResolveVersionId(gameRoot, versionId);
            var version = await LoadMergedAsync(gameRoot, resolvedVersionId);
            var context = CreateContext(gameRoot, version, preset, extraJvmText, instanceId);

            // 和 App 走同一条准备链：算 classpath → 把 native jar 解压/复用到稳定目录 → 回填 NativesDirectory。
            // 以前 CLI 直接 Build(plan)，natives 用的是 legacy 目录，1.12 及更老的版本会缺 DLL。
            var preparation = await LaunchPreparer.PrepareAsync(version, PlatformContext.Current, context, gameRoot, CancellationToken.None);
            context = preparation.Context;
            var plan = LaunchPlanBuilder.Build(version, PlatformContext.Current, context, javaPath, preparation.ClassPath);
            var preflight = await LaunchPreflight.CheckAsync(version, PlatformContext.Current, context, preparedClassPath: preparation.ClassPath);
            if (preflight.IsReady)
            {
                Console.WriteLine("[preflight] " + preflight.Summary());
            }
            else if (flags.Contains("--skip-preflight"))
            {
                Console.WriteLine("[preflight] " + preflight.Summary() + "（--skip-preflight，照样启动）");
                Console.WriteLine(preflight.Describe());
            }
            else
            {
                Console.WriteLine("[preflight] " + preflight.Describe());
                return 3;
            }

            var logPath = Path.Combine(gameRoot, "logs", $"{versionId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            // 本机沙箱不让子进程写 %TEMP%（Java 的 ImageIO 缓存会直接 AccessDenied），
            // 统一把 TEMP/TMP 指到游戏目录下的可写位置——对普通用户也是更干净的做法。
            var tempRoot = Path.Combine(gameRoot, ".tmp");
            Directory.CreateDirectory(tempRoot);
            var environment = flags.Contains("--system-temp")
                ? null   // 诊断用：不覆盖 TEMP，看是不是覆盖导致的 remap 失败
                : new Dictionary<string, string>
                {
                    ["TEMP"] = tempRoot,
                    ["TMP"] = tempRoot,
                    ["TMPDIR"] = tempRoot,
                };

            using var process = new WinProcessLauncher().Start(plan, logPath, environment);
            Console.WriteLine($"[launch] pid={process.Id} log={logPath}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                var exitCode = await process.WaitForExitAsync(cts.Token);
                Console.WriteLine($"[launch] 已退出，exit={exitCode}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[launch] {seconds}s 后仍在运行 → 判定成功，杀掉");
                process.Kill();
            }

            DumpLogTail(logPath, 15);
            return 0;
        }

        case "import":
        {
            var zipPath = Path.GetFullPath(args[1]);

            using var cfHttp = CurseForgeClient.CreateHttpClient("MCLauncher-CLI/0.1.0");
            var cfClient = new CurseForgeClient(
                cfHttp,
                () => CurseForgeKeyStore.Resolve(Launcher.Application.Settings.LauncherDataPaths.ResolveHome()));

            await using var source = new ZipModpackSourceFactory(cfClient).Open(zipPath);
            var pack = source.Pack;

            if (pack.SourceLabel == "CurseForge" && !cfClient.IsConfigured)
            {
                Console.WriteLine("[pack] 这是 CurseForge 整合包：包内只有项目 ID，没有 API Key 就解析不出下载地址");
                return 2;
            }

            var resolvedFiles = await source.ResolveFilesAsync();
            Console.WriteLine($"[pack] {pack.Name} {pack.VersionId} · MC {pack.MinecraftVersion} · {pack.SourceLabel} · {resolvedFiles.Count} 文件 / {resolvedFiles.Sum(f => f.FileSize) / 1024.0 / 1024.0:F1} MB");

            var gameInstaller = new GameInstaller(downloader, meta, urlRewriter);
            var loaderInstaller = new LoaderInstaller(new LoaderMetaClient(http, urlRewriter), meta, gameInstaller);
            var forgeInstaller = new ForgeInstallerRunner(
                downloader, new Launcher.Infrastructure.Java.JavaLocator(), urlRewriter: urlRewriter);
            var neoForgeInstaller = new NeoForgeInstallerRunner(
                downloader, new Launcher.Infrastructure.Java.JavaLocator(), urlRewriter: urlRewriter);
            var modpackInstaller = new ModpackInstaller(
                gameInstaller, loaderInstaller, forgeInstaller, neoForgeInstaller, downloader);

            var packProgress = new Progress<ModpackImportProgress>(p =>
                Console.Write($"\r  [{p.Stage}] {p.Percent,3:F0}%  {Truncate(p.Detail, 50)}{new string(' ', 6)}"));

            var importReport = await modpackInstaller.ImportAsync(source, gameRoot, packProgress);
            Console.WriteLine();
            Console.WriteLine($"[import] {importReport.PackName} {importReport.PackVersionId} → {importReport.InstanceId}（{importReport.Loader} {importReport.LoaderVersion}）");
            Console.WriteLine($"  清单 {importReport.PlannedFiles} 个 / {importReport.PlannedMegabytes:F1} MB，下载 {importReport.DownloadedFiles} · 跳过 {importReport.SkippedFiles} · overrides {importReport.OverrideFiles}");
            Console.WriteLine($"  版本目录 {importReport.InstanceDirectory}");
            return 0;
        }

        case "java":
        {
            var locator = new Launcher.Infrastructure.Java.JavaLocator();
            var javas = await locator.DiscoverAsync();
            Console.WriteLine($"[java] 发现 {javas.Count} 个");
            foreach (var java in javas) Console.WriteLine("  " + java);
            return 0;
        }

        default:
            Console.Error.WriteLine($"未知命令：{command}");
            return 2;
    }

    // ---------- 局部工具 ----------

    Progress<DownloadProgress> Progress()
    {
        var lastPercent = -1;
        return new Progress<DownloadProgress>(p =>
        {
            var percent = (int)p.Percent;
            if (percent == lastPercent) return;
            lastPercent = percent;
            Console.Write($"\r  {percent,3}%  {p.CompletedBytes / 1024.0 / 1024.0,8:F1} MB  {Truncate(p.CurrentFile, 56)}");
        });
    }

    // 版本 JSON 优先读磁盘（加载器合并后的 json 只在磁盘上），没有再回源
    async Task<VersionJson> LoadVersionAsync(string root, string id)
    {
        var path = Path.Combine(root, "versions", id, id + ".json");
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var fromDisk = await System.Text.Json.JsonSerializer.DeserializeAsync<VersionJson>(
                stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (fromDisk is not null) return fromDisk;
        }

        return await meta.GetVersionAsync(id);
    }

    // 沿 inheritsFrom 链合并成完整版本（Forge 的 json 只有增量，缺了父版本就没 mainClass/libraries）
    async Task<VersionJson> LoadMergedAsync(string root, string id, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!seen.Add(id)) throw new InvalidOperationException($"版本继承出现环：{id}");

        var json = await LoadVersionAsync(root, id);
        if (json.InheritsFrom is not { } parentId) return json;

        var parent = await LoadMergedAsync(root, parentId, seen);
        return Launcher.Domain.Versions.VersionMerger.Merge(json, parent);
    }

    static void DumpLogTail(string logPath, int count)
    {
        using var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(logStream, System.Text.Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        foreach (var line in lines.TakeLast(count)) Console.WriteLine("  | " + line);
    }

    static LaunchContext CreateContext(string gameRoot, VersionJson version, JvmPreset preset = JvmPreset.Default, string? extraJvmArgs = null, string? instanceId = null)
    {
        // 加载器版本复用父版本的 client.jar：Fabric 只认父版本 jar，副本会让 remap 失败
        var jarVersionId = version.LauncherParentId ?? version.Id;

        return new LaunchContext(
            PlayerName: "Developer",
            Uuid: LaunchContext.OfflineUuid("Developer"),
            AccessToken: null,
            UserType: "legacy",
            LauncherName: "MCLauncher-CLI",
            LauncherVersion: "0.1.0",
            GameDirectory: Path.Combine(gameRoot, "instances", instanceId ?? version.Id, ".minecraft"),
            AssetsRoot: Path.Combine(gameRoot, "assets"),
            LibrariesRoot: Path.Combine(gameRoot, "libraries"),
            ClientJarPath: Path.Combine(gameRoot, "versions", jarVersionId, jarVersionId + ".jar"),
            MemoryMb: 4096,
            ExtraJvmArgs: extraJvmArgs,
            ExtraGameArgs: null,
            WindowWidth: 1280,
            WindowHeight: 720,
            FullScreen: false,
            JvmPreset: preset);
    }

    // ---------- 下载源 ----------

    // 摘出 --source X / --source=X / --mirror（旧别名），剩下的参数照旧按位置解析。
    // 位置无关：`mcl --source bmclapi install ...` 和 `mcl install ... --source bmclapi` 等价。
    static (string[] Args, DownloadSource? Override) ExtractSource(string[] args)
    {
        var kept = new List<string>(args.Length);
        DownloadSource? source = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--source", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    throw new InvalidOperationException("--source 后面缺少值，可选 official / bmclapi");

                source = ParseSourceArgument(args[++i]);
                continue;
            }

            if (arg.StartsWith("--source=", StringComparison.OrdinalIgnoreCase))
            {
                source = ParseSourceArgument(arg["--source=".Length..]);
                continue;
            }

            if (arg.Equals("--mirror", StringComparison.OrdinalIgnoreCase))
            {
                source ??= DownloadSource.Bmclapi;   // 老开关等价于 --source bmclapi
                continue;
            }

            kept.Add(arg);
        }

        return (kept.ToArray(), source);
    }

    static DownloadSource ParseSourceArgument(string text) =>
        DownloadSourceText.TryParse(text, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"不认识的下载源「{text}」，可选 official / bmclapi");

    // mcl netdiag [URL...] —— 同一批 URL 分别用「直连」和「系统代理」各打一次。
    // 存在的意义：这台机器上「端口在 listening」和「代理真能连通」是两件事，光看系统设置会判断错。
    static async Task<int> RunNetDiagCommandAsync(string[] args)
    {
        var urls = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
        if (urls.Length == 0)
        {
            urls = new[]
            {
                "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
                "https://launchermeta.mojang.com/mc/game/version_manifest.json",
            };
        }

        var probe = ProxyEnvironment.Current;
        Console.WriteLine($"[netdiag] 系统代理：{probe.Describe()}");

        foreach (var url in urls)
        {
            var uri = new Uri(url);
            Console.WriteLine($"[netdiag] {url}");
            Console.WriteLine($"[netdiag]   DefaultProxy → {DescribeProxyFor(uri)}");

            foreach (var route in new[] { ProxyRoute.Direct, ProxyRoute.Proxied })
            {
                var (ok, text) = await ProbeRouteAsync(url, route);
                Console.WriteLine($"[netdiag]   {route,-8} {(ok ? "OK  " : "FAIL")} {text}");
            }
        }

        Console.WriteLine("[netdiag] 提示：两行结论不一致时按 host 分流（镜像直连、官方走代理），不要一刀切。");
        return 0;
    }

    static async Task<(bool Ok, string Text)> ProbeRouteAsync(string url, ProxyRoute route)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = route == ProxyRoute.Proxied,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCLauncher-CLI-netdiag/0.1.0");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            stopwatch.Stop();
            return (response.IsSuccessStatusCode,
                $"{(int)response.StatusCode} {response.ReasonPhrase}  {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return (false, $"{Describe(ex)}  {stopwatch.ElapsedMilliseconds}ms");
        }
    }

    static string DescribeProxyFor(Uri uri)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            return proxy.IsBypassed(uri) ? "绕过（直连）" : proxy.GetProxy(uri)?.ToString() ?? "直连";
        }
        catch (Exception ex)
        {
            return $"查不到（{ex.GetType().Name}）";
        }
    }

    // mcl source           → 打印当前源与 settings.json 路径
    // mcl source bmclapi   → 写入 settings.json
    static int RunSourceCommand(string[] args)
    {
        var path = LauncherDataPaths.ResolveSettingsFile();

        if (args.Length < 2)
        {
            Console.WriteLine($"[source] {DownloadSourceStore.ReadFile(path).ToToken()} ← {path}");
            Console.WriteLine("  切换：mcl source official|bmclapi");
            return 0;
        }

        if (!DownloadSourceText.TryParse(args[1], out var target))
        {
            Console.Error.WriteLine($"[用法] 不认识的下载源「{args[1]}」，可选 official / bmclapi");
            return 2;
        }

        DownloadSourceStore.WriteFile(path, target);
        Console.WriteLine($"[source] 已写入 {target.ToToken()} → {path}");
        return 0;
    }

    static string Truncate(string? text, int max) =>
        text is null ? "" : text.Length <= max ? text : "…" + text[^max..];

    static async Task<int> RunUpdateCommandAsync(string[] args)
    {
        var action = args[1].ToLowerInvariant();
        using var http = Launcher.Infrastructure.Update.UpdateClient.CreateHttpClient();
        var client = new Launcher.Infrastructure.Update.UpdateClient(http);
        var dataDirectory = LauncherDataPaths.ResolveHome();

        switch (action)
        {
            case "check":
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("用法: mcl update check <清单文件|URL|目录> [当前版本]");
                    return 2;
                }

                var current = args.Length >= 4 ? args[3] : "0.0.0";
                var result = await client.CheckAsync(current, args[2]);
                Console.WriteLine($"[update] 当前 {result.CurrentVersion} · 清单 {result.Location ?? "(未配置)"}");

                if (result.Manifest is { } manifest)
                {
                    Console.WriteLine($"[update] 有新版：{manifest.ToSummary(result.CurrentVersion)}");
                    foreach (var file in manifest.Files)
                        Console.WriteLine($"  {file.Path}  {file.Size / 1024d / 1024d:N1} MB  sha256 {file.Sha256[..Math.Min(12, file.Sha256.Length)]}…");
                    if (!string.IsNullOrWhiteSpace(manifest.Notes))
                        Console.WriteLine($"[update] 说明：{manifest.Notes}");
                    return 0;
                }

                Console.WriteLine($"[update] 无需更新：{result.Reason}");
                return 0;
            }

            case "stage":
            {
                if (args.Length < 4)
                {
                    Console.WriteLine("用法: mcl update stage <清单文件|URL|目录> <启动器目录>");
                    return 2;
                }

                var check = await client.CheckAsync("0.0.0", args[2]);
                if (check.Manifest is not { } manifest)
                {
                    Console.WriteLine($"[update] 读不到清单：{check.Reason}");
                    return 1;
                }

                var staging = await client.StageAsync(
                    manifest,
                    args[2],
                    Path.GetFullPath(args[3]),
                    dataDirectory,
                    new Progress<Launcher.Application.Update.UpdateProgress>(p =>
                        Console.WriteLine($"[update] {p.Stage} {p.Percent,5:F1}% {p.Detail}")));

                Console.WriteLine($"[update] 已暂存 {manifest.Version} → {staging.Directory}（下载 {staging.DownloadedFiles} / 跳过 {staging.SkippedFiles}）");
                return 0;
            }

            case "apply":
            {
                if (args.Length < 4)
                {
                    Console.WriteLine("用法: mcl update apply <暂存目录> <启动器目录> [重启的 exe]");
                    return 2;
                }

                var updater = FindUpdater();
                if (updater is null)
                {
                    Console.Error.WriteLine("[update] 找不到 mclupd.exe，先构建 tools/Launcher.Updater");
                    return 1;
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo(updater)
                {
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add("--staging");
                startInfo.ArgumentList.Add(Path.GetFullPath(args[2]));
                startInfo.ArgumentList.Add("--target");
                startInfo.ArgumentList.Add(Path.GetFullPath(args[3]));
                if (args.Length >= 5)
                {
                    startInfo.ArgumentList.Add("--restart");
                    startInfo.ArgumentList.Add(args[4]);
                }

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process is null)
                {
                    Console.Error.WriteLine("[update] 启动 mclupd 失败");
                    return 1;
                }

                await process.WaitForExitAsync();
                Console.WriteLine($"[update] mclupd 退出码 {process.ExitCode}");
                return process.ExitCode;
            }

            default:
                Console.WriteLine("用法: mcl update check|stage|apply …");
                return 2;
        }
    }

    static string? FindUpdater()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "mclupd.exe"),
            Path.Combine(baseDirectory, "..", "Launcher.Updater", "bin", "Release", "net10.0", "mclupd.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "Launcher.Updater", "bin", "Release", "net10.0", "mclupd.exe"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }

        return null;
    }
}
