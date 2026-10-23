using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Domain.Launch;

namespace Launcher.Application.Launch;

/// <summary>
/// 一次 natives 准备的结果。<see cref="ExtractedFiles"/> 是「这次真正写盘的文件数」，
/// 命中缓存时恒为 0——测试和日志都靠这两个字段判断有没有重复解压。
/// </summary>
public sealed record NativesProvision(
    string Directory,
    string Fingerprint,
    int Files,
    int ExtractedFiles,
    bool Reused,
    int JarCount,
    string? Note = null)
{
    /// <summary>一行结论，直接进日志/状态栏。</summary>
    public string Summary() => JarCount == 0
        ? "没有 native jar，无需解压"
        : Reused
            ? $"复用 natives 缓存（{Files} 个文件，指纹 {Fingerprint}）"
            : $"解压 natives（{JarCount} 个 jar → {Files} 个文件，指纹 {Fingerprint}）"
              + (Note is null ? "" : "  " + Note);
}

/// <summary>
/// 把 ≤1.12 的 native jar 解压到稳定目录 <c>&lt;gameRoot&gt;/natives/&lt;versionId&gt;/&lt;指纹&gt;/</c>，
/// 已解压且完整就直接复用。指纹 = 每个 native jar 的「路径 + 长度 + mtime」的 SHA-256 前 16 位，
/// 所以换 Java/LWJGL 版本、重下 jar、甚至只是重新打包都会换目录，旧缓存留着不影响正确性。
/// </summary>
/// <remarks>
/// 为什么用「稳定目录 + 完整标记」而不是「每次 rm -rf 再解」：
/// <list type="bullet">
/// <item>解压是几十毫秒级的纯 IO，但对 185 个 mod 的整合包来说每次都做就是纯浪费；</item>
/// <item>半成品目录被当成有效缓存，会表现为进游戏后 <c>UnsatisfiedLinkError: no lwjgl in java.library.path</c>，
/// 而且日志里看不出是解压中断——所以先解到 <c>.staging-*</c>，写完成标记，再整体 rename 就位。</item>
/// </list>
/// </remarks>
public static class NativesProvisioner
{
    /// <summary>完成标记。目录里有它、指纹对得上、列出的文件长度也对，才算「完整」。</summary>
    public const string MarkerFileName = ".natives-provision.json";

    private const string StagingPrefix = ".staging-";

    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record MarkerFile(string Name, long Length);

    private sealed record Marker(string Fingerprint, IReadOnlyList<MarkerFile> Files);

    /// <summary>
    /// natives 指纹：jar 列表的「路径 + 长度 + mtime」拼串后取 SHA-256 前 16 位。
    /// 不用文件内容哈希是因为 native jar 动辄几 MB，算内容哈希的耗时会超过解压本身。
    /// </summary>
    public static string Fingerprint(IEnumerable<string> nativeJars)
    {
        var builder = new StringBuilder();
        foreach (var jar in nativeJars)
        {
            builder.Append(Normalize(jar)).Append('|');
            try
            {
                var info = new FileInfo(jar);
                builder.Append(info.Exists ? info.Length : -1).Append('|')
                       .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                builder.Append("-1|0");
            }

            builder.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>缓存目录：<c>&lt;gameRoot&gt;/natives/&lt;versionId&gt;/&lt;指纹&gt;</c>。</summary>
    public static string ResolveDirectory(string gameRoot, string versionId, string fingerprint) =>
        Path.Combine(gameRoot, "natives", Sanitize(versionId), fingerprint);

    /// <summary>没有 native jar 时的历史位置（<c>versions/&lt;id&gt;/natives</c>），只为返回值不为空。</summary>
    public static string LegacyDirectory(string gameRoot, string versionId) =>
        Path.Combine(gameRoot, "versions", Sanitize(versionId), "natives");

    /// <summary>同步版：解压/校验要做几十次文件系统调用，调用方通常是异步方法，外面套 <see cref="ProvisionAsync"/>。</summary>
    public static NativesProvision Provision(
        string gameRoot,
        string versionId,
        IReadOnlyList<string> nativeJars,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        if (nativeJars.Count == 0)
        {
            return new NativesProvision(LegacyDirectory(gameRoot, versionId), string.Empty, 0, 0, Reused: true, JarCount: 0);
        }

        var fingerprint = Fingerprint(nativeJars);
        var target = ResolveDirectory(gameRoot, versionId, fingerprint);

        if (IsComplete(target, fingerprint, out var cachedFiles))
            return new NativesProvision(target, fingerprint, cachedFiles, 0, Reused: true, nativeJars.Count);

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        CleanupStaleStaging(parent);

        var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);

            var files = ExtractInto(nativeJars, staging, ct);
            WriteMarker(staging, fingerprint, files);

            // 整体就位：目标目录要么不存在，要么是「不完整」的旧目录，直接换掉
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);

            return new NativesProvision(target, fingerprint, files.Count, files.Count, Reused: false, nativeJars.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);

            // 并发启动/另一个进程刚好把目录装好了：这是好事，直接复用，不报错
            if (IsComplete(target, fingerprint, out var racedFiles))
                return new NativesProvision(target, fingerprint, racedFiles, 0, Reused: true, nativeJars.Count, Note: "并发就位，已复用");

            throw new InvalidOperationException($"natives 解压失败：{target}（{ex.GetType().Name}: {ex.Message}）", ex);
        }
    }

    /// <summary>异步壳子：解压本身是同步 IO，丢线程池避免堵 UI 线程。</summary>
    public static Task<NativesProvision> ProvisionAsync(
        string gameRoot,
        string versionId,
        IReadOnlyList<string> nativeJars,
        CancellationToken ct = default) =>
        Task.Run(() => Provision(gameRoot, versionId, nativeJars, ct), ct);

    /// <summary>
    /// 目录是否「已解压且完整」：完成标记在、指纹对得上、标记里列出的每个文件长度也对。
    /// 只校验长度不校验哈希——native dll 被改坏到长度不变的概率，远小于多算几个哈希的启动开销。
    /// </summary>
    private static bool IsComplete(string directory, string fingerprint, out int fileCount)
    {
        fileCount = 0;
        try
        {
            var markerPath = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(markerPath)) return false;

            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath), MarkerOptions);
            if (marker is null || !string.Equals(marker.Fingerprint, fingerprint, StringComparison.Ordinal)) return false;
            if (marker.Files.Count == 0) return false;

            foreach (var file in marker.Files)
            {
                var info = new FileInfo(Path.Combine(directory, file.Name));
                if (!info.Exists || info.Length != file.Length) return false;
            }

            fileCount = marker.Files.Count;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;   // 标记坏了就当没有，重新解一遍最省事
        }
    }

    private static List<MarkerFile> ExtractInto(IReadOnlyList<string> nativeJars, string staging, CancellationToken ct)
    {
        var written = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var jar in nativeJars)
        {
            ct.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(jar);
            foreach (var entry in archive.Entries)
            {
                // 目录项；native jar 里的 dll 是平铺在根下的，遇到子目录也只取文件名
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileName(entry.FullName);
                if (name.Length == 0) continue;

                var destination = Path.Combine(staging, name);
                entry.ExtractToFile(destination, overwrite: true);
                written[name] = new FileInfo(destination).Length;
            }
        }

        return written
            .Select(kv => new MarkerFile(kv.Key, kv.Value))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteMarker(string directory, string fingerprint, IReadOnlyList<MarkerFile> files)
    {
        var json = JsonSerializer.Serialize(new Marker(fingerprint, files), MarkerOptions);
        File.WriteAllText(Path.Combine(directory, MarkerFileName), json);
    }

    /// <summary>清掉上次崩溃留下的半成品目录（超过 1 小时的），不影响本次结果。</summary>
    private static void CleanupStaleStaging(string parent)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddHours(-1);
            foreach (var dir in Directory.EnumerateDirectories(parent, StagingPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) > deadline) continue;
                TryDelete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理永远是尽力而为：删不掉就留着，反正目录名带时间/guid，不会被当缓存命中
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同上：删不掉不算失败
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Replace('\\', '/');
        }
    }

    /// <summary>版本号里偶尔有 <c>:</c>（自定义 id）之类不能进目录名的字符，统一换掉。</summary>
    private static string Sanitize(string versionId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(versionId.Length);
        foreach (var c in versionId)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return builder.ToString();
    }
}
