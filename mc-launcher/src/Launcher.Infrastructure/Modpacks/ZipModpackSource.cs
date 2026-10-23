using System.IO.Compression;
using Launcher.Application.Abstractions;
using Launcher.Application.Mods;
using Launcher.Domain.Modpacks;

namespace Launcher.Infrastructure.Modpacks;

/// <summary>
/// .mrpack / .zip 整合包（Modrinth 规范）。整包是普通 zip：
/// 根下 modrinth.index.json + overrides/ 目录。
/// 只读打开，不落临时目录，overrides 直接流式解到目标位置。
/// </summary>
public sealed class ZipModpackSource : IModpackSource
{
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;

    private ZipModpackSource(string sourcePath, FileStream stream, ZipArchive archive, ModrinthPack pack)
    {
        SourcePath = sourcePath;
        _stream = stream;
        _archive = archive;
        Pack = pack;
    }

    public string SourcePath { get; }

    public ModrinthPack Pack { get; }

    public static ZipModpackSource Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("整合包文件不存在", path);

        var stream = File.OpenRead(path);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            // index 可能不在根（少数工具会套一层目录），按文件名找第一个
            var indexEntry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("不是 Modrinth 整合包：找不到 modrinth.index.json");

            using var buffer = new MemoryStream();
            using (var entryStream = indexEntry.Open())
                entryStream.CopyTo(buffer);

            var pack = ModrinthPack.Parse(buffer.ToArray());
            return new ZipModpackSource(path, stream, archive, pack);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 解 <c>overrides/</c>，再解 <c>client-overrides/</c>（后者覆盖前者同名文件）。
    /// 越界（<c>../</c>）与长路径都在 <see cref="OverridesExtractor"/> 里统一处理。
    /// </summary>
    public Task<int> ExtractOverridesAsync(
        string targetDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) =>
        OverridesExtractor.ExtractAsync(
            _archive,
            ["overrides", OverridesExtractor.ClientOverridesDirectory],
            targetDirectory,
            progress,
            ct);

    public async ValueTask DisposeAsync()
    {
        _archive.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 按 zip 内容分派：根目录有 modrinth.index.json 走 Modrinth 规范，有 manifest.json 走 CurseForge 规范。
/// .mrpack 和 .zip 是同一个容器，光看扩展名分不出来，只能看里面装的是什么。
/// </summary>
public sealed class ZipModpackSourceFactory(ICurseForgeClient curseForge) : IModpackSourceFactory
{
    public IModpackSource Open(string path)
    {
        // MultiMC / Prism 的「包」是一个实例文件夹，不是 zip：先按目录判，再走文件那条路。
        if (Directory.Exists(path))
        {
            if (MultiMcPackSource.IsMultiMcInstance(path)) return MultiMcPackSource.Open(path);

            throw new InvalidDataException(
                "这个文件夹不是 MultiMC / Prism 实例：缺少 mmc-pack.json。若想导入压缩包，请选 .zip / .mrpack 文件");
        }

        if (!File.Exists(path)) throw new FileNotFoundException("整合包不存在（既不是文件也不是实例文件夹）", path);

        return DetectKind(path) switch
        {
            ModpackKind.Modrinth => ZipModpackSource.Open(path),
            ModpackKind.CurseForge => CurseForgePackSource.Open(path, curseForge),
            _ => throw new InvalidDataException(
                "无法识别的整合包：压缩包根目录既没有 modrinth.index.json，也没有 manifest.json"),
        };
    }

    private enum ModpackKind
    {
        Unknown,
        Modrinth,
        CurseForge,
    }

    private static ModpackKind DetectKind(string path)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var kind = ModpackKind.Unknown;
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
                return ModpackKind.Modrinth;

            if (entry.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                kind = ModpackKind.CurseForge;
        }

        return kind;
    }
}
