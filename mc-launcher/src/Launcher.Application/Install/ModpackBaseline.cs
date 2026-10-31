using System.Security.Cryptography;
using Launcher.Application.Experience;

namespace Launcher.Application.Install;

public sealed record ModpackOrigin(string Source, string ProjectId, string ReleaseId);
public sealed record PackManagedFile(string Sha256, long Bytes, string? Json = null);
public sealed record ModpackBaseline(string Name, string Version, string Source, ModpackOrigin? Origin,
    Dictionary<string, PackManagedFile> Files)
{
    public const string FileName = ".fangyu-pack.json";
    public static ModpackBaseline? Read(string instanceDirectory) => ExperienceFiles.Read<ModpackBaseline>(Path.Combine(instanceDirectory, FileName));

    // Worlds and private launcher state never belong to a pack update, even when the archive includes them.
    public static bool CanManage(string relative)
    {
        var path = relative.Replace('\\', '/');
        return !path.Split('/').Any(p => p.StartsWith('.') || p is ".." or "")
            && !new[] { "saves", "screenshots", "logs", "crash-reports", "backups", "CustomSkinLoader" }
                .Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
            && !new[] { "usercache.json", "usernamecache.json", "launcher_accounts.json", "launcher_profiles.json", "servers.dat", "servers.dat_old" }
                .Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<ModpackBaseline> CaptureAsync(string gameDirectory, string name, string version, string source,
        ModpackOrigin? origin = null, CancellationToken ct = default)
    {
        var files = new Dictionary<string, PackManagedFile>(StringComparer.Ordinal);
        foreach (var file in ExperienceFiles.Files(gameDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');
            if (!CanManage(relative)) continue;
            var size = new FileInfo(file).Length;
            var json = relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && size <= 524288
                ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : null;
            files.Add(relative, new(await HashAsync(file, ct).ConfigureAwait(false) ?? throw new IOException("整合包文件已消失：" + relative), size, json));
        }
        return new(name, version, source, origin, files);
    }

    public static async Task<string?> HashAsync(string path, CancellationToken ct = default)
    {
        ExperienceFiles.RejectLinks(path);
        if (!File.Exists(path)) return null;
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }
}
