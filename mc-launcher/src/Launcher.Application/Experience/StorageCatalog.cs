namespace Launcher.Application.Experience;

public sealed record StorageArea(string Key, string Name, string Description, long Bytes, int Files, bool CanClean)
{
    public string Summary => $"{Bytes / 1048576d:F1} MB · {Files} 个文件";
}

/// <summary>只清理由启动器管理的陈旧缓存，不删除已安装的 Java、游戏依赖或备份。</summary>
public sealed class StorageCatalog(string dataRoot, string gameRoot)
{
    private static readonly (string Key, string Name, string Description, bool Clean)[] Areas =
    [
        ("cache/meta", "版本元数据缓存", "仅清理 7 天前的缓存；再次使用时会重新获取。", true),
        ("cache/resource-presentation", "资源介绍与图标缓存", "仅清理 7 天前的缓存；保留收藏、翻译设置和衣柜。", true),
        ("skin-cache", "账号外观缓存", "仅清理 7 天前的缓存；不包含本地衣柜原图。", true),
        ("java/.cache", "Java 安装包缓存", "仅清理 7 天前的完整安装包；保留断点文件和已安装 Java。", true),
        ("snapshots", "实例快照", "在实例的存档与备份页面管理，不参与缓存清理。", false),
        ("world-backups", "世界备份", "包含恢复前自动备份，不参与缓存清理。", false),
        ("java", "Java 运行环境（含安装包缓存）", "启动游戏所需，保留所有已安装版本。", false)
    ];
    private static bool Eligible(string file, DateTime utcNow) => File.GetLastWriteTimeUtc(file) < utcNow.AddDays(-7) &&
        !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<StorageArea> Scan(CancellationToken ct = default)
    {
        var result = new List<StorageArea>();
        foreach (var area in Areas)
        {
            long bytes = 0; var count = 0;
            foreach (var file in ExperienceFiles.Files(ExperienceFiles.Child(dataRoot, area.Key)))
            { ct.ThrowIfCancellationRequested(); if (area.Clean && !Eligible(file, DateTime.UtcNow)) continue; bytes += new FileInfo(file).Length; count++; }
            result.Add(new(area.Key, area.Name, area.Description, bytes, count, area.Clean && count > 0));
        }
        long gameBytes = 0; var gameFiles = 0;
        foreach (var file in ExperienceFiles.Files(gameRoot)) { ct.ThrowIfCancellationRequested(); gameBytes += new FileInfo(file).Length; gameFiles++; }
        result.Add(new("game", "游戏目录", "游戏、模组、存档与共享资源；请在实例页面管理。", gameBytes, gameFiles, false));
        return result;
    }
    public long Clean(string key, CancellationToken ct = default)
    {
        var area = Areas.SingleOrDefault(a => a.Key == key);
        if (!area.Clean) throw new InvalidOperationException("此目录不属于可清理缓存。");
        long bytes = 0;
        foreach (var file in ExperienceFiles.Files(ExperienceFiles.Child(dataRoot, key)))
        {
            ct.ThrowIfCancellationRequested();
            if (!Eligible(file, DateTime.UtcNow)) continue;
            ExperienceFiles.RejectLinks(file);
            var length = new FileInfo(file).Length; File.Delete(file); bytes += length;
        }
        return bytes;
    }
}
