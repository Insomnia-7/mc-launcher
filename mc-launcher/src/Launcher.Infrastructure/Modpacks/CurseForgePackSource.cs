using System.IO.Compression;
using System.Text.Json;
using Launcher.Application.Abstractions;
using Launcher.Application.Mods;
using Launcher.Domain.Modpacks;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// CurseForge 整合包（根目录 manifest.json + overrides/）。
/// manifest 只给 projectID / fileID，下载地址和落位目录都要现查，
/// 所以 <see cref="Pack"/> 的 Files 在 Open 时是空的，真正的清单由 <see cref="ResolveFilesAsync"/> 产出。
/// 只读打开，overrides 直接流式解到目标位置。
/// </summary>
public sealed class CurseForgePackSource : IModpackSource
{
    /// <summary>CurseForge 的 classId：6=mod、12=资源包、6552=光影、17=世界。</summary>
    private const int CategoryMods = 6;
    private const int CategoryResourcePacks = 12;
    private const int CategoryShaders = 6552;
    private const int CategoryWorlds = 17;

    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly CurseForgeManifest _manifest;
    private readonly ICurseForgeClient _client;

    private CurseForgePackSource(
        string sourcePath,
        FileStream stream,
        ZipArchive archive,
        CurseForgeManifest manifest,
        ICurseForgeClient client)
    {
        SourcePath = sourcePath;
        _stream = stream;
        _archive = archive;
        _manifest = manifest;
        _client = client;
        Pack = manifest.ToPack();
    }

    public string SourcePath { get; }

    public ModrinthPack Pack { get; }

    public static CurseForgePackSource Open(string path, ICurseForgeClient client)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("整合包文件不存在", path);

        var stream = File.OpenRead(path);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            var entry = archive.Entries
                            .FirstOrDefault(e => e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries
                            .FirstOrDefault(e => e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("不是 CurseForge 整合包：找不到 manifest.json");

            using var buffer = new MemoryStream();
            using (var entryStream = entry.Open())
                entryStream.CopyTo(buffer);

            var manifest = CurseForgeManifest.Parse(buffer.ToArray());
            return new CurseForgePackSource(path, stream, archive, manifest, client);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 把项目 / 文件 ID 换成真实下载地址。每个文件两个请求（项目分类 + 下载地址），6 并发。
    /// 单个文件解析失败只跳过它，不让整包导入失败。
    /// </summary>
    public async Task<IReadOnlyList<ModpackFile>> ResolveFilesAsync(
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 先看有没有要解析的文件：files 为空的包（只靠 overrides 分发）根本不需要 API Key，
        // Key 检查必须放在这个早退之后，否则没配 Key 的机器连纯 overrides 包都导不进来。
        var references = _manifest.Files.Where(f => f.Required).ToList();
        if (references.Count == 0) return [];

        if (!_client.IsConfigured)
        {
            throw new CurseForgeNotConfiguredException(
                "CurseForge 整合包要先在设置里填 API Key 才能解析下载地址（包内只有项目 ID，没有直链）");
        }

        const string stage = "解析整合包清单";
        progress?.Report(ModpackImportProgress.Of(stage, 0, $"共 {references.Count} 个文件"));

        var resolved = new ModpackFile?[references.Count];
        var completed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, references.Count), options, async (index, token) =>
        {
            var reference = references[index];
            try
            {
                // 两路并发：项目拿分类（决定落哪个目录），文件列表拿文件名 / 大小 / 哈希。
                // 文件列表里通常已经带下载直链，只有作者关了第三方分发时才补一次 download-url。
                var modTask = _client.GetModAsync(reference.ProjectId, token);
                var filesTask = _client.GetFilesAsync(reference.ProjectId, ct: token);
                await Task.WhenAll(modTask, filesTask).ConfigureAwait(false);

                var mod = await modTask.ConfigureAwait(false);
                var match = (await filesTask.ConfigureAwait(false))
                    .FirstOrDefault(f => f.Id == reference.FileId);

                var url = match?.DownloadUrl
                          ?? await _client
                              .GetDownloadUrlAsync(reference.ProjectId, reference.FileId, token)
                              .ConfigureAwait(false);

                if (url is { Length: > 0 })
                {
                    var fileName = string.IsNullOrWhiteSpace(match?.FileName)
                        ? FileNameFromUrl(url)
                        : match!.FileName;

                    resolved[index] = new ModpackFile(
                        BuildRelativePath(mod?.ClassId ?? CategoryMods, fileName),
                        match?.FileLength ?? 0,
                        match?.Sha1,
                        null,
                        [url],
                        true) { SourceProjectId = reference.ProjectId.ToString(), SourceFileId = reference.FileId.ToString() };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 文件被作者删了、作者关闭第三方分发、分类查询 404——都只影响这一个文件
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                progress?.Report(ModpackImportProgress.Of(
                    stage, done * 100.0 / references.Count, $"{done} / {references.Count}"));
            }
        }).ConfigureAwait(false);

        var files = resolved.Where(f => f is not null).Select(f => f!).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"整合包的 {references.Count} 个文件一个都没解析出来：检查 API Key 是否有效，或这些文件是否已被作者删除");
        }

        return files;
    }

    /// <summary>
    /// 先解 manifest 指定的 overrides 目录（默认 <c>overrides/</c>），再解 <c>client-overrides/</c>。
    /// CurseForge 规范里 client-overrides 覆盖 overrides 的同名文件，所以层顺序不能反。
    /// </summary>
    public Task<int> ExtractOverridesAsync(
        string targetDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) =>
        OverridesExtractor.ExtractAsync(
            _archive,
            [_manifest.OverridesDirectory, OverridesExtractor.ClientOverridesDirectory],
            targetDirectory,
            progress,
            ct);

    public async ValueTask DisposeAsync()
    {
        _archive.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>按 classId 决定落哪个子目录；文件名由调用方给（优先 API 的 fileName，取不到再从直链尾巴取）。</summary>
    private static string BuildRelativePath(int classId, string fileName)
    {
        var directory = classId switch
        {
            CategoryResourcePacks => "resourcepacks",
            CategoryShaders => "shaderpacks",
            CategoryWorlds => "saves",
            _ => "mods",
        };

        return $"{directory}/{fileName}";
    }

    /// <summary>CurseForge 的 CDN 路径末段就是文件名，兜底用。</summary>
    private static string FileNameFromUrl(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath))
            : null;

        return string.IsNullOrWhiteSpace(name) ? "unknown.jar" : name;
    }
}
