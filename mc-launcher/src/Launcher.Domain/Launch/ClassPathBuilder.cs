using Launcher.Domain.Versions;

namespace Launcher.Domain.Launch;

/// <summary>classpath 拼装结果。NativeJars 只对 ≤1.12 的老版本有意义（需要解压到 natives 目录）。</summary>
public sealed record ClassPathResult(
    IReadOnlyList<string> Entries,
    IReadOnlyList<string> NativeJars,
    int SkippedLibraries)
{
    public string ToClassPathString(char separator) => string.Join(separator, Entries);
}

public static class ClassPathBuilder
{
    /// <summary>
    /// 按 rules 过滤后拼 classpath：
    /// - 现代版本（1.13+）的 natives jar 也在 classpath 上，由 LWJGL3 自己解压；
    /// - 老版本（≤1.12）的 natives 走 classifiers，不进 classpath，单独返回给解压流程；
    /// - 同一 group:artifact 出现多次时保留后者（子版本顶掉父版本），否则 Fabric 会因重复 ASM 拒绝启动；
    /// - client.jar 永远排在最后（与官方启动器一致）。
    /// </summary>
    public static ClassPathResult Build(
        VersionJson version,
        PlatformContext ctx,
        string librariesRoot,
        string clientJarPath)
    {
        var byCoordinate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var nativeJars = new List<string>();
        var nativeSeen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, ctx)) continue;

            // 老式 natives：classifier jar 需要解压，不参与 classpath
            if (lib.Natives is not null && lib.Natives.TryGetValue(ctx.OsName, out var classifier))
            {
                var nativesArtifact = lib.Downloads?.Classifiers?.GetValueOrDefault(classifier);
                var relative = nativesArtifact?.Path ?? lib.MavenPath(classifier);
                var nativePath = Path.Combine(librariesRoot, ToNativeSeparators(relative));
                if (nativeSeen.Add(nativePath)) nativeJars.Add(nativePath);
                continue;
            }

            var libraryPath = lib.Downloads?.Artifact?.Path ?? lib.MavenPath();
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                skipped++;
                continue;
            }

            var fullPath = Path.Combine(librariesRoot, ToNativeSeparators(libraryPath));
            var key = CoordinateKey(lib);

            if (byCoordinate.TryGetValue(key, out var existing))
            {
                // 后出现的版本顶掉前面的，但保持首次出现的位置
                if (!string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                    byCoordinate[key] = fullPath;
            }
            else
            {
                byCoordinate[key] = fullPath;
                order.Add(key);
            }
        }

        var entries = order.Select(key => byCoordinate[key]).ToList();

        // Forge 1.17+ 的 patched client 不在 version JSON 的 libraries 里（installer 只把它生成到
        // libraries/net/minecraftforge/...）。它必须顶替原版 client.jar：两者同时进 classpath 会被
        // modlauncher 拆成 minecraft 与 _1._20._1 两个模块，两边都导出同一个包，直接 ResolutionException。
        //
        // NeoForge 则相反 —— 它**一个 client jar 都不要**：
        //   · 塞 patched client：它和 universal jar 的自动模块名都是 neoforge，报
        //     "Module mixinextras.neoforge reads more than one module named neoforge"；
        //   · 塞原版 client.jar：它是自动模块 _1._21._4，与 FML 自己建的 minecraft 模块包重叠，报
        //     "Module minecraft contains package net.minecraft.data, module _1._21._4 exports ..."。
        // 游戏本体由 FML 按 --fml.neoFormVersion 从 libraries 里取 patched 版本。
        // 实测：两种都不放，NeoForge 21.4.123 能正常进主菜单。
        var isNeoForge = version.Id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase);

        if (!isNeoForge &&
            string.Equals(version.MainClass, ModLauncherBootstrapMainClass, StringComparison.Ordinal))
        {
            if (TryResolvePatchedClientJar(version.Id, librariesRoot) is { } patchedClient)
                clientJarPath = patchedClient;
        }

        if (!isNeoForge &&
            !string.IsNullOrWhiteSpace(clientJarPath) &&
            !entries.Contains(clientJarPath, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(clientJarPath);
        }

        return new ClassPathResult(entries, nativeJars, skipped);
    }

    private static string CoordinateKey(Library library)
    {
        var parts = library.Name.Split(':');
        var classifier = parts.Length > 3 ? ":" + parts[3] : "";
        return $"{library.GroupId}:{library.ArtifactId}{classifier}";
    }

    /// <summary>Forge / NeoForge 1.17+ 共用的 bootstrap 主类，用它判断「这个版本的客户端 jar 是 patched 版」。</summary>
    private const string ModLauncherBootstrapMainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher";

    /// <summary>
    /// 按 maven 惯例定位 patched client。
    /// <list type="bullet">
    /// <item>Forge：版本 id <c>1.20.1-forge-47.4.18</c> → <c>net/minecraftforge/forge/1.20.1-47.4.18/forge-1.20.1-47.4.18-client.jar</c></item>
    /// <item>NeoForge：版本 id <c>neoforge-21.4.123</c> → <c>net/neoforged/neoforge/21.4.123/neoforge-21.4.123-client.jar</c></item>
    /// <item>NeoForge 1.20.1 早期：版本 id <c>1.20.1-forge-47.1.x</c> → <c>net/neoforged/forge/1.20.1-47.1.x/</c></item>
    /// </list>
    /// 找不到就返回 null（调用方保留原版 client.jar 的行为不变）。
    /// </summary>
    private static string? TryResolvePatchedClientJar(string versionId, string librariesRoot)
    {
        foreach (var candidate in PatchedClientCandidates(versionId, librariesRoot))
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static IEnumerable<string> PatchedClientCandidates(string versionId, string librariesRoot)
    {
        if (versionId.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
        {
            var neoforgeVersion = versionId["neoforge-".Length..];
            yield return Path.Combine(librariesRoot, "net", "neoforged", "neoforge", neoforgeVersion,
                $"neoforge-{neoforgeVersion}-client.jar");
            yield break;
        }

        var mavenVersion = versionId.Replace("-forge-", "-", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(mavenVersion, versionId, StringComparison.Ordinal)) yield break;

        yield return Path.Combine(librariesRoot, "net", "minecraftforge", "forge", mavenVersion,
            $"forge-{mavenVersion}-client.jar");
        // NeoForge 1.20.1 的等价布局，安装的是同一个 patched client
        yield return Path.Combine(librariesRoot, "net", "neoforged", "forge", mavenVersion,
            $"forge-{mavenVersion}-client.jar");
    }

    private static string ToNativeSeparators(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);
}
