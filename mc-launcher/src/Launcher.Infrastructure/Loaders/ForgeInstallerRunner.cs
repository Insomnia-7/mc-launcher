using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Install;

namespace Launcher.Infrastructure.Loaders;

/// <summary>
/// Forge 安装：下载官方 installer jar，跑 <c>java -jar ... --installClient &lt;gameRoot&gt;</c>。
/// installer 自己解包、跑 processors、写 versions/&lt;id&gt;/ 和 libraries/，我们只负责喂 Java 和验产物。
/// 这条路绕开了 install_profile.json 手工实现的复杂度，代价是装的时候没有细粒度进度。
/// </summary>
public sealed class ForgeInstallerRunner(
    IDownloader downloader,
    IJavaLocator javaLocator,
    TimeSpan? installTimeout = null,
    IUrlRewriter? urlRewriter = null) : IForgeInstaller
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(8);

    /// <summary>installer jar 的下载源；不传就是官方源。</summary>
    private readonly IUrlRewriter _rewriter = urlRewriter ?? OfficialUrlRewriter.Instance;

    public async Task<string> InstallAsync(
        string minecraftVersion,
        string forgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var versionId = $"{minecraftVersion}-forge-{forgeVersion}";
        var versionJson = Path.Combine(gameRoot, "versions", versionId, versionId + ".json");

        if (File.Exists(versionJson))
        {
            progress?.Report(ModpackImportProgress.Of($"Forge {forgeVersion} 已安装", 100, versionId));
            return versionId;
        }

        Directory.CreateDirectory(gameRoot);

        // ① installer jar
        var cacheDirectory = Path.Combine(gameRoot, ".cache", "forge");
        Directory.CreateDirectory(cacheDirectory);
        var jarName = $"forge-{minecraftVersion}-{forgeVersion}-installer.jar";
        var jarPath = Path.Combine(cacheDirectory, jarName);

        if (!File.Exists(jarPath))
        {
            var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{minecraftVersion}-{forgeVersion}/{jarName}";
            url = _rewriter.Rewrite(url);
            progress?.Report(ModpackImportProgress.Of($"下载 Forge installer {forgeVersion}", 0, url));
            await downloader
                .DownloadAsync([new DownloadEntry(url, jarName, 0, null, DownloadKind.Library)], cacheDirectory, null, ct)
                .ConfigureAwait(false);
        }

        if (!File.Exists(jarPath))
            throw new InvalidOperationException($"Forge installer 下载失败：{jarPath}");

        // ② 找 Java：1.20.1 的 Forge 需要 17+，取满足条件的最低版本省内存
        var javas = await javaLocator.DiscoverAsync(ct).ConfigureAwait(false);
        var java = javas.Where(j => j.MajorVersion >= 17).OrderBy(j => j.MajorVersion).FirstOrDefault()
            ?? throw new InvalidOperationException("没有找到 Java 17+，Forge installer 需要它来打补丁");

        // ②.5 Forge installer 要求目标目录里存在 launcher_profiles.json，否则直接报
        //     "There is no minecraft launcher profile ... you need to run the launcher first!"
        EnsureLauncherProfile(gameRoot);

        // ②.6 installer 的 processors 要读 net.minecraft:client:<mavenVersion> 的 client.jar 与 mappings。
        //     这两样不在本地时，installer 会去极慢的 forge maven 拉（实测 25 秒超时都不通），
        //     表现就是"卡住不动、日志只剩十行"。我们自己按 maven 布局摆好，
        //     installer 全部命中本地缓存，45 秒左右就能跑完。
        await SeedInstallerInputsAsync(minecraftVersion, jarPath, gameRoot, progress, ct).ConfigureAwait(false);

        // ③ 跑 installer
        progress?.Report(ModpackImportProgress.Of($"安装 Forge {forgeVersion}", 0, $"java {java.MajorVersion} · {java.ExecutablePath}"));

        var output = new StringBuilder();
        var startInfo = new ProcessStartInfo
        {
            FileName = java.ExecutablePath,
            WorkingDirectory = cacheDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(jarPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(Path.GetFullPath(gameRoot));

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line) output.AppendLine(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line) output.AppendLine(line);
        };

        if (!process.Start())
            throw new InvalidOperationException("Forge installer 启动失败");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(installTimeout ?? DefaultTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Forge installer 超过 {(installTimeout ?? DefaultTimeout).TotalMinutes:F0} 分钟未结束，已终止");
        }

        if (process.ExitCode != 0 || !File.Exists(versionJson))
        {
            var tail = Tail(output.ToString(), 20);
            throw new InvalidOperationException(
                $"Forge installer 失败（exit {process.ExitCode}）" + Environment.NewLine + tail);
        }

        progress?.Report(ModpackImportProgress.Of($"Forge {forgeVersion} 安装完成", 100, versionId));
        return versionId;
    }

    /// <summary>
    /// 把 installer 需要的 <c>net.minecraft:client</c> 输入提前摆到 maven 布局里：client.jar 从本地
    /// <c>versions/&lt;mc&gt;/&lt;mc&gt;.jar</c> 复制（省 23 MB 下载），mappings 从版本 json 的
    /// <c>downloads.client_mappings</c> 下（约 8 MB）。拿不到就静默跳过，让 installer 自己想办法。
    /// </summary>
    private async Task SeedInstallerInputsAsync(
        string minecraftVersion,
        string installerJarPath,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken ct)
    {
        var mavenVersion = ReadMojmapsVersion(installerJarPath);
        if (mavenVersion is null) return;

        var clientDirectory = Path.Combine(gameRoot, "libraries", "net", "minecraft", "client", mavenVersion);
        Directory.CreateDirectory(clientDirectory);

        var jarTarget = Path.Combine(clientDirectory, $"client-{mavenVersion}.jar");
        if (!File.Exists(jarTarget))
        {
            var localClient = Path.Combine(gameRoot, "versions", minecraftVersion, minecraftVersion + ".jar");
            if (File.Exists(localClient))
            {
                progress?.Report(ModpackImportProgress.Of("准备 Forge 安装输入", 0, $"复制 {minecraftVersion}.jar"));
                File.Copy(localClient, jarTarget, overwrite: true);
            }
        }

        var mappingsTarget = Path.Combine(clientDirectory, $"client-{mavenVersion}-mappings.txt");
        if (File.Exists(mappingsTarget)) return;

        var mappings = await ReadClientMappingsAsync(minecraftVersion, gameRoot, ct).ConfigureAwait(false);
        if (mappings is null) return;

        progress?.Report(ModpackImportProgress.Of("准备 Forge 安装输入", 0, "下载 client mappings"));

        var relative = string.Join(Path.DirectorySeparatorChar,
            "libraries", "net", "minecraft", "client", mavenVersion,
            $"client-{mavenVersion}-mappings.txt");

        await downloader.DownloadAsync(
                [new DownloadEntry(mappings.Value.Url, relative, mappings.Value.Size, mappings.Value.Sha1, DownloadKind.Library)],
                gameRoot,
                null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>installer 的 <c>install_profile.json</c> 里 MOJMAPS 写着 client 的 maven 版本。</summary>
    private static string? ReadMojmapsVersion(string installerJarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(installerJarPath);
            var entry = archive.GetEntry("install_profile.json");
            if (entry is null) return null;

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("data", out var data)) return null;

            // MOJMAPS / MC_UNPACKED / MC_SLIM 都指向同一个 net.minecraft:client:<ver> 坐标，
            // 任取一个能解析出来的就够（不同 Forge 版本里这几个键未必齐全）。
            foreach (var key in new[] { "MOJMAPS", "MC_UNPACKED", "MC_SLIM" })
            {
                if (!data.TryGetProperty(key, out var node)) continue;

                // "net.minecraft:client:1.20.1-20230612.114412:mappings@txt"
                var coordinate = ReadClientCoordinate(node);
                if (coordinate is null) continue;

                var parts = coordinate.Trim().Trim('[', ']').Split(':');
                if (parts.Length >= 4 && parts[0] == "net.minecraft" && parts[1] == "client") return parts[2];
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;   // installer 里没有 profile 或格式怪，交给 installer 自己处理
        }
    }

    /// <summary>
    /// <c>data</c> 里的值有三种写法：数组、<c>{"client": "...", "server": "..."}</c>、纯字符串。
    /// 1.20.1 的 Forge 用的是第二种。
    /// </summary>
    private static string? ReadClientCoordinate(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Array when node.GetArrayLength() > 0 => node[0].GetString(),
        JsonValueKind.Object when node.TryGetProperty("client", out var client) && client.ValueKind == JsonValueKind.String
            => client.GetString(),
        JsonValueKind.String => node.GetString(),
        _ => null,
    };

    private static async Task<(string Url, long Size, string? Sha1)?> ReadClientMappingsAsync(
        string minecraftVersion,
        string gameRoot,
        CancellationToken ct)
    {
        var versionJson = Path.Combine(gameRoot, "versions", minecraftVersion, minecraftVersion + ".json");
        if (!File.Exists(versionJson)) return null;

        try
        {
            await using var stream = File.OpenRead(versionJson);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("downloads", out var downloads)) return null;
            if (!downloads.TryGetProperty("client_mappings", out var mappings)) return null;

            var url = mappings.TryGetProperty("url", out var u) ? u.GetString() : null;
            var sha1 = mappings.TryGetProperty("sha1", out var s) ? s.GetString() : null;
            var size = mappings.TryGetProperty("size", out var z) ? z.GetInt64() : 0;

            return url is { Length: > 0 } ? (url, size, sha1) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void EnsureLauncherProfile(string gameRoot)
    {
        var path = Path.Combine(gameRoot, "launcher_profiles.json");
        if (File.Exists(path)) return;

        // Forge installer 只检查文件存在与 profiles 字段，最小骨架就够
        const string minimal = """
            {
              "profiles": {},
              "settings": {},
              "version": 3
            }
            """;

        Directory.CreateDirectory(gameRoot);
        File.WriteAllText(path, minimal, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已经退了就算了
        }
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.TakeLast(lines));
    }
}
