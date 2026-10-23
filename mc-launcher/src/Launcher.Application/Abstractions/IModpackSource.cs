using Launcher.Domain.Modpacks;

namespace Launcher.Application.Abstractions;

/// <summary>整合包导入的阶段进度。Percent 只在当前阶段内有效。</summary>
public sealed record ModpackImportProgress(string Stage, double Percent, string? Detail)
{
    public static ModpackImportProgress Of(string stage, double percent = 0, string? detail = null) =>
        new(stage, percent, detail);
}

/// <summary>一个已打开的整合包：元数据 + 惰性解压 overrides 的能力。</summary>
public interface IModpackSource : IAsyncDisposable
{
    /// <summary>源文件绝对路径（.zip / .mrpack）。</summary>
    string SourcePath { get; }

    /// <summary>解析好的 modrinth.index.json。</summary>
    ModrinthPack Pack { get; }

    /// <summary>把 overrides/ 下的内容按相对路径解到 targetDirectory，返回解出的文件数。</summary>
    Task<int> ExtractOverridesAsync(
        string targetDirectory,
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 展开出真正要下载的文件清单。Modrinth 包的 index 里自带 URL，直接返回 <see cref="Pack"/> 的 Files；
    /// CurseForge 包只给 projectID / fileID，需要在这一步换成下载链接和落位路径。
    /// </summary>
    Task<IReadOnlyList<ModpackFile>> ResolveFilesAsync(
        IProgress<ModpackImportProgress>? progress = null,
        CancellationToken ct = default) => Task.FromResult(Pack.Files);
}

/// <summary>按扩展名打开整合包文件。MRPack 和普通 zip 是同一容器。</summary>
public interface IModpackSourceFactory
{
    IModpackSource Open(string path);
}
