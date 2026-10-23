using System.IO.Compression;
using System.Text.Json;
using Launcher.Application.Install;
using Launcher.Application.Instances;
using Launcher.Domain.Versions;

namespace Launcher.Application.Export;

/// <summary>导出打包时的可选项。默认值就是「能直接再导回来」的最小集合。</summary>
public sealed record InstanceExportOptions(
    bool IncludeMods = true,
    bool IncludeConfigs = true,
    bool IncludeSaves = true,
    bool IncludeLogs = false)
{
    public static readonly InstanceExportOptions Everything = new();
}

/// <summary>导出结果。相对路径都相对于版本目录（<c>instances\&lt;id&gt;</c>）。</summary>
public sealed record InstanceExportReport(
    string InstanceId,
    string ArchivePath,
    long ArchiveBytes,
    int FileCount,
    long SourceBytes,
    TimeSpan Elapsed)
{
    public double Megabytes => ArchiveBytes / 1024d / 1024d;

    public string ToSummary() =>
        $"已导出「{InstanceId}」→ {Path.GetFileName(ArchivePath)}：" +
        $"{Megabytes:N1} MB（原始 {SourceBytes / 1024d / 1024d:N1} MB，{FileCount} 个文件，{Elapsed.TotalSeconds:N1} 秒）";
}

/// <summary>
/// 把一个版本打包成 zip：<c>instance.json</c> / <c>settings.json</c> / <c>.minecraft\**</c>，
/// 外加一份 <c>launcher-instance.json</c> 记录版本 id、游戏版本与加载器。
/// <para>
/// 刻意不打包 <c>versions\&lt;id&gt;</c> 里的 jar 与 <c>libraries</c> / <c>assets</c> ——
/// 那些是按 sha1 共享、可以重新下载的公共资源，塞进备份会让包从几十 MB 涨到几百 MB。
/// </para>
/// </summary>
public sealed class InstanceExportService
{
    public const string ManifestEntryName = "launcher-instance.json";
    private const string GameDirectoryName = ".minecraft";
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public async Task<InstanceExportReport> ExportAsync(
        string gameRoot,
        string instanceId,
        string archivePath,
        InstanceExportOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        options ??= InstanceExportOptions.Everything;
        if (!IsSafeId(instanceId))
            throw new InvalidOperationException($"版本 id 非法：{instanceId}");

        var instanceDirectory = Path.Combine(gameRoot, "instances", instanceId);
        if (!Directory.Exists(instanceDirectory))
            throw new DirectoryNotFoundException($"找不到版本目录：{instanceDirectory}");

        var archiveFull = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(archiveFull)!);
        var temporary = archiveFull + "." + Guid.NewGuid().ToString("N") + ".part";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var files = 0;
        long sourceBytes = 0;
        progress?.Report("打包中…");

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // 版本元数据（两个 json 都在版本目录根部）
                foreach (var name in new[] { InstanceMetadata.FileName, InstanceSettings.FileName })
                {
                    var path = Path.Combine(instanceDirectory, name);
                    if (File.Exists(path))
                    {
                        await AddFileAsync(zip, path, name, ct).ConfigureAwait(false);
                        files++;
                        sourceBytes += new FileInfo(path).Length;
                    }
                }

                // 自己写一份清单，方便将来「导入备份」时不用猜
                var manifest = await BuildManifestAsync(gameRoot, instanceId, ct).ConfigureAwait(false);
                var entry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using (var entryStream = entry.Open())
                {
                    await JsonSerializer.SerializeAsync(entryStream, manifest, ManifestJson, ct).ConfigureAwait(false);
                }

                var gameDirectory = Path.Combine(instanceDirectory, GameDirectoryName);
                if (Directory.Exists(gameDirectory))
                {
                    foreach (var file in EnumerateExportable(gameDirectory, options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.Equals(file.FullName, archiveFull, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(file.FullName, temporary, StringComparison.OrdinalIgnoreCase)) continue;
                        var relative = Path.GetRelativePath(instanceDirectory, file.FullName)
                            .Replace('\\', '/');
                        await AddFileAsync(zip, file.FullName, relative, ct).ConfigureAwait(false);
                        files++;
                        sourceBytes += file.Length;
                        if (files % 200 == 0)
                            progress?.Report($"打包中… {files} 个文件 / {sourceBytes / 1024d / 1024d:N1} MB");
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, archiveFull, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        stopwatch.Stop();
        return new InstanceExportReport(
            instanceId, archiveFull, new FileInfo(archiveFull).Length, files, sourceBytes, stopwatch.Elapsed);
    }

    /// <summary>按扩展名 / 目录名挑出要打包的文件（日志、崩溃报告、缩略图缓存默认排除）。</summary>
    private static IEnumerable<FileInfo> EnumerateExportable(string gameDirectory, InstanceExportOptions options)
    {
        var root = new DirectoryInfo(gameDirectory);
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(gameDirectory, file.FullName);
            if (IsExcluded(relative, options)) continue;
            yield return file;
        }
    }

    private static bool IsExcluded(string relativePath, InstanceExportOptions options)
    {
        var normalized = relativePath.Replace('\\', '/');
        var top = normalized.Split('/')[0];

        if (!options.IncludeLogs &&
            (top.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
             top.Equals("crash-reports", StringComparison.OrdinalIgnoreCase) ||
             normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!options.IncludeMods && top.Equals("mods", StringComparison.OrdinalIgnoreCase)) return true;
        if (!options.IncludeSaves && top.Equals("saves", StringComparison.OrdinalIgnoreCase)) return true;

        if (!options.IncludeConfigs &&
            (top.Equals("config", StringComparison.OrdinalIgnoreCase) ||
             top.Equals("defaultconfigs", StringComparison.OrdinalIgnoreCase) ||
             top.Equals("kubejs", StringComparison.OrdinalIgnoreCase) ||
             top.Equals("scripts", StringComparison.OrdinalIgnoreCase)))
            return true;

        // 这些东西重新生成或与机器绑定，打包进去只添乱
        if (top.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        if (top.Equals(".fabric", StringComparison.OrdinalIgnoreCase)) return true;   // processedMods / remappedJars 都是缓存
        if (top.Equals("downloads", StringComparison.OrdinalIgnoreCase)) return true; // 启动器自己的下载缓存
        if (top.Equals("resourcepacks", StringComparison.OrdinalIgnoreCase) && !options.IncludeMods) return true;
        if (normalized.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalized.EndsWith("latest.log", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<object> BuildManifestAsync(string gameRoot, string instanceId, CancellationToken ct)
    {
        var metadata = InstanceMetadata.TryRead(gameRoot, instanceId);
        var settings = InstanceSettings.Load(gameRoot, instanceId);

        // 实例 id 与版本目录可能不同（同一加载器装第二个包会得到 …-2 后缀），
        // 所以先从注册表拿真正的 versionId，再读版本 json。
        var registration = InstanceRegistry.LoadOrMigrate(gameRoot).Instances
            .FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
        var versionId = registration?.VersionId ?? instanceId;
        var version = await ReadVersionAsync(gameRoot, versionId, ct).ConfigureAwait(false);

        // 版本 json 里可能没有 inheritsFrom（我们生成的加载器 profile 就是这种），
        // 那就退回按版本 id 反解出游戏版本与加载器。
        var parsed = VersionIdParser.Parse(versionId, version?.InheritsFrom);
        var minecraft = registration?.MinecraftVersion
                        ?? version?.InheritsFrom
                        ?? (parsed.IsLoader ? parsed.GameVersion : version?.Id);
        var loader = registration?.Loader ?? (parsed.IsLoader ? parsed.Title(versionId) : null);

        return new
        {
            format = 1,
            exportedAt = DateTimeOffset.Now.ToString("o"),
            versionId,
            name = registration?.Name ?? metadata?.Name ?? instanceId,
            packVersion = registration?.PackVersion ?? metadata?.PackVersion,
            source = registration?.Source ?? metadata?.Source,
            loader,
            minecraft,
            inheritsFrom = version?.InheritsFrom,
            mainClass = version?.MainClass,
            memoryMb = settings.MemoryMb,
            jvmPreset = settings.JvmPreset.ToString(),
        };
    }

    private static async Task<VersionJson?> ReadVersionAsync(string gameRoot, string instanceId, CancellationToken ct)
    {
        var path = Path.Combine(gameRoot, "versions", instanceId, instanceId + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<VersionJson>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static async Task AddFileAsync(ZipArchive zip, string sourcePath, string entryName, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var target = entry.Open();
        await using var source = new FileStream(
            LongPath.Normalize(sourcePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        await source.CopyToAsync(target, 64 * 1024, ct).ConfigureAwait(false);
    }

    private static bool IsSafeId(string instanceId) =>
        instanceId.Length > 0 &&
        !instanceId.Contains('\\') && !instanceId.Contains('/') && !instanceId.Contains(':') &&
        instanceId is not "." and not "..";
}
