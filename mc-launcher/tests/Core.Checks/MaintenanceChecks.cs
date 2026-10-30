using System.IO.Compression;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Domain.Versions;

internal static class MaintenanceChecks
{
    public static async Task RunAsync(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fangyu-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var game = Path.Combine(root, "game"); var data = Path.Combine(root, "data");
        void Check(string name, bool value) => check(name, value, null);
        async Task<bool> Reject(Func<Task> operation) { try { await operation(); return false; } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException) { return true; } }
        try
        {
            var path = MaintenanceFixture.SeedWorld(game, "test", "world", "测试世界");
            var other = MaintenanceFixture.SeedWorld(game, "test", "other", "其他世界");
            var worlds = new WorldCatalog(data, game, "test"); var list = worlds.List();
            Check("世界读取 gzip NBT 名称版本模式与时间", list.Single(w => w.Folder == "world") is { Name: "测试世界", Version: "1.21.4", Mode: "创造", LastPlayed: not null, Bytes: > 0 });
            var backup = await worlds.BackupAsync("world", "测试备份");
            Check("世界备份不包含 session.lock", backup.Files.Count == 2 && backup.Files.All(f => f.Path != "session.lock"));
            File.WriteAllText(Path.Combine(path, "region.mca"), "changed"); File.WriteAllText(Path.Combine(path, "extra"), "new");
            await worlds.RestoreAsync(backup.Id);
            Check("单世界恢复移除备份后新增文件", File.ReadAllText(Path.Combine(path, "region.mca")) == "original world" && !File.Exists(Path.Combine(path, "extra")));
            Check("恢复不触碰其他世界", File.ReadAllText(Path.Combine(other, "region.mca")) == "original world" && File.Exists(Path.Combine(other, "session.lock")));
            Check("恢复前自动备份保留当前状态", worlds.Backups("world").Count == 2);
            using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); Check("取消备份不留下未完成归档", await Reject(() => worlds.BackupAsync("world", "取消", canceled.Token)) && !Directory.EnumerateFiles(Path.Combine(data, "world-backups"), "*.tmp", SearchOption.AllDirectories).Any()); }
            Check("世界路径不能越出 saves", await Reject(() => worlds.BackupAsync("../outside", "越界")));
            var archive = Directory.EnumerateFiles(Path.Combine(data, "world-backups"), backup.Id + ".zip", SearchOption.AllDirectories).Single();
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) { zip.GetEntry("region.mca")!.Delete(); var entry = zip.CreateEntry("region.mca"); using var writer = new StreamWriter(entry.Open()); writer.Write("tampered world"); }
            File.WriteAllText(Path.Combine(path, "region.mca"), "keep");
            Check("损坏归档在替换前被拒绝", await Reject(() => worlds.RestoreAsync(backup.Id)) && File.ReadAllText(Path.Combine(path, "region.mca")) == "keep");
            worlds.DeleteBackup(backup.Id); Check("删除备份不删除当前世界", worlds.Backups("world").Count == 1 && Directory.Exists(path));
            Directory.Delete(path, true); Check("已移除世界仍能从备份找到", worlds.List().Any(w => w.Folder == "world" && w.Notice.Contains("恢复")));
            await worlds.RestoreAsync(worlds.Backups("world").Single().Id); Check("可重新恢复已移除的世界", File.ReadAllText(Path.Combine(path, "region.mca")) == "changed");
            File.WriteAllBytes(Path.Combine(other, "level.dat"), [0, 1]); Check("损坏 NBT 仍显示可备份条目", worlds.List().Single(w => w.Folder == "other").Notice.Length > 0);

            var fixture = new MaintenanceFixture(); fixture.SeedVersion(game); var installer = new GameInstaller(fixture, fixture);
            var report = await installer.InspectAsync(fixture.Version, game);
            Check("定向检查只列出缺失资源并统计全部检查数", report.Checked == 3 && report.Files.Count == 1 && report.Files[0].Entry.Kind == Launcher.Domain.Install.DownloadKind.Asset);
            await installer.RepairAsync(fixture.Version, game);
            Check("修复只下载异常文件", fixture.Downloaded.Count == 1 && (await installer.InspectAsync(fixture.Version, game)).Files.Count == 0);
            var jar = Path.Combine(game, "versions/maintenance/maintenance.jar"); File.WriteAllBytes(jar, Enumerable.Repeat((byte)'x', fixture.Client.Length).ToArray());
            Check("同大小损坏文件通过哈希发现", (await installer.InspectAsync(fixture.Version, game)).Files.Single().Reason == "校验不符");
            await installer.RepairAsync(fixture.Version, game); Check("修复后重新校验并保留世界", fixture.Downloaded.Count == 2 && Directory.Exists(other));
            var merged = new VersionJson { Id = "fabric", LauncherParentId = "maintenance", Downloads = fixture.Version.Downloads };
            Check("加载器修复使用父版本 jar", (await installer.InspectAsync(merged, game)).Files.Count == 0 && !Directory.Exists(Path.Combine(game, "versions/fabric")));
            var unsafeVersion = new VersionJson { Id = "../../instances/test/evil", Downloads = fixture.Version.Downloads };
            Check("修复拒绝越过 versions 子目录", await Reject(() => installer.InspectAsync(unsafeVersion, game)));

            string Seed(string relative, bool old = true) { var file = ExperienceFiles.Child(data, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "cache"); if (old) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8)); return file; }
            var stale = Seed("cache/meta/old.json"); var fresh = Seed("cache/meta/fresh.json", false); var partial = Seed("cache/meta/download.part"); var temporary = Seed("cache/meta/index.tmp"); var runtime = Seed("java/runtime/bin/java.exe"); var package = Seed("java/.cache/runtime.zip");
            var storage = new StorageCatalog(data, game); var areas = storage.Scan();
            Check("缓存扫描仅统计可回收文件", areas.Single(a => a.Key == "cache/meta").Files == 1);
            Check("存储展示备份和游戏占用", areas.Single(a => a.Key == "world-backups").Bytes > 0 && areas.Single(a => a.Key == "game").Bytes > 0);
            Check("缓存清理不删除新缓存与断点文件", storage.Clean("cache/meta") == 5 && !File.Exists(stale) && File.Exists(fresh) && File.Exists(partial) && File.Exists(temporary));
            storage.Clean("java/.cache"); Check("清理安装包保留已安装 Java", !File.Exists(package) && File.Exists(runtime));
            Check("拒绝清理备份目录", await Reject(() => { storage.Clean("world-backups"); return Task.CompletedTask; }) && worlds.Backups("world").Count > 0);
        }
        finally { ExperienceFiles.DeleteTree(root, Path.GetTempPath()); }
    }
}
