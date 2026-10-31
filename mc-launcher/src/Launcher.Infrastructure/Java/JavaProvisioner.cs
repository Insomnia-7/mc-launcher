using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Application.Abstractions;
using Launcher.Application.Java;
using Launcher.Domain.Install;
using Launcher.Domain.Java;

using Launcher.Infrastructure.Http;

namespace Launcher.Infrastructure.Java;

/// <summary>
/// 从 Adoptium（Eclipse Temurin）拉 Windows x64 JRE 并装进 dataDirectory/java/&lt;major&gt;/。
/// 选 Adoptium 的理由：免登录的稳定 API、直接给 sha256、zip 里只有一个顶层目录（解压后剥一层就是 JAVA_HOME）。
///
/// 真实响应形状（2026-10-22 实测
/// <c>GET https://api.adoptium.net/v3/assets/latest/21/hotspot?os=windows&amp;architecture=x64&amp;image_type=jre</c>，HTTP 200，约 2.8 KB）：
/// <code>
/// [ { "binary": { "package": { "link":     "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
///                              "checksum": "d35f31e712f0fcf6ac5a093edc90204fbff22f720ba3950bd09d331d5e621636",   // sha256（不是 sha1）
///                              "name":     "OpenJDK21U-jre_x64_windows_hotspot_21.0.12.1_1.zip",
///                              "size":     48999141 } ,
///                    "installer": { … } },
///     "version": { "major": 21, "openjdk_version": "21.0.12.1+1-LTS" },
///     "vendor": "eclipse" } ]
/// </code>
/// 顶层是数组（一般 1 个元素）；zip 内是单个顶层目录 <c>jdk-21.0.12.1+1-jre/bin/java.exe</c>（已用 Range 请求读 zip 尾部确认）。
/// </summary>
public sealed class JavaProvisioner(HttpClient http, IDownloader downloader, IJavaLocator locator, JavaRuntimeTarget? runtimeTarget = null) : IJavaProvisioner
{
    private readonly JavaRuntimeTarget _runtime=runtimeTarget??JavaRuntimeTarget.Current;
    /// <summary>追加 vendor=eclipse 固定 Temurin：同版本多厂商共存时，不指定厂商的返回顺序并不稳定。</summary>

    /// <summary>Adoptium 是公益镜像，带可识别 UA 比默认值礼貌，也不容易被 CDN 当脚本流量拦。</summary>
    private const string UserAgent = "MinecraftLauncher/1.0 (+https://localhost)";

    /// <summary>
    /// 下载总预算。故意写成常量、不做成可配置项：共享的 <see cref="IDownloader"/> 单文件超时是 120s、
    /// 自己重试 3 次，而 JRE 是单个约 50 MB 的文件 —— 国内直连 GitHub releases 实测只有十几 KB/s，
    /// 一个 120s 窗口根本下不完。预算内一轮接一轮续传（.part 会接着写），慢网也总能下完；超了才认输。
    /// </summary>
    private static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(30);

    /// <summary>两轮续传之间歇一下：同一条烂链路立刻重连通常还是失败，也避免对源打出无意义的重试风暴。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>只用于查元数据的 client；安装包本体走 <see cref="IDownloader"/>，那边有并发/重试/停滞检测。</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = HttpClientFactory.Create(UserAgent, TimeSpan.FromSeconds(30));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 给「下安装包」这条通路专用的 client。不能复用元数据 client：那种 30s/100s 的
    /// <see cref="HttpClient.Timeout"/> 会把整次请求（含读 body）算进去，国内 48 KB/s 的链路上
    /// 每个窗口都被打断，只能靠 .part 一次次续。这里给足 30 分钟，真正的超时判定交给
    /// <see cref="DownloadOptions.PerFileTimeout"/> 和停滞检测。
    /// </summary>
    public static HttpClient CreateDownloadHttpClient()
    {
        var client = HttpClientFactory.Create(UserAgent, TimeSpan.FromMinutes(30));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        return client;
    }

    public async Task<JavaInstall> EnsureAsync(int majorVersion, string dataDirectory,
        IProgress<JavaProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        if (majorVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(majorVersion), "Java 主版本号必须是正数，例如 17");
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        progress?.Report(JavaProvisionProgress.Of($"检查本地 Java {majorVersion}"));

        // 已有能用的就不联网：多数玩家机器上本来就有 Java 17/21，白下 50 MB 是最扎眼的体验问题
        var existing = await FindLocalAsync(majorVersion, dataDirectory, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            progress?.Report(JavaProvisionProgress.Of("已有可用的 Java", 100, existing.ToString()));
            return existing;
        }

        progress?.Report(JavaProvisionProgress.Of($"查询 Adoptium Java {majorVersion}", 2));
        var package = await ResolvePackageAsync(majorVersion, ct).ConfigureAwait(false);

        var javaRoot = Path.Combine(dataDirectory, "java");
        var installDir = Path.Combine(javaRoot, majorVersion.ToString(CultureInfo.InvariantCulture));
        var cacheDir = Path.Combine(javaRoot, ".cache");
        var zipPath = Path.Combine(cacheDir, package.Name);
        Directory.CreateDirectory(cacheDir);

        // DownloadEntry 只认 sha1，Adoptium 给的是 sha256 → 下载器这层不校验，下面自己算。
        await DownloadWithinBudgetAsync(package, cacheDir, zipPath, progress, ct).ConfigureAwait(false);

        progress?.Report(JavaProvisionProgress.Of("校验 Java 安装包", 90));
        var actualSha256 = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
        if (!string.Equals(actualSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(zipPath);
            throw new IOException(
                $"Java 安装包校验失败：期望 sha256 {package.Sha256}，实际 {actualSha256}。" +
                "多半是下载被代理或镜像改过，删掉 .cache 里的文件重试一次。");
        }

        progress?.Report(JavaProvisionProgress.Of($"解压 Java {majorVersion}", 92));
        var javaExe = await ExtractAsync(zipPath, installDir, package, ct).ConfigureAwait(false);
        TryDeleteFile(zipPath);

        // 解压完真跑一次 -version：装出来的东西能不能用，只有 JVM 自己说了算
        progress?.Report(JavaProvisionProgress.Of($"确认 Java {majorVersion} 可用", 98));
        var probed = await locator.ProbeAsync(javaExe, ct).ConfigureAwait(false);
        var install = probed ?? throw new IOException("Java 解压完成但无法运行，请确认系统与处理器架构受支持。");

        progress?.Report(JavaProvisionProgress.Of($"Java {install.MajorVersion} 就绪", 100, install.ToString()));
        return install;
    }

    /// <summary>
    /// 在总预算内反复续传同一个安装包。
    /// <see cref="IDownloader"/> 的单文件超时（默认 120s）是按普通库文件定的，JRE 这种单个 50 MB 的文件在慢网下
    /// 必然超时；好在下载器的 .part 会接着写（下一轮发 Range 请求续传），所以把重试提到这一层：
    /// 断了就在预算内接着下，而不是从头再来或者直接报错。
    /// Kind 用 Library 占位：InstallPlan 里没有 Java 这一类，那个文件不在本次改动范围，只影响下载日志的分类名。
    /// </summary>
    private async Task DownloadWithinBudgetAsync(JavaPackage package, string cacheDir, string zipPath,
        IProgress<JavaProvisionProgress>? progress, CancellationToken ct)
    {
        var entry = new DownloadEntry(package.Link, package.Name, package.Size, Sha1: null, DownloadKind.Library);
        var started = Stopwatch.StartNew();
        var attempt = 0;
        var idleRounds = 0;
        Exception? lastError = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            var stage = attempt == 1
                ? $"下载 Java {package.MajorVersion} 运行时"
                : $"第 {attempt} 次续传 Java {package.MajorVersion}";

            progress?.Report(JavaProvisionProgress.Of(stage, 3, attempt == 1
                ? $"{Megabytes(package.Size)} · {package.Name}"
                : $"{Megabytes(package.Size)} · {package.Name} · 已用 {started.Elapsed.TotalMinutes:F1} 分钟"));

            var before = PartLength(zipPath);
            try
            {
                await downloader.DownloadAsync([entry], cacheDir, Wrap(progress, stage), ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is AggregateException or IOException)
            {
                // DownloadStalledException 是 IOException 的子类，也走这里：停滞换个连接就是它存在的意义
                lastError = ex;
            }

            // .part 有没有变长决定这是「慢」还是「根本没动」：一条死链接不该在这儿空转 15 分钟
            var after = PartLength(zipPath);
            idleRounds = after > before ? 0 : idleRounds + 1;
            var reason = Describe(lastError);

            if (idleRounds >= 3)
                throw new IOException(
                    $"下载 Java {package.MajorVersion} 失败：连续 3 轮没有任何进展（{reason}）。" +
                    "请检查网络/代理后重试；已下载的部分留在 .cache 里，下次接着下。", lastError);

            if (started.Elapsed >= DownloadBudget)
                throw new IOException(
                    $"下载 Java {package.MajorVersion} 失败：{DownloadBudget.TotalMinutes:F0} 分钟内只下到 " +
                    $"{Megabytes(after)} / {Megabytes(package.Size)}（{reason}）。" +
                    "已下载的部分留在 .cache 里，网络好些时重试会接着下。", lastError);

            progress?.Report(JavaProvisionProgress.Of(stage, 3, after > before
                ? $"下载中断（{reason}），已续传到 {Megabytes(after)}，正在重试…"
                : $"下载中断（{reason}），正在重试…"));

            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>.part 是下载器的断点文件（下载器约定 target + ".part"）；不存在就是 0。</summary>
    private static long PartLength(string target)
    {
        try
        {
            var part = new FileInfo(target + ".part");
            return part.Exists ? part.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

    /// <summary>AggregateException 里真正的原因在内层，界面和日志都得能看懂。</summary>
    private static string Describe(Exception? ex)
    {
        if (ex is null) return "未知错误";

        if (ex is AggregateException aggregate)
        {
            var messages = aggregate.Flatten().InnerExceptions
                .Select(inner => inner.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (messages.Length > 0) return string.Join("；", messages);
        }

        return ex.GetBaseException().Message;
    }

    /// <summary>
    /// 先看自己管的 java/&lt;major&gt;/，再看系统里已有的安装（PATH / JAVA_HOME / 常见安装根）。
    /// 多个候选时取「刚好够用」的最小主版本：MC 对新版 Java 并不兼容（1.12 跑在 21 上必挂），
    /// 所以需要 17 时不该被系统里的 21 顶掉。
    /// </summary>
    private async Task<JavaInstall?> FindLocalAsync(int majorVersion, string dataDirectory, CancellationToken ct)
    {
        var managedDir = Path.Combine(dataDirectory, "java", majorVersion.ToString(CultureInfo.InvariantCulture));
        if (FindJavaExe(managedDir) is { } managedExe)
        {
            var managed = await locator.ProbeAsync(managedExe, ct).ConfigureAwait(false);
            if (managed is not null && managed.MajorVersion >= majorVersion && MatchesArchitecture(managed)) return managed;
        }

        var discovered = await locator.DiscoverAsync(ct).ConfigureAwait(false);
        return discovered
            .Where(j => j.MajorVersion >= majorVersion && MatchesArchitecture(j))
            .OrderBy(j => j.MajorVersion)
            .ThenByDescending(j => j.Arch == "x64")
            .FirstOrDefault();
    }

    /// <summary>查 Adoptium 最新 Temurin JRE。厂商固定 eclipse，否则同版本多厂商的返回顺序不稳定。</summary>
    private async Task<JavaPackage> ResolvePackageAsync(int majorVersion, CancellationToken ct)
    {
        var url = _runtime.AssetUrl(majorVersion);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"取 Java {majorVersion} 失败（Adoptium 返回 HTTP {(int)response.StatusCode}）。" +
                "请检查网络/代理，或换一个 Java 大版本（8 / 17 / 21）。");

        var assets = await response.Content
            .ReadFromJsonAsync<List<AdoptiumAsset>>(Json, ct)
            .ConfigureAwait(false);

        var package = assets?
            .Select(a => a.Binary?.Package)
            .FirstOrDefault(p => p is { Link.Length: > 0 });

        if (package is null)
            throw new InvalidOperationException(
                $"Adoptium 没有 Java {majorVersion} 的 {_runtime.Os} / {_runtime.Architecture} JRE（此平台可能不支持该版本）。");

        if (string.IsNullOrWhiteSpace(package.Checksum))
            throw new InvalidOperationException(
                $"Adoptium 没提供 Java {majorVersion} 安装包的 sha256，无法校验完整性，已中止。");

        var version = assets!
            .Select(a => a.Version)
            .FirstOrDefault(v => v?.Major == majorVersion) ?? assets![0].Version;

        if(package.Name is not {Length:>0} name || name!=Path.GetFileName(name) || name.Contains('\\') || !name.EndsWith(_runtime.PackageExtension,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Java 安装包文件名或格式不符合当前平台。");
        return new JavaPackage(
            package.Link!,
            name,
            package.Checksum,
            package.Size,
            version?.Major ?? majorVersion,
            version?.OpenJdkVersion ?? majorVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 先解到同级临时目录再搬进来：中途失败不会在 java/&lt;major&gt;/ 里留半个 JRE
    /// （半成品会被下次的 FindLocalAsync 当成"已装好"，然后启动器一直报找不到主类）。
    /// zip 里只有一层 <c>jdk-x.y.z-jre/</c>，所以搬的时候把那一层剥掉。
    /// </summary>
    private async Task<string> ExtractAsync(string zipPath, string installDir, JavaPackage package, CancellationToken ct)
    {
        var staging = installDir + ".staging-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);

        try
        {
            await Task.Run(async () =>
            {
                if(_runtime.Os=="windows")ZipFile.ExtractToDirectory(zipPath,staging,overwriteFiles:true);
                else
                {
                    await using var input=File.OpenRead(zipPath);await using var gzip=new GZipStream(input,CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gzip,staging,overwriteFiles:false,cancellationToken:ct);
                }
            },ct).ConfigureAwait(false);

            var stagedExe = FindJavaExe(staging)
                ?? throw new IOException(
                    $"Java {package.MajorVersion} 安装包内容异常：找不到 bin/java.exe（{package.Name}）。");

            if(await locator.ProbeAsync(stagedExe,ct).ConfigureAwait(false) is not {} probe || probe.MajorVersion<package.MajorVersion || !MatchesArchitecture(probe))
                throw new IOException("暂存的 Java 无法运行，原安装保持不变。");
            var home = Path.GetDirectoryName(Path.GetDirectoryName(stagedExe)!)!;
            ReplaceDirectory(home, installDir);

            var javaExe = Path.Combine(installDir, "bin", _runtime.Executable);
            if (!File.Exists(javaExe))
                throw new IOException(
                    $"Java {package.MajorVersion} 解压后没找到 bin/java.exe，安装目录可能被其它程序（杀毒/网盘同步）占用。");

            return javaExe;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>旧目录多半是上次解压失败的残骸，先删干净再搬，否则 Directory.Move 会直接抛。</summary>
    private static void ReplaceDirectory(string source, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(source, target);
    }

    private bool MatchesArchitecture(JavaInstall install)=>_runtime.Architecture=="aarch64"?install.Arch is "arm64" or "aarch64":install.Arch=="x64";

    /// <summary>两种布局都能认：剥好的（bin/java.exe 直接在根下）和原始 zip 的（外面还套一层 jdk-xxx）。</summary>
    private string? FindJavaExe(string root)
    {
        if (!Directory.Exists(root)) return null;

        var direct = Path.Combine(root, "bin", _runtime.Executable);
        if (File.Exists(direct)) return direct;

        try
        {
            foreach (var child in Directory.EnumerateDirectories(root))
            {
                var nested = Path.Combine(child, "bin", _runtime.Executable);
                if (File.Exists(nested)) return nested;
                var bundle=Path.Combine(child,"Contents","Home","bin",_runtime.Executable);
                if(File.Exists(bundle))return bundle;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录读不了就等于没找到，交给上层报"安装包内容异常"
        }

        return null;
    }

    /// <summary>Adoptium 的 checksum 是 sha256，而 DownloadEntry 只支持 sha1，所以这里单独算一遍。</summary>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 留在那里下次覆盖 */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 临时目录，失败了也不影响结果 */ }
    }

    /// <summary>把下载进度翻译成阶段进度，界面只认 JavaProvisionProgress 一种。</summary>
    private static IProgress<DownloadProgress>? Wrap(IProgress<JavaProvisionProgress>? outer, string stage) =>
        outer is null ? null : new DownloadProgressAdapter(outer, stage);

    /// <summary>下载进度适配器：同一阶段内的百分比 + 已下载量。</summary>
    private sealed class DownloadProgressAdapter(IProgress<JavaProvisionProgress> outer, string stage)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            outer.Report(JavaProvisionProgress.Of(
                stage,
                value.Percent,
                $"{value.CompletedBytes / 1024.0 / 1024.0:F1} / {value.TotalBytes / 1024.0 / 1024.0:F1} MB · {value.CurrentFile}"));
    }

    /// <summary>解析出来的安装包（只留后面真正要用的字段）。</summary>
    private sealed record JavaPackage(
        string Link,
        string Name,
        string Sha256,
        long Size,
        int MajorVersion,
        string VersionString);

    // ---- Adoptium /v3/assets/latest 响应的最小投影（真实字段名见类注释）----

    private sealed class AdoptiumAsset
    {
        [JsonPropertyName("binary")] public AdoptiumBinary? Binary { get; init; }
        [JsonPropertyName("version")] public AdoptiumVersion? Version { get; init; }
        [JsonPropertyName("vendor")] public string? Vendor { get; init; }
    }

    private sealed class AdoptiumBinary
    {
        [JsonPropertyName("package")] public AdoptiumPackage? Package { get; init; }
    }

    private sealed class AdoptiumPackage
    {
        [JsonPropertyName("link")] public string? Link { get; init; }
        [JsonPropertyName("checksum")] public string? Checksum { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
    }

    private sealed class AdoptiumVersion
    {
        [JsonPropertyName("major")] public int Major { get; init; }
        [JsonPropertyName("openjdk_version")] public string? OpenJdkVersion { get; init; }
    }
}
