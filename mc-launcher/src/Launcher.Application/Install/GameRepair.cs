using System.Security.Cryptography;
using Launcher.Application.Abstractions;
using Launcher.Application.Experience;
using Launcher.Domain.Install;
using Launcher.Domain.Versions;

namespace Launcher.Application.Install;

public sealed record RepairFile(DownloadEntry Entry, string Reason)
{
    public string Path => Entry.RelativePath;
    public string Summary => $"{Reason} · {(Entry.Size > 0 ? $"{Entry.Size / 1048576d:F2} MB" : "大小未知")}";
}
public sealed record GameRepairReport(IReadOnlyList<RepairFile> Files, int Checked, int Unverified, int Unsupported)
{
    public string Summary => $"已检查 {Checked} 个文件；需修复 {Files.Count} 个，约 {Files.Sum(f => f.Entry.Size) / 1048576d:F1} MB。" +
        (Unverified > 0 ? $" {Unverified} 个现有文件缺少校验信息，未判断损坏。" : "") +
        (Unsupported > 0 ? $" {Unsupported} 个库无法自动检查，请重装对应加载器。" : "");
}

public sealed partial class GameInstaller
{
    public async Task<GameRepairReport> InspectAsync(VersionJson version, string gameRoot, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = InstallPlanner.Build(version, _platform);
        var entries = new List<DownloadEntry>(plan.Entries);
        if (version.LauncherParentId is {} parent && version.Downloads?.Client is {} client)
        {
            // 合并后的加载器版本使用父版本 jar，不能修复成子版本的重复 jar。
            if (parent != Path.GetFileName(parent) || parent is "." or "..") throw new InvalidDataException("父版本编号无效。");
            entries.Add(new(client.Url, $"versions/{parent}/{parent}.jar", client.Size, client.Sha1, DownloadKind.Client));
        }
        if (version.AssetIndex is {} reference)
        {
            var index = await meta.GetAssetIndexAsync(reference, ct).ConfigureAwait(false);
            foreach (var obj in index.Objects.Values)
            {
                if (obj.Hash.Length != 40 || !obj.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("资源索引校验值无效。");
                entries.Add(new($"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}", $"assets/{obj.RelativePath}", obj.Size, obj.Hash, DownloadKind.Asset));
            }
        }
        var unique = entries.GroupBy(e => e.RelativePath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var broken = new List<RepairFile>(); var unverified = 0; var count = 0;
        foreach (var entry in unique)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.RelativePath.Replace('\\', '/');
            if (!(relative.StartsWith("versions/", StringComparison.Ordinal) || relative.StartsWith("libraries/", StringComparison.Ordinal) || relative.StartsWith("assets/", StringComparison.Ordinal))) throw new InvalidDataException("修复路径不属于游戏依赖文件。");
            var separator = relative.IndexOf('/');
            var scope = ExperienceFiles.Child(gameRoot, relative[..separator]);
            var path = ExperienceFiles.Child(scope, relative[(separator + 1)..]); ExperienceFiles.RejectLinks(path);
            string? reason = null;
            if (!File.Exists(path)) reason = "文件缺失";
            else if (entry.Size > 0 && new FileInfo(path).Length != entry.Size) reason = "大小不符";
            else if (!string.IsNullOrWhiteSpace(entry.Sha1))
            {
                await using var input = File.OpenRead(path);
                if (!Convert.ToHexStringLower(await SHA1.HashDataAsync(input, ct)).Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase)) reason = "校验不符";
            }
            else unverified++;
            if (reason is not null) broken.Add(new(entry with { Url = _rewriter.Rewrite(entry.Url) }, reason));
            count++;
            progress?.Report(new(count, unique.Length, count, unique.Length, entry.RelativePath));
        }
        return new(broken, count, unverified, plan.SkippedLibraryCount);
    }
    public async Task<GameRepairReport> RepairAsync(VersionJson version, string gameRoot, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var report = await InspectAsync(version, gameRoot, ct: ct).ConfigureAwait(false);
        if (report.Files.Count > 0) await downloader.DownloadAsync(report.Files.Select(f => f.Entry).ToArray(), gameRoot, progress, ct).ConfigureAwait(false);
        var after = await InspectAsync(version, gameRoot, ct: ct).ConfigureAwait(false);
        if (after.Files.Count != 0) throw new IOException("部分游戏文件仍未通过校验，请查看网络设置后重试。");
        return report;
    }
}
