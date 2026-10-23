using Launcher.Domain.Versions;

namespace Launcher.Domain.Install;

/// <summary>
/// 把版本 JSON 折算成"要下哪些文件"。纯计算，不碰磁盘和网络，方便单测。
/// 注意：assets/objects 需要读完 assetIndex 才知其体量，这里只产出 index 本身。
/// </summary>
public static class InstallPlanner
{
    public static InstallPlan Build(VersionJson version, PlatformContext ctx)
    {
        var entries = new List<DownloadEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string url, string relativePath, long size, string? sha1, DownloadKind kind)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            // JSON 里用 / 分隔，本地路径统一成平台分隔符，避免出现 D:\mc\libraries/io/... 这种混写
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            if (!seenPaths.Add(normalized)) return;
            entries.Add(new DownloadEntry(url, normalized, size, sha1, kind));
        }

        // 1) client.jar。加载器版本复用父版本的 jar（重复副本会让 Fabric remap 失败）
        if (version.Downloads?.Client is { } client && version.LauncherParentId is null)
        {
            Add(client.Url,
                $"versions/{version.Id}/{version.Id}.jar",
                client.Size, client.Sha1, DownloadKind.Client);
        }

        // 2) libraries
        var allowed = 0;
        var skipped = 0;

        foreach (var lib in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, ctx))
                continue;

            allowed++;

            // 老式 natives（≤1.12）：natives 字典给分类器名，实际 jar 在 classifiers 里
            if (lib.Natives is not null && lib.Natives.TryGetValue(ctx.OsName, out var classifier))
            {
                var artifact = lib.Downloads?.Classifiers?.GetValueOrDefault(classifier);
                if (artifact is not null)
                {
                    var path = artifact.Path ?? $"libraries/{lib.MavenPath(classifier)}";
                    Add(artifact.Url,
                        path.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) ? path : "libraries/" + path,
                        artifact.Size, artifact.Sha1, DownloadKind.Native);
                    continue;
                }
            }

            if (lib.Downloads?.Artifact is { } art)
            {
                var path = art.Path ?? lib.MavenPath();
                Add(art.Url,
                    path.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) ? path : "libraries/" + path,
                    art.Size, art.Sha1, DownloadKind.Library);
                continue;
            }

            // 只有 maven 坐标的库（Fabric/Quilt 的库都是这种形态）：自己拼 URL
            if (!string.IsNullOrWhiteSpace(lib.MavenBaseUrl))
            {
                var mavenPath = lib.MavenPath();
                var baseUrl = lib.MavenBaseUrl.TrimEnd('/');
                Add($"{baseUrl}/{mavenPath}",
                    "libraries/" + mavenPath,
                    0,      // 大小未知：下载器会跳过大小校验，只做 sha1（这里也没有 sha1）
                    null,
                    DownloadKind.Library);
                continue;
            }

            // 既没有 downloads 也没有 maven 基址：M1 阶段先跳过并计数，避免静默漏库
            skipped++;
        }

        // 3) asset index
        if (version.AssetIndex is { } index)
        {
            Add(index.Url,
                $"assets/indexes/{index.Id}.json",
                index.Size, index.Sha1, DownloadKind.AssetIndex);
        }

        var requiredJava = version.JavaVersion?.MajorVersion ?? 8;

        return new InstallPlan(version.Id, version.MainClass, requiredJava, entries, allowed, skipped);
    }
}
