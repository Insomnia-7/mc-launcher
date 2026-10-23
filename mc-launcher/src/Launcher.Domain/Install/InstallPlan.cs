namespace Launcher.Domain.Install;

public enum DownloadKind
{
    Client,
    Library,
    Native,
    AssetIndex,
    Asset,
    LogConfig,

    /// <summary>整合包清单里的文件（mods / resourcepacks / shaderpacks）。</summary>
    ModpackFile,

    /// <summary>从 Modrinth 之类的社区源单个下载的 Mod。</summary>
    Mod,
}

/// <summary>一条待下载项。RelativePath 是相对"游戏根目录"的路径，可直接拼接使用。</summary>
public sealed record DownloadEntry(
    string Url,
    string RelativePath,
    long Size,
    string? Sha1,
    DownloadKind Kind)
{
    public string FileName => Path.GetFileName(RelativePath);
}

/// <summary>一次安装所需的全量文件清单。</summary>
public sealed record InstallPlan(
    string VersionId,
    string? MainClass,
    int RequiredJavaMajor,
    IReadOnlyList<DownloadEntry> Entries,
    int AllowedLibraryCount,
    int SkippedLibraryCount)
{
    public long TotalSize => Entries.Sum(e => e.Size);

    public long SizeOf(DownloadKind kind) => Entries.Where(e => e.Kind == kind).Sum(e => e.Size);

    public int CountOf(DownloadKind kind) => Entries.Count(e => e.Kind == kind);
}
