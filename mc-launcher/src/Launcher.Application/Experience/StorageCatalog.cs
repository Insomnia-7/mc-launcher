namespace Launcher.Application.Experience;

public sealed record StorageArea(string Key, string Name, string Description, long Bytes, int Files, bool CanClean)
{
    public string Path { get; init; } = "";
    public long ReclaimableBytes { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> SkippedPaths { get; init; } = [];
    public bool ShowsSkipped => Skipped > 0;
    public string Summary => $"总占用 {Bytes / 1048576d:F1} MB · {Files} 个文件 · 可回收 {ReclaimableBytes / 1048576d:F1} MB" + (Skipped > 0 ? $" · 跳过 {Skipped} 项" : "");
}

/// <summary>只读统计跳过链接和不可读项；清理始终逐项复核路径。</summary>
public sealed class StorageCatalog(string dataRoot, string gameRoot)
{
    private static readonly (string Key, string Name, string Description, bool Clean)[] Areas =
    [
        ("cache/meta", "版本元数据缓存", "仅清理 7 天前的完整缓存。", true),
        ("cache/resource-presentation", "资源介绍与图标缓存", "保留收藏、翻译设置与衣柜原图。", true),
        ("skin-cache", "账号外观缓存", "不包含本地衣柜原图。", true),
        ("java/.cache", "Java 安装包缓存", "保留断点文件和已安装 Java。", true),
        ("snapshots", "实例快照", "在游戏管理的存档与备份页面管理。", false),
        ("world-backups", "世界备份", "包含恢复前备份，不参与缓存清理。", false),
        ("java", "Java 运行环境", "启动游戏所需；不重复计算安装包缓存。", false),
        ("game-imports", "整合包导入临时缓存", "仅清理游戏库中 7 天前的完整导入临时文件。", true),
        ("game", "游戏目录", "包含游戏、模组、存档与共享资源；从管理游戏入口删除版本。", false)
    ];
    public string AreaPath(string key) => key switch { "game" => gameRoot, "game-imports" => System.IO.Path.Combine(gameRoot, ".cache", "imports"), _ => ExperienceFiles.Child(dataRoot, key) };
    private static bool Eligible(string file, DateTime utcNow) => File.GetLastWriteTimeUtc(file) < utcNow.AddDays(-7) &&
        !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<StorageArea> Scan(CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var result = new List<StorageArea>();
        foreach (var area in Areas)
        {
            var path = AreaPath(area.Key); progress?.Report(area.Name);
            long bytes = 0, reclaimable = 0; var count = 0; var skipped = 0; var skippedPaths = new List<string>();
            void Skip(string file) { skipped++; if (skippedPaths.Count < 50) skippedPaths.Add(file); }
            foreach (var file in StorageFiles.Enumerate(path, Skip, ct, area.Key == "java" ? ".cache" : area.Key == "game" ? System.IO.Path.Combine(".cache", "imports") : null))
            {
                ct.ThrowIfCancellationRequested();
                try { var size = new FileInfo(file).Length; bytes += size; count++; if (area.Clean && Eligible(file, DateTime.UtcNow)) reclaimable += size; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Skip(file); }
            }
            result.Add(new(area.Key, area.Name, area.Description, bytes, count, area.Clean && reclaimable > 0)
            { Path = path, ReclaimableBytes = reclaimable, Skipped = skipped, SkippedPaths = skippedPaths });
        }
        return result;
    }
    public long Clean(string key, CancellationToken ct = default)
    {
        var area = Areas.SingleOrDefault(a => a.Key == key);
        if (!area.Clean) throw new InvalidOperationException("此目录不属于可清理缓存。");
        long bytes = 0;
        foreach (var file in StorageFiles.Enumerate(AreaPath(key), ct: ct))
        {
            ct.ThrowIfCancellationRequested(); ExperienceFiles.RejectLinks(file);
            if (!Eligible(file, DateTime.UtcNow)) continue;
            var length = new FileInfo(file).Length; File.Delete(file); bytes += length;
        }
        return bytes;
    }
}

public static class StorageFiles
{
    public static IEnumerable<string> Enumerate(string root, Action<string>? skipped = null, CancellationToken ct = default, string? exclude = null)
    {
        var pending = new Stack<string>(); pending.Push(System.IO.Path.GetFullPath(root));
        var excluded = exclude is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, exclude));
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested(); string[] entries;
            try
            {
                if (!Directory.Exists(directory)) continue;
                ExperienceFiles.RejectLinks(directory); entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped?.Invoke(directory); continue; }
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(entry, excluded, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped?.Invoke(entry); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped?.Invoke(entry); continue; }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else yield return entry;
            }
        }
    }
}
