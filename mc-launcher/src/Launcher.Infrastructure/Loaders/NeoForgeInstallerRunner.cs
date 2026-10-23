using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Install;

namespace Launcher.Infrastructure.Loaders;

/// <summary>
/// NeoForge 安装：下载官方 installer jar → 预置 maven 输入 → <c>java -jar ... --installClient &lt;gameRoot&gt;</c>。
/// 结构照 <see cref="ForgeInstallerRunner"/>，差异只有三处：
/// ① installer 地址分新旧两套 maven 布局（1.20.1 的 NeoForge 走 <c>net/neoforged/forge</c>）；
/// ② 版本 id 从 <c>install_profile.json</c> 的 <c>version</c> 字段读，不自己拼；
/// ③ 预置范围按 profile 的 libraries + processors 坐标展开，而不是只补 <c>net.minecraft:client</c>。
/// </summary>
public sealed class NeoForgeInstallerRunner(
    IDownloader downloader,
    IJavaLocator javaLocator,
    TimeSpan? installTimeout = null,
    IUrlRewriter? urlRewriter = null) : INeoForgeInstaller
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(8);

    private const string NeoForgeMaven = "https://maven.neoforged.net/releases";

    /// <summary>installer jar 的下载源；不传就是官方源（镜像走 <see cref="IUrlRewriter"/>）。</summary>
    private readonly IUrlRewriter _rewriter = urlRewriter ?? OfficialUrlRewriter.Instance;

    public async Task<string> InstallAsync(
        string minecraftVersion,
        string neoForgeVersion,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(neoForgeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        var legacyLayout = IsLegacyLayout(neoForgeVersion);
        var jarName = legacyLayout
            ? $"forge-{minecraftVersion}-{neoForgeVersion}-installer.jar"
            : $"neoforge-{neoForgeVersion}-installer.jar";

        // ① 已装过就不下：先用可推导的 id 探一遍（新版 neoforge-<ver>，旧版 forge-<mc>-<ver>）
        foreach (var candidate in ExpectedVersionIds(minecraftVersion, neoForgeVersion, legacyLayout))
        {
            if (!File.Exists(VersionJsonPath(gameRoot, candidate))) continue;
            progress?.Report(ModpackImportProgress.Of($"NeoForge {neoForgeVersion} 已安装", 100, candidate));
            return candidate;
        }

        Directory.CreateDirectory(gameRoot);

        // ② installer jar
        var cacheDirectory = Path.Combine(gameRoot, ".cache", "neoforge");
        Directory.CreateDirectory(cacheDirectory);
        var jarPath = Path.Combine(cacheDirectory, jarName);

        if (!File.Exists(jarPath))
        {
            var url = legacyLayout
                ? $"{NeoForgeMaven}/net/neoforged/forge/{minecraftVersion}-{neoForgeVersion}/{jarName}"
                : $"{NeoForgeMaven}/net/neoforged/neoforge/{neoForgeVersion}/{jarName}";
            url = _rewriter.Rewrite(url);

            progress?.Report(ModpackImportProgress.Of($"下载 NeoForge installer {neoForgeVersion}", 0, url));
            await downloader
                .DownloadAsync([new DownloadEntry(url, jarName, 0, null, DownloadKind.Library)], cacheDirectory, null, ct)
                .ConfigureAwait(false);
        }

        if (!File.Exists(jarPath))
            throw new InvalidOperationException($"NeoForge installer 下载失败：{jarPath}");

        // ③ installer 自己声明版本 id（neoforge-21.4.123），不拼
        var profile = ReadInstallProfile(jarPath)
            ?? throw new InvalidOperationException($"NeoForge installer 里读不到 install_profile.json：{jarPath}");

        var versionId = string.IsNullOrWhiteSpace(profile.Version)
            ? (legacyLayout ? $"forge-{minecraftVersion}-{neoForgeVersion}" : $"neoforge-{neoForgeVersion}")
            : profile.Version!;

        var versionJson = VersionJsonPath(gameRoot, versionId);
        if (File.Exists(versionJson))
        {
            progress?.Report(ModpackImportProgress.Of($"NeoForge {neoForgeVersion} 已安装", 100, versionId));
            return versionId;
        }

        // ④ 找 Java：MC ≥ 1.20.5 要 21，更早要 17；取满足条件的最低版本省内存
        var requiredJava = RequiredJavaMajor(profile.MinecraftVersion ?? minecraftVersion);
        var javas = await javaLocator.DiscoverAsync(ct).ConfigureAwait(false);
        var java = javas.Where(j => j.MajorVersion >= requiredJava).OrderBy(j => j.MajorVersion).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"没有找到 Java {requiredJava}+，NeoForge installer 需要它来打补丁（当前发现 {javas.Count} 个 Java）");

        // ⑤ installer 要求目标目录里存在 launcher_profiles.json
        EnsureLauncherProfile(gameRoot);

        // ⑥ 预置 installer 需要的 maven 产物：本机连不上 maven.neoforged.net，不预置就会卡死
        var seedFailures = await SeedInstallerInputsAsync(profile, gameRoot, progress, ct).ConfigureAwait(false);

        // ⑦ 跑 installer
        progress?.Report(ModpackImportProgress.Of(
            $"安装 NeoForge {neoForgeVersion}", 0, $"java {java.MajorVersion} · {java.ExecutablePath}"));

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
            throw new InvalidOperationException("NeoForge installer 启动失败");

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
            throw new TimeoutException(
                $"NeoForge installer 超过 {(installTimeout ?? DefaultTimeout).TotalMinutes:F0} 分钟未结束，已终止"
                + FormatSeedFailures(seedFailures));
        }

        if (process.ExitCode != 0 || !File.Exists(versionJson))
        {
            var tail = Tail(output.ToString(), 20);

            // installer 会在下载/补丁阶段之前就把 version json 抄进 versions/<id>/，
            // 失败时留着它会让下一次调用误判「已安装」直接返回。这个目录是本次跑出来的
            // （原本不存在，上面第 86 行早就返回了），清掉让重试从干净状态开始。
            TryRemoveVersionDirectory(gameRoot, versionId);

            throw new InvalidOperationException(
                $"NeoForge installer 失败（exit {process.ExitCode}）" + Environment.NewLine + tail
                + FormatSeedFailures(seedFailures));
        }

        progress?.Report(ModpackImportProgress.Of($"NeoForge {neoForgeVersion} 安装完成", 100, versionId));
        return versionId;
    }

    /// <summary>
    /// 把 installer 需要的 maven 产物按标准布局摆到 <c>&lt;gameRoot&gt;/libraries/</c>。
    /// 范围：<c>libraries[]</c>（带 url/sha1/size，完整性最好）+ processors 的 jar / classpath / args 坐标
    /// + <c>data</c> 里除「installer 自己下载」和「processor 输出」之外的坐标。
    /// 返回预置失败的坐标（真的装挂了再拼进异常，不提前中断）。
    /// </summary>
    private async Task<List<string>> SeedInstallerInputsAsync(
        InstallProfile profile,
        string gameRoot,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken ct)
    {
        // 同一个 maven 路径只留一条；libraries 的下载元数据优先（先加进去，后面的合成条目让位）
        var seeds = new Dictionary<string, SeedEntry>(StringComparer.OrdinalIgnoreCase);

        void Add(string coordinate, string? url, long size, string? sha1)
        {
            if (!TryBuildMavenPath(coordinate, out var mavenPath)) return;
            if (seeds.TryGetValue(mavenPath, out var existing) && existing.HasMetadata) return;

            seeds[mavenPath] = new SeedEntry(
                mavenPath,
                url ?? $"{NeoForgeMaven}/{mavenPath}",
                size,
                sha1,
                HasMetadata: url is not null);
        }

        foreach (var library in profile.Libraries)
            Add(library.Name, library.Url, library.Size, library.Sha1);

        foreach (var coordinate in profile.ProcessorCoordinates)
            Add(coordinate, null, 0, null);

        foreach (var coordinate in profile.DataCoordinates)
        {
            if (IsMojangMappings(coordinate)) continue;   // MOJMAPS 由 installer 的 DOWNLOAD_MOJMAPS 从 Mojang 拉
            Add(coordinate, null, 0, null);
        }

        var failures = new List<string>();
        if (seeds.Count == 0) return failures;

        DownloadEntry ToEntry(SeedEntry seed) => new(
            seed.Url,
            Path.Combine("libraries", seed.MavenPath.Replace('/', Path.DirectorySeparatorChar)),
            seed.Size,
            seed.Sha1,
            DownloadKind.Library);

        var withMetadata = seeds.Values.Where(s => s.HasMetadata).Select(ToEntry).ToList();
        var derived = seeds.Values.Where(s => !s.HasMetadata).Select(ToEntry).ToList();

        var seedProgress = progress is null ? null : new SeedProgress(progress);

        if (withMetadata.Count > 0)
        {
            progress?.Report(ModpackImportProgress.Of(
                "准备 NeoForge 安装输入", 0, $"{withMetadata.Count} 个带校验的 installer 依赖"));
            await downloader.DownloadAsync(withMetadata, gameRoot, seedProgress, ct).ConfigureAwait(false);
        }

        if (derived.Count > 0)
        {
            try
            {
                progress?.Report(ModpackImportProgress.Of(
                    "准备 NeoForge 安装输入", 0, $"{derived.Count} 个按坐标推导的 installer 依赖"));
                await downloader.DownloadAsync(derived, gameRoot, seedProgress, ct).ConfigureAwait(false);
            }
            catch (AggregateException ex)
            {
                // 少数坐标是 processor 自己产出的（不同 NeoForge 版本的输出名不一样），
                // 下载 404 不代表安装会失败：记下来，installer 真失败时一起报。
                failures.AddRange(ex.InnerExceptions.Select(e => e.Message));
            }
        }

        return failures;
    }

    /// <summary>解析 installer 里的 <c>install_profile.json</c>。</summary>
    private static InstallProfile? ReadInstallProfile(string installerJarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(installerJarPath);
            var entry = archive.GetEntry("install_profile.json");
            if (entry is null) return null;

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            string? version = ReadString(root, "version");
            string? minecraft = ReadString(root, "minecraft");

            var libraries = new List<ProfileLibrary>();
            if (root.TryGetProperty("libraries", out var libraryNodes) && libraryNodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in libraryNodes.EnumerateArray())
                {
                    if (ReadString(library, "name") is not { Length: > 0 } name) continue;

                    string? url = null;
                    long size = 0;
                    string? sha1 = null;
                    if (library.TryGetProperty("downloads", out var downloads) &&
                        downloads.ValueKind == JsonValueKind.Object &&
                        downloads.TryGetProperty("artifact", out var artifact) &&
                        artifact.ValueKind == JsonValueKind.Object)
                    {
                        url = ReadString(artifact, "url");
                        if (artifact.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed))
                            size = parsed;
                        sha1 = ReadString(artifact, "sha1");
                    }

                    libraries.Add(new ProfileLibrary(name, url, size, sha1));
                }
            }

            // processors 的 jar / classpath / args 里出现的坐标：installer 起来后会按 classpath 解析，
            // 少一个就会去 maven.neoforged.net 拉（本机 21 秒超时）。
            var processorCoordinates = new List<string>();
            if (root.TryGetProperty("processors", out var processors) && processors.ValueKind == JsonValueKind.Array)
            {
                foreach (var processor in processors.EnumerateArray())
                {
                    if (ReadString(processor, "jar") is { Length: > 0 } processorJar)
                        processorCoordinates.Add(processorJar);

                    if (processor.TryGetProperty("classpath", out var classpath) && classpath.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in classpath.EnumerateArray())
                            if (item.GetString() is { Length: > 0 } coordinate)
                                processorCoordinates.Add(coordinate);
                    }

                    if (processor.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arg in args.EnumerateArray())
                            if (arg.GetString() is { Length: > 0 } text && TryNormalizeCoordinate(text, out var coordinate))
                                processorCoordinates.Add(coordinate);
                    }
                }
            }

            var dataCoordinates = new List<string>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in data.EnumerateObject())
                {
                    if (ProcessorOutputKeys.Contains(property.Name)) continue;

                    foreach (var raw in ReadCoordinateCandidates(property.Value))
                        if (TryNormalizeCoordinate(raw, out var coordinate))
                            dataCoordinates.Add(coordinate);
                }
            }

            return new InstallProfile(version, minecraft, libraries, processorCoordinates, dataCoordinates);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>data</c> 里的值有三种写法：数组、<c>{"client": "...", "server": "..."}</c>、纯字符串。
    /// 这里把能拿到的字符串都摊平；是不是坐标由 <see cref="TryNormalizeCoordinate"/> 判。
    /// </summary>
    private static IEnumerable<string> ReadCoordinateCandidates(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    if (item.GetString() is { Length: > 0 } text)
                        yield return text;
                break;

            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } value)
                        yield return value;
                break;

            case JsonValueKind.String:
                if (node.GetString() is { Length: > 0 } single)
                    yield return single;
                break;
        }
    }

    /// <summary>
    /// 坐标归一化：去掉 <c>[ ]</c> 包装、掐掉 <c>@ext</c> 以外的噪声，只留
    /// <c>group:artifact:version[:classifier][@ext]</c>。占位符（<c>{ROOT}</c>）、本地路径（<c>/data/...</c>）、
    /// 裸版本号（<c>1.21.4</c>）都会被拒。
    /// </summary>
    private static bool TryNormalizeCoordinate(string raw, out string coordinate)
    {
        coordinate = string.Empty;

        var text = raw.Trim();
        if (text.StartsWith('[') || text.EndsWith(']'))
            text = text.Trim('[', ']').Trim();

        if (text.Length == 0) return false;
        if (text.Contains('{') || text.Contains('/') || text.Contains(' ') || text.Contains('"') || text.Contains('\''))
            return false;

        var at = text.LastIndexOf('@');
        var ext = "jar";
        if (at >= 0)
        {
            ext = text[(at + 1)..];
            text = text[..at];
            if (ext.Length == 0) ext = "jar";
        }

        if (!ext.All(char.IsLetterOrDigit)) return false;

        var parts = text.Split(':');
        if (parts.Length is < 3 or > 4) return false;
        if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
        if (parts.Any(part => part.Any(char.IsWhiteSpace))) return false;

        // group 不一定带点：NeoForge installer 真的会要 commons-io / commons-beanutils /
        // commons-logging / commons-collections 这几个「无点」group。只要求它是合法 groupId
        // 且含字母，从而排除 1.21.4、20241203.161809 这类裸版本号。
        var group = parts[0];
        if (!group.Any(char.IsLetter)) return false;
        if (!group.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        if (!parts[1].All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        if (!parts[2].All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '+')) return false;

        coordinate = at >= 0 ? $"{text}@{ext}" : text;
        return true;
    }

    /// <summary>坐标 → maven 相对路径（不带 <c>libraries/</c> 前缀）。</summary>
    private static bool TryBuildMavenPath(string? coordinate, out string mavenPath)
    {
        mavenPath = string.Empty;
        if (!TryNormalizeCoordinate(coordinate ?? string.Empty, out var normalized)) return false;

        var text = normalized;
        var ext = "jar";
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            ext = text[(at + 1)..];
            text = text[..at];
        }

        var parts = text.Split(':');
        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? parts[3] : null;
        if (classifier is { Length: 0 }) classifier = null;

        var fileName = classifier is null
            ? $"{artifact}-{version}.{ext}"
            : $"{artifact}-{version}-{classifier}.{ext}";

        mavenPath = $"{group.Replace('.', '/')}/{artifact}/{version}/{fileName}";
        return true;
    }

    /// <summary>MOJMAPS（<c>net.minecraft:client:*:mappings</c>）由 installer 自己从 Mojang 下，不预置。</summary>
    private static bool IsMojangMappings(string coordinate)
    {
        var text = coordinate;
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[..at];

        var parts = text.Split(':');
        return parts.Length >= 4 &&
               parts[0] == "net.minecraft" &&
               (parts[1] == "client" || parts[1] == "server") &&
               parts[3] == "mappings";
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    /// <summary>MC ≥ 1.20.5 要 Java 21，更早要 17。</summary>
    private static int RequiredJavaMajor(string minecraftVersion)
    {
        var parts = minecraftVersion.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var major = parts.Length > 0 && int.TryParse(parts[0], out var parsedMajor) ? parsedMajor : 1;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var parsedMinor) ? parsedMinor : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;

        if (major > 1) return 21;
        if (minor > 20) return 21;
        return minor == 20 && patch >= 5 ? 21 : 17;
    }

    /// <summary>NeoForge 47.x 是 1.20.1 的旧布局（<c>net/neoforged/forge</c>），20.x / 21.x 走 <c>neoforge</c>。</summary>
    private static bool IsLegacyLayout(string neoForgeVersion)
    {
        var first = neoForgeVersion.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return first.Length > 0 && int.TryParse(first[0], out var major) && major >= 40;
    }

    /// <summary>可推导的版本 id，用来在下载 7 MB 的 jar 之前判断「是不是已经装过」。</summary>
    private static IEnumerable<string> ExpectedVersionIds(string minecraftVersion, string neoForgeVersion, bool legacyLayout)
    {
        if (legacyLayout) yield return $"forge-{minecraftVersion}-{neoForgeVersion}";
        else yield return $"neoforge-{neoForgeVersion}";

        if (legacyLayout) yield return $"neoforge-{neoForgeVersion}";
        else yield return $"forge-{minecraftVersion}-{neoForgeVersion}";
    }

    private static string VersionJsonPath(string gameRoot, string versionId) =>
        Path.Combine(gameRoot, "versions", versionId, versionId + ".json");

    /// <summary>失败后清掉本次跑出来的半成品 version 目录（只删 versions/&lt;id&gt;/，不动 libraries）。</summary>
    private static void TryRemoveVersionDirectory(string gameRoot, string versionId)
    {
        try
        {
            var directory = Path.Combine(gameRoot, "versions", versionId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 删不掉就算了，主要目的只是别让下一次误判已安装
        }
    }

    private static void EnsureLauncherProfile(string gameRoot)
    {
        var path = Path.Combine(gameRoot, "launcher_profiles.json");
        if (File.Exists(path)) return;

        // installer 只检查文件存在与 profiles 字段，最小骨架就够
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

    /// <summary>把 installer 输出尾部拼进异常消息；没有预置失败时返回空串。</summary>
    private static string FormatSeedFailures(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0) return string.Empty;

        var preview = string.Join(Environment.NewLine, failures.Take(5).Select(message => "  " + message));
        return Environment.NewLine + $"预置失败的坐标（{failures.Count} 个）：" + Environment.NewLine + preview;
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

    /// <summary>installer 的 <c>install_profile.json</c> 里我们关心的那部分。</summary>
    private sealed record InstallProfile(
        string? Version,
        string? MinecraftVersion,
        IReadOnlyList<ProfileLibrary> Libraries,
        IReadOnlyList<string> ProcessorCoordinates,
        IReadOnlyList<string> DataCoordinates);

    /// <summary>一条 <c>libraries[]</c>：坐标 + 下载器给的 url/sha1/size（可能缺）。</summary>
    private sealed record ProfileLibrary(string Name, string? Url, long Size, string? Sha1);

    /// <summary>一条待预置的 maven 文件。HasMetadata 表示 url 来自 profile 而非自己拼的。</summary>
    private sealed record SeedEntry(string MavenPath, string Url, long Size, string? Sha1, bool HasMetadata);

    /// <summary>
    /// <c>data</c> 里这些键是 processor 的产出或 installer 自己下载的，预置它们既下不到也会挡住 installer：
    /// <c>MAPPINGS</c>/<c>MERGED_MAPPINGS</c> 由 installertools 从 neoform zip 里抽/合并，
    /// <c>MOJMAPS</c> 由 DOWNLOAD_MOJMAPS 从 Mojang 拉，<c>MC_*</c> 与 <c>PATCHED</c> 是打补丁的中间产物。
    /// </summary>
    private static readonly HashSet<string> ProcessorOutputKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "MAPPINGS",
        "MERGED_MAPPINGS",
        "MOJMAPS",
        "MC_UNPACKED",
        "MC_SLIM",
        "MC_EXTRA",
        "MC_SRG",
        "PATCHED",
    };

    /// <summary>下载器进度 → 整合包导入进度（阶段名固定，detail 用文件名）。</summary>
    private sealed class SeedProgress(IProgress<ModpackImportProgress> inner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            inner.Report(ModpackImportProgress.Of(
                "准备 NeoForge 安装输入",
                value.TotalFiles > 0 ? Math.Clamp(value.CompletedFiles * 100.0 / value.TotalFiles, 0, 100) : 0,
                value.CurrentFile));
    }
}
