using System.Diagnostics;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Launcher.Application.Abstractions;
using Launcher.Domain.Java;

namespace Launcher.Infrastructure.Java;

/// <summary>
/// Java 发现与探测。只扫已知安装根目录 + PATH + JAVA_HOME +（可选）调用方给的额外根，不做全盘搜索。
/// </summary>
public sealed class JavaLocator : IJavaLocator
{
    /// <summary>
    /// 额外的 Java 安装根目录。典型用途：让发现逻辑也能看见启动器自己下载的 JRE，
    /// 例如 <c>new JavaLocator([Path.Combine(AppPaths.DataDirectory, "java")])</c>。
    /// 每个根两种布局都认：<c>&lt;root&gt;/bin/java.exe</c>（根本身就是一个 JAVA_HOME）
    /// 和 <c>&lt;root&gt;/*/bin/java.exe</c>（根下面是各版本目录，即 java/21/bin/java.exe）。
    /// 传 null 或省略时，行为与没有这个参数之前完全一致。
    /// </summary>
    private readonly string[] _extraRoots;

    private static readonly string JavaExeName = OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <param name="extraRoots">额外扫描根，见 <see cref="_extraRoots"/>；空则只扫 PATH / JAVA_HOME / 已知安装根。</param>
    public JavaLocator(IEnumerable<string>? extraRoots = null)
    {
        _extraRoots = extraRoots is null
            ? []
            : extraRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => root.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static readonly string[] KnownRoots = OperatingSystem.IsWindows() ?
    [
        @"C:\Program Files\Java",
        @"C:\Program Files (x86)\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Eclipse Foundation",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\IBM\Semeru",
        @"C:\Program Files\SapMachine",
        @"C:\Program Files\JetBrains",
        @"C:\Program Files\Android\Android Studio",
        @"C:\Program Files\Common Files\Oracle\Java",
    ] : OperatingSystem.IsMacOS() ? ["/Library/Java/JavaVirtualMachines",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library/Java/JavaVirtualMachines"),"/opt/homebrew/opt","/usr/local/opt"] : ["/usr/lib/jvm","/usr/java","/opt/java"];

    private static readonly TimeSpan ProbeCacheLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, ProbeState> _probes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ProbeState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public JavaInstall? Install;
        public (long Length, long Modified, long ReleaseModified) Fingerprint;
        public DateTime ExpiresAt;
    }

    private List<string> FindCandidates(CancellationToken ct)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) PATH 里的 java
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var exe = Path.Combine(dir.Trim(), JavaExeName);
            if (File.Exists(exe)) candidates.Add(exe);
        }

        // 2) JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Path.Combine(javaHome, "bin", JavaExeName);
            if (File.Exists(exe)) candidates.Add(exe);
        }

        // 3) 已知安装根：depth 1 和 depth 2 都看（Zulu 会多一层）
        foreach (var root in KnownRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            foreach (var binDir in Descend(root, maxDepth: OperatingSystem.IsMacOS()?5:3, ct))
            {
                var exe = Path.Combine(binDir, JavaExeName);
                if (File.Exists(exe)) candidates.Add(exe);
            }
        }

        // 4) 调用方给的额外根：<root>/bin/java.exe 和 <root>/*/bin/java.exe 两种布局都认。
        //    后者就是启动器自己装的 java/<major>/bin/java.exe。
        foreach (var root in _extraRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            var direct = Path.Combine(root, "bin", JavaExeName);
            if (File.Exists(direct)) candidates.Add(direct);

            foreach (var binDir in Descend(root, maxDepth: 2, ct))
            {
                var exe = Path.Combine(binDir, JavaExeName);
                if (File.Exists(exe)) candidates.Add(exe);
            }
        }

        return candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<JavaInstall>> DiscoverAsync(CancellationToken ct = default)
    {
        var ordered = await Task.Run(() => FindCandidates(ct), ct).ConfigureAwait(false);
        var results = new List<JavaInstall>();

        // 并行探测：12 个 JVM 串行要 3.6s，并行 + 限流 4 大约 1s
        using var gate = new SemaphoreSlim(4);
        var tasks = ordered.Select(async exe =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await ProbeAsync(exe, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        });

        foreach (var install in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (install is not null) results.Add(install);
        }

        return results
            .GroupBy(j => j.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(j => j.MajorVersion)
            .ThenBy(j => j.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<JavaInstall?> ProbeAsync(string javaExePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolveExecutable(javaExePath);
        if (path is null) return null;
        var state = _probes.GetOrAdd(path, _ => new ProbeState());
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fingerprint = Fingerprint(path);
            if (state.Install is not null && state.Fingerprint == fingerprint && DateTime.UtcNow < state.ExpiresAt)
                return state.Install;
            var install = await ProbeProcessAsync(path, ct).ConfigureAwait(false);
            if (install is not null && fingerprint == Fingerprint(path))
            {
                state.Install = install;
                state.Fingerprint = fingerprint;
                state.ExpiresAt = DateTime.UtcNow + ProbeCacheLifetime;
            }
            else state.Install = null;
            return install;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally { state.Gate.Release(); }
    }

    private static string? ResolveExecutable(string executable)
    {
        var name = OperatingSystem.IsWindows() && !Path.HasExtension(executable) ? executable + ".exe" : executable;
        if (File.Exists(name)) return Path.GetFullPath(name);
        if (Path.GetFileName(name) != name) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim().Trim('"'), name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static (long Length, long Modified, long ReleaseModified) Fingerprint(string path)
    {
        var info = new FileInfo(path);
        var home = Path.GetDirectoryName(Path.GetDirectoryName(path));
        var release = home is null ? null : new FileInfo(Path.Combine(home, "release"));
        return (info.Length, info.LastWriteTimeUtc.Ticks, release?.Exists == true ? release.LastWriteTimeUtc.Ticks : 0);
    }

    private static async Task<JavaInstall?> ProbeProcessAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-XshowSettings:properties");
            psi.ArgumentList.Add("-version");
            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
                return Parse(path, string.Concat(await stderr.ConfigureAwait(false), await stdout.ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    internal static JavaInstall? Parse(string javaExePath, string output)
    {
        var match = Regex.Match(output, @"version\s+""(?<ver>[^""]+)""");
        if (!match.Success)
            return null;

        var versionString = match.Groups["ver"].Value;
        var parts = versionString.Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
            return null;

        // 1.8.0_421 → 8
        if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var minor))
            major = minor;

        var vendor =
            output.Contains("Zulu", StringComparison.OrdinalIgnoreCase) ? "Zulu" :
            output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Adoptium", StringComparison.OrdinalIgnoreCase) ? "Adoptium" :
            output.Contains("Corretto", StringComparison.OrdinalIgnoreCase) ? "Amazon" :
            output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "Microsoft" :
            output.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ? "Oracle" :
            output.Contains("Semeru", StringComparison.OrdinalIgnoreCase) ? "IBM" :
            "OpenJDK";

        var arch =
            output.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64" :
            output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("x86_64", StringComparison.OrdinalIgnoreCase) || output.Contains("amd64",StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";

        return new JavaInstall(javaExePath, major, versionString, vendor, arch);
    }

    private static IEnumerable<string> Descend(string root, int maxDepth, CancellationToken ct)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            string[] children;
            try { children = Directory.GetDirectories(current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                var bin = Path.Combine(child, "bin");
                if (Directory.Exists(bin)) yield return bin;   // 找到了一个 JAVA_HOME
                queue.Enqueue((child, depth + 1));
            }
        }
    }
}
