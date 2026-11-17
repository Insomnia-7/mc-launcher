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
        ("game-tasks", "游戏下载暂存", "已完成或已取消任务可清理；等待、暂停、失败任务的断点保留。", true),
        ("legacy-tasks", "旧版下载暂存", "清理旧数据目录中的已结束任务文件，保留任务记录。", true),
        ("shared-assets", "共享游戏资源", "所有实例共用，不随单个实例或任务清理。", false),
        ("shared-libraries", "共享运行库", "多个版本共用，保留完整依赖。", false),
        ("shared-versions", "版本与加载器文件", "原版依赖与加载器文件共用；列表仅显示可玩实例。", false),
        ("game", "实例与其他游戏文件", "实例独立保存模组、配置和存档，不重复统计公共资源和任务暂存。", false)
    ];
    public string AreaPath(string key) => key switch { "game" => gameRoot, "game-imports" => System.IO.Path.Combine(gameRoot, ".cache", "imports"), "game-tasks"=>System.IO.Path.Combine(gameRoot,".cache","tasks"),"legacy-tasks"=>System.IO.Path.Combine(dataRoot,"task-center"),"shared-assets"=>System.IO.Path.Combine(gameRoot,"assets"),"shared-libraries"=>System.IO.Path.Combine(gameRoot,"libraries"),"shared-versions"=>System.IO.Path.Combine(gameRoot,"versions"), _ => ExperienceFiles.Child(dataRoot, key) };
    private bool Eligible(string key,string file, DateTime utcNow)
    {
        if(key is "game-tasks" or "legacy-tasks")
        {
            var parts=System.IO.Path.GetRelativePath(AreaPath(key),file).Split(System.IO.Path.DirectorySeparatorChar);
            if(parts.Length<2||!Guid.TryParseExact(parts[0],"N",out _))return false;
            var task=ExperienceFiles.Read<Launcher.Application.Operations.DurableTaskRecord>(System.IO.Path.Combine(dataRoot,"task-center",parts[0]+".json"));
            if(task is not null)return !task.NeedsRecovery&&(task.State==Launcher.Application.Operations.DurableTaskState.Completed||task.State==Launcher.Application.Operations.DurableTaskState.Canceled&&task.Partials.Count==0);
            return File.GetLastWriteTimeUtc(file)<utcNow.AddDays(-7);
        }
        return File.GetLastWriteTimeUtc(file) < utcNow.AddDays(-7) && !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }
    public IReadOnlyList<StorageArea> Scan(CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var result = new List<StorageArea>();
        foreach (var area in Areas)
        {
            var path = AreaPath(area.Key); progress?.Report(area.Name);
            long bytes = 0, reclaimable = 0; var count = 0; var skipped = 0; var skippedPaths = new List<string>();
            void Skip(string file) { skipped++; if (skippedPaths.Count < 50) skippedPaths.Add(file); }
            foreach (var file in StorageFiles.Enumerate(path, Skip, ct, area.Key == "java" ? ".cache" : null,area.Key=="game"?[System.IO.Path.Combine(".cache","imports"),System.IO.Path.Combine(".cache","tasks"),"assets","libraries","versions"]:null))
            {
                ct.ThrowIfCancellationRequested();
                try { var size = new FileInfo(file).Length; bytes += size; count++; if (area.Clean && Eligible(area.Key,file, DateTime.UtcNow)) reclaimable += size; }
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
            if (!Eligible(key,file, DateTime.UtcNow)) continue;
            var length = new FileInfo(file).Length; File.Delete(file); bytes += length;
        }
        return bytes;
    }
}

public static class StorageFiles
{
    public static IEnumerable<string> Enumerate(string root, Action<string>? skipped = null, CancellationToken ct = default, string? exclude = null,IReadOnlyList<string>? excludes=null)
    {
        var pending = new Stack<string>(); pending.Push(System.IO.Path.GetFullPath(root));
        var excluded = exclude is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, exclude));
        var excludedPaths=(excludes??[]).Select(p=>System.IO.Path.GetFullPath(System.IO.Path.Combine(root,p))).ToHashSet(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
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
                if (excludedPaths.Contains(entry)||string.Equals(entry, excluded, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped?.Invoke(entry); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped?.Invoke(entry); continue; }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else yield return entry;
            }
        }
    }
}
