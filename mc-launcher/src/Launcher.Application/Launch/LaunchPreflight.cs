using Launcher.Domain.Launch;
using Launcher.Domain.Versions;

namespace Launcher.Application.Launch;

/// <summary>预检发现的缺口类别。</summary>
public enum PreflightGapKind
{
    /// <summary>classpath 上的某个 jar 不存在。</summary>
    ClassPathJar = 0,

    /// <summary>natives 目录缺失或为空。</summary>
    NativesDirectory = 1,

    /// <summary>assets/indexes/&lt;index&gt;.json 不存在。</summary>
    AssetIndex = 2,
}

/// <summary>
/// 一条缺口。<see cref="Blocking"/> 区分「启动必定失败」和「大概率能跑，只是不完整」：
/// 库 jar 和 asset index 缺了，Java 侧要么 NoClassDefFoundError 要么资源加载直接抛；
/// 而 natives 目录在 1.13+ 被 LWJGL3 自己解压到临时目录，缺了通常也能进游戏。
/// </summary>
public sealed record PreflightGap(PreflightGapKind Kind, string Path, bool Blocking, string? Detail = null)
{
    public string KindText => Kind switch
    {
        PreflightGapKind.ClassPathJar => "库文件",
        PreflightGapKind.NativesDirectory => "natives 目录",
        PreflightGapKind.AssetIndex => "资源索引",
        _ => "文件",
    };
}

/// <summary>
/// 启动前预检结果。<c>Describe()</c> 直接给「缺 N 个文件，先补全再启动」这种能贴到界面上的文案，
/// 调用方不必再自己拼字符串。
/// </summary>
public sealed record LaunchPreflightReport(
    IReadOnlyList<PreflightGap> Gaps,
    int CheckedJars,
    string NativesDirectory,
    string? AssetIndexPath)
{
    public int BlockingCount => Gaps.Count(g => g.Blocking);

    public int AdvisoryCount => Gaps.Count(g => !g.Blocking);

    /// <summary>只有阻塞项为 0 才算「可以启动」。</summary>
    public bool IsReady => BlockingCount == 0;

    /// <summary>一行结论，适合放状态栏。</summary>
    public string Summary() => IsReady
        ? $"预检通过（{CheckedJars} 个库文件、natives、资源索引都在）" + (AdvisoryCount > 0 ? $"，另有 {AdvisoryCount} 条提示" : "")
        : $"缺少 {BlockingCount} 个文件，先补全再启动";

    /// <summary>
    /// 多行明细。<paramref name="limit"/> 限制每类最多列几条，避免 185 个 mod 的实例刷屏。
    /// </summary>
    public string Describe(int limit = 8)
    {
        var builder = new System.Text.StringBuilder(Summary());

        var blocking = Gaps.Where(g => g.Blocking).ToList();
        foreach (var group in blocking.GroupBy(g => g.Kind).OrderBy(g => g.Key))
        {
            builder.AppendLine();
            builder.Append($"  [{group.First().KindText}]").AppendLine();
            foreach (var gap in group.Take(limit))
            {
                builder.Append("    · ").Append(gap.Path);
                if (gap.Detail is { Length: > 0 }) builder.Append("  (").Append(gap.Detail).Append(')');
                builder.AppendLine();
            }

            if (group.Count() > limit) builder.Append("    · …还有 ").Append(group.Count() - limit).AppendLine(" 个");
        }

        foreach (var gap in Gaps.Where(g => !g.Blocking))
        {
            builder.AppendLine();
            builder.Append("  [提示] ").Append(gap.KindText).Append(' ').Append(gap.Path);
            builder.AppendLine(gap.Detail is { Length: > 0 } ? "  (" + gap.Detail + ")" : "");
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// 拉起 Java 之前的完整性预检：classpath 上每个 jar、natives 目录、asset index。
/// 存在的目的很实际——缺库时 Java 只会抛 <c>NoClassDefFoundError</c> 或者跑到一半崩，
/// 报错点和真正原因隔着几千行日志；在这里挡住就能直接告诉用户「缺哪个文件」。
/// 纯读操作，不写盘、不动配置。
/// </summary>
public static class LaunchPreflight
{
    public static async Task<LaunchPreflightReport> CheckAsync(
        VersionJson version,
        PlatformContext ctx,
        LaunchContext launch,
        CancellationToken ct = default, ClassPathResult? preparedClassPath = null)
    {
        // classpath 拼装是纯 CPU（要跑 rules、去重、Forge patched client 判断），丢给线程池别堵调用方
        var classPath = preparedClassPath ?? await Task.Run(
            () => ClassPathBuilder.Build(version, ctx, launch.LibrariesRoot, launch.ClientJarPath),
            ct).ConfigureAwait(false);

        // 和 LaunchPlanBuilder 用同一个解析：预检看的目录必须就是 -Djava.library.path 指的那个，
        // 否则会出现「预检说缺 natives、进程其实用的是另一份」的错位。
        var nativesDirectory = LaunchPlanBuilder.ResolveNativesDirectory(launch);

        // ≤1.12 的 natives 是 classifier jar，必须解压到这个目录才用得着；
        // 1.13+ 的 natives jar 直接在 classpath 上，目录只是个历史位置。
        var needsExtractedNatives = classPath.NativeJars.Count > 0;

        var assetIndexPath = ResolveAssetIndexPath(version, launch.AssetsRoot);

        // 三类检查互不依赖，并行跑；jar 之间存在文件系统抖动，单条 File.Exists 也就微秒级
        var jarTask = Task.Run(() => CheckJars(classPath.Entries, ct), ct);
        var nativesTask = Task.Run(() => CheckNatives(nativesDirectory, needsExtractedNatives), ct);
        var assetTask = Task.Run(() => CheckAssetIndex(assetIndexPath), ct);

        await Task.WhenAll(jarTask, nativesTask, assetTask).ConfigureAwait(false);

        var gaps = new List<PreflightGap>(jarTask.Result);
        if (nativesTask.Result is { } nativesGap) gaps.Add(nativesGap);
        if (assetTask.Result is { } assetGap) gaps.Add(assetGap);

        // 输出顺序要稳定：同样的缺口每次都按 类别 → 路径 排，方便比对两次预检
        gaps.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(a.Path, b.Path);
        });

        return new LaunchPreflightReport(gaps, classPath.Entries.Count, nativesDirectory, assetIndexPath);
    }

    /// <summary>和 <see cref="LaunchPlanBuilder"/> 用同一套 assets 命名规则，避免两处对不上。</summary>
    private static string? ResolveAssetIndexPath(VersionJson version, string assetsRoot)
    {
        var indexId = version.Assets;
        if (string.IsNullOrWhiteSpace(indexId)) indexId = version.AssetIndex?.Id;
        if (string.IsNullOrWhiteSpace(indexId)) return null;   // 老版本 JSON 可能两个字段都没有，无从校验

        return Path.Combine(assetsRoot, "indexes", indexId + ".json");
    }

    private static List<PreflightGap> CheckJars(IReadOnlyList<string> entries, CancellationToken ct)
    {
        var missing = new System.Collections.Concurrent.ConcurrentBag<PreflightGap>();

        Parallel.ForEach(
            entries,
            new ParallelOptions { CancellationToken = ct },
            entry =>
            {
                if (!Exists(entry)) missing.Add(new PreflightGap(PreflightGapKind.ClassPathJar, entry, Blocking: true));
            });

        return [.. missing];
    }

    private static PreflightGap? CheckNatives(string directory, bool needsExtractedNatives)
    {
        if (!DirectoryExists(directory))
        {
            return new PreflightGap(
                PreflightGapKind.NativesDirectory,
                directory,
                Blocking: needsExtractedNatives,
                Detail: needsExtractedNatives ? "老版本需要在这里解压 natives，目录不存在" : "1.13+ 会自行解压到临时目录，可忽略");
        }

        try
        {
            var hasAnyFile = Directory.EnumerateFileSystemEntries(directory).Any();
            if (hasAnyFile) return null;

            return new PreflightGap(
                PreflightGapKind.NativesDirectory,
                directory,
                Blocking: needsExtractedNatives,
                Detail: needsExtractedNatives ? "目录为空" : "目录为空，1.13+ 可忽略");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreflightGap(PreflightGapKind.NativesDirectory, directory, Blocking: needsExtractedNatives, Detail: ex.Message);
        }
    }

    private static PreflightGap? CheckAssetIndex(string? indexPath)
    {
        if (indexPath is null || Exists(indexPath)) return null;
        return new PreflightGap(PreflightGapKind.AssetIndex, indexPath, Blocking: true);
    }

    /// <summary>
    /// 单个路径是否存在。路径里带非法字符、超长、权限不足都算「没有」——
    /// 预检本身不能因为一个坏路径就把整个启动流程炸掉。
    /// </summary>
    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 目录是否存在。<c>File.Exists</c> 对目录永远返回 false，natives 这种「目录」不能拿它判——
    /// 之前就是这里写错，导致解压好的目录仍被报成「目录不存在」。
    /// </summary>
    private static bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
