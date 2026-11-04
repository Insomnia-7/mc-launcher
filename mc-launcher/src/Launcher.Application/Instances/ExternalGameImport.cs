using System.Security.Cryptography;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Operations;

namespace Launcher.Application.Instances;

/// <summary>Copy a reviewed external layout into an empty library. Never writes into the source.</summary>
public static class ExternalGameImport
{
    public static async Task CopyAsync(string source, string version, string runtime, string destination, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source); runtime = Path.GetFullPath(runtime); destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!GameLibraryDiscovery.Versions(source).Contains(version)) throw new InvalidDataException("版本清单不可用。");
        if (!runtime.Equals(source, comparison) && !runtime.StartsWith(prefix, comparison)) throw new InvalidDataException("运行目录必须位于来源游戏库内。");
        if (!Directory.Exists(runtime)) throw new DirectoryNotFoundException("原运行目录不存在。");
        if (destination.Equals(source, comparison) || destination.StartsWith(prefix, comparison) || source.StartsWith(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("来源与目标必须是互不包含的目录。");
        ExperienceFiles.RejectLinks(source); ExperienceFiles.RejectLinks(runtime); ExperienceFiles.RejectLinks(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("请选择空文件夹，避免覆盖已有游戏。");
        Directory.CreateDirectory(destination);
        var stage = ExperienceFiles.Child(destination, ".fangyu-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var moved = new List<string>();
        try
        {
            foreach (var name in new[] { "versions", "assets", "libraries" })
            {
                var from = Path.Combine(source, name);
                if (Directory.Exists(from)) await CopyTreeAsync(from, Path.Combine(stage, name), progress, ct);
            }
            var game = Path.Combine(stage, "instances", version, ".minecraft");
            Directory.CreateDirectory(game);
            var excluded = new HashSet<string>(["versions", "assets", "libraries", "instances", ".cache", "PCL", "HMCL", "natives"], StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Directory.GetFileSystemEntries(runtime))
            {
                ct.ThrowIfCancellationRequested(); ExperienceFiles.RejectLinks(entry);
                var name = Path.GetFileName(entry);
                if (excluded.Contains(name)) continue;
                if (Directory.Exists(entry)) await CopyTreeAsync(entry, Path.Combine(game, name), progress, ct);
                else if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && name is not ("launcher_profiles.json" or "launcher_accounts.json" or "accounts.json" or "account.json"))
                    await CopyFileAsync(entry, Path.Combine(game, name), ct);
                else if (name is "options.txt" or "servers.dat") await CopyFileAsync(entry, Path.Combine(game, name), ct);
            }
            new InstanceMetadata(version, null, "本地复制导入", null, version, DateTimeOffset.UtcNow.ToString("o"), version, false).Write(stage, version);
            InstanceRegistry.LoadOrMigrate(stage);
            ct.ThrowIfCancellationRequested();
            foreach (var dir in Directory.GetDirectories(stage))
            {
                var target = Path.Combine(destination, Path.GetFileName(dir));
                ExperienceFiles.RejectLinks(target); Directory.Move(dir, target); moved.Add(target);
            }
            DurableExecution.Current?.MarkCommitted();
        }
        catch
        {
            foreach (var dir in moved) ExperienceFiles.DeleteTree(dir, destination);
            throw;
        }
        finally { ExperienceFiles.DeleteTree(stage, destination); }
    }
    public static async Task CopyTreeAsync(string source, string target, IProgress<string>? progress, CancellationToken ct)
    {
        ExperienceFiles.RejectLinks(source); ExperienceFiles.RejectLinks(target); Directory.CreateDirectory(target);
        foreach (var file in ExperienceFiles.Files(source))
        {
            ct.ThrowIfCancellationRequested(); var relative = Path.GetRelativePath(source, file);
            progress?.Report(relative);
            await CopyFileAsync(file, ExperienceFiles.Child(target, relative), ct);
        }
    }
    internal static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        ExperienceFiles.RejectLinks(source); ExperienceFiles.RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            await input.CopyToAsync(output, ct);
        await using var original = File.OpenRead(source); await using var copy = File.OpenRead(target);
        var originalHash = await SHA256.HashDataAsync(original, ct);
        var copyHash = await SHA256.HashDataAsync(copy, ct);
        if (!originalHash.SequenceEqual(copyHash)) throw new IOException("复制校验失败：" + Path.GetFileName(source));
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }
}
