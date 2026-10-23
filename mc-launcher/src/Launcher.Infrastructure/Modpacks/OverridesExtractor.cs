using System.IO.Compression;
using Launcher.Application.Abstractions;
using Launcher.Application.Install;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// overrides 的分层解压。Modrinth / CurseForge 两个规范都允许包里同时有
/// <c>overrides/</c> 与 <c>client-overrides/</c>，语义是后者覆盖前者的同名文件。
/// <para>
/// 实现就是按层顺序依次写盘：先 <c>overrides/</c>，再 <c>client-overrides/</c>，
/// 同名文件后写的自然盖掉先写的。不要按名字排序 —— 那样覆盖顺序会变成字典序，
/// <c>client-overrides</c> 恰好排在 <c>overrides</c> 前面，覆盖方向就反了。
/// </para>
/// <para>路径统一的走 <see cref="LongPath"/>：整合包里 300+ 字符的条目很常见，普通 Path.Combine 会炸。</para>
/// </summary>
internal static class OverridesExtractor
{
    public const string Stage = "解压 overrides";

    /// <summary>客户端覆盖层目录名（CurseForge 规范里就是固定这个词）。</summary>
    public const string ClientOverridesDirectory = "client-overrides";

    /// <summary>每写这么多文件报一次进度。</summary>
    private const int ProgressEvery = 50;

    /// <summary>
    /// 按 <paramref name="layers"/> 顺序解压到 <paramref name="targetDirectory"/>，返回写出的文件数
    /// （覆盖写也算一次，所以可能大于最终落地文件数）。
    /// </summary>
    public static async Task<int> ExtractAsync(
        ZipArchive archive,
        IReadOnlyList<string> layers,
        string targetDirectory,
        IProgress<ModpackImportProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var plan = BuildPlan(archive, layers);
        if (plan.Count == 0) return 0;

        var root = Path.GetFullPath(targetDirectory);
        LongPath.EnsureDirectory(root);

        var written = 0;
        foreach (var (entry, relative) in plan)
        {
            ct.ThrowIfCancellationRequested();

            var destination = LongPath.Combine(root, relative);
            LongPath.EnsureDirectory(Path.GetDirectoryName(destination)!);

            await using (var source = entry.Open())
            await using (var target = LongPath.CreateFile(destination))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);

            written++;
            if (written % ProgressEvery == 0)
                progress?.Report(ModpackImportProgress.Of(
                    Stage, written * 100.0 / plan.Count, $"{written} / {plan.Count}"));
        }

        progress?.Report(ModpackImportProgress.Of(Stage, 100, $"{written} 个文件"));
        return written;
    }

    /// <summary>按层顺序展开成「条目 + 相对路径」，目录项（Name 为空）跳过。</summary>
    private static List<(ZipArchiveEntry Entry, string Relative)> BuildPlan(
        ZipArchive archive,
        IReadOnlyList<string> layers)
    {
        var plan = new List<(ZipArchiveEntry, string)>();

        foreach (var layer in layers)
        {
            if (string.IsNullOrWhiteSpace(layer)) continue;
            var prefix = layer.TrimEnd('/', '\\') + "/";

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
                if (relative.Length == 0) continue;

                plan.Add((entry, relative));
            }
        }

        return plan;
    }
}
