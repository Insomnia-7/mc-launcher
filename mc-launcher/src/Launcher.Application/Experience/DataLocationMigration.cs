using Launcher.Application.Instances;

namespace Launcher.Application.Experience;

public static class DataLocationMigration
{
    /// <summary>Verified copy; caller switches the bootstrap pointer only after success. Originals remain intact.</summary>
    public static async Task CopyAsync(string source, string destination, string gameRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination); gameRoot = Path.GetFullPath(gameRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Prefix(string p) => p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (source.Equals(destination, comparison) || source.StartsWith(Prefix(destination), comparison) || destination.StartsWith(Prefix(source), comparison))
            throw new IOException("请选择与原数据目录互不包含的空文件夹。");
        if (destination.Equals(gameRoot,comparison)) throw new IOException("数据目录与游戏库请使用不同文件夹。");
        ExperienceFiles.RejectLinks(source); ExperienceFiles.RejectLinks(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("目标文件夹必须为空。");
        Directory.CreateDirectory(destination);
        var stage = ExperienceFiles.Child(destination, ".fangyu-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); var moved = new List<string>();
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                ct.ThrowIfCancellationRequested(); var name = Path.GetFileName(entry);
                // Logs remain open in this process. Game libraries retain their separately selected location.
                if (name is "logs" or "game-root.txt" || gameRoot.Equals(entry, comparison)) continue;
                if (gameRoot.StartsWith(Prefix(entry), comparison)) throw new IOException("游戏库嵌在数据分类目录中，请先单独迁移游戏库。");
                ExperienceFiles.RejectLinks(entry);
                if (Directory.Exists(entry)) await ExternalGameImport.CopyTreeAsync(entry, Path.Combine(stage, name), progress, ct);
                else
                {
                    // Use the same copy-and-hash implementation for individual configuration files.
                    await ExternalGameImport.CopyFileAsync(entry, Path.Combine(stage, name), ct);
                }
            }
            File.WriteAllText(Path.Combine(stage, "game-root.txt"), gameRoot);
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.GetFileSystemEntries(stage))
            {
                var target = Path.Combine(destination, Path.GetFileName(entry)); ExperienceFiles.RejectLinks(target);
                if (Directory.Exists(entry)) Directory.Move(entry, target); else File.Move(entry, target);
                moved.Add(target);
            }
        }
        catch
        {
            foreach (var entry in moved) { if (Directory.Exists(entry)) ExperienceFiles.DeleteTree(entry, destination); else { ExperienceFiles.RejectLinks(entry); File.Delete(entry); } }
            throw;
        }
        finally { ExperienceFiles.DeleteTree(stage, destination); }
    }
}
