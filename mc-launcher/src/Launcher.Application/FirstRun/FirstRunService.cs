using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Java;
using Launcher.Domain.Versions;

namespace Launcher.Application.FirstRun;

/// <summary>
/// 首次启动向导的推荐逻辑：判断「本机到底能不能直接玩」，不能的话从版本清单里挑三个候选。
/// <para>
/// 只做判断、不下载、不落盘——界面拿到 <see cref="FirstRunPlan"/> 后自己决定装哪个。
/// 这样这段逻辑可以脱离 WPF 与网络真实状态单独测（<see cref="SelectChoices"/> 是纯函数）。
/// </para>
/// </summary>
public sealed class FirstRunService(IVersionMetaClient versionMeta, IJavaLocator javaLocator)
{
    /// <summary>
    /// 粗估一个版本装下来要下多少：客户端 jar + libraries + assets 索引与资源对象，量级在 300–700 MB。
    /// <para>
    /// 这里是**故意粗糙**的固定值：真实体积要等拿到该版本的 version.json 才能算准（还要算上资源对象），
    /// 而向导在用户点之前就只有一个「大概多大」的问题。所以按 500 MB 给量级，界面上写成「约 500 MB」，
    /// 不要显示到小数点。真正精确的数字留给安装进度条（那里是逐文件的真实字节）。
    /// </para>
    /// </summary>
    public const long EstimatedInstallBytes = 500L * 1024 * 1024;

    /// <summary>没有 Java 时自动下载的 JRE 体积量级，与 <c>JavaProvisioner</c> 的下载包一致。</summary>
    private const int EstimatedJavaBytesMb = 180;

    /// <summary>
    /// 评估 <paramref name="gameRoot"/>：已经有可用版本就返回「可以直接启动」，否则给出候选版本。
    /// </summary>
    public async Task<FirstRunPlan> PlanAsync(string gameRoot, CancellationToken ct = default)
    {
        var installed = CountInstalledVersions(gameRoot);
        if (installed > 0)
        {
            // 已经有版本：不拉清单、不推下载，避免首次启动的暖路径多一次网络往返。
            return new FirstRunPlan(
                [],
                NeedsInstall: false,
                Headline: $"本机已经有 {installed} 个可用版本，选一个直接启动就行。");
        }

        VersionManifest manifest;
        try
        {
            manifest = await versionMeta.GetManifestAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 断网时向导不能整页崩掉：给一句能看懂的话 + 空候选，界面退回「手动选版本」。
            return new FirstRunPlan(
                [],
                NeedsInstall: true,
                Headline: $"没能拿到版本清单（{ex.Message}），检查网络后重试，或者手动选一个版本安装。");
        }

        var choices = SelectChoices(manifest, hasAnyVersion: false);
        var headline = choices.Count == 0
            ? "版本清单里没找到可推荐的版本，请手动选一个版本安装。"
            : $"第一次玩：推荐先装 {choices[0].VersionId}，装完点启动就能进游戏。";

        return new FirstRunPlan(choices, NeedsInstall: true, headline);
    }

    /// <summary>
    /// 检查本机有没有满足 <paramref name="requiredMajor"/> 的 Java。
    /// 返回 null 表示没问题；否则返回一句中文提示（会显示在向导里）。
    /// </summary>
    public async Task<string?> DetectJavaProblemAsync(int requiredMajor, CancellationToken ct = default)
    {
        IReadOnlyList<JavaInstall> found;
        try
        {
            found = await javaLocator.DiscoverAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"没能扫描本机 Java（{ex.Message}），启动时会自动下载 Java {requiredMajor}（约 {EstimatedJavaBytesMb} MB）。";
        }

        // DiscoverAsync 按主版本降序返回，所以 found[0] 就是本机最高版本。
        if (found.Any(j => j.MajorVersion >= requiredMajor))
        {
            return null;
        }

        return found.Count == 0
            ? $"本机没找到 Java {requiredMajor}，启动时会自动下载（约 {EstimatedJavaBytesMb} MB）。"
            : $"本机最高只有 Java {found[0].MajorVersion}，启动时会自动下载 Java {requiredMajor}（约 {EstimatedJavaBytesMb} MB）。";
    }

    /// <summary>
    /// 从版本清单里挑候选：最新正式版 → 最近的 1.20.x 正式版 → 最新快照。
    /// <para>
    /// 纯函数，不碰网络也不碰磁盘，方便以后直接加测试。
    /// <paramref name="hasAnyVersion"/> 为 true 时返回空列表——本机已经能玩了，不该再推下载。
    /// </para>
    /// </summary>
    public static IReadOnlyList<FirstRunChoice> SelectChoices(VersionManifest manifest, bool hasAnyVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (hasAnyVersion)
        {
            return [];
        }

        var choices = new List<FirstRunChoice>(3);
        var release = manifest.Latest?.Release;
        var snapshot = manifest.Latest?.Snapshot;

        if (!string.IsNullOrWhiteSpace(release))
        {
            choices.Add(new FirstRunChoice(
                release,
                $"Minecraft {release}（最新正式版）",
                "最稳的选择：正式版，模组和光影都跟得上。",
                EstimatedInstallBytes,
                Recommended: false));
        }

        // 「常见稳定版」：最近的 1.20.x 正式版。1.20 系列是现在整合包与模组最集中的一档，
        // 装完立刻能玩到大多数包。找不到就退化成最新正式版（已经在上面了就不再重复列一次）。
        var common = FindLatestRelease(manifest, static id => id.StartsWith("1.20.", StringComparison.Ordinal));
        if (common is not null && !string.Equals(common, release, StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(new FirstRunChoice(
                common,
                $"Minecraft {common}（常用稳定版）",
                "很多整合包还停在 1.20.x，兼容性最好。",
                EstimatedInstallBytes,
                Recommended: false));
        }

        if (!string.IsNullOrWhiteSpace(snapshot)
            && !string.Equals(snapshot, release, StringComparison.OrdinalIgnoreCase)
            && !choices.Any(c => string.Equals(c.VersionId, snapshot, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new FirstRunChoice(
                snapshot,
                $"Minecraft {snapshot}（预览版，可能不稳定）",
                "想提前试新内容就选它；模组通常还没跟上。",
                EstimatedInstallBytes,
                Recommended: false));
        }

        if (choices.Count == 0)
        {
            return [];
        }

        // 推荐只给第一个：顺序本身就是优先级（正式版 > 常用稳定版 > 快照）。
        return [choices[0] with { Recommended = true }, .. choices.Skip(1)];
    }

    /// <summary>清单里 releaseTime 最新的、满足 <paramref name="predicate"/> 的正式版 id。</summary>
    private static string? FindLatestRelease(VersionManifest manifest, Func<string, bool> predicate)
    {
        string? best = null;
        var bestTime = DateTimeOffset.MinValue;

        foreach (var entry in manifest.Versions)
        {
            if (!string.Equals(entry.Type, "release", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Id)
                || !predicate(entry.Id))
            {
                continue;
            }

            if (entry.ReleaseTime > bestTime)
            {
                bestTime = entry.ReleaseTime;
                best = entry.Id;
            }
        }

        return best;
    }

    /// <summary>
    /// 数「能启动」的版本目录：<c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 存在，并且要么有同名的
    /// <c>&lt;id&gt;.jar</c>（原版），要么 json 里带 <c>inheritsFrom</c>（Fabric/Forge 这类增量版本）。
    /// <para>
    /// 只看 json 会把「下载到一半崩了」的半成品算成能玩——json 先落盘、jar 后下载，
    /// 那种目录点启动会直接失败。这里的判定与 <c>InstanceRegistry</c> / <c>InstanceDeletion</c> 保持同一口径。
    /// </para>
    /// </summary>
    private static int CountInstalledVersions(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            return 0;
        }

        string versionsRoot;
        try
        {
            versionsRoot = Path.Combine(gameRoot, "versions");
        }
        catch (ArgumentException)
        {
            return 0;
        }

        if (!Directory.Exists(versionsRoot))
        {
            return 0;
        }

        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.Length == 0)
            {
                continue;
            }

            var jsonPath = Path.Combine(directory, name + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, name + ".jar")) || HasParentVersion(jsonPath))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>json 里有没有 <c>inheritsFrom</c>；读不动就当作没有（宁可多问一句「要不要装」）。</summary>
    private static bool HasParentVersion(string jsonPath)
    {
        try
        {
            using var stream = File.OpenRead(jsonPath);
            var json = JsonSerializer.Deserialize<VersionJson>(stream);
            return !string.IsNullOrWhiteSpace(json?.InheritsFrom);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
