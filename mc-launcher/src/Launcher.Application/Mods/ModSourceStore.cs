using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Domain.Content;
using Launcher.Domain.Install;

namespace Launcher.Application.Mods;

public sealed record ModOrigin(string Source, string? ProjectId, string? VersionId, string? VersionNumber, string Sha1, long Size);
public sealed record PlannedModFile(DownloadEntry Entry, ModOrigin Origin, string? ModIdHint = null, bool RequiresExactVersion = false);
public sealed record PreparedModInstall(string Title, string Version, string RootFile, long RootSize, string TargetDirectory, IReadOnlyList<PlannedModFile> Files);

/// <summary>按实际文件哈希绑定来源；随实例备份一起保存，不把 jar 内 modId 当成平台 ID。</summary>
public static class ModSourceStore
{
    public const string FileName = ".launcher-sources.json";
    public static Dictionary<string, ModOrigin> Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            return new Dictionary<string, ModOrigin>((JsonSerializer.Deserialize<Dictionary<string, ModOrigin>>(File.ReadAllText(path)) ?? []).Where(p => p.Value is { Sha1: not null, Source: not null }).ToDictionary(p => p.Key, p => p.Value), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    public static async Task<ModOrigin?> CaptureAsync(string directory, PlannedModFile file, bool required, CancellationToken ct)
    {
        var path = Install.LongPath.Combine(directory, file.Entry.RelativePath);
        if (!File.Exists(path))
        {
            if (required) throw new FileNotFoundException("下载完成后缺少模组文件", path);
            return null;
        }
        var size = new FileInfo(path).Length;
        var hash = await HashAsync(path, ct).ConfigureAwait(false);
        if ((file.Entry.Size > 0 && size != file.Entry.Size) ||
            (!string.IsNullOrWhiteSpace(file.Entry.Sha1) && !hash.Equals(file.Entry.Sha1, StringComparison.OrdinalIgnoreCase)))
        {
            if (required) throw new InvalidDataException($"模组文件校验失败：{file.Entry.RelativePath}");
            return null;
        }
        return file.Origin with { Sha1 = hash, Size = size };
    }

    public static async Task SaveAsync(string directory, IReadOnlyDictionary<string, ModOrigin> origins, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, FileName);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(origins, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task RecordAsync(string directory, IEnumerable<PlannedModFile> files, CancellationToken ct = default)
    {
        var origins = Load(directory);
        var changed = false;
        foreach (var file in files)
        {
            var origin = await CaptureAsync(directory, file, required: false, ct).ConfigureAwait(false);
            if (origin is null) continue;
            origins[file.Entry.RelativePath] = origin;
            changed = true;
        }
        if (changed) await SaveAsync(directory, origins, ct).ConfigureAwait(false);
    }
}

internal static class ModInstallExecutor
{
    public static async Task<string?> FindExistingAsync(PreparedModInstall plan, PlannedModFile file, DownloadCategory category, CancellationToken ct)
    {
        var existing = ModFileGuard.FindExisting(plan.TargetDirectory, file.Entry.RelativePath);
        if (existing is not null) return existing;
        if (category.NeedsModIdGuard())
        {
            existing = ModFileGuard.FindExistingByModId(plan.TargetDirectory, file.Origin.ProjectId, file.ModIdHint);
            if (existing is not null) return existing;
        }
        if (file.Origin.ProjectId is null) return null;
        foreach (var (name, origin) in ModSourceStore.Load(plan.TargetDirectory))
        {
            if (origin.Source != file.Origin.Source || origin.ProjectId != file.Origin.ProjectId) continue;
            var path = Install.LongPath.Combine(plan.TargetDirectory, name);
            if (File.Exists(path) && string.Equals(await ModSourceStore.HashAsync(path, ct).ConfigureAwait(false), origin.Sha1, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }

    public static async Task<int> InstallAsync(PreparedModInstall plan, DownloadCategory category, IDownloader downloader,
        IProgress<ModInstallProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(plan.TargetDirectory);
        var pendingFiles = new List<PlannedModFile>();
        foreach (var file in plan.Files)
            if (await FindExistingAsync(plan, file, category, ct).ConfigureAwait(false) is null) pendingFiles.Add(file);
        var pending = pendingFiles.ToArray();
        if (pending.Length > 0)
            await downloader.DownloadAsync(pending.Select(f => f.Entry).ToArray(), plan.TargetDirectory,
                new ProgressAdapter(progress), ct).ConfigureAwait(false);
        await ModSourceStore.RecordAsync(plan.TargetDirectory, pending, ct).ConfigureAwait(false);
        progress?.Report(ModInstallProgress.Of("完成", 100, plan.RootFile));
        return plan.Files.Count - pending.Length;
    }
    private sealed class ProgressAdapter(IProgress<ModInstallProgress>? progress) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => progress?.Report(ModInstallProgress.Of("下载", value.Percent, value.CurrentFile));
    }
}
